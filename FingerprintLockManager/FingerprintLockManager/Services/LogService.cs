namespace FingerprintLockManager
{
    /// <summary>
    /// 日志服务（需求 9）
    ///
    /// 需求 9：开关柜子的信息，柜子不需要保存，根节点也不需要保存，只需要发出来。
    /// 正常情况下，上位机在线的话就记录日志，用 SQLite 数据库记录。
    /// 上位机不在线则不管，数据发了就发了，不强求能真正被记录。
    ///
    /// 本服务委托 LogDbService（SQLite）进行日志持久化。
    /// </summary>
    public class LogService
    {
        /// <summary>添加日志（上位机在线时记录到 SQLite）</summary>
        public void AddLog(LogEntry log)
        {
            try
            {
                LogDbService.Current.AddLog(log);
            }
            catch
            {
                // 写入日志失败时忽略（需求 9：不强求能真正被记录）
            }
        }

        /// <summary>批量添加日志</summary>
        public void AddLogs(List<LogEntry> logs)
        {
            try
            {
                LogDbService.Current.AddLogs(logs);
            }
            catch
            {
                // 批量写入失败时忽略
            }
        }

        /// <summary>
        /// 查询日志（支持按设备、用户、时间范围筛选）
        /// </summary>
        public List<LogEntry> QueryLogs(string? deviceId = null, string? userId = null,
            DateTime? startTime = null, DateTime? endTime = null, int limit = 1000)
        {
            try
            {
                return LogDbService.Current.QueryLogs(deviceId, userId, startTime, endTime, limit);
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
                return LogDbService.Current.GetLogCount();
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
                LogDbService.Current.ClearLogs();
            }
            catch
            {
                // 清除失败时忽略
            }
        }
    }
}
