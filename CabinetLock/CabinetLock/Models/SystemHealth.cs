namespace CabinetLock
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

        /// <summary>关联柜机 device_id；空表示系统级告警（Mesh/SD 等）。</summary>
        public string DeviceId { get; init; } = "";

        public string SeverityText => Severity switch
        {
            SystemAlertSeverity.Critical => "异常",
            SystemAlertSeverity.Warning => "注意",
            _ => "提示"
        };

        public bool CanOpenCabinet => !string.IsNullOrWhiteSpace(DeviceId);
    }

    public sealed class SystemHealthSnapshot
    {
        public SdVersionInfo Version { get; init; } = new();
        public List<Device> Devices { get; init; } = new();
        public List<SystemAlert> Alerts { get; init; } = new();
        public List<LogEntry> RecentLogs { get; init; } = new();
        public DateTime RefreshedAt { get; init; } = DateTime.Now;
        public int BoundStudentCount { get; init; }
        public int TotalStudentCount { get; init; }
        public int OpenSyncCount { get; init; }
        public int FailedSyncCount { get; init; }
        public string ScopeUserId { get; init; } = "";
        public string ScopeRole { get; init; } = "";

        public int OnlineCount => Devices.Count(device => device.IsOnline);
        public int CriticalCount => Alerts.Count(alert => alert.Severity == SystemAlertSeverity.Critical);
        public int WarningCount => Alerts.Count(alert => alert.Severity == SystemAlertSeverity.Warning);
        public int PendingLogCount => Devices.Sum(device => device.Status.PendingLogCount);
        public int SynchronizedCount
        {
            get
            {
                uint expected = CabinetSyncService.ComposePermissionVersion(
                    Version.UsersVersion, Version.ClassesVersion,
                    Version.PermissionsVersion, Version.FpVersion);
                return Devices.Count(device =>
                    device.IsOnline && device.Status.PermissionVersion == expected);
            }
        }
        public double SdUsagePercent => Version.SdTotalBytes == 0
            ? 0
            : Version.SdUsedBytes * 100d / Version.SdTotalBytes;
    }
}
