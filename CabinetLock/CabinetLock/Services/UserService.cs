namespace CabinetLock
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
            List<User> users = _root.Read<User>("users");
            foreach (User user in users) NormalizeClassAssignments(user);
            return users.OrderBy(u => u.Role).ThenBy(u => u.UserId).ToList();
        }

        public PagedResult<User> QueryVisibleUsersPage(
            int pageIndex, int pageSize, string? role = null, string? keyword = null,
            string? classId = null, string? className = null,
            UserPageSort sort = UserPageSort.RoleThenId)
        {
            User? current = Scope.CurrentUser;
            PagedResult<User> result = BusinessDatabase.QueryUsers(new UserPageQuery
            {
                PageIndex = pageIndex,
                PageSize = pageSize,
                Role = role,
                Keyword = keyword,
                ClassId = classId,
                ClassName = className,
                ScopeRole = current?.Role ?? "",
                ScopeUserId = current?.UserId ?? "",
                ScopeClassIds = Scope.GetVisibleClassIds(),
                Sort = sort
            });
            foreach (User user in result.Items) NormalizeClassAssignments(user);
            return result;
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

        public StudentBindingStatistics GetVisibleStudentBindingStatistics()
        {
            User? current = Scope.CurrentUser;
            return BusinessDatabase.QueryStudentBindingStatistics(
                current?.Role ?? "", current?.UserId ?? "", Scope.GetVisibleClassIds());
        }

        public User? GetUser(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return null;
            User? user = BusinessDatabase.ReadUser(userId);
            if (user != null) NormalizeClassAssignments(user);
            return user;
        }

        public User? GetUserByCode(string userCode)
        {
            if (string.IsNullOrWhiteSpace(userCode)) return null;
            return GetAllUsers().FirstOrDefault(user => string.Equals(
                user.DisplayId, userCode.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public static string CreateInternalUserId() => $"usr_{Guid.NewGuid():N}";

        public User? GetUserByFingerprint(int fingerprintId)
        {
            return _root.Read<User>("users")
                .FirstOrDefault(u => u.FingerprintId == fingerprintId);
        }

        public bool AddUser(User user, string password)
        {
            if (user == null) return false;
            if (SystemAdministratorPolicy.IsReserved(user))
                SystemAdministratorPolicy.Normalize(user);
            PrepareNewIdentity(user);
            if (string.IsNullOrWhiteSpace(user.UserId) || string.IsNullOrWhiteSpace(user.UserCode)) return false;

            // 登录阶段创建内置管理员时尚未建立 CurrentUser；正常操作必须经过角色范围校验。
            if (Scope.CurrentUser != null) Scope.EnsureCanCreate(user);
            bool requiresPassword = !string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase);
            if (requiresPassword && !PasswordHelper.IsPasswordAcceptable(password)) return false;

            var users = _root.Read<User>("users");
            if (users.Any(u => string.Equals(u.UserId, user.UserId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(u.DisplayId, user.UserCode, StringComparison.OrdinalIgnoreCase))) return false;

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
            NormalizeClassAssignments(user);
            user.AssignedDeviceIds ??= string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase)
                ? new List<string>() : null;
            user.CabinetAssignments ??= string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase)
                ? new List<CabinetAssignment>() : null;
            return SaveNewUserWithDefaultPermissions(users, user);
        }

        public bool AddUser(User user)
        {
            if (user == null) return false;
            if (SystemAdministratorPolicy.IsReserved(user))
                SystemAdministratorPolicy.Normalize(user);
            PrepareNewIdentity(user);
            if (string.IsNullOrWhiteSpace(user.UserId) || string.IsNullOrWhiteSpace(user.UserCode)) return false;
            if (Scope.CurrentUser != null) Scope.EnsureCanCreate(user);
            var users = _root.Read<User>("users");
            if (users.Any(u => string.Equals(u.UserId, user.UserId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(u.DisplayId, user.UserCode, StringComparison.OrdinalIgnoreCase))) return false;
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
            NormalizeClassAssignments(user);
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
            if (SystemAdministratorPolicy.IsReserved(existing))
            {
                // Profile edits must not be able to rename the recovery login,
                // change its role, or overwrite its password outside the password flow.
                user.UserId = SystemAdministratorPolicy.UserId;
                user.UserCode = SystemAdministratorPolicy.UserId;
                user.Role = "admin";
                user.PasswordSalt = existing.PasswordSalt;
                user.PasswordHash = existing.PasswordHash;
                if (user.CreateTime == default) user.CreateTime = existing.CreateTime;
                SystemAdministratorPolicy.Normalize(user);
            }
            else
            {
                user.UserCode = string.IsNullOrWhiteSpace(user.UserCode)
                    ? existing.DisplayId : user.UserCode.Trim();
            }
            if (users.Any(item => !string.Equals(item.UserId, user.UserId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(item.DisplayId, user.UserCode, StringComparison.OrdinalIgnoreCase))) return false;

            // 教师只能修改本班学生，且不能借此改变角色或班级范围。
            Scope.EnsureCanUpdate(existing, user);

            user.AssignedDeviceIds ??= existing.AssignedDeviceIds?.ToList();
            user.CabinetAssignments ??= existing.CabinetAssignments?.Select(item => new CabinetAssignment
            {
                DeviceId = item.DeviceId,
                FingerprintIds = item.FingerprintIds.ToList(),
                LockIds = item.LockIds?.ToList(),
                UpdateTime = item.UpdateTime
            }).ToList();
            if (string.Equals(user.Role, "teacher", StringComparison.OrdinalIgnoreCase) && user.ClassIds == null)
                user.ClassIds = existing.GetResponsibleClassIds().ToList();
            NormalizeClassAssignments(user);
            user.UpdateTime = DateTime.Now;
            int index = users.IndexOf(existing);
            users[index] = user;
            bool saved = _root.Save("users", users);
            if (saved)
                QueueCabinetRefresh(user, user.Enabled ? "修改用户资料" : "停用用户");
            return saved;
        }

        public bool DeleteUser(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return false;
            var users = _root.Read<User>("users");
            var existing = users.FirstOrDefault(u => u.UserId == userId);
            if (existing == null) return false;
            if (SystemAdministratorPolicy.IsReserved(existing)) return false;

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
            App.CabinetSyncQueueService.EnqueueUserDeletion(
                userId, affectedDevices, "删除用户并清理柜机数据");
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
            bool saved = _root.Save("users", users);
            if (saved)
            {
                try
                {
                    string[] known = App.DeviceService.GetAllDevices()
                        .Where(device => !DeviceService.IsTrueRoot(device))
                        .Select(device => device.DeviceId).ToArray();
                    foreach (string deviceId in App.CabinetBindingService
                                 .GetAssignedDeviceIds(user, known))
                        App.CabinetSyncQueueService.EnqueueCabinet(
                            deviceId, "更新用户主指纹并清理旧槽位");
                    App.CabinetSyncQueueService.Trigger();
                }
                catch { }
            }
            return saved;
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

        public bool SetTeacherClasses(string teacherId, IEnumerable<string>? classIds)
        {
            if (!Scope.IsAdmin) throw new UnauthorizedAccessException("只有管理员可以调整教师负责班级");
            var users = _root.Read<User>("users");
            User? teacher = users.FirstOrDefault(user => string.Equals(
                user.UserId, teacherId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(user.Role, "teacher", StringComparison.OrdinalIgnoreCase));
            if (teacher == null) return false;
            teacher.SetResponsibleClassIds(classIds);
            teacher.UpdateTime = DateTime.Now;
            return _root.Save("users", users);
        }

        public bool SetClassTeachers(string classId, IEnumerable<string>? teacherIds)
        {
            if (!Scope.IsAdmin) throw new UnauthorizedAccessException("只有管理员可以调整班级负责教师");
            if (string.IsNullOrWhiteSpace(classId)) return false;
            HashSet<string> selected = (teacherIds ?? Array.Empty<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var users = _root.Read<User>("users");
            bool changed = false;
            foreach (User teacher in users.Where(user => string.Equals(
                         user.Role, "teacher", StringComparison.OrdinalIgnoreCase)))
            {
                List<string> ids = teacher.GetResponsibleClassIds().ToList();
                bool assigned = ids.Contains(classId, StringComparer.OrdinalIgnoreCase);
                bool shouldAssign = selected.Contains(teacher.UserId);
                if (assigned == shouldAssign) continue;
                if (shouldAssign) ids.Add(classId);
                else ids.RemoveAll(id => string.Equals(id, classId, StringComparison.OrdinalIgnoreCase));
                teacher.SetResponsibleClassIds(ids);
                teacher.UpdateTime = DateTime.Now;
                changed = true;
            }
            return !changed || _root.Save("users", users);
        }

        private static void NormalizeClassAssignments(User user)
        {
            if (string.IsNullOrWhiteSpace(user.UserCode)) user.UserCode = user.UserId;
            if (string.Equals(user.Role, "teacher", StringComparison.OrdinalIgnoreCase))
                user.SetResponsibleClassIds(user.GetResponsibleClassIds());
            else
                user.ClassIds = null;
        }

        private static void PrepareNewIdentity(User user)
        {
            user.UserCode = string.IsNullOrWhiteSpace(user.UserCode)
                ? user.UserId?.Trim() ?? "" : user.UserCode.Trim();
            if (SystemAdministratorPolicy.IsReserved(user))
            {
                user.UserId = SystemAdministratorPolicy.UserId;
                user.UserCode = SystemAdministratorPolicy.UserId;
                return;
            }
            if (string.IsNullOrWhiteSpace(user.UserId) ||
                string.Equals(user.UserId, user.UserCode, StringComparison.OrdinalIgnoreCase))
                user.UserId = CreateInternalUserId();
            NormalizeClassAssignments(user);
        }

        private static void QueueCabinetRefresh(User user, string reason)
        {
            try
            {
                string[] known = App.DeviceService.GetAllDevices()
                    .Where(device => !DeviceService.IsTrueRoot(device))
                    .Select(device => device.DeviceId).ToArray();
                HashSet<string> assigned = App.CabinetBindingService
                    .GetAssignedDeviceIds(user, known);
                if (user.Enabled)
                    App.CabinetSyncQueueService.EnqueueUser(user.UserId, assigned, reason);
                else
                    App.CabinetSyncQueueService.EnqueueUserDeletion(user.UserId, assigned, reason);
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
                used = BusinessDatabase.ReadUsedFingerprintIds();
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
                        UserCode = u.DisplayId,
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
                            UserCode = token.Value<string>("user_code") ??
                                token.Value<string>("user_id") ?? "",
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
                .ThenBy(u => u.UserCode)
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
        public string UserCode { get; set; } = "";
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
        public int? FingerprintId { get; set; }
        public bool Enabled { get; set; } = true;

        public override string ToString()
        {
            string fp = FingerprintId.HasValue ? $" [指纹:{FingerprintId.Value}]" : "";
            return $"{Name} ({(string.IsNullOrWhiteSpace(UserCode) ? UserId : UserCode)}) [{Role}]{fp}";
        }
    }
}
