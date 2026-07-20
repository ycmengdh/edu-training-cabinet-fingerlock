using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FingerprintLockManager
{
    /// <summary>
    /// 上位机操作审计日志服务。
    /// 存储：%APPDATA%\FingerprintLockManager\cache\operation_logs.json
    /// </summary>
    public class OperationLogService
    {
        private static readonly object FileLock = new();
        private const string FileName = "operation_logs.json";
        private const int MaxEntries = 20000;

        public void Write(string module, string action, string? target = null,
            string result = "info", string? detail = null, string? operatorId = null,
            string? operatorName = null)
        {
            try
            {
                var current = App.CurrentUser;
                var entry = new OperationLogEntry
                {
                    Time = DateTime.Now,
                    OperatorId = operatorId ?? current?.UserId ?? "",
                    OperatorName = operatorName ?? current?.Name ?? "",
                    Module = module ?? "",
                    Action = action ?? "",
                    Target = target ?? "",
                    Result = result ?? "info",
                    Detail = detail ?? ""
                };
                Append(entry);
            }
            catch
            {
                // 审计写失败不能影响主流程
            }
        }

        public void Append(OperationLogEntry entry)
        {
            if (entry == null) return;
            lock (FileLock)
            {
                var list = ReadAllUnlocked();
                entry.Id = list.Count == 0 ? 1 : list.Max(x => x.Id) + 1;
                if (entry.Time == default) entry.Time = DateTime.Now;
                list.Add(entry);
                if (list.Count > MaxEntries)
                    list = list.Skip(list.Count - MaxEntries).ToList();
                WriteAllUnlocked(list);
            }
        }

        public List<OperationLogEntry> Query(
            string? keyword = null,
            DateTime? startTime = null,
            DateTime? endTime = null,
            int limit = 100,
            int offset = 0)
        {
            var query = Filter(keyword, startTime, endTime);
            if (offset > 0) query = query.Skip(offset);
            return query.Take(limit > 0 ? limit : 100).ToList();
        }

        public int Count(string? keyword = null, DateTime? startTime = null, DateTime? endTime = null)
        {
            return Filter(keyword, startTime, endTime).Count();
        }

        public List<OperationLogEntry> QueryAll(
            string? keyword = null,
            DateTime? startTime = null,
            DateTime? endTime = null,
            int max = 50000)
        {
            return Filter(keyword, startTime, endTime).Take(max > 0 ? max : 50000).ToList();
        }

        private IEnumerable<OperationLogEntry> Filter(
            string? keyword, DateTime? startTime, DateTime? endTime)
        {
            IEnumerable<OperationLogEntry> query;
            lock (FileLock)
            {
                query = ReadAllUnlocked().OrderByDescending(x => x.Time).ToList();
            }

            if (startTime.HasValue) query = query.Where(x => x.Time >= startTime.Value);
            if (endTime.HasValue) query = query.Where(x => x.Time <= endTime.Value);
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                string k = keyword.Trim();
                query = query.Where(x =>
                    Contains(x.OperatorId, k) ||
                    Contains(x.OperatorName, k) ||
                    Contains(x.Module, k) ||
                    Contains(x.Action, k) ||
                    Contains(x.Target, k) ||
                    Contains(x.Result, k) ||
                    Contains(x.Detail, k));
            }
            return query;
        }

        private static bool Contains(string? value, string keyword) =>
            !string.IsNullOrEmpty(value) &&
            value.Contains(keyword, StringComparison.OrdinalIgnoreCase);

        private static List<OperationLogEntry> ReadAllUnlocked()
        {
            try
            {
                string path = GetFilePath();
                if (!File.Exists(path)) return new List<OperationLogEntry>();
                string json = File.ReadAllText(path);
                if (string.IsNullOrWhiteSpace(json)) return new List<OperationLogEntry>();
                var arr = JToken.Parse(json) as JArray;
                if (arr == null) return new List<OperationLogEntry>();
                return arr.ToObject<List<OperationLogEntry>>() ?? new List<OperationLogEntry>();
            }
            catch
            {
                return new List<OperationLogEntry>();
            }
        }

        private static void WriteAllUnlocked(List<OperationLogEntry> list)
        {
            string dir = LocalCacheService.GetCacheDirectory();
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string path = GetFilePath();
            string json = JsonConvert.SerializeObject(list, Formatting.None);
            File.WriteAllText(path, json);
        }

        private static string GetFilePath() =>
            Path.Combine(LocalCacheService.GetCacheDirectory(), FileName);
    }
}
