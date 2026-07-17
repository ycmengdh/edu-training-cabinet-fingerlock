using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;

namespace FingerprintLockManager
{
    /// <summary>
    /// Root SD data gateway. It intentionally has no local cache or file
    /// fallback: the root node is the only business-data authority.
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
                throw new RootDataUnavailableException($"根节点表 {table} 数据模型无效", ex);
            }
        }

        public JArray ReadArray(string table)
        {
            if (!App.SdStorageService.IsAvailable)
                throw new RootDataUnavailableException("根节点数据服务未连接");

            var snapshot = App.SdStorageService.QueryTableSnapshot(table);
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Json))
                throw new RootDataUnavailableException($"读取根节点表 {table} 失败");

            try
            {
                var token = JToken.Parse(snapshot.Json);
                JArray? array = token as JArray ?? token["items"] as JArray;
                if (array == null)
                    throw new RootDataUnavailableException($"根节点表 {table} 格式无效");
                _readVersions[table] = snapshot.Version;
                return array;
            }
            catch (RootDataUnavailableException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new RootDataUnavailableException($"解析根节点表 {table} 失败", ex);
            }
        }

        public bool Save<T>(string table, IEnumerable<T> items)
        {
            return SaveArray(table, JArray.FromObject(items));
        }

        public bool SaveArray(string table, JArray array)
        {
            if (!App.SdStorageService.IsAvailable || array == null) return false;
            if (!_readVersions.TryRemove(table, out uint baseVersion)) return false;

            return App.SdStorageService.SaveTable(
                table, array.ToString(Newtonsoft.Json.Formatting.None), baseVersion);
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
