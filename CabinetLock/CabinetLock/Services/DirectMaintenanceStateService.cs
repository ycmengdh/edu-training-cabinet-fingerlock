using System.IO;
using Newtonsoft.Json;

namespace CabinetLock
{
    public static class DirectMaintenanceStateService
    {
        private const string StateFileName = "direct-maintenance.json";

        private static string StateFilePath =>
            Path.Combine(SqlitePaths.GetDataDirectory(), StateFileName);

        public sealed class SessionSnapshot
        {
            public string DeviceId { get; set; } = "";
            public DateTime StartedAt { get; set; }
            public string DataHash { get; set; } = "";
            public Dictionary<string, uint> Versions { get; set; } = new(
                StringComparer.OrdinalIgnoreCase);

            public bool MatchesRemote(SdVersionInfo remote, out string conflict)
            {
                conflict = "";
                var checks = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase)
                {
                    ["users"] = remote.UsersVersion,
                    ["classes"] = remote.ClassesVersion,
                    ["permissions"] = remote.PermissionsVersion,
                    ["devices"] = remote.DevicesVersion,
                    ["fingerprints"] = remote.FpVersion,
                    ["system_settings"] = remote.SettingsVersion,
                };
                foreach ((string table, uint remoteVersion) in checks)
                {
                    if (!Versions.TryGetValue(table, out uint expected)) continue;
                    if (string.Equals(table, "fingerprints", StringComparison.OrdinalIgnoreCase) &&
                        expected == 0)
                        continue;
                    if (remoteVersion == expected) continue;
                    conflict = $"SD 表 {table} 已由 {expected} 更新到 {remoteVersion}";
                    return false;
                }
                return true;
            }
        }

        public static void BeginSession(string deviceId)
        {
            try
            {
                if (File.Exists(StateFilePath)) return;
                BusinessDatabase.Initialize();
                var snapshot = new SessionSnapshot
                {
                    DeviceId = deviceId?.Trim() ?? "",
                    StartedAt = DateTime.Now,
                    DataHash = BusinessUploadStateService.CaptureCurrentDataHash(),
                    Versions = CaptureComparableVersions()
                };
                string tempPath = StateFilePath + ".tmp";
                File.WriteAllText(tempPath, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
                File.Move(tempPath, StateFilePath, true);
            }
            catch
            {
            }
        }

        public static bool TryGetPendingChanges(
            out SessionSnapshot? snapshot,
            out string reason)
        {
            snapshot = null;
            reason = "";
            try
            {
                if (!File.Exists(StateFilePath)) return false;
                snapshot = JsonConvert.DeserializeObject<SessionSnapshot>(
                    File.ReadAllText(StateFilePath));
                if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.DataHash)) return false;

                string currentHash = BusinessUploadStateService.CaptureCurrentDataHash();
                if (string.Equals(currentHash, snapshot.DataHash, StringComparison.OrdinalIgnoreCase))
                    return false;

                reason = string.IsNullOrWhiteSpace(snapshot.DeviceId)
                    ? "柜机直连期间的本机变更"
                    : $"柜机 {snapshot.DeviceId} 直连期间的本机变更";
                return true;
            }
            catch
            {
                reason = "无法校验的柜机直连本机变更";
                return true;
            }
        }

        public static void CompleteSession()
        {
            try
            {
                if (File.Exists(StateFilePath)) File.Delete(StateFilePath);
            }
            catch
            {
            }
        }

        private static Dictionary<string, uint> CaptureComparableVersions() => new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["users"] = BusinessDatabase.GetTableVersion("users"),
            ["classes"] = BusinessDatabase.GetTableVersion("classes"),
            ["permissions"] = Math.Max(
                BusinessDatabase.GetTableVersion("permissions"),
                BusinessDatabase.GetTableVersion("role_permissions")),
            ["devices"] = BusinessDatabase.GetTableVersion("devices"),
            ["fingerprints"] = BusinessDatabase.GetTableVersion("fingerprints"),
            ["system_settings"] = BusinessDatabase.GetTableVersion("system_settings"),
        };
    }
}
