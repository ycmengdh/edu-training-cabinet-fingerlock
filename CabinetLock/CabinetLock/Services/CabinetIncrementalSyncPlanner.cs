using System.Text;

namespace CabinetLock
{
    public sealed record CabinetPermissionDescriptor(
        int FingerprintId, string UserId, string Name, int Role, int LockMask);

    public sealed class CabinetIncrementalSyncPlan
    {
        public int[] MissingFingerprintIds { get; init; } = Array.Empty<int>();
        public int[] FingerprintIdsToVerify { get; init; } = Array.Empty<int>();
        public int[] PermissionUpsertFingerprintIds { get; init; } = Array.Empty<int>();
        public int[] StaleFingerprintIds { get; init; } = Array.Empty<int>();
        public int TrustedFingerprintCount { get; init; }
        public int UnchangedPermissionCount { get; init; }
        public int BackupPermissionCount { get; init; }
        public int OrphanPermissionCount { get; init; }
        public bool UseFullPermissionTransaction { get; init; }
    }

    /// <summary>
    /// 根据柜机槽位快照规划增量同步。槽位存在代表模板存在；只有明确待处理的
    /// 用户才做模板 CRC 校验，避免每次同步重复读取全部指纹模板。
    /// </summary>
    public static class CabinetIncrementalSyncPlanner
    {
        private const int FirmwareNameMaxUtf8Bytes = 32;
        private const int FullTransactionMinimumChanges = 4;

        public static CabinetIncrementalSyncPlan Build(
            IEnumerable<CabinetPermissionDescriptor>? expectedPermissions,
            IEnumerable<FingerprintSlotRecord>? actualSlots,
            IEnumerable<string>? usersRequiringFingerprintVerification = null,
            int reportedPermissionCount = -1)
        {
            CabinetPermissionDescriptor[] expected = (expectedPermissions ??
                    Array.Empty<CabinetPermissionDescriptor>())
                .Where(item => item.FingerprintId > 0)
                .GroupBy(item => item.FingerprintId)
                .Select(group => group.Last())
                .ToArray();
            FingerprintSlotRecord[] actual = (actualSlots ??
                    Array.Empty<FingerprintSlotRecord>())
                .Where(item => item.Slot > 0)
                .ToArray();
            HashSet<string> verifyUsers = (usersRequiringFingerprintVerification ??
                    Array.Empty<string>())
                .Where(userId => !string.IsNullOrWhiteSpace(userId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            Dictionary<int, FingerprintSlotRecord> primaryBySlot = actual
                .Where(item => !item.IsBackup)
                .GroupBy(item => item.Slot)
                .ToDictionary(group => group.Key, group => group.Last());
            HashSet<int> expectedIds = expected
                .Select(item => item.FingerprintId)
                .ToHashSet();

            var missing = new List<int>();
            var verify = new List<int>();
            var upserts = new List<int>();
            int trusted = 0;
            int unchanged = 0;
            foreach (CabinetPermissionDescriptor target in expected)
            {
                if (!primaryBySlot.TryGetValue(target.FingerprintId,
                        out FingerprintSlotRecord? slot))
                {
                    missing.Add(target.FingerprintId);
                    upserts.Add(target.FingerprintId);
                    continue;
                }

                if (verifyUsers.Contains(target.UserId))
                    verify.Add(target.FingerprintId);
                else
                    trusted++;

                if (PermissionMatches(target, slot))
                    unchanged++;
                else
                    upserts.Add(target.FingerprintId);
            }

            int[] stale = primaryBySlot.Keys
                .Where(fingerprintId => !expectedIds.Contains(fingerprintId))
                .OrderBy(fingerprintId => fingerprintId)
                .ToArray();
            int backupCount = actual.Count(item => item.IsBackup && item.Bound &&
                item.FingerprintId > 0 && !string.IsNullOrWhiteSpace(item.UserId));
            int visiblePermissionCount = actual.Count(item => item.Bound);
            int orphanCount = reportedPermissionCount < 0
                ? 0
                : Math.Max(0, reportedPermissionCount - visiblePermissionCount);
            int changedPermissionCount = upserts.Count + stale.Length;
            int finalPermissionCount = expected.Length + backupCount;
            bool useFullTransaction = orphanCount > 0 ||
                changedPermissionCount >= FullTransactionMinimumChanges &&
                changedPermissionCount * 2 >= Math.Max(1, finalPermissionCount);

            return new CabinetIncrementalSyncPlan
            {
                MissingFingerprintIds = missing.OrderBy(id => id).ToArray(),
                FingerprintIdsToVerify = verify.OrderBy(id => id).ToArray(),
                PermissionUpsertFingerprintIds = upserts.Distinct().OrderBy(id => id).ToArray(),
                StaleFingerprintIds = stale,
                TrustedFingerprintCount = trusted,
                UnchangedPermissionCount = unchanged,
                BackupPermissionCount = backupCount,
                OrphanPermissionCount = orphanCount,
                UseFullPermissionTransaction = useFullTransaction
            };
        }

        private static bool PermissionMatches(
            CabinetPermissionDescriptor expected, FingerprintSlotRecord actual)
        {
            if (!actual.Bound || actual.IsBackup ||
                actual.FingerprintId != expected.FingerprintId ||
                !string.Equals(actual.UserId, expected.UserId,
                    StringComparison.OrdinalIgnoreCase) ||
                actual.Role != expected.Role ||
                (actual.LockMask & 0x0F) != (expected.LockMask & 0x0F))
                return false;

            // 固件姓名字段为 32 字节。超长 UTF-8 姓名可能在旧固件中被截断，
            // 不应因此导致每次同步都重复写同一权限。
            return Encoding.UTF8.GetByteCount(expected.Name ?? "") > FirmwareNameMaxUtf8Bytes ||
                string.Equals(actual.Name, expected.Name, StringComparison.Ordinal);
        }
    }
}
