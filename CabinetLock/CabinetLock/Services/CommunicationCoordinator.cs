namespace CabinetLock
{
    public enum CommunicationOperationKind
    {
        CabinetSync,
        SdSync,
        Maintenance,
        FingerprintEnrollment,
        Ota
    }

    public enum CommunicationMode
    {
        Normal,
        Enrollment,
        Synchronizing,
        Ota
    }

    public sealed record CommunicationOperationSnapshot(
        Guid OperationId,
        CommunicationOperationKind? OperationKind,
        CommunicationMode Mode,
        string Description,
        string TargetDeviceId,
        DateTime StartedAt)
    {
        public static CommunicationOperationSnapshot Idle { get; } = new(
            Guid.Empty, null, CommunicationMode.Normal, "", "", DateTime.MinValue);

        public bool IsActive => Mode != CommunicationMode.Normal;

        public string DisplayText => Mode switch
        {
            CommunicationMode.Ota => "OTA 进行中",
            CommunicationMode.Enrollment => "指纹录入中",
            CommunicationMode.Synchronizing => "数据同步中",
            _ => "通讯空闲"
        };
    }

    /// <summary>
    /// Coordinates complete business transactions over the single Root/serial link.
    /// Receiving is never paused; only outbound business operations are serialized.
    /// </summary>
    public sealed class CommunicationCoordinator
    {
        private readonly SemaphoreSlim _exclusiveGate = new(1, 1);
        private readonly object _stateLock = new();
        private readonly AsyncLocal<Guid?> _ambientOperationId = new();
        private CommunicationOperationSnapshot _current = CommunicationOperationSnapshot.Idle;
        private TaskCompletionSource<bool> _otaCleared = CompletedSignal();
        private int _otaWaiters;

        public event Action<CommunicationOperationSnapshot>? StateChanged;

        public CommunicationOperationSnapshot Current
        {
            get
            {
                lock (_stateLock) return _current;
            }
        }

        public bool IsOtaPendingOrActive
        {
            get
            {
                lock (_stateLock)
                    return _otaWaiters > 0 ||
                        _current.OperationKind == CommunicationOperationKind.Ota;
            }
        }

        public bool IsBackgroundTrafficAllowed
        {
            get
            {
                lock (_stateLock)
                    return _otaWaiters == 0 && !_current.IsActive;
            }
        }

        public Task RunExclusiveAsync(
            CommunicationOperationKind kind,
            string description,
            string? targetDeviceId,
            Func<CancellationToken, Task> operation,
            CancellationToken cancellationToken = default) =>
            RunExclusiveAsync<object?>(kind, description, targetDeviceId,
                async token =>
                {
                    await operation(token).ConfigureAwait(false);
                    return null;
                }, cancellationToken);

        public async Task<T> RunExclusiveAsync<T>(
            CommunicationOperationKind kind,
            string description,
            string? targetDeviceId,
            Func<CancellationToken, Task<T>> operation,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(operation);

            CommunicationOperationSnapshot nestedState = Current;
            if (_ambientOperationId.Value is Guid ambientId &&
                ambientId != Guid.Empty && ambientId == nestedState.OperationId)
            {
                bool otaBoundaryChanged =
                    (nestedState.OperationKind == CommunicationOperationKind.Ota) !=
                    (kind == CommunicationOperationKind.Ota);
                if (otaBoundaryChanged)
                    throw new InvalidOperationException("不能在 OTA 与普通业务事务之间嵌套操作");
                return await operation(cancellationToken).ConfigureAwait(false);
            }

            bool isOta = kind == CommunicationOperationKind.Ota;
            bool otaWaiterRegistered = false;
            bool gateHeld = false;
            bool operationActive = false;
            Guid operationId = Guid.NewGuid();
            Guid? previousAmbient = null;

            if (isOta)
            {
                RegisterOtaWaiter();
                otaWaiterRegistered = true;
            }

            try
            {
                while (true)
                {
                    if (!isOta)
                        await WaitForOtaClearAsync(cancellationToken).ConfigureAwait(false);

                    await _exclusiveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    gateHeld = true;

                    bool otaHasPriority;
                    CommunicationOperationSnapshot started;
                    lock (_stateLock)
                    {
                        otaHasPriority = !isOta &&
                            (_otaWaiters > 0 ||
                             _current.OperationKind == CommunicationOperationKind.Ota);
                        if (!otaHasPriority)
                        {
                            if (isOta)
                            {
                                _otaWaiters--;
                                otaWaiterRegistered = false;
                            }
                            started = new CommunicationOperationSnapshot(
                                operationId,
                                kind,
                                ResolveMode(kind),
                                description?.Trim() ?? "",
                                targetDeviceId?.Trim() ?? "",
                                DateTime.Now);
                            _current = started;
                        }
                        else
                        {
                            started = CommunicationOperationSnapshot.Idle;
                        }
                    }

                    if (!otaHasPriority)
                    {
                        operationActive = true;
                        previousAmbient = _ambientOperationId.Value;
                        _ambientOperationId.Value = operationId;
                        RaiseStateChanged(started);
                        break;
                    }

                    gateHeld = false;
                    _exclusiveGate.Release();
                }

                return await operation(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (operationActive)
                {
                    _ambientOperationId.Value = previousAmbient;
                    CommunicationOperationSnapshot idle;
                    lock (_stateLock)
                    {
                        if (_current.OperationId == operationId)
                            _current = CommunicationOperationSnapshot.Idle;
                        SignalOtaClearedIfNeededUnsafe();
                        idle = _current;
                    }
                    RaiseStateChanged(idle);
                }

                if (gateHeld)
                    _exclusiveGate.Release();

                if (otaWaiterRegistered)
                    UnregisterOtaWaiter();
            }
        }

        public bool CanSend(string? command, out string reason)
        {
            reason = "";
            string normalized = command?.Trim() ?? "";
            CommunicationOperationSnapshot current;
            int otaWaiters;
            lock (_stateLock)
            {
                current = _current;
                otaWaiters = _otaWaiters;
            }

            if (string.Equals(normalized, Protocol.CmdRegister,
                    StringComparison.OrdinalIgnoreCase))
                return true;

            if (!current.IsActive && otaWaiters == 0)
                return true;

            if (_ambientOperationId.Value is Guid ambientId &&
                ambientId != Guid.Empty && ambientId == current.OperationId)
                return true;

            bool allowed = current.OperationKind switch
            {
                CommunicationOperationKind.Ota => IsOtaCommand(normalized),
                CommunicationOperationKind.SdSync => IsSdCommand(normalized),
                CommunicationOperationKind.FingerprintEnrollment =>
                    IsEnrollmentCommand(normalized),
                _ => false
            };
            if (allowed) return true;

            reason = current.IsActive
                ? $"{current.DisplayText}，已阻止无关命令 {normalized}"
                : $"OTA 正在等待通讯通道，已阻止命令 {normalized}";
            return false;
        }

        private async Task WaitForOtaClearAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task signal;
                lock (_stateLock)
                {
                    if (_otaWaiters == 0 &&
                        _current.OperationKind != CommunicationOperationKind.Ota)
                        return;
                    signal = _otaCleared.Task;
                }
                await signal.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private void RegisterOtaWaiter()
        {
            lock (_stateLock)
            {
                if (_otaWaiters == 0 &&
                    _current.OperationKind != CommunicationOperationKind.Ota)
                    _otaCleared = PendingSignal();
                _otaWaiters++;
            }
        }

        private void UnregisterOtaWaiter()
        {
            lock (_stateLock)
            {
                if (_otaWaiters > 0) _otaWaiters--;
                SignalOtaClearedIfNeededUnsafe();
            }
        }

        private void SignalOtaClearedIfNeededUnsafe()
        {
            if (_otaWaiters == 0 &&
                _current.OperationKind != CommunicationOperationKind.Ota)
                _otaCleared.TrySetResult(true);
        }

        private static CommunicationMode ResolveMode(CommunicationOperationKind kind) =>
            kind switch
            {
                CommunicationOperationKind.Ota => CommunicationMode.Ota,
                CommunicationOperationKind.FingerprintEnrollment =>
                    CommunicationMode.Enrollment,
                _ => CommunicationMode.Synchronizing
            };

        private static bool IsOtaCommand(string command) =>
            command.StartsWith("CABINET_OTA_", StringComparison.OrdinalIgnoreCase);

        private static bool IsSdCommand(string command) =>
            command.StartsWith("SD_", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, Protocol.CmdUploadFpTemplate,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, Protocol.CmdDownloadFpTemplate,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, Protocol.CmdDeleteFpTemplate,
                StringComparison.OrdinalIgnoreCase);

        private static bool IsEnrollmentCommand(string command) =>
            string.Equals(command, Protocol.CmdAddFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, Protocol.CmdCancelEnroll,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, Protocol.CmdAddBackupFingerprint,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, Protocol.CmdStartFingerprintTest,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, Protocol.CmdStopFingerprintTest,
                StringComparison.OrdinalIgnoreCase);

        private void RaiseStateChanged(CommunicationOperationSnapshot state)
        {
            try { StateChanged?.Invoke(state); }
            catch { }
        }

        private static TaskCompletionSource<bool> CompletedSignal()
        {
            var signal = PendingSignal();
            signal.TrySetResult(true);
            return signal;
        }

        private static TaskCompletionSource<bool> PendingSignal() => new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
