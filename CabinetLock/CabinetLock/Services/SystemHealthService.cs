namespace CabinetLock
{
    /// <summary>Combines root snapshots into operator-facing health signals.</summary>
    public sealed class SystemHealthService
    {
        public SystemHealthSnapshot? LastSnapshot { get; private set; }

        public SystemHealthSnapshot LoadSnapshot()
        {
            var version = App.SdStorageService.QueryVersion()
                ?? throw new RootDataUnavailableException("读取根节点版本信息失败");
            return LoadSnapshot(version);
        }

        public Task<SystemHealthSnapshot> LoadSnapshotAsync() =>
            App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.SdSync,
                "读取系统运行状态",
                App.SdStorageService.RootDeviceId,
                _ => LoadSnapshotCoreAsync());

        private async Task<SystemHealthSnapshot> LoadSnapshotCoreAsync()
        {
            HealthScope scope = CaptureScope();
            Task<SdVersionInfo?> versionTask = App.SdStorageService.QueryVersionAsync();
            Task<LocalHealthData> localTask = Task.Run(() => LoadLocalData(scope));
            await Task.WhenAll(versionTask, localTask).ConfigureAwait(false);
            SdVersionInfo version = await versionTask.ConfigureAwait(false)
                ?? throw new RootDataUnavailableException("读取根节点版本信息失败");
            return CreateSnapshot(version, await localTask.ConfigureAwait(false));
        }

        public SystemHealthSnapshot LoadSnapshot(SdVersionInfo version)
        {
            if (version == null) throw new ArgumentNullException(nameof(version));
            return CreateSnapshot(version, LoadLocalData(CaptureScope()));
        }

        private SystemHealthSnapshot CreateSnapshot(
            SdVersionInfo version, LocalHealthData local)
        {
            var snapshot = new SystemHealthSnapshot
            {
                Version = version,
                Devices = local.Devices,
                RecentLogs = local.RecentLogs,
                Alerts = BuildAlerts(local.Devices, version, local.RecentLogs),
                BoundStudentCount = local.StudentBindings.BoundStudents,
                TotalStudentCount = local.StudentBindings.TotalStudents,
                OpenSyncCount = local.OpenSyncCount,
                FailedSyncCount = local.FailedSyncCount,
                ScopeUserId = local.Scope.UserId,
                ScopeRole = local.Scope.Role,
                RefreshedAt = DateTime.Now
            };
            LastSnapshot = snapshot;
            return snapshot;
        }

        private static LocalHealthData LoadLocalData(HealthScope scope)
        {
            var devices = App.DeviceService.GetAllDevices()
                .Where(device => !device.IsRoot)
                .ToList();
            var recentLogs = App.LogService.QueryLogs(limit: 12);
            StudentBindingStatistics bindings =
                BusinessDatabase.QueryStudentBindingStatistics(
                    scope.Role, scope.UserId, scope.ClassIds);
            (int open, int failed) =
                App.CabinetSyncQueueService.CountOpenAndFailed();
            return new LocalHealthData
            {
                Devices = devices,
                RecentLogs = recentLogs,
                StudentBindings = bindings,
                OpenSyncCount = open,
                FailedSyncCount = failed,
                Scope = scope
            };
        }

        private static HealthScope CaptureScope()
        {
            User? current = App.CurrentUser;
            string role = current?.Role ?? "";
            IReadOnlyCollection<string>? classIds =
                string.Equals(role, "admin", StringComparison.OrdinalIgnoreCase)
                    ? null
                    : string.Equals(role, "teacher", StringComparison.OrdinalIgnoreCase)
                        ? current?.GetResponsibleClassIds().ToArray() ?? Array.Empty<string>()
                        : Array.Empty<string>();
            return new HealthScope(current?.UserId ?? "", role, classIds);
        }

        private sealed class LocalHealthData
        {
            public List<Device> Devices { get; init; } = new();
            public List<LogEntry> RecentLogs { get; init; } = new();
            public StudentBindingStatistics StudentBindings { get; init; }
            public int OpenSyncCount { get; init; }
            public int FailedSyncCount { get; init; }
            public HealthScope Scope { get; init; } = new("", "", Array.Empty<string>());
        }

        private sealed record HealthScope(
            string UserId, string Role, IReadOnlyCollection<string>? ClassIds);

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
                    Message = "尚未发现柜子",
                    ActionHint = "检查柜子供电和 Mesh 配置"
                });
            }

            foreach (Device device in devices)
            {
                string deviceId = device.DeviceId ?? string.Empty;
                string deviceName = device.DeviceName ?? string.Empty;
                string source = string.IsNullOrWhiteSpace(deviceName) ? deviceId : deviceName;
                if (!device.IsOnline)
                {
                    alerts.Add(new SystemAlert
                    {
                        Severity = SystemAlertSeverity.Critical,
                        Source = source,
                        DeviceId = deviceId,
                        Message = "柜子离线",
                        ActionHint = "双击打开柜子详情 · 检查供电与 Mesh"
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
                        DeviceId = deviceId,
                        Message = "状态数据超过 3 分钟未更新",
                        ActionHint = "双击打开柜子 · 检查心跳"
                    });
                }
                if (!device.Status.TimeSynced)
                {
                    alerts.Add(new SystemAlert
                    {
                        Severity = SystemAlertSeverity.Warning,
                        Source = source,
                        DeviceId = deviceId,
                        Message = "设备时间尚未同步",
                        ActionHint = "双击打开柜子 · 检查链路"
                    });
                }
                if (device.Status.PermissionVersion != version.GlobalVersion)
                {
                    alerts.Add(new SystemAlert
                    {
                        Severity = SystemAlertSeverity.Warning,
                        Source = source,
                        DeviceId = deviceId,
                        Message = $"权限版本 {device.Status.PermissionVersion}，根节点版本 {version.GlobalVersion}",
                        ActionHint = "双击打开柜子并同步权限"
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
                        DeviceId = deviceId,
                        Message = $"有 {device.Status.PendingLogCount} 条日志等待上报",
                        ActionHint = "保持在线，双击可查看柜子"
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
                    Message = "柜子运行多个固件版本",
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
