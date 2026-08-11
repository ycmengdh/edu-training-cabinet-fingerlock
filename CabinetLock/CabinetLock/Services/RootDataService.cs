using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;

namespace CabinetLock
{
    /// <summary>
    /// 业务数据网关：运行期读写本机 business.db；
    /// SD 卡仅在启动拉取 / 关闭上传时由 SdBusinessSyncService 同步。
    /// </summary>
    public class RootDataService
    {
        private readonly ConcurrentDictionary<string, uint> _readVersions = new();

        public List<T> Read<T>(string table) where T : class
        {
            var array = ReadArray(table);
            try
            {
                return array.ToObject<List<T>>() ?? new List<T>();
            }
            catch (Exception ex)
            {
                throw new RootDataUnavailableException($"业务表 {table} 数据模型无效", ex);
            }
        }

        public JArray ReadArray(string table)
        {
            try
            {
                // logs 表不在 business.db，由 LogService 走 logs.db
                if (string.Equals(table, "logs", StringComparison.OrdinalIgnoreCase))
                {
                    var logs = LogDatabase.ReadAllUnlock();
                    return JArray.FromObject(logs);
                }

                BusinessDatabase.Initialize();
                var array = BusinessDatabase.ReadArray(table);
                uint version = BusinessDatabase.GetTableVersion(table);
                _readVersions[table] = version;
                return array;
            }
            catch (ArgumentException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new RootDataUnavailableException($"读取本地业务表 {table} 失败", ex);
            }
        }

        public bool Save<T>(string table, IEnumerable<T> items)
        {
            return SaveArray(table, JArray.FromObject(items));
        }

        public bool SaveArray(string table, JArray array)
        {
            if (array == null) return false;

            try
            {
                if (string.Equals(table, "logs", StringComparison.OrdinalIgnoreCase))
                {
                    // 兼容旧调用：整表覆盖开锁日志本地库
                    LogDatabase.ClearUnlock();
                    LogDatabase.MergeUnlockFromArray(array);
                    return true;
                }

                BusinessDatabase.Initialize();
                uint current = BusinessDatabase.GetTableVersion(table);
                if (_readVersions.TryGetValue(table, out uint cached) && cached > current)
                    current = cached;

                uint next = current + 1;
                BusinessDatabase.ReplaceTable(table, array, next);
                _readVersions[table] = next;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static uint GetTableVersion(string table, SdVersionInfo? version)
        {
            if (version == null) return 0;
            return table switch
            {
                "users" => version.UsersVersion,
                "classes" => version.ClassesVersion,
                "permissions" => version.PermissionsVersion,
                "role_permissions" => version.PermissionsVersion,
                "devices" => version.DevicesVersion,
                "fingerprints" => version.FpVersion,
                "system_settings" => version.SettingsVersion,
                "logs" => version.LogsVersion,
                _ => 0
            };
        }
    }

    public sealed class RootDataUnavailableException : Exception
    {
        public RootDataUnavailableException(string message) : base(message) { }
        public RootDataUnavailableException(string message, Exception innerException)
            : base(message, innerException) { }
    }
}
