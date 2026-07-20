namespace FingerprintLockManager
{
    /// <summary>
    /// 用户服务。所有读写都通过根节点 SD 的 users.json 完成。
    /// V2.7：新增 GetVisibleUsers / GetVisibleUsersBrief 按当前用户角色过滤数据范围。
    /// </summary>
    public class UserService
    {
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
            if (user == null || string.IsNullOrWhiteSpace(user.UserId) ||
                !PasswordHelper.IsPasswordAcceptable(password)) return false;

            var users = _root.Read<User>("users");
            if (users.Any(u => u.UserId == user.UserId)) return false;

            string salt = PasswordHelper.GenerateSalt();
            user.PasswordSalt = salt;
            user.PasswordHash = PasswordHelper.HashPassword(password, salt);
            user.CreateTime = user.CreateTime == default ? DateTime.Now : user.CreateTime;
            user.UpdateTime = DateTime.Now;
            user.Enabled = true;
            users.Add(user);
            return _root.Save("users", users);
        }

        public bool AddUser(User user)
        {
            if (user == null || string.IsNullOrWhiteSpace(user.UserId)) return false;
            var users = _root.Read<User>("users");
            if (users.Any(u => u.UserId == user.UserId)) return false;
            if (string.IsNullOrEmpty(user.PasswordSalt)) user.PasswordSalt = PasswordHelper.GenerateSalt();
            user.PasswordHash ??= "";
            user.CreateTime = user.CreateTime == default ? DateTime.Now : user.CreateTime;
            user.UpdateTime = DateTime.Now;
            user.Enabled = true;
            users.Add(user);
            return _root.Save("users", users);
        }

        public bool UpdateUser(User user)
        {
            if (user == null || string.IsNullOrWhiteSpace(user.UserId)) return false;
            var users = _root.Read<User>("users");
            var existing = users.FirstOrDefault(u => u.UserId == user.UserId);
            if (existing == null) return false;

            // V2.7：教师只能修改本班学生
            Scope.EnsureCanModify(existing);

            user.UpdateTime = DateTime.Now;
            int index = users.IndexOf(existing);
            users[index] = user;
            return _root.Save("users", users);
        }

        public bool DeleteUser(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return false;
            var users = _root.Read<User>("users");
            var existing = users.FirstOrDefault(u => u.UserId == userId);
            if (existing == null) return false;

            // V2.7：教师只能删除本班学生
            Scope.EnsureCanModify(existing);

            int removed = users.RemoveAll(u => u.UserId == userId);
            if (removed == 0) return false;
            if (!_root.Save("users", users)) return false;

            var permissions = _root.Read<UserPermission>("permissions");
            permissions.RemoveAll(p => p.UserId == userId);
            _root.Save("permissions", permissions);
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

        public bool ResetPassword(string userId, string newPassword)
        {
            if (string.IsNullOrWhiteSpace(userId) ||
                !PasswordHelper.IsPasswordAcceptable(newPassword)) return false;
            var users = _root.Read<User>("users");
            var user = users.FirstOrDefault(u => u.UserId == userId);
            if (user == null) return false;

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
            return _root.Save("users", users);
        }

        public int GetNextFingerprintId()
        {
            return _root.Read<User>("users")
                .Where(u => u.FingerprintId.HasValue)
                .Select(u => u.FingerprintId!.Value)
                .DefaultIfEmpty(0)
                .Max() + 1;
        }

        /// <summary>
        /// 获取下一个可用的指纹 ID（仅基于本地缓存中已用的 ID 计算，不依赖 SD）。
        /// 同时考虑本地指纹模板库中已存在的 fingerprintId 与本地缓存 users 表中的 fingerprint_id。
        /// </summary>
        public int GetNextFingerprintIdLocal()
        {
            int maxFromCache = 0;
            try
            {
                // 本地指纹模板库的 fingerprintId
                var metas = LocalCacheService.ReadAllFpTemplateMetas();
                if (metas.Count > 0)
                    maxFromCache = metas.Max(m => m.FingerprintId);
            }
            catch
            {
                // 忽略
            }

            int maxFromUsers = 0;
            try
            {
                // 本地缓存 users 表的 fingerprint_id（SD 不可用时的兜底）
                var users = LocalCacheService.ReadTable("users");
                if (users != null)
                {
                    foreach (var token in users.OfType<Newtonsoft.Json.Linq.JObject>())
                    {
                        var fpId = token.Value<int?>("fingerprint_id");
                        if (fpId.HasValue && fpId.Value > maxFromUsers)
                            maxFromUsers = fpId.Value;
                    }
                }
            }
            catch
            {
                // 忽略
            }

            return Math.Max(maxFromCache, maxFromUsers) + 1;
        }

        /// <summary>
        /// 获取所有用户的简要信息列表（用于指纹模板关联选择）。
        /// 优先从 SD 卡读取；SD 不可用时回落到本地缓存的 users 表。
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
                        FingerprintId = u.FingerprintId
                    });
                }
            }
            catch (RootDataUnavailableException)
            {
                // SD 不可用时从本地缓存读取
                try
                {
                    var arr = LocalCacheService.ReadTable("users");
                    if (arr != null)
                    {
                        foreach (var token in arr.OfType<Newtonsoft.Json.Linq.JObject>())
                        {
                            result.Add(new UserBrief
                            {
                                UserId = token.Value<string>("user_id") ?? "",
                                Name = token.Value<string>("name") ?? "",
                                Role = token.Value<string>("role") ?? "",
                                FingerprintId = token.Value<int?>("fingerprint_id")
                            });
                        }
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

        public override string ToString()
        {
            string fp = FingerprintId.HasValue ? $" [指纹:{FingerprintId.Value}]" : "";
            return $"{Name} ({UserId}) [{Role}]{fp}";
        }
    }
}
