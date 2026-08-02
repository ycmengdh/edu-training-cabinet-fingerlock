namespace CabinetLock
{
    /// <summary>
    /// 上位机操作审计日志服务。
    /// 存储：%APPDATA%\CabinetLock\data\logs.db → operation_logs
    /// </summary>
    public class OperationLogService
    {
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
            try
            {
                LogDatabase.AppendOperation(entry);
            }
            catch
            {
                // ignore
            }
        }

        public List<OperationLogEntry> Query(
            string? keyword = null,
            DateTime? startTime = null,
            DateTime? endTime = null,
            int limit = 100,
            int offset = 0)
        {
            return LogDatabase.QueryOperations(keyword, startTime, endTime, limit, offset);
        }

        public int Count(string? keyword = null, DateTime? startTime = null, DateTime? endTime = null)
        {
            return LogDatabase.CountOperations(keyword, startTime, endTime);
        }

        public List<OperationLogEntry> QueryAll(
            string? keyword = null,
            DateTime? startTime = null,
            DateTime? endTime = null,
            int max = 50000)
        {
            return LogDatabase.QueryAllOperations(keyword, startTime, endTime, max);
        }
    }
}
