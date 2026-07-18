using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FingerprintLockManager
{
    /// <summary>
    /// Optional message HMAC for sensitive management commands.
    /// Canonical string: cmd|device_id|msg_id|ts|nonce|compact_data
    /// </summary>
    public static class MessageHmac
    {
        private static readonly HashSet<string> SensitiveCommands = new(StringComparer.OrdinalIgnoreCase)
        {
            Protocol.CmdControlLock,
            Protocol.CmdAddFingerprint,
            Protocol.CmdRestoreFingerprint,
            Protocol.CmdDeleteFingerprint,
            Protocol.CmdSdSave,
            Protocol.CmdWriteConfig,
            Protocol.CmdBeginPermissionSync,
            Protocol.CmdSyncPermission,
            Protocol.CmdCommitPermissionSync,
            Protocol.CmdClearPermissions,
            Protocol.CmdSyncPermissions
        };

        public static bool IsSensitive(string? cmd) =>
            !string.IsNullOrEmpty(cmd) && SensitiveCommands.Contains(cmd);

        public static void ApplyIfEnabled(Message message)
        {
            if (message == null) return;
            var cfg = ConfigHelper.Current;
            if (!cfg.HmacEnabled || string.IsNullOrEmpty(cfg.HmacKey)) return;
            if (!IsSensitive(message.Cmd)) return;

            long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            string nonce = Guid.NewGuid().ToString("N").Substring(0, 16);
            string dataCompact = CompactData(message.Data);
            string sig = Sign(cfg.HmacKey, message.Cmd, message.DeviceId ?? "",
                message.MsgId ?? "", ts, nonce, dataCompact);

            message.HmacTs = ts;
            message.HmacNonce = nonce;
            message.HmacSig = sig;
        }

        public static string Sign(string key, string cmd, string deviceId, string msgId,
            long ts, string nonce, string dataCompact)
        {
            string canonical = $"{cmd}|{deviceId}|{msgId}|{ts}|{nonce}|{dataCompact}";
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
            byte[] hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        public static string CompactData(object? data)
        {
            if (data == null) return "{}";
            if (data is string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "{}";
                try { return JToken.Parse(s).ToString(Formatting.None); }
                catch { return s; }
            }
            if (data is JToken token) return token.ToString(Formatting.None);
            return JsonConvert.SerializeObject(data, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore,
                Formatting = Formatting.None
            });
        }
    }
}
