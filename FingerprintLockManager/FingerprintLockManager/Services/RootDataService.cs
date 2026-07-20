using Newtonsoft.Json.Linq;
using System.Collections.Concurrent;

namespace FingerprintLockManager
{
    /// <summary>
    /// Root SD data gateway. SD 卡是业务数据的主权威，但当 SD 不可用时，
    /// 自动降级到本地磁盘缓存（LocalCacheService），保证 UI 与命令下发不中断；
    /// SD 恢复后由 App 层负责将本地缓存回传到 SD 卡。
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
            // 路径 1：SD 不可用 —— 直接读本地缓存
            if (!App.SdStorageService.IsAvailable)
            {
                var cached = LocalCacheService.ReadTable(table);
                if (cached != null)
                {
                    _readVersions[table] = LocalCacheService.ReadTableVersion(table);
                    return cached;
                }
                string reason = App.SdStorageService.IsRootConnected &&
                    App.SdStorageService.IsStorageReady == false
                    ? "根节点通讯正常，但 SD 卡未就绪，无法读取账号数据"
                    : "根节点数据服务未连接";
                throw new RootDataUnavailableException(reason);
            }

            // 路径 2：SD 可用 —— 读取并同步到本地缓存
            var snapshot = App.SdStorageService.QueryTableSnapshot(table);
            if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Json))
            {
                // SD 读取失败：用本地缓存兜底，避免界面空白
                var fallback = LocalCacheService.ReadTable(table);
                if (fallback != null)
                {
                    _readVersions[table] = LocalCacheService.ReadTableVersion(table);
                    return fallback;
                }
                string detail = App.SdStorageService.LastError;
                throw new RootDataUnavailableException(string.IsNullOrWhiteSpace(detail)
                    ? $"读取根节点表 {table} 失败"
                    : detail);
            }

            try
            {
                var token = JToken.Parse(snapshot.Json);
                JArray? array = token as JArray ?? token["items"] as JArray;
                if (array == null)
                    throw new RootDataUnavailableException($"根节点表 {table} 格式无效");
                _readVersions[table] = snapshot.Version;
                // 同步到本地缓存（失败不影响主流程）
                LocalCacheService.WriteTable(table, array);
                LocalCacheService.WriteTableVersion(table, snapshot.Version);
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
            if (array == null) return false;

            // 路径 1：SD 不可用 —— 仅写本地缓存
            if (!App.SdStorageService.IsAvailable)
            {
                uint v = LocalCacheService.ReadTableVersion(table) + 1;
                LocalCacheService.WriteTable(table, array);
                LocalCacheService.WriteTableVersion(table, v);
                _readVersions[table] = v;
                return true;
            }

            // 路径 2：SD 可用 —— 写 SD，成功后同步到本地缓存
            if (!_readVersions.TryRemove(table, out uint baseVersion)) return false;

            bool saved = App.SdStorageService.SaveTable(
                table, array.ToString(Newtonsoft.Json.Formatting.None), baseVersion);
            if (saved)
            {
                // SD 保存成功后同步到本地缓存（失败不影响主流程）
                LocalCacheService.WriteTable(table, array);
                LocalCacheService.WriteTableVersion(table, baseVersion + 1);
                _readVersions[table] = baseVersion + 1;
            }
            return saved;
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
