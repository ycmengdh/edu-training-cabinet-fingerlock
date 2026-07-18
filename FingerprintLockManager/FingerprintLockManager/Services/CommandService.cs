using System.Collections.Concurrent;

namespace FingerprintLockManager
{
    /// <summary>等待柜子 ACK/ERROR 的命令服务，用于需要明确执行结果的操作。</summary>
    public sealed class CommandService
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResult>> _pending = new();
        private readonly ConcurrentDictionary<string, TaskCompletionSource<FingerprintEnrollmentResult>>
            _pendingEnrollments = new();
        private readonly ConcurrentDictionary<string, BroadcastPending> _pendingBroadcasts = new();

        public async Task<CommandResult> SendAsync(
            string deviceId, Message message, int timeoutMs = 5000)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || message == null)
                return CommandResult.Failed("参数无效");

            var tcs = new TaskCompletionSource<CommandResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(message.MsgId, tcs))
                return CommandResult.Failed("消息编号冲突");

            if (!App.MeshBridge.SendToDevice(deviceId, message))
            {
                _pending.TryRemove(message.MsgId, out _);
                return CommandResult.Failed("设备链路不可用");
            }

            Task completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (completed == tcs.Task) return await tcs.Task;

            _pending.TryRemove(message.MsgId, out _);
            return CommandResult.Failed("等待设备确认超时");
        }

        public void HandleAck(string msgId, string result)
        {
            if (_pending.TryRemove(msgId, out var pending))
                pending.TrySetResult(CommandResult.Succeeded(result));
        }

        public void HandleError(string msgId, string errorCode, string message)
        {
            if (_pending.TryRemove(msgId, out var pending))
                pending.TrySetResult(CommandResult.Failed(message, errorCode));
        }

        public void HandleConnectionChanged(bool connected)
        {
            if (connected) return;
            foreach (var pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out var pending))
                    pending.TrySetResult(CommandResult.Failed("设备链路已断开"));
            }
            foreach (var pair in _pendingEnrollments)
            {
                if (_pendingEnrollments.TryRemove(pair.Key, out var pending))
                {
                    pending.TrySetResult(FingerprintEnrollmentResult.Failed("设备链路已断开"));
                }
            }
            foreach (var pair in _pendingBroadcasts)
            {
                if (_pendingBroadcasts.TryRemove(pair.Key, out var pending))
                    pending.FailRemaining("设备链路已断开");
            }
        }

        public async Task<FingerprintEnrollmentResult> EnrollFingerprintAsync(
            string deviceId, string userId, int fingerprintId, bool replace = false,
            int timeoutMs = 130_000)
        {
            var message = Message.Create(Protocol.CmdAddFingerprint, deviceId, new
            {
                fingerprint_id = fingerprintId,
                user_id = userId,
                replace
            });
            var completion = new TaskCompletionSource<FingerprintEnrollmentResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingEnrollments.TryAdd(message.MsgId, completion))
                return FingerprintEnrollmentResult.Failed("消息编号冲突");

            CommandResult accepted = await SendAsync(deviceId, message);
            if (!accepted.Success)
            {
                _pendingEnrollments.TryRemove(message.MsgId, out _);
                return FingerprintEnrollmentResult.Failed(accepted.ErrorMessage);
            }

            Task completed = await Task.WhenAny(completion.Task, Task.Delay(timeoutMs));
            if (completed == completion.Task) return await completion.Task;

            _pendingEnrollments.TryRemove(message.MsgId, out _);
            return FingerprintEnrollmentResult.Failed("等待指纹录入结果超时");
        }

        public async Task<CommandResult> RestoreFingerprintAsync(
            string deviceId, string userId, int fingerprintId, byte[] templateBytes,
            bool replace = true, int timeoutMs = 15_000)
        {
            if (templateBytes == null || templateBytes.Length == 0)
                return CommandResult.Failed("模板数据为空");

            string hex = BitConverter.ToString(templateBytes).Replace("-", "");
            var message = Message.Create(Protocol.CmdRestoreFingerprint, deviceId, new
            {
                fingerprint_id = fingerprintId,
                user_id = userId,
                template_hex = hex,
                replace
            });
            return await SendAsync(deviceId, message, timeoutMs);
        }

        public void HandleFingerprintEnrollmentResult(
            string msgId, FingerprintEnrollmentResult result)
        {
            if (_pendingEnrollments.TryRemove(msgId, out var pending))
                pending.TrySetResult(result);
        }

        public void HandlePermissionSyncResult(string deviceId, string msgId, string result)
        {
            if (_pendingBroadcasts.TryGetValue(msgId, out var broadcast))
            {
                bool completed = broadcast.Record(deviceId,
                    string.Equals(result, "success", StringComparison.OrdinalIgnoreCase));
                if (completed) _pendingBroadcasts.TryRemove(msgId, out _);
            }

            // Targeted permission commits can use the normal command waiter.
            if (_pending.TryRemove(msgId, out var pending))
            {
                pending.TrySetResult(string.Equals(result, "success", StringComparison.OrdinalIgnoreCase)
                    ? CommandResult.Succeeded(result)
                    : CommandResult.Failed("柜子未能提交权限事务"));
            }
        }

        public async Task<BroadcastCommandResult> SendBroadcastAsync(
            Message message, IEnumerable<string> expectedDeviceIds, int timeoutMs = 30_000)
        {
            string[] expected = expectedDeviceIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (expected.Length == 0)
                return BroadcastCommandResult.Failed("没有在线柜子可确认同步");

            var pending = new BroadcastPending(expected);
            if (!_pendingBroadcasts.TryAdd(message.MsgId, pending))
                return BroadcastCommandResult.Failed("消息编号冲突");
            if (!App.MeshBridge.Broadcast(message))
            {
                _pendingBroadcasts.TryRemove(message.MsgId, out _);
                return BroadcastCommandResult.Failed("广播链路不可用", expected);
            }

            Task completed = await Task.WhenAny(pending.Completion, Task.Delay(timeoutMs));
            if (completed == pending.Completion) return await pending.Completion;

            _pendingBroadcasts.TryRemove(message.MsgId, out _);
            return pending.Timeout();
        }

        private sealed class BroadcastPending
        {
            private readonly object _lock = new();
            private readonly HashSet<string> _remaining;
            private readonly HashSet<string> _confirmed =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _failed =
                new(StringComparer.OrdinalIgnoreCase);
            private readonly TaskCompletionSource<BroadcastCommandResult> _completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);

            public BroadcastPending(IEnumerable<string> expected)
            {
                _remaining = new HashSet<string>(expected, StringComparer.OrdinalIgnoreCase);
            }

            public Task<BroadcastCommandResult> Completion => _completion.Task;

            public bool Record(string deviceId, bool success)
            {
                lock (_lock)
                {
                    if (!_remaining.Remove(deviceId)) return false;
                    (success ? _confirmed : _failed).Add(deviceId);
                    if (_remaining.Count > 0) return false;
                    _completion.TrySetResult(BuildResult(""));
                    return true;
                }
            }

            public BroadcastCommandResult Timeout()
            {
                lock (_lock)
                {
                    return BuildResult("等待部分柜子确认超时");
                }
            }

            public void FailRemaining(string message)
            {
                lock (_lock)
                {
                    foreach (string deviceId in _remaining) _failed.Add(deviceId);
                    _remaining.Clear();
                    _completion.TrySetResult(BuildResult(message));
                }
            }

            private BroadcastCommandResult BuildResult(string message) => new()
            {
                Success = _remaining.Count == 0 && _failed.Count == 0,
                ConfirmedDeviceIds = _confirmed.OrderBy(id => id).ToArray(),
                FailedDeviceIds = _failed.OrderBy(id => id).ToArray(),
                MissingDeviceIds = _remaining.OrderBy(id => id).ToArray(),
                ErrorMessage = message
            };
        }
    }

    public sealed class CommandResult
    {
        public bool Success { get; private init; }
        public string Result { get; private init; } = "";
        public string ErrorCode { get; private init; } = "";
        public string ErrorMessage { get; private init; } = "";

        public static CommandResult Succeeded(string result) => new()
        {
            Success = true,
            Result = result
        };

        public static CommandResult Failed(string message, string errorCode = "") => new()
        {
            ErrorCode = errorCode,
            ErrorMessage = message
        };
    }

    public sealed class FingerprintEnrollmentResult
    {
        public bool Success { get; init; }
        public string DeviceId { get; init; } = "";
        public string UserId { get; init; } = "";
        public int FingerprintId { get; init; } = -1;
        public byte[]? TemplateBytes { get; init; }
        public string ErrorMessage { get; init; } = "";

        public static FingerprintEnrollmentResult Failed(string message) => new()
        {
            ErrorMessage = message
        };
    }

    public sealed class BroadcastCommandResult
    {
        public bool Success { get; init; }
        public string[] ConfirmedDeviceIds { get; init; } = Array.Empty<string>();
        public string[] FailedDeviceIds { get; init; } = Array.Empty<string>();
        public string[] MissingDeviceIds { get; init; } = Array.Empty<string>();
        public string ErrorMessage { get; init; } = "";

        public static BroadcastCommandResult Failed(
            string message, IEnumerable<string>? missing = null) => new()
        {
            ErrorMessage = message,
            MissingDeviceIds = missing?.ToArray() ?? Array.Empty<string>()
        };
    }
}
