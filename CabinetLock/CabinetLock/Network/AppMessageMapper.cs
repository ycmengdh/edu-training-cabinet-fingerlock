using System.Text;
using Newtonsoft.Json.Linq;

namespace CabinetLock
{
    /// <summary>
    /// Message ↔ AppMessage 映射。
    /// 统一协议模型：
    ///   传输层 A5/5A 帧 → 应用层 B1/0F 二进制信封(cmd_id/msg_id/device_id) → payload。
    /// 简单命令（CONTROL_LOCK/ACK/ERROR/HEARTBEAT）用定长二进制 payload；
    /// 复杂命令通常使用 UTF-8 JSON；SD_SNAPSHOT_* 使用原始二进制负载。
    /// 不再使用“整包 JSON 消息”作为主路径。
    /// </summary>
    public static class AppMessageMapper
    {
        public static ushort SessionId { get; } = CreateSessionId();

        private static readonly HashSet<ushort> NeedsAckCmds = new()
        {
            CmdIds.ControlLock,
            CmdIds.AddFingerprint,
            CmdIds.CancelEnroll,
            CmdIds.StartFingerprintTest,
            CmdIds.StopFingerprintTest,
            CmdIds.DeleteFingerprint,
            CmdIds.RestoreFingerprint,
            CmdIds.DeleteAllFingerprints,
            // V2.7 副指纹命令需要 ACK
            CmdIds.AddBackupFingerprint,
            CmdIds.DeleteBackupFingerprint,
            CmdIds.BackupFpListRequest,
            CmdIds.BeginPermissionSync,
            CmdIds.SyncPermission,
            CmdIds.CommitPermissionSync,
            CmdIds.ClearPermissions,
            CmdIds.DeleteUserPermission,
            CmdIds.SyncPermissions,
            CmdIds.WriteConfig,
            CmdIds.Reboot,
            CmdIds.SdSave,
            CmdIds.UploadFpTemplate,
            CmdIds.DownloadFpTemplate,
            CmdIds.DeleteFpTemplate,
            CmdIds.ReadConfig,
            CmdIds.ReadStatus,
            CmdIds.FingerprintListRequest,
            CmdIds.SdQuery,
            CmdIds.SdQueryVersion,
            CmdIds.SdSnapshotManifest,
            CmdIds.SdSnapshotBegin,
            CmdIds.SdSnapshotCommit,
            CmdIds.SdSnapshotDownload,
            CmdIds.CabinetOtaBegin,
            CmdIds.CabinetOtaChunk,
            CmdIds.CabinetOtaCommit,
            CmdIds.CabinetOtaStart,
            CmdIds.CabinetOtaStatus,
            CmdIds.CabinetOtaNodes,
        };

        public static AppMessage ToApp(Message msg)
        {
            if (msg == null) throw new ArgumentNullException(nameof(msg));

            ushort cmdId = CmdIds.ToCmdId(msg.Cmd) ?? 0;
            if (cmdId == 0 && !string.IsNullOrEmpty(msg.Cmd))
                throw new ArgumentException($"未知命令: {msg.Cmd}", nameof(msg));
            ushort msgId = ParseMsgId(msg.MsgId);
            var flags = AppMessageFlags.None;
            if (NeedsAckCmds.Contains(cmdId)) flags |= AppMessageFlags.NeedsAck;
            if (string.IsNullOrEmpty(msg.DeviceId)) flags |= AppMessageFlags.Broadcast;
            if (cmdId == CmdIds.Ack) flags |= AppMessageFlags.IsAck;
            if (cmdId == CmdIds.Error) flags |= AppMessageFlags.IsError;

            return new AppMessage
            {
                Flags = flags,
                CmdId = cmdId,
                MsgId = msgId,
                CorrId = msg.CorrId == 0 ? SessionId : msg.CorrId,
                DeviceId = msg.DeviceId ?? "",
                SourceDeviceId = msg.SourceDeviceId ?? "",
                TimestampUnix = (uint)DateTimeOffset.Now.ToUnixTimeSeconds(),
                Payload = BuildPayload(cmdId, msg.Data),
            };
        }

        public static Message ToMessage(AppMessage app)
        {
            if (app == null) throw new ArgumentNullException(nameof(app));

            string cmd = CmdIds.ToCmdName(app.CmdId) ?? $"CMD_0x{app.CmdId:X4}";
            string deviceId = (app.DeviceId ?? "").Trim();
            string sourceId = (app.SourceDeviceId ?? "").Trim();
            // 固件多数上行不填 source_id；在线判定必须有非空来源 ID
            if (string.IsNullOrEmpty(sourceId)) sourceId = deviceId;

            // 二进制信封 device_id 为空时，尝试从 JSON data 回填（兼容异常包）
            object? data = UnpackPayload(app.CmdId, app.Payload);
            if (string.IsNullOrEmpty(deviceId) && data is Newtonsoft.Json.Linq.JObject jo)
            {
                string? fromData = jo["device_id"]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(fromData))
                {
                    deviceId = fromData;
                    if (string.IsNullOrEmpty(sourceId)) sourceId = fromData;
                }
            }

            return new Message
            {
                MsgId = app.MsgId == 0 ? "" : app.MsgId.ToString(),
                CorrId = app.CorrId,
                Cmd = cmd,
                DeviceId = deviceId,
                SourceDeviceId = sourceId,
                Data = data,
                Timestamp = app.TimestampUnix == 0
                    ? DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    : DateTimeOffset.FromUnixTimeSeconds(app.TimestampUnix).LocalDateTime
                        .ToString("yyyy-MM-dd HH:mm:ss"),
            };
        }

        public static ushort ParseMsgId(string? msgId)
        {
            if (string.IsNullOrEmpty(msgId)) return 0;
            if (ushort.TryParse(msgId, out ushort n) && n != 0) return n;
            int h = msgId.GetHashCode();
            ushort v = (ushort)(h & 0xFFFF);
            return v == 0 ? (ushort)1 : v;
        }

        private static ushort CreateSessionId()
        {
            ushort sessionId;
            do
            {
                sessionId = (ushort)System.Security.Cryptography.RandomNumberGenerator.GetInt32(1, 65536);
            } while (sessionId == 0);
            return sessionId;
        }

        private static byte[] BuildPayload(ushort cmdId, object? data)
        {
            if (IsSnapshotCommand(cmdId) && data is byte[] raw)
                return raw;

            switch (cmdId)
            {
                case CmdIds.ControlLock:
                {
                    var jo = AsJObject(data);
                    byte lockId = (byte)(jo?["lock_id"]?.Value<int>() ?? 0);
                    string action = jo?["action"]?.Value<string>() ?? "open";
                    byte act = action.Equals("close", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0;
                    return BinaryMessageCodec.ControlLockPayload.Pack(lockId, act);
                }
                case CmdIds.TimeSync:
                {
                    var jo = AsJObject(data);
                    uint ts = jo?["timestamp"]?.Value<uint>()
                              ?? (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                    // 4-byte LE timestamp (no dedicated packer class required)
                    byte[] buf = new byte[4];
                    buf[0] = (byte)(ts & 0xFF);
                    buf[1] = (byte)((ts >> 8) & 0xFF);
                    buf[2] = (byte)((ts >> 16) & 0xFF);
                    buf[3] = (byte)((ts >> 24) & 0xFF);
                    return buf;
                }
                case CmdIds.Ack:
                {
                    var jo = AsJObject(data);
                    ushort refId = (ushort)(jo?["ref_msg_id"]?.Value<int>() ?? 0);
                    string tag = jo?["result"]?.Value<string>() ?? "ok";
                    return BinaryMessageCodec.AckPayload.Pack(refId, 0, tag);
                }
                case CmdIds.Error:
                {
                    var jo = AsJObject(data);
                    ushort refId = (ushort)(jo?["ref_msg_id"]?.Value<int>() ?? 0);
                    ushort code = (ushort)(jo?["error_code"]?.Value<int>() ?? 0);
                    string message = jo?["message"]?.Value<string>() ?? "";
                    return BinaryMessageCodec.ErrorPayload.Pack(refId, code, message);
                }
                case CmdIds.Heartbeat:
                case CmdIds.HeartbeatAck:
                    return Array.Empty<byte>();
                default:
                {
                    if (data == null) return Encoding.UTF8.GetBytes("{}");
                    if (data is string s)
                    {
                        if (string.IsNullOrWhiteSpace(s)) return Encoding.UTF8.GetBytes("{}");
                        return Encoding.UTF8.GetBytes(s);
                    }
                    string json = JsonHelper.Serialize(data);
                    if (string.IsNullOrEmpty(json)) json = "{}";
                    return Encoding.UTF8.GetBytes(json);
                }
            }
        }

        private static object? UnpackPayload(ushort cmdId, byte[]? payload)
        {
            payload ??= Array.Empty<byte>();
            if (IsSnapshotCommand(cmdId))
                return payload;

            switch (cmdId)
            {
                case CmdIds.ControlLock:
                    if (BinaryMessageCodec.ControlLockPayload.TryUnpack(payload, out byte lockId, out byte action))
                    {
                        return new JObject
                        {
                            ["lock_id"] = lockId,
                            ["action"] = action == 1 ? "close" : "open",
                        };
                    }
                    break;
                case CmdIds.TimeSync:
                    if (payload.Length >= 4)
                    {
                        uint ts = (uint)(payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24));
                        return new JObject { ["timestamp"] = ts };
                    }
                    break;
                case CmdIds.Ack:
                    if (BinaryMessageCodec.AckPayload.TryUnpack(payload, out ushort refId, out _, out string tag))
                    {
                        return new JObject
                        {
                            ["result"] = string.IsNullOrEmpty(tag) ? "ok" : tag,
                            ["ref_msg_id"] = refId,
                        };
                    }
                    return new JObject { ["result"] = "ok" };
                case CmdIds.Error:
                    if (BinaryMessageCodec.ErrorPayload.TryUnpack(payload, out ushort eref, out ushort ecode, out string emsg))
                    {
                        return new JObject
                        {
                            ["error_code"] = ecode,
                            ["message"] = emsg ?? "",
                            ["ref_msg_id"] = eref,
                        };
                    }
                    break;
                case CmdIds.StatusResponse:
                case CmdIds.StatusReport:
                    if (BinaryMessageCodec.CabinetStatusPayload.TryUnpack(payload, out var status) &&
                        status != null)
                    {
                        return new JObject
                        {
                            ["uptime"] = status.Uptime,
                            ["lock_status"] = new JArray(
                                (status.LockMask & 0x01) != 0 ? 1 : 0,
                                (status.LockMask & 0x02) != 0 ? 1 : 0,
                                (status.LockMask & 0x04) != 0 ? 1 : 0,
                                (status.LockMask & 0x08) != 0 ? 1 : 0),
                            ["fingerprint_count"] = status.FingerprintCount,
                            ["perm_count"] = status.PermissionCount,
                            ["perm_version"] = status.PermissionVersion,
                            ["mesh_layer"] = status.MeshLayer,
                            ["mesh_send_failures"] = status.SendFailures,
                            ["mesh_queue_full"] = status.QueueFull,
                            ["mesh_link_rssi"] = status.Rssi,
                            ["mesh_assoc_expire"] = status.AssocExpire,
                            ["fp_poll_max_ms"] = status.FingerprintPollMaxMs,
                            ["work_mode"] = (status.Flags & 0x02) != 0 ? "mesh" : "debug",
                            ["time_synced"] = (status.Flags & 0x01) != 0,
                            ["fingerprint_ready"] = (status.Flags & 0x04) != 0,
                        };
                    }
                    break;
                case CmdIds.Heartbeat:
                case CmdIds.HeartbeatAck:
                    if (BinaryMessageCodec.HeartbeatPayload.TryUnpack(payload,
                            out uint freeHeap, out uint freePsram, out ushort minFree,
                            out byte layer, out byte topology, out ushort sendFail, out ushort qFull, out ushort recoveries))
                    {
                        return new JObject
                        {
                            ["free_heap"] = freeHeap,
                            ["free_psram"] = freePsram,
                            ["min_free_heap"] = minFree,
                            ["mesh_layer"] = layer,
                            ["mesh_node_type"] = (topology >> 3) & 0x07,
                            ["child_count"] = topology & 0x07,
                            ["mesh_send_failures"] = sendFail,
                            ["mesh_queue_full"] = qFull,
                            ["mesh_recoveries"] = recoveries,
                        };
                    }
                    return new JObject();
            }

            if (payload.Length == 0) return new JObject();
            string text = Encoding.UTF8.GetString(payload);
            if (string.IsNullOrWhiteSpace(text)) return new JObject();
            if (text[0] == '{' || text[0] == '[')
            {
                try { return JsonHelper.Deserialize<JToken>(text); }
                catch { /* fall through */ }
            }
            return new JObject { ["raw"] = text };
        }

        private static JObject? AsJObject(object? data)
        {
            if (data == null) return null;
            if (data is JObject jo) return jo;
            if (data is JToken jt) return jt as JObject ?? JObject.FromObject(jt);
            try { return JObject.FromObject(data); }
            catch { return null; }
        }

        private static bool IsSnapshotCommand(ushort cmdId) =>
            cmdId >= CmdIds.SdSnapshotManifest &&
            cmdId <= CmdIds.SdSnapshotDownloadPart;
    }
}
