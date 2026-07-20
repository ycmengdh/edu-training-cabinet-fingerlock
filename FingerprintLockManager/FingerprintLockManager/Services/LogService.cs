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

        /// <summary>
        /// V2.7：查询当前用户可见范围内的日志。
        /// Admin 全部；Teacher 仅本班学生 + 自己的日志；Student 仅自己的日志。
        /// 设备维度不限制（教师可看到所有柜子的日志，但只能看到本班学生的操作）。
        /// </summary>
        public List<LogEntry> QueryVisibleLogs(string? deviceId = null, string? userId = null,
            DateTime? startTime = null, DateTime? endTime = null, string? result = null,
            int limit = 1000, int offset = 0, string? keyword = null)
        {
            var query = VisibleFilter(deviceId, userId, startTime, endTime, result, keyword);
            if (offset > 0) query = query.Skip(offset);
            return query.Take(limit > 0 ? limit : 1000).ToList();
        }

        /// <summary>
        /// V2.7：统计当前用户可见范围内的日志总数（与 QueryVisibleLogs 配套用于分页）。
        /// </summary>
        public int CountVisibleLogs(string? deviceId = null, string? userId = null,
            DateTime? startTime = null, DateTime? endTime = null, string? result = null,
            string? keyword = null)
        {
            return VisibleFilter(deviceId, userId, startTime, endTime, result, keyword).Count();
        }

        private IEnumerable<LogEntry> VisibleFilter(string? deviceId, string? userId,
            DateTime? startTime, DateTime? endTime, string? result, string? keyword)
        {
            var scope = DataScopeContext.Instance;
            var current = scope.CurrentUser;
            if (current == null) return Enumerable.Empty<LogEntry>();

            HashSet<string>? visibleUserIds = null;
            if (!scope.IsAdmin)
            {
                var visibleUsers = App.UserService.GetVisibleUsers();
                visibleUserIds = new HashSet<string>(
                    visibleUsers.Select(u => u.UserId), StringComparer.OrdinalIgnoreCase);
            }

            var query = Filter(deviceId, userId, startTime, endTime, result);
            if (visibleUserIds != null)
            {
                // 仅保留可见用户的日志（user_id 为空的开锁失败日志也保留，便于教师看到异常）
                query = query.Where(l => string.IsNullOrEmpty(l.UserId) || visibleUserIds.Contains(l.UserId));
            }
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                string k = keyword.Trim();
                query = query.Where(l =>
                    ContainsIgnoreCase(l.DeviceId, k) ||
                    ContainsIgnoreCase(l.UserId, k) ||
                    ContainsIgnoreCase(l.Action, k) ||
                    ContainsIgnoreCase(l.Result, k) ||
                    ContainsIgnoreCase(l.Reason, k) ||
                    l.LockId.ToString().Contains(k, StringComparison.OrdinalIgnoreCase));
            }
            return query;
        }

        private static bool ContainsIgnoreCase(string? value, string keyword) =>
            !string.IsNullOrEmpty(value) &&
            value.Contains(keyword, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// V2.7：聚合当前用户可见范围内的失败日志原因。
        /// </summary>
        public List<(string Reason, int Count)> AggregateVisibleFailReasons(
            string? deviceId = null, string? userId = null,
            DateTime? startTime = null, DateTime? endTime = null, int top = 5)
        {
            var scope = DataScopeContext.Instance;
            var current = scope.CurrentUser;
            if (current == null) return new List<(string, int)>();

            HashSet<string>? visibleUserIds = null;
            if (!scope.IsAdmin)
            {
                var visibleUsers = App.UserService.GetVisibleUsers();
                visibleUserIds = new HashSet<string>(
                    visibleUsers.Select(u => u.UserId), StringComparer.OrdinalIgnoreCase);
            }

            var query = Filter(deviceId, userId, startTime, endTime, "fail");
            if (visibleUserIds != null)
            {
                query = query.Where(l => string.IsNullOrEmpty(l.UserId) || visibleUserIds.Contains(l.UserId));
            }
            return query
                .GroupBy(l => string.IsNullOrWhiteSpace(l.Reason) ? "(无原因)" : l.Reason)
                .Select(g => (Reason: g.Key, Count: g.Count()))
                .OrderByDescending(x => x.Count)
                .Take(top > 0 ? top : 5)
                .ToList();
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
