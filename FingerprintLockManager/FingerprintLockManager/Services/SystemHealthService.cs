namespace FingerprintLockManager
{
    /// <summary>Combines root snapshots into operator-facing health signals.</summary>
    public sealed class SystemHealthService
    {
        public SystemHealthSnapshot LoadSnapshot()
        {
            var version = App.SdStorageService.QueryVersion()
                ?? throw new RootDataUnavailableException("读取根节点版本信息失败");
            var devices = App.DeviceService.GetAllDevices()
                .Where(device => !device.IsRoot)
                .ToList();
            var recentLogs = App.LogService.QueryLogs(limit: 12);
            return new SystemHealthSnapshot
            {
                Version = version,
                Devices = devices,
                RecentLogs = recentLogs,
                Alerts = BuildAlerts(devices, version, recentLogs),
                RefreshedAt = DateTime.Now
            };
        }

        public static List<SystemAlert> BuildAlerts(
            IReadOnlyCollection<Device> devices,
            SdVersionInfo version,
            IReadOnlyCollection<LogEntry>? recentLogs = null,
            DateTime? now = null)
        {
            DateTime current = now ?? DateTime.Now;
            var alerts = new List<SystemAlert>();
            if (devices.Count == 0)
            {
                alerts.Add(new SystemAlert
                {
                    Severity = SystemAlertSeverity.Warning,
                    Source = "Mesh",
                    Message = "尚未发现柜子节点",
                    ActionHint = "检查柜子供电和 Mesh 配置"
                });
            }

            foreach (Device device in devices)
            {
                string source = string.IsNullOrWhiteSpace(device.DeviceName)
                    ? device.DeviceId
                    : device.DeviceName;
                if (!device.IsOnline)
                {
                    alerts.Add(new SystemAlert
                    {
                        Severity = SystemAlertSeverity.Critical,
                        Source = source,
                        Message = "柜子节点离线",
                        ActionHint = "检查供电、天线和 Mesh 路由"
                    });
                    continue;
                }

                if (device.LastSeenTime.HasValue &&
                    current - device.LastSeenTime.Value > TimeSpan.FromMinutes(3))
                {
                    alerts.Add(new SystemAlert
                    {
                        Severity = SystemAlertSeverity.Critical,
                        Source = source,
                        Message = "状态数据超过 3 分钟未更新",
                        ActionHint = "刷新设备并检查心跳"
                    });
                }
                if (!device.Status.TimeSynced)
                {
                    alerts.Add(new SystemAlert
                    {
                        Severity = SystemAlertSeverity.Warning,
                        Source = source,
                        Message = "设备时间尚未同步",
                        ActionHint = "检查根节点与上位机连接"
                    });
                }
                if (device.Status.PermissionVersion != version.GlobalVersion)
                {
                    alerts.Add(new SystemAlert
                    {
                        Severity = SystemAlertSeverity.Warning,
                        Source = source,
                        Message = $"权限版本 {device.Status.PermissionVersion}，根节点版本 {version.GlobalVersion}",
                        ActionHint = "重新执行权限同步"
                    });
                }
                if (device.Status.PendingLogCount > 0)
                {
                    alerts.Add(new SystemAlert
                    {
                        Severity = device.Status.PendingLogCount >= 20
                            ? SystemAlertSeverity.Warning
                            : SystemAlertSeverity.Info,
                        Source = source,
                        Message = $"有 {device.Status.PendingLogCount} 条日志等待上报",
                        ActionHint = "保持设备在线并观察队列是否下降"
                    });
                }
            }

            if (version.SdTotalBytes > 0)
            {
                double usage = version.SdUsedBytes * 100d / version.SdTotalBytes;
                if (usage >= 85)
                {
                    alerts.Add(new SystemAlert
                    {
                        Severity = SystemAlertSeverity.Critical,
                        Source = "根节点 SD",
                        Message = $"存储使用率已达 {usage:F0}%",
                        ActionHint = "导出并归档历史日志"
                    });
                }
                else if (usage >= 70)
                {
                    alerts.Add(new SystemAlert
                    {
                        Severity = SystemAlertSeverity.Warning,
                        Source = "根节点 SD",
                        Message = $"存储使用率已达 {usage:F0}%",
                        ActionHint = "计划日志归档"
                    });
                }
            }

            int recentFailures = recentLogs?.Count(log =>
                string.Equals(log.Result, "fail", StringComparison.OrdinalIgnoreCase)) ?? 0;
            if (recentFailures >= 3)
            {
                alerts.Add(new SystemAlert
                {
                    Severity = SystemAlertSeverity.Warning,
                    Source = "开锁日志",
                    Message = $"最近记录中有 {recentFailures} 次失败",
                    ActionHint = "查看失败原因和相关设备"
                });
            }

            string[] firmwareVersions = devices
                .Where(device => !string.IsNullOrWhiteSpace(device.FirmwareVersion))
                .Select(device => device.FirmwareVersion)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (firmwareVersions.Length > 1)
            {
                alerts.Add(new SystemAlert
                {
                    Severity = SystemAlertSeverity.Warning,
                    Source = "固件版本",
                    Message = "柜子节点运行多个固件版本",
                    ActionHint = "核对兼容性并制定统一升级批次"
                });
            }

            return alerts
                .OrderByDescending(alert => alert.Severity)
                .ThenBy(alert => alert.Source)
                .ToList();
        }
    }
}
