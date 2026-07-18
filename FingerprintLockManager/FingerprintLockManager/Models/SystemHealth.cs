namespace FingerprintLockManager
{
    public enum SystemAlertSeverity
    {
        Info = 0,
        Warning = 1,
        Critical = 2
    }

    public sealed class SystemAlert
    {
        public SystemAlertSeverity Severity { get; init; }
        public string Source { get; init; } = "";
        public string Message { get; init; } = "";
        public string ActionHint { get; init; } = "";

        public string SeverityText => Severity switch
        {
            SystemAlertSeverity.Critical => "异常",
            SystemAlertSeverity.Warning => "注意",
            _ => "提示"
        };
    }

    public sealed class SystemHealthSnapshot
    {
        public SdVersionInfo Version { get; init; } = new();
        public List<Device> Devices { get; init; } = new();
        public List<SystemAlert> Alerts { get; init; } = new();
        public List<LogEntry> RecentLogs { get; init; } = new();
        public DateTime RefreshedAt { get; init; } = DateTime.Now;

        public int OnlineCount => Devices.Count(device => device.IsOnline);
        public int CriticalCount => Alerts.Count(alert => alert.Severity == SystemAlertSeverity.Critical);
        public int WarningCount => Alerts.Count(alert => alert.Severity == SystemAlertSeverity.Warning);
        public int PendingLogCount => Devices.Sum(device => device.Status.PendingLogCount);
        public int SynchronizedCount => Devices.Count(device =>
            device.IsOnline && device.Status.PermissionVersion == Version.GlobalVersion);
        public double SdUsagePercent => Version.SdTotalBytes == 0
            ? 0
            : Version.SdUsedBytes * 100d / Version.SdTotalBytes;
    }
}
