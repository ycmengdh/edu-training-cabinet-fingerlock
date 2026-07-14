using Newtonsoft.Json;

namespace FingerprintLockManager
{
    /// <summary>
    /// 内存数据仓库（根节点 SD 卡为唯一持久化源）
    ///
    /// 业务主数据（users/classes/permissions/devices/authorizations/fp_templates）走 SD 卡：
    ///   启动时全量加载到内存，各 Service 操作内存副本（线程安全快照），写操作异步回写 SD 卡。
    ///
    /// 日志与下发状态走上位机本地 SQLite（见 LogDbService），不经过本类。
    ///
    /// SD 卡表映射：
    ///   users.json                  - 用户表
    ///   classes.json                 - 班级表
    ///   role_permissions.json        - 角色默认权限表
    ///   user_permissions.json        - 个人权限覆盖表
    ///   device_authorizations.json   - 设备授权关系表（学生×柜子×锁权限）
    ///   devices.json                 - 设备注册表
    ///   fingerprint_templates.json   - 指纹模板元数据表
    /// </summary>
    public class DataStore
    {
        /// <summary>全局单例：内存数据仓库，根节点 SD 卡为唯一持久化源</summary>
        public static DataStore Current { get; } = new DataStore();

        // ====== 内存表（线程安全，所有访问经 _lock）======
        private readonly object _lock = new();
        private List<User> _users = new();
        private List<ClassInfo> _classes = new();
        private List<RolePermission> _rolePerms = new();
        private List<UserPermission> _userPerms = new();
        private List<DeviceAuthorization> _deviceAuths = new();
        private List<Device> _devices = new();
        private List<FingerprintTemplate> _fpTemplates = new();

        // 自增 ID 序列（从已加载数据恢复）
        private int _userPermIdSeq = 1;
        private long _deviceAuthIdSeq = 1;

        /// <summary>数据是否已从 SD 卡加载完成</summary>
        public bool IsLoaded { get; private set; }

        /// <summary>数据加载完成事件（UI 据此启用登录）</summary>
        public event Action? Loaded;

        // ====== 加载 ======

        /// <summary>
        /// 从根节点 SD 卡全量加载所有业务表。
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
                    System.Diagnostics.Debug.WriteLine("[DataStore] SD 卡不可用，稍后重试");
                    return;
                }

                // 逐表加载（SD 卡命令是串行的，无需并行）
                var usersJson = await sd.QueryTableAsync(TableUsers);
                var classesJson = await sd.QueryTableAsync(TableClasses);
                var rolePermsJson = await sd.QueryTableAsync(TableRolePermissions);
                var userPermsJson = await sd.QueryTableAsync(TableUserPermissions);
                var deviceAuthsJson = await sd.QueryTableAsync(TableDeviceAuthorizations);
                var devicesJson = await sd.QueryTableAsync(TableDevices);
                var fpTemplatesJson = await sd.QueryTableAsync(TableFpTemplates);

                lock (_lock)
                {
                    _users = DeserializeList<User>(usersJson);
                    _classes = DeserializeList<ClassInfo>(classesJson);
                    _rolePerms = DeserializeList<RolePermission>(rolePermsJson);
                    _userPerms = DeserializeList<UserPermission>(userPermsJson);
                    _deviceAuths = DeserializeList<DeviceAuthorization>(deviceAuthsJson);
                    _devices = DeserializeList<Device>(devicesJson);
                    _fpTemplates = DeserializeList<FingerprintTemplate>(fpTemplatesJson);

                    // 恢复自增 ID 序列
                    _userPermIdSeq = _userPerms.Count > 0 ? _userPerms.Max(p => p.Id) + 1 : 1;
                    _deviceAuthIdSeq = _deviceAuths.Count > 0 ? _deviceAuths.Max(a => a.Id) + 1 : 1;
                }

                // 首次使用：初始化默认数据并回写 SD 卡
                await InitDefaultDataIfNeeded();

                IsLoaded = true;
                System.Diagnostics.Debug.WriteLine($"[DataStore] 加载完成：用户 {_users.Count}，" +
                    $"班级 {_classes.Count}，设备授权 {_deviceAuths.Count}，" +
                    $"指纹模板 {_fpTemplates.Count}，设备 {_devices.Count}");
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
                        ClassId = null,
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
        public List<ClassInfo> GetClasses() { lock (_lock) return _classes.ToList(); }
        public List<RolePermission> GetRolePermissions() { lock (_lock) return _rolePerms.ToList(); }
        public List<UserPermission> GetUserPermissions() { lock (_lock) return _userPerms.ToList(); }
        public List<DeviceAuthorization> GetDeviceAuthorizations() { lock (_lock) return _deviceAuths.ToList(); }
        public List<Device> GetDevices() { lock (_lock) return _devices.ToList(); }
        public List<FingerprintTemplate> GetFingerprintTemplates() { lock (_lock) return _fpTemplates.ToList(); }

        // ====== 自增 ID ======

        public int NextUserPermissionId()
        {
            lock (_lock) { return _userPermIdSeq++; }
        }

        public long NextDeviceAuthorizationId()
        {
            lock (_lock) { return _deviceAuthIdSeq++; }
        }

        // ====== 写操作（更新内存 + 异步回写 SD 卡）======

        /// <summary>在锁内修改用户表，并触发异步回写</summary>
        public void MutateUsers(Action<List<User>> action)
        {
            lock (_lock) action(_users);
            _ = SaveUsersAsync();
        }

        /// <summary>在锁内修改班级表，并触发异步回写</summary>
        public void MutateClasses(Action<List<ClassInfo>> action)
        {
            lock (_lock) action(_classes);
            _ = SaveClassesAsync();
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

        /// <summary>在锁内修改设备授权表，并触发异步回写</summary>
        public void MutateDeviceAuthorizations(Action<List<DeviceAuthorization>> action)
        {
            lock (_lock) action(_deviceAuths);
            _ = SaveDeviceAuthorizationsAsync();
        }

        /// <summary>在锁内修改设备表，并触发异步回写</summary>
        public void MutateDevices(Action<List<Device>> action)
        {
            lock (_lock) action(_devices);
            _ = SaveDevicesAsync();
        }

        /// <summary>在锁内修改指纹模板表，并触发异步回写</summary>
        public void MutateFingerprintTemplates(Action<List<FingerprintTemplate>> action)
        {
            lock (_lock) action(_fpTemplates);
            _ = SaveFingerprintTemplatesAsync();
        }

        /// <summary>强制保存所有表到 SD 卡（根节点重连或程序退出时调用）</summary>
        public async Task FlushAllAsync()
        {
            await SaveUsersAsync();
            await SaveClassesAsync();
            await SaveRolePermissionsAsync();
            await SaveUserPermissionsAsync();
            await SaveDeviceAuthorizationsAsync();
            await SaveDevicesAsync();
            await SaveFingerprintTemplatesAsync();
        }

        // ====== 单表保存（私有，异步）======

        private async Task SaveUsersAsync()
        {
            List<User> snapshot;
            lock (_lock) snapshot = _users.ToList();
            await SaveTableAsync(TableUsers, snapshot);
        }

        private async Task SaveClassesAsync()
        {
            List<ClassInfo> snapshot;
            lock (_lock) snapshot = _classes.ToList();
            await SaveTableAsync(TableClasses, snapshot);
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

        private async Task SaveDeviceAuthorizationsAsync()
        {
            List<DeviceAuthorization> snapshot;
            lock (_lock) snapshot = _deviceAuths.ToList();
            await SaveTableAsync(TableDeviceAuthorizations, snapshot);
        }

        private async Task SaveDevicesAsync()
        {
            List<Device> snapshot;
            lock (_lock) snapshot = _devices.ToList();
            await SaveTableAsync(TableDevices, snapshot);
        }

        private async Task SaveFingerprintTemplatesAsync()
        {
            List<FingerprintTemplate> snapshot;
            lock (_lock) snapshot = _fpTemplates.ToList();
            await SaveTableAsync(TableFpTemplates, snapshot);
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
        private const string TableClasses = "classes";
        private const string TableRolePermissions = "role_permissions";
        private const string TableUserPermissions = "user_permissions";
        private const string TableDeviceAuthorizations = "device_authorizations";
        private const string TableDevices = "devices";
        private const string TableFpTemplates = "fingerprint_templates";
    }
}
