using Newtonsoft.Json.Linq;

namespace FingerprintLockManager
{
    /// <summary>
    /// 柜机绑定：用户可在模板库拥有多枚指纹，但<strong>每台柜机对每个用户只启用一枚</strong>
    ///（<see cref="CabinetAssignment.ActiveFingerprintId"/>），节省 AS608 约 200 槽位。
    /// 不同柜子可为同一用户选择不同手指；副指纹仅本机录入，不走本服务全局分配。
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

        public int? GetActiveFingerprintId(User user, string deviceId)
            => GetActiveFingerprintId(user, deviceId, ReadUserTemplates(user?.UserId ?? ""));

        public int? GetActiveFingerprintId(
            User user, string deviceId, IReadOnlyCollection<FingerprintTemplate> templates)
        {
            if (user == null || string.IsNullOrWhiteSpace(deviceId)) return null;
            List<FingerprintTemplate> userTemplates = templates.Where(item => string.Equals(
                item.UserId, user.UserId, StringComparison.OrdinalIgnoreCase)).ToList();
            if (!IsStudent(user)) return ResolveDefaultFingerprintId(user, userTemplates);

            CabinetAssignment? assignment = ResolveAssignments(
                    user, new[] { deviceId }, ReadLegacy())
                .FirstOrDefault(item => SameDevice(item.DeviceId, deviceId));
            if (assignment == null) return null;
            if (assignment.ActiveFingerprintId.HasValue && userTemplates.Any(item => item.Enabled &&
                    item.FingerprintId == assignment.ActiveFingerprintId.Value))
                return assignment.ActiveFingerprintId;
            return ResolveDefaultFingerprintId(user, userTemplates);
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

        public bool SetActiveFingerprint(string userId, string deviceId, int fingerprintId)
        {
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(deviceId) ||
                fingerprintId <= 0 || !IsOwnedEnabledFingerprint(userId, fingerprintId)) return false;

            List<User> users = _root.Read<User>("users");
            User? user = users.FirstOrDefault(item => string.Equals(
                item.UserId, userId, StringComparison.OrdinalIgnoreCase));
            if (user == null) return false;
            DataScopeContext.Instance.EnsureCanModify(user);

            List<CabinetAssignment> assignments = ResolveAssignments(
                user, ReadKnownDeviceIds().Append(deviceId), ReadLegacy());
            CabinetAssignment? assignment = assignments.FirstOrDefault(item =>
                SameDevice(item.DeviceId, deviceId));
            if (assignment == null) return false;

            assignment.ActiveFingerprintId = fingerprintId;
            assignment.UpdateTime = DateTime.Now;
            ApplyAssignments(user, assignments);
            bool saved = _root.Save("users", users);
            if (saved)
            {
                App.CabinetSyncQueueService.EnqueueUser(
                    userId, new[] { deviceId }, "更换柜机当前指纹");
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
                         !item.ActiveFingerprintId.HasValue))
            {
                assignment.ActiveFingerprintId = fingerprintId;
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
                        current.Add(new CabinetAssignment
                        {
                            DeviceId = deviceId,
                            ActiveFingerprintId = ResolveDefaultFingerprintId(user),
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
                foreach (string deviceId in requestedDevices)
                    App.CabinetSyncQueueService.EnqueueCabinet(deviceId, assigned ? "分配学生" : "解除学生分配");
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
                foreach (string deviceId in affected)
                    App.CabinetSyncQueueService.EnqueueCabinet(deviceId, "移除学生全部柜机绑定");
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
            string[] affected = ResolveAssignments(user, knownDeviceIds, ReadLegacy())
                .Select(item => item.DeviceId).Append(deviceId)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            ApplyAssignments(user, new List<CabinetAssignment>
            {
                new()
                {
                    DeviceId = deviceId.Trim(),
                    ActiveFingerprintId = ResolveDefaultFingerprintId(user),
                    UpdateTime = DateTime.Now
                }
            });
            bool saved = _root.Save("users", users);
            if (saved)
            {
                foreach (string affectedDeviceId in affected)
                    App.CabinetSyncQueueService.EnqueueCabinet(affectedDeviceId, "调整学生柜机分配");
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
                ActiveFingerprintId = defaultFingerprintId,
                UpdateTime = user.UpdateTime ?? user.CreateTime
            }).ToList();
        }

        private static void ApplyAssignments(User user, IEnumerable<CabinetAssignment> assignments)
        {
            // 按柜去重：同一 device_id 只保留一条，即该柜仅一枚 active 指纹
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
            // 一柜一人一指纹：ActiveFingerprintId 至多一个有效正整数
            if (clone.ActiveFingerprintId is <= 0) clone.ActiveFingerprintId = null;
            return clone;
        }

        private static CabinetAssignment Clone(CabinetAssignment item) => new()
        {
            DeviceId = item.DeviceId.Trim(),
            ActiveFingerprintId = item.ActiveFingerprintId,
            UpdateTime = item.UpdateTime
        };

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
                return BusinessDatabase.ReadAllFpTemplateMetas().Where(item =>
                    string.Equals(item.UserId, userId, StringComparison.OrdinalIgnoreCase)).ToList();
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
