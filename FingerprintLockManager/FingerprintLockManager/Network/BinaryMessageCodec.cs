using System.Buffers.Binary;
using System.Text;

namespace FingerprintLockManager
{
    /// <summary>
    /// 应用层二进制消息标志位（信封 flags 字段）。
    /// </summary>
    [Flags]
    public enum AppMessageFlags : byte
    {
        None = 0,
        NeedsAck = 1 << 0,
        IsAck = 1 << 1,
        IsError = 1 << 2,
        HasHmac = 1 << 3,
        MultiPart = 1 << 4,
        Broadcast = 1 << 7,
    }

    /// <summary>
    /// 应用层二进制消息（外层 0xA5 0x5A 帧内部的负载）。
    /// 多字节整数均为小端。
    /// </summary>
    public sealed class AppMessage
    {
        public AppMessageFlags Flags { get; set; }
        public ushort CmdId { get; set; }
        public ushort MsgId { get; set; }
        public ushort CorrId { get; set; }
        public string DeviceId { get; set; } = "";
        public string SourceDeviceId { get; set; } = "";
        public uint TimestampUnix { get; set; }
        public byte[] Payload { get; set; } = Array.Empty<byte>();

        /// <summary>HMAC 秒级时间戳（仅 HasHmac 时有效）</summary>
        public uint? HmacTs { get; set; }

        /// <summary>HMAC 随机数 8 字节（仅 HasHmac 时有效）</summary>
        public byte[]? HmacNonce { get; set; }

        /// <summary>HMAC-SHA256 原始 32 字节签名（仅 HasHmac 时有效）</summary>
        public byte[]? HmacSig { get; set; }

        public bool NeedsAck => (Flags & AppMessageFlags.NeedsAck) != 0;
        public bool IsAck => (Flags & AppMessageFlags.IsAck) != 0;
        public bool IsError => (Flags & AppMessageFlags.IsError) != 0;
        public bool HasHmac => (Flags & AppMessageFlags.HasHmac) != 0;
        public bool IsBroadcast => (Flags & AppMessageFlags.Broadcast) != 0;
    }

    /// <summary>
    /// 应用层二进制消息编解码器。
    /// 信封格式见项目二进制协议方案；magic = LE uint16 0x0FB1（线上字节 B1 0F）。
    /// </summary>
    public static class BinaryMessageCodec
    {
        /// <summary>应用消息魔数（小端 0x0FB1，首字节 0xB1）</summary>
        public const ushort AppMagic = 0x0FB1;
        public const byte AppMagicLo = 0xB1; // 线上第 0 字节
        public const byte AppMagicHi = 0x0F; // 线上第 1 字节

        public const byte ProtoVer = 0x01;
        public const int HeaderSize = 18;
        public const int HmacBlockSize = 4 + 8 + 32; // ts + nonce + sig
        public const int MaxIdLen = 24;
        public const int MaxPayloadLen = 65535;

        /// <summary>将应用消息编码为字节数组（不含外层 A5 帧）。</summary>
        public static byte[] Encode(AppMessage message)
        {
            if (message == null) throw new ArgumentNullException(nameof(message));

            byte[] deviceId = Encoding.ASCII.GetBytes(message.DeviceId ?? "");
            byte[] sourceId = Encoding.ASCII.GetBytes(message.SourceDeviceId ?? "");
            if (deviceId.Length > MaxIdLen)
                throw new ArgumentException($"device_id 超过 {MaxIdLen} 字节", nameof(message));
            if (sourceId.Length > MaxIdLen)
                throw new ArgumentException($"source_id 超过 {MaxIdLen} 字节", nameof(message));

            byte[] payload = message.Payload ?? Array.Empty<byte>();
            if (payload.Length > MaxPayloadLen)
                throw new ArgumentException("payload 过长", nameof(message));

            bool hasHmac = (message.Flags & AppMessageFlags.HasHmac) != 0;
            if (hasHmac)
            {
                if (message.HmacNonce == null || message.HmacNonce.Length != 8)
                    throw new ArgumentException("HasHmac 时 hmac_nonce 必须为 8 字节", nameof(message));
                if (message.HmacSig == null || message.HmacSig.Length != 32)
                    throw new ArgumentException("HasHmac 时 hmac_sig 必须为 32 字节", nameof(message));
            }

            int hmacSize = hasHmac ? HmacBlockSize : 0;
            int total = HeaderSize + hmacSize + deviceId.Length + sourceId.Length + payload.Length;
            byte[] buffer = new byte[total];
            Span<byte> span = buffer;

            span[0] = AppMagicLo;
            span[1] = AppMagicHi;
            span[2] = ProtoVer;
            span[3] = (byte)message.Flags;
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4, 2), message.CmdId);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), message.MsgId);
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(8, 2), message.CorrId);
            span[10] = (byte)deviceId.Length;
            span[11] = (byte)sourceId.Length;
            BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(12, 2), (ushort)payload.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(14, 4), message.TimestampUnix);

            int offset = HeaderSize;
            if (hasHmac)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(offset, 4), message.HmacTs ?? 0);
                offset += 4;
                message.HmacNonce.CopyTo(span.Slice(offset, 8));
                offset += 8;
                message.HmacSig.CopyTo(span.Slice(offset, 32));
                offset += 32;
            }

            if (deviceId.Length > 0)
            {
                deviceId.CopyTo(span.Slice(offset, deviceId.Length));
                offset += deviceId.Length;
            }

            if (sourceId.Length > 0)
            {
                sourceId.CopyTo(span.Slice(offset, sourceId.Length));
                offset += sourceId.Length;
            }

            if (payload.Length > 0)
                payload.CopyTo(span.Slice(offset, payload.Length));

            return buffer;
        }

        /// <summary>尝试解码应用消息；魔数/长度/边界非法时返回 false。</summary>
        public static bool TryDecode(byte[]? data, out AppMessage? msg)
        {
            msg = null;
            if (data == null || data.Length < HeaderSize) return false;
            return TryDecode(data.AsSpan(), out msg);
        }

        /// <summary>尝试解码应用消息（Span 重载）。</summary>
        public static bool TryDecode(ReadOnlySpan<byte> data, out AppMessage? msg)
        {
            msg = null;
            if (data.Length < HeaderSize) return false;

            if (data[0] != AppMagicLo || data[1] != AppMagicHi) return false;
            if (data[2] != ProtoVer) return false;

            var flags = (AppMessageFlags)data[3];
            ushort cmdId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2));
            ushort msgId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
            ushort corrId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));
            int deviceIdLen = data[10];
            int sourceIdLen = data[11];
            int payloadLen = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(12, 2));
            uint timestamp = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(14, 4));

            if (deviceIdLen > MaxIdLen || sourceIdLen > MaxIdLen) return false;

            bool hasHmac = (flags & AppMessageFlags.HasHmac) != 0;
            int hmacSize = hasHmac ? HmacBlockSize : 0;
            int needed = HeaderSize + hmacSize + deviceIdLen + sourceIdLen + payloadLen;
            if (data.Length < needed) return false;

            int offset = HeaderSize;
            uint? hmacTs = null;
            byte[]? hmacNonce = null;
            byte[]? hmacSig = null;
            if (hasHmac)
            {
                hmacTs = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
                offset += 4;
                hmacNonce = data.Slice(offset, 8).ToArray();
                offset += 8;
                hmacSig = data.Slice(offset, 32).ToArray();
                offset += 32;
            }

            string deviceId = deviceIdLen == 0
                ? ""
                : Encoding.ASCII.GetString(data.Slice(offset, deviceIdLen));
            offset += deviceIdLen;

            string sourceId = sourceIdLen == 0
                ? ""
                : Encoding.ASCII.GetString(data.Slice(offset, sourceIdLen));
            offset += sourceIdLen;

            byte[] payload = payloadLen == 0
                ? Array.Empty<byte>()
                : data.Slice(offset, payloadLen).ToArray();

            msg = new AppMessage
            {
                Flags = flags,
                CmdId = cmdId,
                MsgId = msgId,
                CorrId = corrId,
                DeviceId = deviceId,
                SourceDeviceId = sourceId,
                TimestampUnix = timestamp,
                Payload = payload,
                HmacTs = hmacTs,
                HmacNonce = hmacNonce,
                HmacSig = hmacSig,
            };
            return true;
        }

        // ===== 常用负载编解码 =====

        /// <summary>心跳负载（18 字节定长）。</summary>
        public static class HeartbeatPayload
        {
            public const int Size = 18;

            public static byte[] Pack(
                uint freeHeap, uint freePsram, ushort minFreeHeap,
                byte meshLayer, byte flags,
                ushort sendFail, ushort queueFull, ushort recoveries)
            {
                byte[] buf = new byte[Size];
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(0, 4), freeHeap);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), freePsram);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(8, 2), minFreeHeap);
                buf[10] = meshLayer;
                buf[11] = flags;
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(12, 2), sendFail);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(14, 2), queueFull);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(16, 2), recoveries);
                return buf;
            }

            public static bool TryUnpack(
                ReadOnlySpan<byte> data,
                out uint freeHeap, out uint freePsram, out ushort minFreeHeap,
                out byte meshLayer, out byte flags,
                out ushort sendFail, out ushort queueFull, out ushort recoveries)
            {
                freeHeap = freePsram = 0;
                minFreeHeap = sendFail = queueFull = recoveries = 0;
                meshLayer = flags = 0;
                if (data.Length < Size) return false;

                freeHeap = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0, 4));
                freePsram = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
                minFreeHeap = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));
                meshLayer = data[10];
                flags = data[11];
                sendFail = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(12, 2));
                queueFull = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(14, 2));
                recoveries = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(16, 2));
                return true;
            }
        }

        /// <summary>ACK 负载：ref_msg_id u16 + result_code u16 + result_tag（u8 len + 字节，最长 64，与固件 packAck 对齐）。</summary>
        public static class AckPayload
        {
            public const int MaxTagLen = 64;

            public static byte[] Pack(ushort refMsgId, ushort resultCode, string? resultTag = null)
            {
                byte[] tag = EncodeBoundedString(resultTag, MaxTagLen);
                byte[] buf = new byte[4 + 1 + tag.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), refMsgId);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2, 2), resultCode);
                buf[4] = (byte)tag.Length;
                if (tag.Length > 0) tag.CopyTo(buf.AsSpan(5));
                return buf;
            }

            public static bool TryUnpack(
                ReadOnlySpan<byte> data,
                out ushort refMsgId, out ushort resultCode, out string resultTag)
            {
                refMsgId = resultCode = 0;
                resultTag = "";
                if (data.Length < 5) return false;

                refMsgId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0, 2));
                resultCode = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2, 2));
                int tagLen = data[4];
                if (tagLen > MaxTagLen || data.Length < 5 + tagLen) return false;
                resultTag = tagLen == 0 ? "" : Encoding.UTF8.GetString(data.Slice(5, tagLen));
                return true;
            }
        }

        /// <summary>ERROR 负载：ref_msg_id u16 + error_code u16 + message（u8 len + 字节，最长 64）。</summary>
        public static class ErrorPayload
        {
            public const int MaxMessageLen = 64;

            public static byte[] Pack(ushort refMsgId, ushort errorCode, string? message = null)
            {
                byte[] text = EncodeBoundedString(message, MaxMessageLen);
                byte[] buf = new byte[4 + 1 + text.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), refMsgId);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2, 2), errorCode);
                buf[4] = (byte)text.Length;
                if (text.Length > 0) text.CopyTo(buf.AsSpan(5));
                return buf;
            }

            public static bool TryUnpack(
                ReadOnlySpan<byte> data,
                out ushort refMsgId, out ushort errorCode, out string message)
            {
                refMsgId = errorCode = 0;
                message = "";
                if (data.Length < 5) return false;

                refMsgId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0, 2));
                errorCode = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2, 2));
                int len = data[4];
                if (len > MaxMessageLen || data.Length < 5 + len) return false;
                message = len == 0 ? "" : Encoding.UTF8.GetString(data.Slice(5, len));
                return true;
            }
        }

        /// <summary>开锁负载：lock_id u8 + action u8。</summary>
        public static class ControlLockPayload
        {
            public const int Size = 2;

            public static byte[] Pack(byte lockId, byte action) => new[] { lockId, action };

            public static bool TryUnpack(ReadOnlySpan<byte> data, out byte lockId, out byte action)
            {
                lockId = action = 0;
                if (data.Length < Size) return false;
                lockId = data[0];
                action = data[1];
                return true;
            }
        }

        /// <summary>
        /// 权限同步行负载：
        /// version u32, total u16, sequence u16, fingerprint_id u16, role u8, lock_mask u8,
        /// expire_days u32, name (u8 len + utf8), user_id (u8 len + utf8)。
        /// </summary>
        public static class SyncPermissionPayload
        {
            public const int FixedSize = 4 + 2 + 2 + 2 + 1 + 1 + 4; // 16
            public const int MaxNameLen = 32;
            public const int MaxUserIdLen = 32;

            public static byte[] Pack(
                uint version, ushort total, ushort sequence,
                ushort fingerprintId, byte role, byte lockMask, uint expireDays,
                string? name, string? userId)
            {
                byte[] nameBytes = EncodeBoundedString(name, MaxNameLen);
                byte[] userBytes = EncodeBoundedString(userId, MaxUserIdLen);
                byte[] buf = new byte[FixedSize + 1 + nameBytes.Length + 1 + userBytes.Length];
                Span<byte> span = buf;

                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(0, 4), version);
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(4, 2), total);
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(6, 2), sequence);
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(8, 2), fingerprintId);
                span[10] = role;
                span[11] = lockMask;
                BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(12, 4), expireDays);

                int offset = FixedSize;
                span[offset++] = (byte)nameBytes.Length;
                if (nameBytes.Length > 0)
                {
                    nameBytes.CopyTo(span.Slice(offset, nameBytes.Length));
                    offset += nameBytes.Length;
                }

                span[offset++] = (byte)userBytes.Length;
                if (userBytes.Length > 0)
                    userBytes.CopyTo(span.Slice(offset, userBytes.Length));

                return buf;
            }

            public static bool TryUnpack(
                ReadOnlySpan<byte> data,
                out uint version, out ushort total, out ushort sequence,
                out ushort fingerprintId, out byte role, out byte lockMask, out uint expireDays,
                out string name, out string userId)
            {
                version = expireDays = 0;
                total = sequence = fingerprintId = 0;
                role = lockMask = 0;
                name = userId = "";
                if (data.Length < FixedSize + 2) return false;

                version = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0, 4));
                total = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4, 2));
                sequence = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6, 2));
                fingerprintId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(8, 2));
                role = data[10];
                lockMask = data[11];
                expireDays = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(12, 4));

                int offset = FixedSize;
                int nameLen = data[offset++];
                if (nameLen > MaxNameLen || data.Length < offset + nameLen + 1) return false;
                name = nameLen == 0 ? "" : Encoding.UTF8.GetString(data.Slice(offset, nameLen));
                offset += nameLen;

                int userLen = data[offset++];
                if (userLen > MaxUserIdLen || data.Length < offset + userLen) return false;
                userId = userLen == 0 ? "" : Encoding.UTF8.GetString(data.Slice(offset, userLen));
                return true;
            }
        }

        /// <summary>
        /// 指纹模板负载：fingerprint_id u16 + flags u8 + user_id (u8 len + utf8) + template raw。
        /// </summary>
        public static class TemplatePayload
        {
            public const int MaxUserIdLen = 32;

            public static byte[] Pack(ushort fingerprintId, byte flags, string? userId, byte[]? template)
            {
                byte[] userBytes = EncodeBoundedString(userId, MaxUserIdLen);
                byte[] tmpl = template ?? Array.Empty<byte>();
                byte[] buf = new byte[2 + 1 + 1 + userBytes.Length + tmpl.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), fingerprintId);
                buf[2] = flags;
                buf[3] = (byte)userBytes.Length;
                int offset = 4;
                if (userBytes.Length > 0)
                {
                    userBytes.CopyTo(buf.AsSpan(offset));
                    offset += userBytes.Length;
                }

                if (tmpl.Length > 0)
                    tmpl.CopyTo(buf.AsSpan(offset));
                return buf;
            }

            public static bool TryUnpack(
                ReadOnlySpan<byte> data,
                out ushort fingerprintId, out byte flags, out string userId, out byte[] template)
            {
                fingerprintId = 0;
                flags = 0;
                userId = "";
                template = Array.Empty<byte>();
                if (data.Length < 4) return false;

                fingerprintId = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0, 2));
                flags = data[2];
                int userLen = data[3];
                if (userLen > MaxUserIdLen || data.Length < 4 + userLen) return false;
                userId = userLen == 0 ? "" : Encoding.UTF8.GetString(data.Slice(4, userLen));
                int tmplOffset = 4 + userLen;
                int tmplLen = data.Length - tmplOffset;
                template = tmplLen == 0 ? Array.Empty<byte>() : data.Slice(tmplOffset, tmplLen).ToArray();
                return true;
            }
        }

        /// <summary>
        /// SD 分片负载：part_index u16 + part_total u16 + table_version u32 + table_id u8 + chunk。
        /// </summary>
        public static class SdPartPayload
        {
            public const int HeaderSize = 2 + 2 + 4 + 1; // 9

            public static byte[] Pack(
                ushort partIndex, ushort partTotal, uint tableVersion, byte tableId, byte[]? chunk)
            {
                byte[] data = chunk ?? Array.Empty<byte>();
                byte[] buf = new byte[HeaderSize + data.Length];
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), partIndex);
                BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2, 2), partTotal);
                BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), tableVersion);
                buf[8] = tableId;
                if (data.Length > 0) data.CopyTo(buf.AsSpan(HeaderSize));
                return buf;
            }

            public static bool TryUnpack(
                ReadOnlySpan<byte> data,
                out ushort partIndex, out ushort partTotal, out uint tableVersion,
                out byte tableId, out byte[] chunk)
            {
                partIndex = partTotal = 0;
                tableVersion = 0;
                tableId = 0;
                chunk = Array.Empty<byte>();
                if (data.Length < HeaderSize) return false;

                partIndex = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(0, 2));
                partTotal = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(2, 2));
                tableVersion = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(4, 4));
                tableId = data[8];
                int chunkLen = data.Length - HeaderSize;
                chunk = chunkLen == 0 ? Array.Empty<byte>() : data.Slice(HeaderSize, chunkLen).ToArray();
                return true;
            }
        }

        /// <summary>SD 查询负载：table_id u8。</summary>
        public static class SdQueryPayload
        {
            public static byte[] Pack(byte tableId) => new[] { tableId };

            public static bool TryUnpack(ReadOnlySpan<byte> data, out byte tableId)
            {
                tableId = 0;
                if (data.Length < 1) return false;
                tableId = data[0];
                return true;
            }
        }

        /// <summary>时间同步负载：timestamp u32。</summary>
        public static class TimeSyncPayload
        {
            public static byte[] Pack(uint timestamp)
            {
                byte[] buf = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(buf, timestamp);
                return buf;
            }

            public static bool TryUnpack(ReadOnlySpan<byte> data, out uint timestamp)
            {
                timestamp = 0;
                if (data.Length < 4) return false;
                timestamp = BinaryPrimitives.ReadUInt32LittleEndian(data);
                return true;
            }
        }

        private static byte[] EncodeBoundedString(string? value, int maxLen)
        {
            if (string.IsNullOrEmpty(value)) return Array.Empty<byte>();
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            if (bytes.Length <= maxLen) return bytes;
            // 截断到 maxLen（避免半个 UTF-8 序列时尽量回退）
            int len = maxLen;
            while (len > 0 && (bytes[len] & 0xC0) == 0x80) len--;
            byte[] trimmed = new byte[len];
            Buffer.BlockCopy(bytes, 0, trimmed, 0, len);
            return trimmed;
        }
    }
}
