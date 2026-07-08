namespace FingerprintLockManager
{
    /// <summary>
    /// 日志服务
    /// 负责开锁/关锁等操作日志的记录、查询与清理
    /// </summary>
    public class LogService
    {
        /// <summary>
        /// 添加日志
        /// </summary>
        /// <param name="log">日志条目</param>
        public void AddLog(LogEntry log)
        {
            try
            {
                if (log == null) return;

                // 设置创建时间
                if (log.CreateTime == default(DateTime))
                {
                    log.CreateTime = DateTime.Now;
                }

                DatabaseService.Fsql.Insert(log).ExecuteAffrows();
            }
            catch
            {
                // 写入日志失败时忽略，避免影响主流程
            }
        }

        /// <summary>
        /// 批量添加日志
        /// </summary>
        /// <param name="logs">日志条目列表</param>
        public void AddLogs(List<LogEntry> logs)
        {
            try
            {
                if (logs == null || logs.Count == 0) return;

                // 补全创建时间
                DateTime now = DateTime.Now;
                foreach (var log in logs)
                {
                    if (log.CreateTime == default(DateTime))
                    {
                        log.CreateTime = now;
                    }
                }

                DatabaseService.Fsql.Insert<LogEntry>()
                    .AppendData(logs)
                    .ExecuteAffrows();
            }
            catch
            {
                // 批量写入失败时忽略
            }
        }

        /// <summary>
        /// 查询日志（支持按设备、用户、时间范围筛选）
        /// </summary>
        /// <param name="deviceId">设备 ID（null 表示不限制）</param>
        /// <param name="userId">用户 ID（null 表示不限制）</param>
        /// <param name="startTime">起始时间（null 表示不限制）</param>
        /// <param name="endTime">结束时间（null 表示不限制）</param>
        /// <param name="limit">返回最大条数，默认 1000</param>
        /// <returns>符合条件的日志列表；异常时返回空列表</returns>
        public List<LogEntry> QueryLogs(string deviceId = null, string userId = null,
            DateTime? startTime = null, DateTime? endTime = null, int limit = 1000)
        {
            try
            {
                var query = DatabaseService.Fsql.Select<LogEntry>();

                if (!string.IsNullOrEmpty(deviceId))
                {
                    query = query.Where(l => l.DeviceId == deviceId);
                }

                if (!string.IsNullOrEmpty(userId))
                {
                    query = query.Where(l => l.UserId == userId);
                }

                if (startTime.HasValue)
                {
                    query = query.Where(l => l.CreateTime >= startTime.Value);
                }

                if (endTime.HasValue)
                {
                    query = query.Where(l => l.CreateTime <= endTime.Value);
                }

                // 限制条数，避免返回过多数据；按时间倒序排列，最新日志在前
                if (limit <= 0) limit = 1000;

                return query
                    .OrderByDescending(l => l.CreateTime)
                    .Take(limit)
                    .ToList();
            }
            catch
            {
                return new List<LogEntry>();
            }
        }

        /// <summary>
        /// 获取日志总数
        /// </summary>
        /// <returns>日志总条数；异常返回 0</returns>
        public long GetLogCount()
        {
            try
            {
                return DatabaseService.Fsql.Select<LogEntry>()
                    .Count();
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// 清除所有日志
        /// </summary>
        public void ClearLogs()
        {
            try
            {
                DatabaseService.Fsql.Delete<LogEntry>()
                    .Where(l => true)
                    .ExecuteAffrows();
            }
            catch
            {
                // 清除失败时忽略
            }
        }
    }
}
