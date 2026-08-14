using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;

namespace CabinetLock
{
    public sealed class MaintenanceService
    {
        private readonly ConcurrentDictionary<string, MaintenanceRuntimeState> _states =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _syncGates =
            new(StringComparer.OrdinalIgnoreCase);

        public event Action<string>? StateChanged;

        public MaintenanceSettings GetSettings() => BusinessDatabase.GetMaintenanceSettings();

        public bool NeedsConfigurationSync(string deviceId, string? firmwareVersion)
        {
            if (string.IsNullOrWhiteSpace(deviceId) ||
                !MaintenanceSettings.SupportsDevicePinEncoding(firmwareVersion))
                return false;

            uint expectedVersion = GetSettings().Version;
            return _states.TryGetValue(deviceId, out MaintenanceRuntimeState? state) &&
                state.ConfigVersion < expectedVersion;
        }

        public async Task<MaintenancePasswordUpdateResult> ChangePinAsync(
            string pin, CancellationToken cancellationToken = default)
        {
            if (!IsAdministrator())
                return MaintenancePasswordUpdateResult.Failed("只有管理员可以修改维护密码");
            if (!MaintenanceSettings.IsValidPin(pin))
                return MaintenancePasswordUpdateResult.Failed("维护密码必须是由按键 1-4 组成的 6 位密码");

            MaintenanceSettings settings = BusinessDatabase.SetMaintenancePin(pin);
            SdBusinessSyncService.SyncResult sdResult = await App.SdBusinessSyncService.PushBusinessToSdAsync(
                cancellationToken: cancellationToken).ConfigureAwait(false);
            try
            {
                App.CabinetSyncQueueService.EnqueueMaintenance(
                    App.DeviceService.GetAllDevices()
                        .Where(device => !DeviceService.IsTrueRoot(device))
                        .Select(device => device.DeviceId),
                    "维护密码变更");
            }
            catch { }
            IReadOnlyList<string> failed = await SyncOnlineDevicesAsync(cancellationToken)
                .ConfigureAwait(false);
            App.CabinetSyncQueueService.Trigger();
            App.OperationLogService.Write("柜子维护", "修改维护密码", result:
                sdResult.Success && failed.Count == 0 ? "success" : "partial",
                detail: $"版本 {settings.Version}；柜机同步失败 {failed.Count} 台");
            return new MaintenancePasswordUpdateResult
            {
                Success = sdResult.Success && failed.Count == 0,
                Version = settings.Version,
                FailedDeviceIds = failed,
                Message = !sdResult.Success
                    ? sdResult.Message
                    : failed.Count == 0
                        ? "维护密码已保存并同步到全部在线柜机"
                        : $"密码已保存，{failed.Count} 台柜机将在下次上线时补同步"
            };
        }

        public Task<CommandResult> EnterAsync(
            string deviceId, int lockMask, CancellationToken cancellationToken = default)
            => App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.Maintenance,
                $"进入柜机 {deviceId} 维护模式",
                deviceId,
                token => EnterCoreAsync(deviceId, lockMask),
                cancellationToken);

        private async Task<CommandResult> EnterCoreAsync(string deviceId, int lockMask)
        {
            if (!IsAdministrator()) return CommandResult.Failed("只有管理员可以进入柜子维护模式");
            if ((lockMask & 0x0F) == 0) return CommandResult.Failed("至少选择一把允许开启的锁");
            var message = Message.Create(Protocol.CmdEnterMaintenance, deviceId, new
            {
                lock_mask = lockMask & 0x0F,
                operator_id = App.CurrentUser?.UserId ?? "admin"
            });
            CommandResult result = await App.CommandService.SendAsync(deviceId, message)
                .ConfigureAwait(false);
            App.OperationLogService.Write("柜子维护", "远程进入维护模式", deviceId,
                result.Success ? "success" : "failed",
                $"允许锁掩码 0x{lockMask & 0x0F:X1}；{result.ErrorMessage}");
            return result;
        }

        public Task<CommandResult> ExitAsync(
            string deviceId, CancellationToken cancellationToken = default)
            => App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.Maintenance,
                $"退出柜机 {deviceId} 维护模式",
                deviceId,
                _ => ExitCoreAsync(deviceId),
                cancellationToken);

        private async Task<CommandResult> ExitCoreAsync(string deviceId)
        {
            if (!IsAdministrator()) return CommandResult.Failed("只有管理员可以退出柜子维护模式");
            var message = Message.Create(Protocol.CmdExitMaintenance, deviceId, new
            {
                operator_id = App.CurrentUser?.UserId ?? "admin"
            });
            CommandResult result = await App.CommandService.SendAsync(deviceId, message)
                .ConfigureAwait(false);
            App.OperationLogService.Write("柜子维护", "远程退出维护模式", deviceId,
                result.Success ? "success" : "failed", result.ErrorMessage);
            return result;
        }

        public Task<bool> SyncDeviceAsync(
            string deviceId, CancellationToken cancellationToken = default)
            => App.CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.Maintenance,
                $"同步柜机 {deviceId} 维护配置",
                deviceId,
                token => SyncDeviceCoreAsync(deviceId, token),
                cancellationToken);

        private async Task<bool> SyncDeviceCoreAsync(
            string deviceId, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return false;
            SemaphoreSlim gate = _syncGates.GetOrAdd(deviceId, _ => new SemaphoreSlim(1, 1));
            if (!await gate.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return true;
            try
            {
                DeviceClient? device = App.MeshBridge.GetOnlineDevices().FirstOrDefault(candidate =>
                    candidate.IsOnline && !candidate.IsRoot && string.Equals(
                        candidate.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
                if (device == null ||
                    !MaintenanceSettings.SupportsDevicePinEncoding(device.FirmwareVersion))
                {
                    return false;
                }
                MaintenanceSettings settings = GetSettings();
                var message = Message.Create(Protocol.CmdSyncMaintenanceConfig, deviceId, new
                {
                    pin = MaintenanceSettings.EncodeForDevice(settings.Pin),
                    pin_encoding = MaintenanceSettings.DevicePinEncoding,
                    version = settings.Version
                });
                CommandResult result = await App.CommandService.SendAsync(deviceId, message, 8_000)
                    .ConfigureAwait(false);
                return result.Success;
            }
            catch
            {
                return false;
            }
            finally
            {
                gate.Release();
            }
        }

        public void HandleReported(string deviceId, JObject data)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || data == null) return;
            bool? active = data.Value<bool?>("maintenance_active") ??
                data.Value<bool?>("active");
            int? mask = data.Value<int?>("maintenance_lock_mask");
            string? source = data.Value<string>("maintenance_source") ??
                data.Value<string>("source");
            uint? configVersion = data.Value<uint?>("maintenance_config_version");
            if (!active.HasValue && !mask.HasValue && source == null &&
                !configVersion.HasValue)
                return;

            _states.AddOrUpdate(deviceId,
                _ => new MaintenanceRuntimeState(
                    active ?? false, (mask ?? 0) & 0x0F,
                    source ?? "local", configVersion ?? 0),
                (_, current) => new MaintenanceRuntimeState(
                    active ?? current.Active,
                    (mask ?? current.LockMask) & 0x0F,
                    source ?? current.Source,
                    configVersion ?? current.ConfigVersion));
            StateChanged?.Invoke(deviceId);
        }

        public void ApplyState(Device device)
        {
            if (device == null || !_states.TryGetValue(device.DeviceId, out var state)) return;
            device.MaintenanceActive = state.Active;
            device.MaintenanceLockMask = state.LockMask;
            device.MaintenanceSource = state.Source;
        }

        public async Task<IReadOnlyList<string>> SyncOnlineDevicesAsync(
            CancellationToken cancellationToken = default)
        {
            string[] deviceIds = App.MeshBridge.GetOnlineDevices()
                .Where(device => device.IsOnline && !device.IsRoot &&
                                 !string.IsNullOrWhiteSpace(device.DeviceId))
                .Select(device => device.DeviceId)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var failed = new List<string>();
            foreach (string deviceId in deviceIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool success = await SyncDeviceAsync(deviceId, cancellationToken)
                    .ConfigureAwait(false);
                App.CabinetSyncQueueService.RecordMaintenanceOutcome(
                    deviceId, success, success ? "" : "柜机未确认维护配置");
                if (!success) failed.Add(deviceId);
            }
            return failed.OrderBy(id => id).ToArray();
        }

        private static bool IsAdministrator() => string.Equals(
            App.CurrentUser?.Role, "admin", StringComparison.OrdinalIgnoreCase);
    }

    public sealed record MaintenanceRuntimeState(
        bool Active, int LockMask, string Source, uint ConfigVersion);

    public sealed class MaintenancePasswordUpdateResult
    {
        public bool Success { get; init; }
        public uint Version { get; init; }
        public string Message { get; init; } = "";
        public IReadOnlyList<string> FailedDeviceIds { get; init; } = Array.Empty<string>();

        public static MaintenancePasswordUpdateResult Failed(string message) =>
            new() { Message = message };
    }
}
