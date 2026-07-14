using Newtonsoft.Json;
using System.Threading;

namespace FingerprintLockManager
{
    /// <summary>
    /// 内存数据仓库（替代原 SQLite 数据库）
    ///
    /// 根节点 SD 卡是唯一持久化数据源。上位机启动时从 SD 卡全量加载到内存，
    /// 各 Service 操作内存副本（线程安全快照），写操作异步回写 SD 卡。
    /// 上位机不再持有任何本地数据库文件。
    ///
    /// SD 卡表映射：
    ///   users.json             - 用户表
    ///   role_permissions.json  - 角色默认权限表
    ///   user_permissions.json  - 个人权限覆盖表
    ///   devices.json           - 设备注册表
    ///   logs.json              - 操作日志表
    /// </summary>
    public class DataStore
    {
        /// <summary>全局单例：内存数据仓库，根节点 SD 卡为唯一持久化源</summary>
        public static DataStore Current { get; } = new DataStore();

        // ====== 内存表（线程安全，所有访问经 _lock）======
        private readonly object _lock = new();
        private List<User> _users = new();
        private List<RolePermission> _rolePerms = new();
        private List<UserPermission> _userPerms = new();
        private List<Device> _devices = new();
        private List<LogEntry> _logs = new();

        // 自增 ID 序列（从已加载数据恢复）
        private int _userPermIdSeq = 1;
        private long _logIdSeq = 1;

        // 日志脏标记 + 延迟批量保存（避免每条日志都写 SD 卡）
        private bool _logsDirty = false;
        private readonly Timer _logFlushTimer;

        /// <summary>数据是否已从 SD 卡加载完成</summary>
        public bool IsLoaded { get; private set; }

        /// <summary>数据加载完成事件（UI 据此启用登录）</summary>
        public event Action? Loaded;

        private DataStore()
        {
            // 每 5 秒检查一次日志脏标记，脏则批量刷盘
            _logFlushTimer = new Timer(_ => FlushLogsIfNeeded(), null, 5000, 5000);
        }

        // ====== 加载 ======

        /// <summary>
        /// 从根节点 SD 卡全量加载所有表。
        /// 根节点注册成功后由 App 调用。加载完成后触发 Loaded 事件。
        /// 首次使用（SD 卡为空）时自动初始化默认管理员与角色权限。
        /// </summary>
        public async Task LoadFromSdCardAsync()
        {
            try
            {
                var sd = App.SdStorageService;
                if (!sd.IsAvailable)
                {
                    // SD 卡不可用，保持空数据，等待根节点连上后重试
                    System.Diagnostics.Debug.WriteLine("[DataStore] SD 卡不可用，稍后重试");
                    return;
                }

                // 逐表加载（SD 卡命令是串行的，无需并行）
                var usersJson = await sd.QueryTableAsync(TableUsers);
                var rolePermsJson = await sd.QueryTableAsync(TableRolePermissions);
                var userPermsJson = await sd.QueryTableAsync(TableUserPermissions);
                var devicesJson = await sd.QueryTableAsync(TableDevices);
                var logsJson = await sd.QueryTableAsync(TableLogs);

                lock (_lock)
                {
                    _users = DeserializeList<User>(usersJson);
                    _rolePerms = DeserializeList<RolePermission>(rolePermsJson);
                    _userPerms = DeserializeList<UserPermission>(userPermsJson);
                    _devices = DeserializeList<Device>(devicesJson);
                    _logs = DeserializeList<LogEntry>(logsJson);

                    // 恢复自增 ID 序列
                    _userPermIdSeq = _userPerms.Count > 0 ? _userPerms.Max(p => p.Id) + 1 : 1;
                    _logIdSeq = _logs.Count > 0 ? _logs.Max(l => l.Id) + 1 : 1;
                }

                // 首次使用：初始化默认数据并回写 SD 卡
                await InitDefaultDataIfNeeded();

                IsLoaded = true;
                System.Diagnostics.Debug.WriteLine($"[DataStore] 加载完成：用户 {_users.Count}，" +
                    $"角色权限 {_rolePerms.Count}，个人权限 {_userPerms.Count}，" +
                    $"设备 {_devices.Count}，日志 {_logs.Count}");
                Loaded?.Invoke();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DataStore] 加载失败：{ex.Message}");
            }
        }

        /// <summary>
        /// 首次使用时初始化默认管理员账号（admin/admin123）与 3 条角色默认权限。
        /// 仅在对应表为空时插入，不覆盖已有数据。
        /// </summary>
        private async Task InitDefaultDataIfNeeded()
        {
            bool needSaveUsers = false;
            bool needSaveRolePerms = false;

            lock (_lock)
            {
                // 初始化角色默认权限
                if (_rolePerms.Count == 0)
                {
                    var now = DateTime.Now;
                    _rolePerms = new List<RolePermission>
                    {
                        new() { Role = "admin", Lock0 = true, Lock1 = true, Lock2 = true, Lock3 = true, UpdateTime = now },
                        new() { Role = "teacher", Lock0 = false, Lock1 = true, Lock2 = true, Lock3 = true, UpdateTime = now },
                        new() { Role = "student", Lock0 = false, Lock1 = false, Lock2 = false, Lock3 = false, UpdateTime = now }
                    };
                    needSaveRolePerms = true;
                }

                // 初始化默认管理员
                if (!_users.Any(u => u.Role == "admin"))
                {
                    string salt = PasswordHelper.GenerateSalt();
                    _users.Add(new User
                    {
                        UserId = "admin",
                        Name = "系统管理员",
                        Role = "admin",
                        FingerprintId = null,
                        PasswordSalt = salt,
                        PasswordHash = PasswordHelper.HashPassword("admin123", salt),
                        CreateTime = DateTime.Now,
                        UpdateTime = DateTime.Now
                    });
                    needSaveUsers = true;
                }
            }

            if (needSaveRolePerms) await SaveRolePermissionsAsync();
            if (needSaveUsers) await SaveUsersAsync();
        }

        // ====== 快照读取（线程安全，返回副本）======

        public List<User> GetUsers() { lock (_lock) return _users.ToList(); }
        public List<RolePermission> GetRolePermissions() { lock (_lock) return _rolePerms.ToList(); }
        public List<UserPermission> GetUserPermissions() { lock (_lock) return _userPerms.ToList(); }
        public List<Device> GetDevices() { lock (_lock) return _devices.ToList(); }
        public List<LogEntry> GetLogs() { lock (_lock) return _logs.ToList(); }

        // ====== 自增 ID ======

        public int NextUserPermissionId()
        {
            lock (_lock) { return _userPermIdSeq++; }
        }

        // ====== 写操作（更新内存 + 异步回写 SD 卡）======
        // 每次写操作在锁内修改内存列表后，立即触发异步整表保存。
        // 保存失败（根节点离线）时静默忽略，内存数据仍有效；根节点恢复后可手动 FlushAll。

        /// <summary>在锁内修改用户表，并触发异步回写</summary>
        public void MutateUsers(Action<List<User>> action)
        {
            lock (_lock) action(_users);
            _ = SaveUsersAsync();
        }

        /// <summary>在锁内修改角色权限表，并触发异步回写</summary>
        public void MutateRolePermissions(Action<List<RolePermission>> action)
        {
            lock (_lock) action(_rolePerms);
            _ = SaveRolePermissionsAsync();
        }

        /// <summary>在锁内修改个人权限覆盖表，并触发异步回写</summary>
        public void MutateUserPermissions(Action<List<UserPermission>> action)
        {
            lock (_lock) action(_userPerms);
            _ = SaveUserPermissionsAsync();
        }

        /// <summary>在锁内修改设备表，并触发异步回写</summary>
        public void MutateDevices(Action<List<Device>> action)
        {
            lock (_lock) action(_devices);
            _ = SaveDevicesAsync();
        }

        // ====== 日志（脏标记 + 延迟批量保存）======

        /// <summary>
        /// 追加一条日志到内存（仅标记脏，不立即写 SD 卡）。
        /// 由后台 Timer 每 5 秒批量刷盘，避免频繁写。
        /// </summary>
        public void AddLog(LogEntry log)
        {
            if (log == null) return;
            lock (_lock)
            {
                log.Id = _logIdSeq++;
                if (log.CreateTime == default(DateTime))
                {
                    log.CreateTime = DateTime.Now;
                }
                _logs.Add(log);
                _logsDirty = true;
            }
        }

        /// <summary>批量追加日志</summary>
        public void AddLogs(List<LogEntry> logs)
        {
            if (logs == null || logs.Count == 0) return;
            lock (_lock)
            {
                DateTime now = DateTime.Now;
                foreach (var log in logs)
                {
                    log.Id = _logIdSeq++;
                    if (log.CreateTime == default(DateTime)) log.CreateTime = now;
                    _logs.Add(log);
                }
                _logsDirty = true;
            }
        }

        /// <summary>清除所有日志</summary>
        public void ClearLogs()
        {
            lock (_lock)
            {
                _logs.Clear();
                _logIdSeq = 1;
                _logsDirty = true;
            }
        }

        /// <summary>Timer 回调：日志脏则批量刷盘</summary>
        private void FlushLogsIfNeeded()
        {
            if (!_logsDirty) return;
            List<LogEntry> snapshot;
            lock (_lock)
            {
                if (!_logsDirty) return;
                _logsDirty = false;
                snapshot = _logs.ToList();
            }
            _ = SaveTableAsync(TableLogs, snapshot);
        }

        /// <summary>强制保存所有脏数据（根节点重连或程序退出时调用）</summary>
        public async Task FlushAllAsync()
        {
            await SaveUsersAsync();
            await SaveRolePermissionsAsync();
            await SaveUserPermissionsAsync();
            await SaveDevicesAsync();
            // 日志立即刷盘
            List<LogEntry> logSnapshot;
            lock (_lock) { _logsDirty = false; logSnapshot = _logs.ToList(); }
            await SaveTableAsync(TableLogs, logSnapshot);
        }

        // ====== 单表保存（私有，异步）======

        private async Task SaveUsersAsync()
        {
            List<User> snapshot;
            lock (_lock) snapshot = _users.ToList();
            await SaveTableAsync(TableUsers, snapshot);
        }

        private async Task SaveRolePermissionsAsync()
        {
            List<RolePermission> snapshot;
            lock (_lock) snapshot = _rolePerms.ToList();
            await SaveTableAsync(TableRolePermissions, snapshot);
        }

        private async Task SaveUserPermissionsAsync()
        {
            List<UserPermission> snapshot;
            lock (_lock) snapshot = _userPerms.ToList();
            await SaveTableAsync(TableUserPermissions, snapshot);
        }

        private async Task SaveDevicesAsync()
        {
            List<Device> snapshot;
            lock (_lock) snapshot = _devices.ToList();
            await SaveTableAsync(TableDevices, snapshot);
        }

        /// <summary>序列化列表为 JSON 并写入 SD 卡</summary>
        private async Task SaveTableAsync<T>(string tableName, List<T> data)
        {
            try
            {
                var sd = App.SdStorageService;
                if (!sd.IsAvailable) return;
                string json = JsonConvert.SerializeObject(data);
                await sd.SaveTableAsync(tableName, json);
            }
            catch
            {
                // 保存失败静默忽略，内存数据仍有效
            }
        }

        // ====== 工具 ======

        /// <summary>反序列化 JSON 为列表；空或失败返回空列表</summary>
        private static List<T> DeserializeList<T>(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new List<T>();
            try
            {
                return JsonConvert.DeserializeObject<List<T>>(json) ?? new List<T>();
            }
            catch
            {
                return new List<T>();
            }
        }

        // ====== 表名常量 ======
        private const string TableUsers = "users";
        private const string TableRolePermissions = "role_permissions";
        private const string TableUserPermissions = "user_permissions";
        private const string TableDevices = "devices";
        private const string TableLogs = "logs";
    }
}
