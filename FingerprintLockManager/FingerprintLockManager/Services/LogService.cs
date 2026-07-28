namespace FingerprintLockManager
{
    /// <summary>
    /// 开锁日志服务。运行期读写本机 logs.db（unlock_logs）；
    /// 启动可从 SD 合并；柜机上报时本地追加（根节点仍可落 SD）。
    /// </summary>
    public class LogService
    {
        public void AddLog(LogEntry log)
        {
            if (log == null) return;
            LogDatabase.AppendUnlock(log);
        }

        public void AddLogs(List<LogEntry> logs)
        {
            if (logs == null || logs.Count == 0) return;
            LogDatabase.AppendUnlockMany(logs);
        }

        public List<LogEntry> QueryLogs(string? deviceId = null, string? userId = null,
            DateTime? startTime = null, DateTime? endTime = null, string? result = null,
            int limit = 1000, int offset = 0)
        {
            return LogDatabase.QueryUnlock(deviceId, userId, startTime, endTime, result, limit, offset);
        }

        public int CountLogs(string? deviceId = null, string? userId = null,
            DateTime? startTime = null, DateTime? endTime = null, string? result = null)
        {
            return LogDatabase.CountUnlock(deviceId, userId, startTime, endTime, result);
        }

        /// <summary>
        /// V2.7：查询当前用户可见范围内的日志。
        /// Admin 全部；Teacher 仅本班学生 + 自己的日志；Student 仅自己的日志。
        /// </summary>
        public List<LogEntry> QueryVisibleLogs(string? deviceId = null, string? userId = null,
            DateTime? startTime = null, DateTime? endTime = null, string? result = null,
            int limit = 1000, int offset = 0, string? keyword = null)
        {
            var query = VisibleFilter(deviceId, userId, startTime, endTime, result, keyword);
            if (offset > 0) query = query.Skip(offset);
            return query.Take(limit > 0 ? limit : 1000).ToList();
        }

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
            return LogDatabase.GetUnlockCount();
        }

        public void ClearLogs()
        {
            LogDatabase.ClearUnlock();
        }

        private IEnumerable<LogEntry> Filter(string? deviceId, string? userId,
            DateTime? startTime, DateTime? endTime, string? result)
        {
            // 读取全量后内存过滤（与旧实现一致，数据量受 logs.db 上限约束）
            return LogDatabase.QueryUnlock(deviceId, userId, startTime, endTime, result,
                limit: int.MaxValue, offset: 0);
        }
    }
}
