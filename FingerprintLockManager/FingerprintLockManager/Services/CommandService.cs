using System.Collections.Concurrent;

namespace FingerprintLockManager
{
    /// <summary>等待柜子 ACK/ERROR 的命令服务，用于需要明确执行结果的操作。</summary>
    public sealed class CommandService
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<CommandResult>> _pending = new();

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
}
