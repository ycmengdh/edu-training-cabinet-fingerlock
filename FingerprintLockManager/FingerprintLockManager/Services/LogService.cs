namespace FingerprintLockManager
{
    /// <summary>
    /// 日志服务
    /// 负责开锁/关锁等操作日志的记录、查询与清理。
    /// 数据持久化于根节点 SD 卡 logs.json。
    /// 日志写入采用脏标记 + 延迟批量刷盘（每 5 秒），避免频繁写 SD 卡。
    /// </summary>
    public class LogService
    {
        /// <summary>添加日志</summary>
        public void AddLog(LogEntry log)
        {
            try
            {
                DataStore.Current.AddLog(log);
            }
            catch
            {
                // 写入日志失败时忽略
            }
        }

        /// <summary>批量添加日志</summary>
        public void AddLogs(List<LogEntry> logs)
        {
            try
            {
                DataStore.Current.AddLogs(logs);
            }
            catch
            {
                // 批量写入失败时忽略
            }
        }

        /// <summary>
        /// 查询日志（支持按设备、用户、时间范围筛选）
        /// </summary>
        public List<LogEntry> QueryLogs(string deviceId = null, string userId = null,
            DateTime? startTime = null, DateTime? endTime = null, int limit = 1000)
        {
            try
            {
                var query = DataStore.Current.GetLogs().AsQueryable();

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

        /// <summary>获取日志总数</summary>
        public long GetLogCount()
        {
            try
            {
                return DataStore.Current.GetLogs().Count;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>清除所有日志</summary>
        public void ClearLogs()
        {
            try
            {
                DataStore.Current.ClearLogs();
            }
            catch
            {
                // 清除失败时忽略
            }
        }
    }
}
