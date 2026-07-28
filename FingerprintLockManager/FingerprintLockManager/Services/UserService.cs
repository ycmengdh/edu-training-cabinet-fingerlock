namespace FingerprintLockManager
{
    /// <summary>
    /// 用户服务。所有读写都通过根节点 SD 的 users.json 完成。
    /// V2.7：新增 GetVisibleUsers / GetVisibleUsersBrief 按当前用户角色过滤数据范围。
    /// </summary>
    public class UserService
    {
        private const int FingerprintSlotCount = 300;
        private readonly RootDataService _root = new RootDataService();
        private static IDataScopeContext Scope => DataScopeContext.Instance;

        public List<User> GetAllUsers()
        {
            return _root.Read<User>("users")
                .OrderBy(u => u.Role).ThenBy(u => u.UserId).ToList();
        }

        /// <summary>
        /// V2.7：获取当前用户可见范围内的用户列表。
        /// Admin 全部；Teacher 本班学生 + 自己 + 管理员（只读）；Student 仅自己。
        /// </summary>
        public List<User> GetVisibleUsers()
        {
            var all = GetAllUsers();
            var current = Scope.CurrentUser;
            if (current == null) return new List<User>();
            return all.Where(u => Scope.CanSee(u)).ToList();
        }

        public List<User> GetUsersByRole(string role)
        {
            if (string.IsNullOrWhiteSpace(role)) return new List<User>();
            return GetAllUsers().Where(u => u.Role == role).ToList();
        }

        /// <summary>V2.7：获取当前用户可见范围内指定角色的用户。</summary>
        public List<User> GetVisibleUsersByRole(string role)
        {
            if (string.IsNullOrWhiteSpace(role)) return new List<User>();
            return GetVisibleUsers().Where(u => u.Role == role).ToList();
        }

        public User? GetUser(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return null;
            return _root.Read<User>("users")
                .FirstOrDefault(u => u.UserId == userId);
        }

        public User? GetUserByFingerprint(int fingerprintId)
        {
            return _root.Read<User>("users")
                .FirstOrDefault(u => u.FingerprintId == fingerprintId);
        }

        public bool AddUser(User user, string password)
        {
            if (user == null || string.IsNullOrWhiteSpace(user.UserId)) return false;

            // 登录阶段创建内置管理员时尚未建立 CurrentUser；正常操作必须经过角色范围校验。
            if (Scope.CurrentUser != null) Scope.EnsureCanCreate(user);
            bool requiresPassword = !string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase);
            if (requiresPassword && !PasswordHelper.IsPasswordAcceptable(password)) return false;

            var users = _root.Read<User>("users");
            if (users.Any(u => u.UserId == user.UserId)) return false;

            if (requiresPassword)
            {
                string salt = PasswordHelper.GenerateSalt();
                user.PasswordSalt = salt;
                user.PasswordHash = PasswordHelper.HashPassword(password, salt);
            }
            else
            {
                user.PasswordSalt = "";
                user.PasswordHash = "";
            }
            user.CreateTime = user.CreateTime == default ? DateTime.Now : user.CreateTime;
            user.UpdateTime = DateTime.Now;
            user.Enabled = true;
            user.AssignedDeviceIds ??= string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase)
                ? new List<string>() : null;
            user.CabinetAssignments ??= string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase)
                ? new List<CabinetAssignment>() : null;
            return SaveNewUserWithDefaultPermissions(users, user);
        }

        public bool AddUser(User user)
        {
            if (user == null || string.IsNullOrWhiteSpace(user.UserId)) return false;
            if (Scope.CurrentUser != null) Scope.EnsureCanCreate(user);
            var users = _root.Read<User>("users");
            if (users.Any(u => u.UserId == user.UserId)) return false;
            if (string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase))
            {
                user.PasswordSalt = "";
                user.PasswordHash = "";
            }
            else
            {
                if (string.IsNullOrEmpty(user.PasswordSalt)) user.PasswordSalt = PasswordHelper.GenerateSalt();
                user.PasswordHash ??= "";
            }
            user.CreateTime = user.CreateTime == default ? DateTime.Now : user.CreateTime;
            user.UpdateTime = DateTime.Now;
            user.Enabled = true;
            user.AssignedDeviceIds ??= string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase)
                ? new List<string>() : null;
            user.CabinetAssignments ??= string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase)
                ? new List<CabinetAssignment>() : null;
            return SaveNewUserWithDefaultPermissions(users, user);
        }

        private bool SaveNewUserWithDefaultPermissions(List<User> users, User user)
        {
            List<UserPermission> permissions = _root.Read<UserPermission>("permissions");
            permissions.RemoveAll(permission => string.Equals(
                permission.UserId, user.UserId, StringComparison.OrdinalIgnoreCase));
            permissions.AddRange(new RolePermissionService()
                .CreateDefaultUserPermissions(user.UserId, user.Role));

            users.Add(user);
            if (!_root.Save("users", users)) return false;
            if (_root.Save("permissions", permissions)) return true;

            users.RemoveAll(existing => string.Equals(
                existing.UserId, user.UserId, StringComparison.OrdinalIgnoreCase));
            _root.Save("users", users);
            return false;
        }

        public bool UpdateUser(User user)
        {
            if (user == null || string.IsNullOrWhiteSpace(user.UserId)) return false;
            var users = _root.Read<User>("users");
            var existing = users.FirstOrDefault(u => u.UserId == user.UserId);
            if (existing == null) return false;

            // 教师只能修改本班学生，且不能借此改变角色或班级范围。
            Scope.EnsureCanUpdate(existing, user);

            user.AssignedDeviceIds ??= existing.AssignedDeviceIds?.ToList();
            user.CabinetAssignments ??= existing.CabinetAssignments?.Select(item => new CabinetAssignment
            {
                DeviceId = item.DeviceId,
                ActiveFingerprintId = item.ActiveFingerprintId,
                UpdateTime = item.UpdateTime
            }).ToList();
            user.UpdateTime = DateTime.Now;
            int index = users.IndexOf(existing);
            users[index] = user;
            bool saved = _root.Save("users", users);
            if (saved && existing.Enabled != user.Enabled)
                QueueCabinetRefresh(user, "用户启用状态变化");
            return saved;
        }

        public bool DeleteUser(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return false;
            var users = _root.Read<User>("users");
            var existing = users.FirstOrDefault(u => u.UserId == userId);
            if (existing == null) return false;

            // V2.7：教师只能删除本班学生
            Scope.EnsureCanModify(existing);

            string[] affectedDevices = Array.Empty<string>();
            try
            {
                string[] known = App.DeviceService.GetAllDevices()
                    .Where(device => !DeviceService.IsTrueRoot(device))
                    .Select(device => device.DeviceId).ToArray();
                affectedDevices = App.CabinetBindingService
                    .GetAssignedDeviceIds(existing, known).ToArray();
            }
            catch { }

            int removed = users.RemoveAll(u => u.UserId == userId);
            if (removed == 0) return false;
            if (!_root.Save("users", users)) return false;

            var permissions = _root.Read<UserPermission>("permissions");
            permissions.RemoveAll(p => p.UserId == userId);
            _root.Save("permissions", permissions);
            foreach (string deviceId in affectedDevices)
                App.CabinetSyncQueueService.EnqueueCabinet(deviceId, "删除用户并清理柜机数据");
            App.CabinetSyncQueueService.Trigger();
            return true;
        }

        public bool AssignFingerprint(string userId, int fingerprintId)
        {
            if (string.IsNullOrWhiteSpace(userId) || fingerprintId <= 0) return false;
            var users = _root.Read<User>("users");
            var existing = users.FirstOrDefault(u => u.FingerprintId == fingerprintId && u.UserId != userId);
            if (existing != null) return false;

            var user = users.FirstOrDefault(u => u.UserId == userId);
            if (user == null) return false;

            // V2.7：教师只能为本班学生分配指纹
            Scope.EnsureCanModify(user);

            user.FingerprintId = fingerprintId;
            user.UpdateTime = DateTime.Now;
            return _root.Save("users", users);
        }

        /// <summary>清除用户主指纹编号；柜子模板清理由调用方负责。</summary>
        public bool ClearFingerprint(string userId, int fingerprintId)
        {
            if (string.IsNullOrWhiteSpace(userId) || fingerprintId <= 0) return false;
            var users = _root.Read<User>("users");
            var user = users.FirstOrDefault(u =>
                string.Equals(u.UserId, userId, StringComparison.OrdinalIgnoreCase));
            if (user == null || user.FingerprintId != fingerprintId) return false;

            Scope.EnsureCanModify(user);
            user.FingerprintId = null;
            user.UpdateTime = DateTime.Now;
            return _root.Save("users", users);
        }

        public bool ResetPassword(string userId, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(userId) ||
                !PasswordHelper.IsPasswordAcceptable(newPassword)) return false;
            var users = _root.Read<User>("users");
            var user = users.FirstOrDefault(u => u.UserId == userId);
            if (user == null) return false;
            if (string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase)) return false;

            // 登录哈希迁移时 CurrentUser 尚未建立；本人改密或管理员/教师按范围操作。
            var current = Scope.CurrentUser;
            if (current != null &&
                !string.Equals(current.UserId, userId, StringComparison.OrdinalIgnoreCase))
            {
                Scope.EnsureCanModify(user);
            }

            user.PasswordSalt = PasswordHelper.GenerateSalt();
            user.PasswordHash = PasswordHelper.HashPassword(newPassword, user.PasswordSalt);
            user.UpdateTime = DateTime.Now;
            return _root.Save("users", users);
        }

        public bool SetEnabled(string userId, bool enabled)
        {
            if (string.IsNullOrWhiteSpace(userId)) return false;
            var users = _root.Read<User>("users");
            var user = users.FirstOrDefault(u => u.UserId == userId);
            if (user == null) return false;

            // V2.7：教师只能启用/停用本班学生
            Scope.EnsureCanModify(user);

            user.Enabled = enabled;
            user.UpdateTime = DateTime.Now;
            bool saved = _root.Save("users", users);
            if (saved) QueueCabinetRefresh(user, enabled ? "启用用户" : "停用用户");
            return saved;
        }

        private static void QueueCabinetRefresh(User user, string reason)
        {
            try
            {
                string[] known = App.DeviceService.GetAllDevices()
                    .Where(device => !DeviceService.IsTrueRoot(device))
                    .Select(device => device.DeviceId).ToArray();
                foreach (string deviceId in App.CabinetBindingService
                             .GetAssignedDeviceIds(user, known))
                    App.CabinetSyncQueueService.EnqueueCabinet(deviceId, reason);
                App.CabinetSyncQueueService.Trigger();
            }
            catch { }
        }

        public int GetNextFingerprintId()
        {
            HashSet<int> used = _root.Read<User>("users")
                .Where(u => u.FingerprintId.HasValue)
                .Select(u => u.FingerprintId!.Value)
                .ToHashSet();
            return FindAvailableFingerprintId(used);
        }

        /// <summary>
        /// 获取下一个可用的指纹 ID（仅基于本地缓存中已用的 ID 计算，不依赖 SD）。
        /// 同时考虑本地指纹模板库中已存在的 fingerprintId 与本地缓存 users 表中的 fingerprint_id。
        /// </summary>
        public int GetNextFingerprintIdLocal()
        {
            var used = new HashSet<int>();
            try
            {
                foreach (FingerprintTemplate meta in BusinessDatabase.ReadAllFpTemplateMetas())
                    used.Add(meta.FingerprintId);
            }
            catch
            {
                // 忽略
            }

            try
            {
                // 本机业务库 users 表
                var users = BusinessDatabase.ReadArray("users");
                if (users != null)
                {
                    foreach (var token in users.OfType<Newtonsoft.Json.Linq.JObject>())
                    {
                        var fpId = token.Value<int?>("fingerprint_id");
                        if (fpId.HasValue) used.Add(fpId.Value);
                    }
                }
            }
            catch
            {
                // 忽略
            }

            return FindAvailableFingerprintId(used);
        }

        private static int FindAvailableFingerprintId(IReadOnlySet<int> used)
        {
            for (int fingerprintId = 1; fingerprintId < FingerprintSlotCount; fingerprintId++)
            {
                if (!used.Contains(fingerprintId)) return fingerprintId;
            }
            throw new InvalidOperationException("指纹槽位已满，请先删除不再使用的指纹");
        }

        /// <summary>
        /// 获取所有用户的简要信息列表（用于指纹模板关联选择）。
        /// 读取本机 business.db users 表。
        /// </summary>
        public List<UserBrief> GetAllUsersBrief()
        {
            var result = new List<UserBrief>();
            try
            {
                var users = _root.Read<User>("users");
                foreach (var u in users)
                {
                    result.Add(new UserBrief
                    {
                        UserId = u.UserId,
                        Name = u.Name,
                        Role = u.Role,
                        FingerprintId = u.FingerprintId,
                        Enabled = u.Enabled
                    });
                }
            }
            catch (RootDataUnavailableException)
            {
                try
                {
                    var arr = BusinessDatabase.ReadArray("users");
                    foreach (var token in arr.OfType<Newtonsoft.Json.Linq.JObject>())
                    {
                        result.Add(new UserBrief
                        {
                            UserId = token.Value<string>("user_id") ?? "",
                            Name = token.Value<string>("name") ?? "",
                            Role = token.Value<string>("role") ?? "",
                            FingerprintId = token.Value<int?>("fingerprint_id"),
                            Enabled = token.Value<bool?>("enabled") ?? true
                        });
                    }
                }
                catch
                {
                    // 忽略
                }
            }
            return result
                .OrderBy(u => u.Role)
                .ThenBy(u => u.UserId)
                .ToList();
        }

        /// <summary>
        /// V2.7：获取当前用户可见范围内的用户简要信息列表。
        /// </summary>
        public List<UserBrief> GetVisibleUsersBrief()
        {
            var all = GetAllUsersBrief();
            var current = Scope.CurrentUser;
            if (current == null) return new List<UserBrief>();
            // UserBrief 没有 ClassId，需通过 CanSee(User) 判断；这里重建 User 做判断
            var visibleUsers = GetVisibleUsers();
            var visibleIds = new HashSet<string>(visibleUsers.Select(u => u.UserId), StringComparer.OrdinalIgnoreCase);
            return all.Where(b => visibleIds.Contains(b.UserId)).ToList();
        }
    }

    /// <summary>用户简要信息（用于指纹模板关联选择，避免传输密码哈希等敏感字段）</summary>
    public class UserBrief
    {
        public string UserId { get; set; } = "";
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
        public int? FingerprintId { get; set; }
        public bool Enabled { get; set; } = true;

        public override string ToString()
        {
            string fp = FingerprintId.HasValue ? $" [指纹:{FingerprintId.Value}]" : "";
            return $"{Name} ({UserId}) [{Role}]{fp}";
        }
    }
}
