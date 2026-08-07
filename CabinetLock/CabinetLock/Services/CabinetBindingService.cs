using Newtonsoft.Json.Linq;

namespace CabinetLock
{
    /// <summary>
    /// 柜机绑定：用户可在模板库拥有多枚指纹，每台柜机可选择下发其中一枚或多枚。
    /// 未明确选择时默认使用最早的一枚有效指纹。
    /// </summary>
    public sealed class CabinetBindingService
    {
        private const string LegacyTableName = "cabinet_user_bindings";
        private const string AllDevices = "*";
        private readonly RootDataService _root = new();

        public bool IsAssigned(string deviceId, string userId)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(userId)) return false;
            User? user = _root.Read<User>("users").FirstOrDefault(item =>
                string.Equals(item.UserId, userId, StringComparison.OrdinalIgnoreCase));
            if (user == null) return false;
            return !IsStudent(user) || ResolveAssignments(user, new[] { deviceId }, ReadLegacy())
                .Any(item => SameDevice(item.DeviceId, deviceId));
        }

        public HashSet<string> GetAssignedDeviceIds(User user, IEnumerable<string> knownDeviceIds)
        {
            ArgumentNullException.ThrowIfNull(user);
            string[] known = NormalizeIds(knownDeviceIds);
            if (!IsStudent(user)) return known.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return ResolveAssignments(user, known, ReadLegacy())
                .Select(item => item.DeviceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyList<CabinetAssignment> GetAssignments(
            User user, IEnumerable<string> knownDeviceIds)
        {
            ArgumentNullException.ThrowIfNull(user);
            return ResolveAssignments(user, NormalizeIds(knownDeviceIds), ReadLegacy())
                .OrderBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public Dictionary<string, IReadOnlyList<CabinetAssignment>> GetAssignments(
            IReadOnlyCollection<User> users, IEnumerable<string> knownDeviceIds)
        {
            var result = new Dictionary<string, IReadOnlyList<CabinetAssignment>>(
                StringComparer.OrdinalIgnoreCase);
            if (users == null || users.Count == 0) return result;
            string[] known = NormalizeIds(knownDeviceIds);
            List<CabinetUserBinding> legacy = ReadLegacy();
            foreach (User user in users)
            {
                if (user == null || string.IsNullOrWhiteSpace(user.UserId)) continue;
                result[user.UserId] = ResolveAssignments(user, known, legacy)
                    .OrderBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            return result;
        }

        public IReadOnlyList<int> GetSelectedFingerprintIds(User user, string deviceId)
            => GetSelectedFingerprintIds(user, deviceId, ReadUserTemplates(user?.UserId ?? ""));

        public IReadOnlyList<int> GetSelectedFingerprintIds(
            User user, string deviceId, IReadOnlyCollection<FingerprintTemplate> templates)
        {
            if (user == null || string.IsNullOrWhiteSpace(deviceId)) return Array.Empty<int>();
            List<FingerprintTemplate> userTemplates = templates.Where(item => string.Equals(
                item.UserId, user.UserId, StringComparison.OrdinalIgnoreCase)).ToList();
            CabinetAssignment? assignment = ResolveAssignments(
                    user, new[] { deviceId }, ReadLegacy())
                .FirstOrDefault(item => SameDevice(item.DeviceId, deviceId));
            if (IsStudent(user) && assignment == null) return Array.Empty<int>();

            HashSet<int> enabledIds = userTemplates.Where(item => item.Enabled)
                .Select(item => item.FingerprintId).ToHashSet();
            List<int> selected = NormalizeFingerprintIds(assignment)
                .Where(enabledIds.Contains).ToList();
            if (selected.Count > 0) return selected;
            if (assignment != null) return Array.Empty<int>();
            int? fallback = ResolveDefaultFingerprintId(user, userTemplates);
            return fallback.HasValue ? new[] { fallback.Value } : Array.Empty<int>();
        }

        public bool[] GetLockPermissions(User user, string deviceId, IEnumerable<bool> fallbackPermissions)
        {
            ArgumentNullException.ThrowIfNull(user);
            bool[] permissions = fallbackPermissions?.Take(4).ToArray() ?? Array.Empty<bool>();
            Array.Resize(ref permissions, 4);
            CabinetAssignment? assignment = ResolveAssignments(
                    user, ReadKnownDeviceIds().Append(deviceId), ReadLegacy())
                .FirstOrDefault(item => SameDevice(item.DeviceId, deviceId));
            if (assignment?.LockIds != null)
            {
                permissions = new bool[4];
                foreach (int lockId in assignment.LockIds.Where(id => id >= 0 && id < permissions.Length))
                    permissions[lockId] = true;
            }
            PermissionPolicy.Enforce(user.Role, permissions);
            return permissions;
        }

        public bool SetAssignmentConfiguration(
            string userId, string deviceId, IEnumerable<int> fingerprintIds,
            IEnumerable<int> lockIds, bool enqueueSync = true)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(deviceId) ||
                fingerprintIds == null || lockIds == null) return false;
            int[] selectedFingerprints = fingerprintIds.Where(id => id > 0)
                .Distinct().OrderBy(id => id).ToArray();
            int[] selectedLocks = lockIds.Where(id => id >= 0 && id < 4)
                .Distinct().OrderBy(id => id).ToArray();
            if (selectedFingerprints.Length == 0 ||
                selectedFingerprints.Any(id => !IsOwnedEnabledFingerprint(userId, id))) return false;

            List<User> users = _root.Read<User>("users");
            User? user = users.FirstOrDefault(item => string.Equals(
                item.UserId, userId, StringComparison.OrdinalIgnoreCase));
            if (user == null || selectedLocks.Any(id => !PermissionPolicy.CanGrant(user.Role, id))) return false;
            DataScopeContext.Instance.EnsureCanModify(user);

            List<CabinetAssignment> assignments = ResolveAssignments(
                user, ReadKnownDeviceIds().Append(deviceId), ReadLegacy());
            CabinetAssignment? assignment = assignments.FirstOrDefault(item =>
                SameDevice(item.DeviceId, deviceId));
            if (assignment == null)
            {
                assignment = new CabinetAssignment { DeviceId = deviceId.Trim() };
                assignments.Add(assignment);
            }
            assignment.FingerprintIds = selectedFingerprints.ToList();
            assignment.LockIds = selectedLocks.ToList();
            assignment.UpdateTime = DateTime.Now;
            ApplyAssignments(user, assignments);
            bool saved = _root.Save("users", users);
            if (saved && enqueueSync)
            {
                // 指纹选择可能移除旧槽位，需做柜级核对以清掉快照外记录。
                App.CabinetSyncQueueService.EnqueueCabinet(deviceId, "更新柜机权限与指纹");
                App.CabinetSyncQueueService.Trigger();
            }
            return saved;
        }

        public int? ResolveDefaultFingerprintId(User user)
            => ResolveDefaultFingerprintId(user, ReadUserTemplates(user?.UserId ?? ""));

        public int? ResolveDefaultFingerprintId(
            User user, IReadOnlyCollection<FingerprintTemplate> templates)
        {
            if (user == null) return null;
            if (user.FingerprintId.HasValue && templates.Any(item =>
                    item.Enabled && item.FingerprintId == user.FingerprintId.Value &&
                    string.Equals(item.UserId, user.UserId, StringComparison.OrdinalIgnoreCase)))
                return user.FingerprintId;
            return templates.Where(item => item.Enabled && string.Equals(
                    item.UserId, user.UserId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.FingerIndex)
                .ThenBy(item => item.FingerprintId)
                .Select(item => (int?)item.FingerprintId)
                .FirstOrDefault() ?? user.FingerprintId;
        }

        public bool SetSelectedFingerprints(
            string userId, string deviceId, IEnumerable<int> fingerprintIds)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(deviceId) ||
                fingerprintIds == null) return false;
            int[] selectedIds = fingerprintIds.Where(id => id > 0).Distinct().OrderBy(id => id).ToArray();
            if (selectedIds.Length == 0 || selectedIds.Any(id => !IsOwnedEnabledFingerprint(userId, id)))
                return false;

            List<User> users = _root.Read<User>("users");
            User? user = users.FirstOrDefault(item => string.Equals(
                item.UserId, userId, StringComparison.OrdinalIgnoreCase));
            if (user == null) return false;
            DataScopeContext.Instance.EnsureCanModify(user);

            List<CabinetAssignment> assignments = ResolveAssignments(
                user, ReadKnownDeviceIds().Append(deviceId), ReadLegacy());
            CabinetAssignment? assignment = assignments.FirstOrDefault(item =>
                SameDevice(item.DeviceId, deviceId));
            if (assignment == null)
            {
                if (IsStudent(user)) return false;
                assignment = new CabinetAssignment { DeviceId = deviceId.Trim() };
                assignments.Add(assignment);
            }

            assignment.FingerprintIds = selectedIds.ToList();
            assignment.UpdateTime = DateTime.Now;
            ApplyAssignments(user, assignments);
            bool saved = _root.Save("users", users);
            if (saved)
            {
                App.CabinetSyncQueueService.EnqueueUser(
                    userId, new[] { deviceId }, "更新柜机指纹选择");
                App.CabinetSyncQueueService.Trigger();
            }
            return saved;
        }

        public bool AssignFingerprintToEmptyAssignments(string userId, int fingerprintId)
        {
            if (string.IsNullOrWhiteSpace(userId) || fingerprintId <= 0 ||
                !IsOwnedEnabledFingerprint(userId, fingerprintId)) return false;
            List<User> users = _root.Read<User>("users");
            User? user = users.FirstOrDefault(item => string.Equals(
                item.UserId, userId, StringComparison.OrdinalIgnoreCase));
            if (user == null) return false;
            DataScopeContext.Instance.EnsureCanModify(user);

            List<CabinetAssignment> assignments = ResolveAssignments(
                user, ReadKnownDeviceIds(), ReadLegacy());
            bool changed = false;
            foreach (CabinetAssignment assignment in assignments.Where(item =>
                         NormalizeFingerprintIds(item).Count == 0))
            {
                assignment.FingerprintIds = new List<int> { fingerprintId };
                assignment.UpdateTime = DateTime.Now;
                changed = true;
            }
            if (!changed) return true;
            ApplyAssignments(user, assignments);
            bool saved = _root.Save("users", users);
            if (saved)
            {
                App.CabinetSyncQueueService.EnqueueUser(userId,
                    assignments.Select(item => item.DeviceId), "新指纹补齐柜机选择");
                App.CabinetSyncQueueService.Trigger();
            }
            return saved;
        }

        public bool RemoveFingerprintFromCabinet(
            string userId, string deviceId, int fingerprintId, bool enqueueSync = true)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(deviceId) ||
                fingerprintId <= 0) return false;
            List<User> users = _root.Read<User>("users");
            User? user = users.FirstOrDefault(item => string.Equals(
                item.UserId, userId, StringComparison.OrdinalIgnoreCase));
            if (user == null) return false;
            DataScopeContext.Instance.EnsureCanModify(user);

            List<CabinetAssignment> assignments = ResolveAssignments(
                user, ReadKnownDeviceIds().Append(deviceId), ReadLegacy());
            CabinetAssignment? assignment = assignments.FirstOrDefault(item =>
                SameDevice(item.DeviceId, deviceId));
            IReadOnlyList<int> current = GetSelectedFingerprintIds(user, deviceId);
            if (!current.Contains(fingerprintId)) return true;
            int[] remaining = current.Where(id => id != fingerprintId).ToArray();
            if (remaining.Length == 0 && IsStudent(user))
            {
                if (assignment != null) assignments.Remove(assignment);
            }
            else
            {
                assignment ??= new CabinetAssignment { DeviceId = deviceId.Trim() };
                if (!assignments.Contains(assignment)) assignments.Add(assignment);
                assignment.FingerprintIds = remaining.ToList();
                assignment.UpdateTime = DateTime.Now;
            }
            ApplyAssignments(user, assignments);
            bool saved = _root.Save("users", users);
            if (saved && enqueueSync)
            {
                App.CabinetSyncQueueService.EnqueueCabinet(deviceId, "移除柜机指纹");
                App.CabinetSyncQueueService.Trigger();
            }
            return saved;
        }

        public HashSet<string> GetExcludedUserIds(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<User> users = _root.Read<User>("users");
            List<CabinetUserBinding> legacy = ReadLegacy();
            return users.Where(IsStudent)
                .Where(user => !ResolveAssignments(user, new[] { deviceId }, legacy)
                    .Any(item => SameDevice(item.DeviceId, deviceId)))
                .Select(user => user.UserId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        public bool Assign(string deviceId, string userId) =>
            SetUsersAssignment(deviceId, new[] { userId }, true);

        public bool Remove(string deviceId, string userId) =>
            SetUsersAssignment(deviceId, new[] { userId }, false);

        public IReadOnlyList<User> GetAssignedStudents(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return Array.Empty<User>();
            List<User> users = _root.Read<User>("users");
            List<CabinetUserBinding> legacy = ReadLegacy();
            string[] known = ReadKnownDeviceIds().Append(deviceId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            return users.Where(IsStudent)
                .Where(user => ResolveAssignments(user, known, legacy).Any(item =>
                    SameDevice(item.DeviceId, deviceId)))
                .OrderBy(user => user.Name)
                .ThenBy(user => user.DisplayId)
                .ToList();
        }

        public bool RemoveDeviceAssignments(string deviceId, out int affectedStudentCount)
        {
            affectedStudentCount = 0;
            if (string.IsNullOrWhiteSpace(deviceId)) return false;

            List<User> users = _root.Read<User>("users");
            List<CabinetUserBinding> legacy = ReadLegacy();
            string[] known = ReadKnownDeviceIds().Append(deviceId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (User user in users.Where(IsStudent))
            {
                List<CabinetAssignment> assignments = ResolveAssignments(user, known, legacy);
                if (assignments.RemoveAll(item => SameDevice(item.DeviceId, deviceId)) == 0) continue;
                DataScopeContext.Instance.EnsureCanModify(user);
                ApplyAssignments(user, assignments);
                affectedStudentCount++;
            }

            if (affectedStudentCount > 0 && !_root.Save("users", users)) return false;
            legacy.RemoveAll(item => SameDevice(item.DeviceId, deviceId));
            SaveLegacy(legacy);
            return true;
        }

        public bool SetUsersAssignment(
            string deviceId, IEnumerable<string> userIds, bool assigned)
            => SetUsersAssignments(new[] { deviceId }, userIds, assigned);

        public bool SetUsersAssignments(
            IEnumerable<string> deviceIds, IEnumerable<string> userIds, bool assigned)
        {
            if (deviceIds == null || userIds == null) return false;
            string[] requestedDevices = NormalizeIds(deviceIds);
            if (requestedDevices.Length == 0) return false;
            var requested = userIds.Where(id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (requested.Count == 0) return false;

            List<User> users = _root.Read<User>("users");
            List<User> targets = users.Where(user => requested.Contains(user.UserId)).ToList();
            if (targets.Count != requested.Count) return false;
            foreach (User user in targets) DataScopeContext.Instance.EnsureCanModify(user);

            string[] known = ReadKnownDeviceIds().Concat(requestedDevices)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            List<CabinetUserBinding> legacy = ReadLegacy();
            foreach (User user in targets)
            {
                List<CabinetAssignment> current = ResolveAssignments(user, known, legacy);
                foreach (string deviceId in requestedDevices)
                {
                    CabinetAssignment? existing = current.FirstOrDefault(item =>
                        SameDevice(item.DeviceId, deviceId));
                    if (assigned && existing == null)
                    {
                        int? defaultFingerprintId = ResolveDefaultFingerprintId(user);
                        current.Add(new CabinetAssignment
                        {
                            DeviceId = deviceId,
                            FingerprintIds = ToFingerprintList(defaultFingerprintId),
                            UpdateTime = DateTime.Now
                        });
                    }
                    else if (!assigned && existing != null)
                    {
                        current.Remove(existing);
                    }
                }
                ApplyAssignments(user, current);
            }
            bool saved = _root.Save("users", users);
            if (saved)
            {
                foreach (User user in targets)
                {
                    if (assigned)
                        App.CabinetSyncQueueService.EnqueueUser(
                            user.UserId, requestedDevices, "分配学生");
                    else
                        App.CabinetSyncQueueService.EnqueueUserDeletion(
                            user.UserId, requestedDevices, "解除学生分配");
                }
                App.CabinetSyncQueueService.Trigger();
            }
            return saved;
        }

        public bool RemoveFromAll(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return false;
            List<CabinetUserBinding> legacy = ReadLegacy();
            legacy.RemoveAll(item => string.Equals(
                item.UserId, userId, StringComparison.OrdinalIgnoreCase));
            SaveLegacy(legacy);

            List<User> users = _root.Read<User>("users");
            User? user = users.FirstOrDefault(item => string.Equals(
                item.UserId, userId, StringComparison.OrdinalIgnoreCase));
            if (user == null) return true;
            DataScopeContext.Instance.EnsureCanModify(user);
            string[] affected = ResolveAssignments(user, ReadKnownDeviceIds(), legacy)
                .Select(item => item.DeviceId).ToArray();
            ApplyAssignments(user, new List<CabinetAssignment>());
            bool saved = _root.Save("users", users);
            if (saved)
            {
                App.CabinetSyncQueueService.EnqueueUserDeletion(
                    userId, affected, "移除学生全部柜机绑定");
                App.CabinetSyncQueueService.Trigger();
            }
            return saved;
        }

        public bool AssignExclusive(
            string userId, string deviceId, IEnumerable<string> knownDeviceIds)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(deviceId)) return false;
            List<User> users = _root.Read<User>("users");
            User? user = users.FirstOrDefault(item => string.Equals(
                item.UserId, userId, StringComparison.OrdinalIgnoreCase));
            if (user == null) return false;
            DataScopeContext.Instance.EnsureCanModify(user);
            string[] previous = ResolveAssignments(user, knownDeviceIds, ReadLegacy())
                .Select(item => item.DeviceId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            int? defaultFingerprintId = ResolveDefaultFingerprintId(user);
            ApplyAssignments(user, new List<CabinetAssignment>
            {
                new()
                {
                    DeviceId = deviceId.Trim(),
                    FingerprintIds = ToFingerprintList(defaultFingerprintId),
                    UpdateTime = DateTime.Now
                }
            });
            bool saved = _root.Save("users", users);
            if (saved)
            {
                App.CabinetSyncQueueService.EnqueueUserDeletion(userId,
                    previous.Where(id => !SameDevice(id, deviceId)), "调整学生柜机分配");
                App.CabinetSyncQueueService.EnqueueUser(
                    userId, new[] { deviceId }, "调整学生柜机分配");
                App.CabinetSyncQueueService.Trigger();
            }
            return saved;
        }

        public bool MigrateLegacyBindings()
        {
            List<CabinetUserBinding> legacy = ReadLegacy();
            List<User> users = _root.Read<User>("users");
            string[] known = ReadKnownDeviceIds();
            bool changed = false;
            foreach (User user in users.Where(IsStudent).Where(user => user.CabinetAssignments == null))
            {
                ApplyAssignments(user, ResolveAssignments(user, known, legacy));
                changed = true;
            }

            if (changed && !_root.Save("users", users)) return false;
            if (legacy.Count > 0) SaveLegacy(new List<CabinetUserBinding>());
            return true;
        }

        private static List<CabinetAssignment> ResolveAssignments(
            User user, IEnumerable<string> knownDeviceIds, IReadOnlyList<CabinetUserBinding> legacy)
        {
            if (user.CabinetAssignments != null)
            {
                return user.CabinetAssignments
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.DeviceId))
                    .GroupBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase)
                    .Select(group => Clone(group.Last()))
                    .ToList();
            }

            IEnumerable<string> deviceIds;
            if (user.AssignedDeviceIds != null)
            {
                deviceIds = user.AssignedDeviceIds;
            }
            else
            {
                deviceIds = knownDeviceIds.Where(deviceId =>
                    IsLegacyAssigned(legacy, deviceId, user.UserId));
            }

            int? defaultFingerprintId = ResolveDefaultFingerprintIdStatic(user);
            return NormalizeIds(deviceIds).Select(deviceId => new CabinetAssignment
            {
                DeviceId = deviceId,
                FingerprintIds = ToFingerprintList(defaultFingerprintId),
                UpdateTime = user.UpdateTime ?? user.CreateTime
            }).ToList();
        }

        private static void ApplyAssignments(User user, IEnumerable<CabinetAssignment> assignments)
        {
            // 按柜去重：同一 device_id 只保留一条，指纹选择保存在集合中。
            List<CabinetAssignment> normalized = assignments
                .Where(item => item != null && !string.IsNullOrWhiteSpace(item.DeviceId))
                .GroupBy(item => item.DeviceId.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => NormalizeAssignment(group.Last()))
                .OrderBy(item => item.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            user.CabinetAssignments = normalized;
            user.AssignedDeviceIds = normalized.Select(item => item.DeviceId).ToList();
            user.UpdateTime = DateTime.Now;
        }

        private static CabinetAssignment NormalizeAssignment(CabinetAssignment item)
        {
            CabinetAssignment clone = Clone(item);
            clone.FingerprintIds = NormalizeFingerprintIds(clone).ToList();
            clone.LockIds = clone.LockIds?.Where(id => id >= 0 && id < 4)
                .Distinct().OrderBy(id => id).ToList();
            return clone;
        }

        private static CabinetAssignment Clone(CabinetAssignment item) => new()
        {
            DeviceId = item.DeviceId.Trim(),
            FingerprintIds = item.FingerprintIds.ToList(),
            LockIds = item.LockIds?.ToList(),
            UpdateTime = item.UpdateTime
        };

        private static IReadOnlyList<int> NormalizeFingerprintIds(CabinetAssignment? assignment)
        {
            if (assignment == null) return Array.Empty<int>();
            return assignment.FingerprintIds.Where(id => id > 0)
                .Distinct().OrderBy(id => id).ToArray();
        }

        private static List<int> ToFingerprintList(int? fingerprintId) =>
            fingerprintId is > 0 ? new List<int> { fingerprintId.Value } : new List<int>();

        private static int? ResolveDefaultFingerprintIdStatic(User user)
        {
            List<FingerprintTemplate> templates = ReadUserTemplates(user.UserId);
            if (user.FingerprintId.HasValue && templates.Any(item =>
                    item.Enabled && item.FingerprintId == user.FingerprintId.Value))
                return user.FingerprintId;
            return templates.Where(item => item.Enabled)
                .OrderBy(item => item.FingerIndex)
                .ThenBy(item => item.FingerprintId)
                .Select(item => (int?)item.FingerprintId)
                .FirstOrDefault() ?? user.FingerprintId;
        }

        private static bool IsOwnedEnabledFingerprint(string userId, int? fingerprintId) =>
            fingerprintId.HasValue && ReadUserTemplates(userId).Any(item =>
                item.Enabled && item.FingerprintId == fingerprintId.Value);

        private static List<FingerprintTemplate> ReadUserTemplates(string userId)
        {
            try
            {
                return BusinessDatabase.ReadFpTemplateMetasForUsers(new[] { userId });
            }
            catch
            {
                return new List<FingerprintTemplate>();
            }
        }

        private static bool IsLegacyAssigned(
            IReadOnlyList<CabinetUserBinding> items, string deviceId, string userId)
        {
            CabinetUserBinding? exact = items.FirstOrDefault(item => Same(item, deviceId, userId));
            if (exact != null) return exact.Assigned;
            CabinetUserBinding? fallback = items.FirstOrDefault(item => Same(item, AllDevices, userId));
            return fallback?.Assigned ?? true;
        }

        private static bool IsStudent(User user) => string.Equals(
            user.Role, "student", StringComparison.OrdinalIgnoreCase);

        private static string[] ReadKnownDeviceIds() => BusinessDatabase.ReadArray("devices")
            .OfType<JObject>()
            .Where(item => !(item.Value<bool?>("is_root") ?? false))
            .Select(item => item.Value<string>("device_id") ?? "")
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        private static string[] NormalizeIds(IEnumerable<string> ids) => ids
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        private static List<CabinetUserBinding> ReadLegacy()
        {
            try
            {
                return (LocalCacheService.ReadTable(LegacyTableName) ?? new JArray())
                    .OfType<JObject>()
                    .Select(item => item.ToObject<CabinetUserBinding>())
                    .Where(item => item != null && !string.IsNullOrWhiteSpace(item.DeviceId) &&
                        !string.IsNullOrWhiteSpace(item.UserId))
                    .Cast<CabinetUserBinding>()
                    .ToList();
            }
            catch
            {
                return new List<CabinetUserBinding>();
            }
        }

        private static void SaveLegacy(List<CabinetUserBinding> items) =>
            LocalCacheService.WriteTable(LegacyTableName, JArray.FromObject(items));

        private static bool Same(CabinetUserBinding item, string deviceId, string userId) =>
            SameDevice(item.DeviceId, deviceId) &&
            string.Equals(item.UserId, userId, StringComparison.OrdinalIgnoreCase);

        private static bool SameDevice(string left, string right) => string.Equals(
            left, right, StringComparison.OrdinalIgnoreCase);
    }

    public sealed class CabinetUserBinding
    {
        public string DeviceId { get; set; } = "";
        public string UserId { get; set; } = "";
        public bool Assigned { get; set; } = true;
        public DateTime UpdateTime { get; set; }
    }
}
