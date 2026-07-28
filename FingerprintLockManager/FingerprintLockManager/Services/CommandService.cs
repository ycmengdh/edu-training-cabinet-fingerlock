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

        /// <summary>
        /// 发送命令并等待 ACK。超时后以相同 msg_id 重试，间隔 250/500/1000ms，最多发送 4 次。
        /// </summary>
        public async Task<CommandResult> SendAsync(
            string deviceId, Message message, int timeoutMs = 5000)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || message == null)
                return CommandResult.Failed("参数无效");

            var tcs = new TaskCompletionSource<CommandResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(message.MsgId, tcs))
                return CommandResult.Failed("消息编号冲突");

            // 同 msg_id 最多发送 4 次；柜机按 msg_id 重放响应，不重复执行业务。
            int[] retryDelaysMs = { 250, 500, 1000 };
            const int maxAttempts = 4;
            int attempt = 0;
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            try
            {
                while (true)
                {
                    attempt++;
                    if (!App.MeshBridge.SendToDevice(deviceId, message))
                    {
                        // 首次发送失败直接返回；后续重试发送失败则继续等 ACK 或下一轮
                        if (attempt == 1)
                        {
                            _pending.TryRemove(message.MsgId, out _);
                            return CommandResult.Failed("设备链路不可用");
                        }
                    }

                    if (tcs.Task.IsCompleted)
                        return await tcs.Task;

                    if (attempt >= maxAttempts)
                    {
                        int remainingMs = (int)Math.Max(0,
                            (deadline - DateTime.UtcNow).TotalMilliseconds);
                        if (remainingMs == 0)
                        {
                            _pending.TryRemove(message.MsgId, out _);
                            if (tcs.Task.IsCompleted) return await tcs.Task;
                            return CommandResult.Failed("等待设备确认超时");
                        }

                        Task lastWait = await Task.WhenAny(tcs.Task, Task.Delay(remainingMs));
                        if (lastWait == tcs.Task) return await tcs.Task;

                        _pending.TryRemove(message.MsgId, out _);
                        if (tcs.Task.IsCompleted) return await tcs.Task;
                        return CommandResult.Failed("等待设备确认超时");
                    }

                    int delayMs = retryDelaysMs[attempt - 1];
                    int untilDeadlineMs = (int)Math.Max(0,
                        (deadline - DateTime.UtcNow).TotalMilliseconds);
                    int waitMs = Math.Min(delayMs, untilDeadlineMs);
                    if (waitMs == 0)
                    {
                        _pending.TryRemove(message.MsgId, out _);
                        if (tcs.Task.IsCompleted) return await tcs.Task;
                        return CommandResult.Failed("等待设备确认超时");
                    }

                    Task completed = await Task.WhenAny(tcs.Task, Task.Delay(waitMs));
                    if (completed == tcs.Task) return await tcs.Task;
                    // 超时未 ACK：保持 pending，同 msg_id 重发
                }
            }
            catch
            {
                _pending.TryRemove(message.MsgId, out _);
                throw;
            }
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

        /// <summary>录入过程进度回调：phase/step/total/hint（UI 可订阅显示放指提示）。</summary>
        public event Action<string, int, int, string>? EnrollProgressChanged;

        public async Task<FingerprintEnrollmentResult> EnrollFingerprintAsync(
            string deviceId, string userId = "", int fingerprintId = 0, bool replace = true,
            int timeoutMs = 180_000,
            Action<string, int, int, string>? onProgress = null)
        {
            // 新流程：fingerprint_id=0 表示由柜子自动分配（录入到临时槽 ID=0，
            // 检测通过后迁移到 allocLocalFpId() 分配的真实 ID 并回报）。
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

            void ProgressHandler(string msgId, string phase, int step, int total, string hint)
            {
                if (!string.Equals(msgId, message.MsgId, StringComparison.Ordinal)) return;
                onProgress?.Invoke(phase, step, total, hint);
                EnrollProgressChanged?.Invoke(phase, step, total, hint);
            }
            App.MessageHandler.OnEnrollProgress += ProgressHandler;

            try
            {
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
            finally
            {
                App.MessageHandler.OnEnrollProgress -= ProgressHandler;
            }
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

        public async Task<CommandResult> StartFingerprintTestAsync(
            string deviceId, int fingerprintId, byte[] templateBytes,
            string testToken, int timeoutMs = 15_000)
        {
            if (fingerprintId <= 0 || templateBytes == null || templateBytes.Length == 0 ||
                string.IsNullOrWhiteSpace(testToken))
            {
                return CommandResult.Failed("指纹测试参数无效");
            }

            var message = Message.Create(Protocol.CmdStartFingerprintTest, deviceId, new
            {
                fingerprint_id = fingerprintId,
                template_hex = Convert.ToHexString(templateBytes),
                test_token = testToken
            });
            return await SendAsync(deviceId, message, timeoutMs);
        }

        public Task<CommandResult> StopFingerprintTestAsync(
            string deviceId, string testToken, int timeoutMs = 8_000)
        {
            var message = Message.Create(Protocol.CmdStopFingerprintTest, deviceId, new
            {
                test_token = testToken ?? ""
            });
            return SendAsync(deviceId, message, timeoutMs);
        }

        public async Task<PermissionProbeResult?> QueryPermissionAsync(
            string deviceId, string userId, int timeoutMs = 8_000)
        {
            var message = Message.Create(Protocol.CmdReadPermissions, deviceId,
                new { user_id = userId });
            var completion = new TaskCompletionSource<PermissionProbeResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(string responseDeviceId, string msgId, PermissionProbeResult result)
            {
                if (string.Equals(msgId, message.MsgId, StringComparison.Ordinal) &&
                    string.Equals(responseDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    completion.TrySetResult(result);
            }
            App.MessageHandler.OnPermissionsResponse += Handler;
            try
            {
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                int[] waits = { 600, 1200, timeoutMs };
                foreach (int requestedWait in waits)
                {
                    if (!App.MeshBridge.SendToDevice(deviceId, message)) return null;
                    int remaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (remaining == 0) break;
                    Task completed = await Task.WhenAny(completion.Task,
                        Task.Delay(Math.Min(requestedWait, remaining)));
                    if (completed == completion.Task) return await completion.Task;
                }
                int finalRemaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                if (finalRemaining > 0)
                {
                    Task completed = await Task.WhenAny(completion.Task, Task.Delay(finalRemaining));
                    if (completed == completion.Task) return await completion.Task;
                }
                return null;
            }
            finally
            {
                App.MessageHandler.OnPermissionsResponse -= Handler;
            }
        }

        public async Task<FingerprintProbeResult?> QueryFingerprintAsync(
            string deviceId, int fingerprintId, byte[] templateBytes,
            int timeoutMs = 12_000)
        {
            uint expectedCrc32 = ComputeTemplateCrc32(templateBytes);
            var message = Message.Create(Protocol.CmdCheckFingerprint, deviceId, new
            {
                fingerprint_id = fingerprintId,
                expected_crc32 = expectedCrc32
            });
            var completion = new TaskCompletionSource<FingerprintProbeResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(string responseDeviceId, string msgId, FingerprintProbeResult result)
            {
                if (string.Equals(msgId, message.MsgId, StringComparison.Ordinal) &&
                    string.Equals(responseDeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
                    completion.TrySetResult(result);
            }
            App.MessageHandler.OnFingerprintCheckResponse += Handler;
            try
            {
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
                int[] waits = { 800, 1600, timeoutMs };
                foreach (int requestedWait in waits)
                {
                    if (!App.MeshBridge.SendToDevice(deviceId, message)) return null;
                    int remaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (remaining == 0) break;
                    Task completed = await Task.WhenAny(completion.Task,
                        Task.Delay(Math.Min(requestedWait, remaining)));
                    if (completed == completion.Task) return await completion.Task;
                }
                int finalRemaining = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                if (finalRemaining > 0)
                {
                    Task completed = await Task.WhenAny(completion.Task, Task.Delay(finalRemaining));
                    if (completed == completion.Task) return await completion.Task;
                }
                return null;
            }
            finally
            {
                App.MessageHandler.OnFingerprintCheckResponse -= Handler;
            }
        }

        public Task<CommandResult> UpsertPermissionAsync(
            string deviceId, User user, bool[] permissions, uint version,
            int timeoutMs = 8_000)
        {
            PermissionPolicy.Enforce(user.Role, permissions);
            var message = Message.Create(Protocol.CmdSyncPermission, deviceId, new
            {
                fingerprint_id = user.FingerprintId,
                user_id = user.UserId,
                name = user.Name,
                role = user.Role switch { "admin" => 0, "teacher" => 1, _ => 2 },
                lock_permissions = new
                {
                    lock_0 = permissions.ElementAtOrDefault(0),
                    lock_1 = permissions.ElementAtOrDefault(1),
                    lock_2 = permissions.ElementAtOrDefault(2),
                    lock_3 = permissions.ElementAtOrDefault(3)
                },
                version
            });
            return SendAsync(deviceId, message, timeoutMs);
        }

        public static uint ComputeTemplateCrc32(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return 0;
            uint crc = 0xFFFFFFFFU;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++)
                    crc = (crc >> 1) ^ ((crc & 1U) != 0 ? 0xEDB88320U : 0U);
            }
            return crc ^ 0xFFFFFFFFU;
        }

        /// <summary>
        /// V2.7：录入设备专属副指纹（仅本机生效，不上报 SD 卡）。
        /// 复用 ADD_FINGERPRINT_RESULT 结果通道（固件在主/副录入完成时都发此命令）。
        /// 录入完成后由调用方决定：覆盖全局主指纹 或 仅作为本机备用。
        /// </summary>
        public async Task<FingerprintEnrollmentResult> EnrollBackupFingerprintAsync(
            string deviceId, string userId,
            int timeoutMs = 180_000,
            Action<string, int, int, string>? onProgress = null)
        {
            var message = Message.Create(Protocol.CmdAddBackupFingerprint, deviceId, new
            {
                user_id = userId
            });
            var completion = new TaskCompletionSource<FingerprintEnrollmentResult>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pendingEnrollments.TryAdd(message.MsgId, completion))
                return FingerprintEnrollmentResult.Failed("消息编号冲突");

            void ProgressHandler(string msgId, string phase, int step, int total, string hint)
            {
                if (!string.Equals(msgId, message.MsgId, StringComparison.Ordinal)) return;
                onProgress?.Invoke(phase, step, total, hint);
                EnrollProgressChanged?.Invoke(phase, step, total, hint);
            }
            App.MessageHandler.OnEnrollProgress += ProgressHandler;

            try
            {
                CommandResult accepted = await SendAsync(deviceId, message);
                if (!accepted.Success)
                {
                    _pendingEnrollments.TryRemove(message.MsgId, out _);
                    return FingerprintEnrollmentResult.Failed(accepted.ErrorMessage);
                }

                Task completed = await Task.WhenAny(completion.Task, Task.Delay(timeoutMs));
                if (completed == completion.Task) return await completion.Task;

                _pendingEnrollments.TryRemove(message.MsgId, out _);
                return FingerprintEnrollmentResult.Failed("等待副指纹录入结果超时");
            }
            finally
            {
                App.MessageHandler.OnEnrollProgress -= ProgressHandler;
            }
        }

        /// <summary>V2.7：删除指定柜子上的本机副指纹。</summary>
        public async Task<CommandResult> DeleteBackupFingerprintAsync(
            string deviceId, string userId, int timeoutMs = 10_000)
        {
            var message = Message.Create(Protocol.CmdDeleteBackupFingerprint, deviceId, new
            {
                user_id = userId
            });
            return await SendAsync(deviceId, message, timeoutMs);
        }

        /// <summary>
        /// V2.7：请求指定柜子的本机副指纹清单。
        /// 返回原始 JSON 字符串（含 count + backups 数组），由调用方解析。
        /// </summary>
        public async Task<string?> GetBackupFingerprintListAsync(
            string deviceId, int timeoutMs = 8_000)
        {
            var message = Message.Create(Protocol.CmdBackupFpListRequest, deviceId);
            var completion = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            void ListHandler(string did, string json)
            {
                if (string.Equals(did, deviceId, StringComparison.OrdinalIgnoreCase))
                {
                    completion.TrySetResult(json);
                }
            }
            App.MessageHandler.OnBackupFpList += ListHandler;
            try
            {
                CommandResult accepted = await SendAsync(deviceId, message, timeoutMs);
                if (!accepted.Success)
                {
                    return null;
                }
                Task completed = await Task.WhenAny(completion.Task, Task.Delay(timeoutMs));
                return completed == completion.Task ? await completion.Task : null;
            }
            finally
            {
                App.MessageHandler.OnBackupFpList -= ListHandler;
            }
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

            // DeviceId empty → mesh broadcast; non-empty → unicast (paced per-cabinet sync).
            bool sent = string.IsNullOrWhiteSpace(message.DeviceId)
                ? App.MeshBridge.Broadcast(message)
                : App.MeshBridge.SendToDevice(message.DeviceId, message);
            if (!sent)
            {
                _pendingBroadcasts.TryRemove(message.MsgId, out _);
                return BroadcastCommandResult.Failed(
                    string.IsNullOrWhiteSpace(message.DeviceId) ? "广播链路不可用" : "单柜发送失败",
                    expected);
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

    public sealed class FingerprintTestEvent
    {
        public string DeviceId { get; init; } = "";
        public string Event { get; init; } = "";
        public string TestToken { get; init; } = "";
        public int FingerprintId { get; init; } = -1;
        public int Confidence { get; init; }
        public int IdleTimeoutSeconds { get; init; } = 60;
    }

    public sealed class PermissionProbeResult
    {
        public bool Found { get; init; }
        public string UserId { get; init; } = "";
        public int FingerprintId { get; init; } = -1;
        public int Role { get; init; } = 2;
        public bool[] Permissions { get; init; } = new bool[4];
        public uint Version { get; init; }
    }

    public sealed class FingerprintProbeResult
    {
        public int FingerprintId { get; init; } = -1;
        public bool Exists { get; init; }
        public bool Readable { get; init; }
        public bool Matches { get; init; }
        public uint ExpectedCrc32 { get; init; }
        public uint ActualCrc32 { get; init; }
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

        public static BroadcastCommandResult Succeeded(IEnumerable<string> confirmed) => new()
        {
            Success = true,
            ConfirmedDeviceIds = confirmed.ToArray(),
        };
    }
}
