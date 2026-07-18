using Newtonsoft.Json.Linq;

namespace FingerprintLockManager
{
    /// <summary>
    /// 日志服务。日志查询和清理操作都针对根节点 logs.json。
    /// 柜子上报的开锁日志由根节点先落 SD，再转发给上位机展示。
    /// </summary>
    public class LogService
    {
        private readonly RootDataService _root = new RootDataService();

        public void AddLog(LogEntry log)
        {
            if (log == null) return;
            var logs = ReadLogs();
            logs.Add(log);
            _root.Save("logs", logs);
        }

        public void AddLogs(List<LogEntry> logs)
        {
            if (logs == null || logs.Count == 0) return;
            var current = ReadLogs();
            current.AddRange(logs);
            _root.Save("logs", current);
        }

        public List<LogEntry> QueryLogs(string? deviceId = null, string? userId = null,
            DateTime? startTime = null, DateTime? endTime = null, string? result = null,
            int limit = 1000, int offset = 0)
        {
            var query = Filter(deviceId, userId, startTime, endTime, result);
            if (offset > 0) query = query.Skip(offset);
            return query.Take(limit > 0 ? limit : 1000).ToList();
        }

        public int CountLogs(string? deviceId = null, string? userId = null,
            DateTime? startTime = null, DateTime? endTime = null, string? result = null)
        {
            return Filter(deviceId, userId, startTime, endTime, result).Count();
        }

        public List<(string Reason, int Count)> AggregateFailReasons(
            string? deviceId = null, string? userId = null,
            DateTime? startTime = null, DateTime? endTime = null, int top = 5)
        {
            return Filter(deviceId, userId, startTime, endTime, "fail")
                .GroupBy(l => string.IsNullOrWhiteSpace(l.Reason) ? "(无原因)" : l.Reason)
                .Select(g => (Reason: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count)
                .Take(top > 0 ? top : 5)
                .ToList();
        }

        public long GetLogCount()
        {
            return ReadLogs().Count;
        }

        public void ClearLogs()
        {
            _root.Save("logs", Array.Empty<LogEntry>());
        }

        private IEnumerable<LogEntry> Filter(string? deviceId, string? userId,
            DateTime? startTime, DateTime? endTime, string? result)
        {
            var query = ReadLogs().AsEnumerable();
            if (!string.IsNullOrWhiteSpace(deviceId)) query = query.Where(l => l.DeviceId == deviceId);
            if (!string.IsNullOrWhiteSpace(userId)) query = query.Where(l => l.UserId == userId);
            if (startTime.HasValue) query = query.Where(l => l.CreateTime >= startTime.Value);
            if (endTime.HasValue) query = query.Where(l => l.CreateTime <= endTime.Value);
            if (!string.IsNullOrWhiteSpace(result))
            {
                query = query.Where(l =>
                    string.Equals(l.Result, result, StringComparison.OrdinalIgnoreCase));
            }
            return query.OrderByDescending(l => l.CreateTime);
        }

        private List<LogEntry> ReadLogs()
        {
            var result = new List<LogEntry>();
            foreach (var token in _root.ReadArray("logs").OfType<JObject>())
            {
                var log = new LogEntry
                {
                    Id = token.Value<long?>("id") ?? token.Value<long?>("log_seq") ?? 0,
                    DeviceId = token.Value<string>("device_id") ?? "",
                    UserId = token.Value<string>("user_id") ?? "",
                    LockId = token.Value<int?>("lock_id") ?? 0,
                    Action = token.Value<string>("action") ?? "",
                    Result = token.Value<string>("result") ?? "",
                    Reason = token.Value<string>("reason") ?? "",
                    CreateTime = ReadTime(token)
                };
                result.Add(log);
            }
            return result;
        }

        private static DateTime ReadTime(JObject token)
        {
            if (DateTime.TryParse(token.Value<string>("create_time"), out var date)) return date;
            long unix = token.Value<long?>("time") ?? token.Value<long?>("timestamp") ?? 0;
            return unix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime
                : DateTime.MinValue;
        }
    }
}
