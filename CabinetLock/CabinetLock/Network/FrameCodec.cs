using System.IO;
using System.Text;
using System.Threading;

namespace CabinetLock
{
    /// <summary>
    /// 协议帧编解码器（二进制帧，用于串口等无消息边界的链路）
    /// 帧格式：0xA5 0x5A + 版本1B + 长度2B(大端,负载长度) + 负载 + CRC16 2B(大端,MODBUS)。
    /// 版本 0x01 是普通 JSON 帧，版本 0x02 是带 4 字节头的 JSON 分片帧。
    /// </summary>
    public static class FrameCodec
    {
        /// <summary>帧头标记</summary>
        public const byte Head0 = 0xA5;
        public const byte Head1 = 0x5A;

        /// <summary>协议版本</summary>
        public const byte Version = 0x01;
        public const byte FragmentVersion = 0x02;

        /// <summary>单个协议帧的最大负载长度</summary>
        // ESP32 ProtocolFrame accepts normal payloads up to 1400 bytes and
        // reserves four bytes for its fragment header.
        public const int MaxPayload = 1404;
        private const int NormalPayload = 1400;
        private const int FragmentHeaderSize = 4;
        public const int MaxMessagePayload = 65536;
        private static int _nextFragmentId;

        /// <summary>帧最小长度（头2 + 版本1 + 长度2 + 负载0 + CRC2 = 7）</summary>
        public const int MinFrameLength = 7;

        /// <summary>
        /// 将 JSON 字符串编码为完整二进制帧（过渡兼容；新代码优先使用 <see cref="Encode(byte[])"/>）。
        /// </summary>
        /// <param name="jsonString">JSON 字符串（不含尾部换行）</param>
        /// <returns>完整帧字节数组；输入为空返回 null</returns>
        public static byte[]? Encode(string? jsonString)
        {
            if (string.IsNullOrEmpty(jsonString)) return null;
            return Encode(Encoding.UTF8.GetBytes(jsonString));
        }

        /// <summary>
        /// 将原始字节负载编码为完整二进制帧（可自动分片）。
        /// 用于二进制应用消息或 JSON UTF-8 字节。
        /// </summary>
        /// <param name="payload">负载字节；null 或空返回 null</param>
        /// <returns>完整帧（或多帧拼接）字节数组</returns>
        public static byte[]? Encode(byte[]? payload)
        {
            if (payload == null || payload.Length == 0) return null;
            if (payload.Length > MaxMessagePayload) return null;
            if (payload.Length <= NormalPayload)
            {
                return EncodeSingle(Version, payload);
            }

            int chunkSize = NormalPayload - FragmentHeaderSize;
            int total = (payload.Length + chunkSize - 1) / chunkSize;
            if (total > 255) return null;

            byte messageId = (byte)(Interlocked.Increment(ref _nextFragmentId) & 0xFF);
            if (messageId == 0) messageId = 1;
            using var output = new MemoryStream();
            for (int sequence = 0; sequence < total; sequence++)
            {
                int offset = sequence * chunkSize;
                int length = Math.Min(chunkSize, payload.Length - offset);
                byte[] fragment = new byte[FragmentHeaderSize + length];
                fragment[0] = messageId;
                fragment[1] = (byte)sequence;
                fragment[2] = (byte)total;
                Buffer.BlockCopy(payload, offset, fragment, FragmentHeaderSize, length);
                output.Write(EncodeSingle(FragmentVersion, fragment));
            }
            return output.ToArray();
        }

        private static byte[] EncodeSingle(byte version, byte[] payload)
        {
            // 帧：头2 + 版本1 + 长度2 + 负载 + CRC2
            byte[] frame = new byte[MinFrameLength + payload.Length];
            frame[0] = Head0;
            frame[1] = Head1;
            frame[2] = version;
            frame[3] = (byte)((payload.Length >> 8) & 0xFF);
            frame[4] = (byte)(payload.Length & 0xFF);
            Buffer.BlockCopy(payload, 0, frame, 5, payload.Length);

            // CRC-16/MODBUS 覆盖：版本 + 长度 + 负载
            ushort crc = CalcCrc16Modbus(frame, 2, 3 + payload.Length);
            frame[frame.Length - 2] = (byte)((crc >> 8) & 0xFF);
            frame[frame.Length - 1] = (byte)(crc & 0xFF);
            return frame;
        }

        /// <summary>
        /// 从完整二进制帧解码出 JSON 字符串（过渡兼容）。
        /// </summary>
        /// <param name="frame">完整帧字节数组</param>
        /// <returns>JSON 字符串；校验失败或长度不足返回 null</returns>
        public static string? Decode(byte[]? frame)
        {
            if (!TryDecodeBytes(frame, out byte[]? payload) || payload == null) return null;
            return Encoding.UTF8.GetString(payload);
        }

        /// <summary>
        /// 从完整二进制帧解码出原始负载字节（仅普通帧 Version=0x01，不分片）。
        /// 分片消息请使用 <see cref="FrameStreamDecoder"/>。
        /// </summary>
        public static bool TryDecodeBytes(byte[]? frame, out byte[]? payload)
        {
            payload = null;
            if (!TryDecodeFrame(frame, 0, frame?.Length ?? 0,
                    out byte frameVersion, out byte[]? raw, out _)) return false;
            if (frameVersion != Version || raw == null) return false;
            payload = raw;
            return true;
        }

        /// <summary>
        /// 尝试从缓冲区中解析一帧，返回该帧的 JSON 与消耗的字节数
        /// 用于流式接收场景：找到帧头后尝试解码，不足则等待更多数据。
        /// </summary>
        /// <param name="buffer">接收缓冲区</param>
        /// <param name="offset">起始偏移</param>
        /// <param name="count">有效字节数</param>
        /// <param name="json">输出的 JSON 字符串</param>
        /// <param name="consumed">本次消耗的字节数</param>
        /// <returns>成功解析一帧返回 true；数据不足或校验失败返回 false</returns>
        public static bool TryDecode(byte[] buffer, int offset, int count, out string? json, out int consumed)
        {
            json = null;
            if (!TryDecodeBytes(buffer, offset, count, out byte[]? payload, out consumed) ||
                payload == null) return false;
            json = Encoding.UTF8.GetString(payload);
            return true;
        }

        /// <summary>
        /// 尝试从缓冲区中解析一帧，返回原始负载字节与消耗的字节数（普通帧）。
        /// </summary>
        public static bool TryDecodeBytes(byte[] buffer, int offset, int count,
            out byte[]? payload, out int consumed)
        {
            payload = null;
            consumed = 0;
            if (!TryDecodeFrame(buffer, offset, count, out byte frameVersion,
                    out byte[]? raw, out consumed)) return false;
            if (frameVersion != Version || raw == null) return false;
            payload = raw;
            return true;
        }

        /// <summary>
        /// 解析一个完整协议帧。该方法同时接受普通帧和分片帧，供流式重组器使用。
        /// consumed 在帧校验失败时也会推进到该帧末尾，避免坏帧阻塞后续数据。
        /// </summary>
        internal static bool TryDecodeFrame(byte[]? buffer, int offset, int count,
            out byte frameVersion, out byte[]? payload, out int consumed)
        {
            frameVersion = 0;
            payload = null;
            consumed = 0;

            if (buffer == null || offset < 0 || count <= 0 ||
                offset > buffer.Length || count > buffer.Length - offset) return false;

            int headIdx = -1;
            int end = offset + count;
            for (int i = offset; i + 1 < end; i++)
            {
                if (buffer[i] == Head0 && buffer[i + 1] == Head1)
                {
                    headIdx = i;
                    break;
                }
            }

            if (headIdx < 0)
            {
                // 保留末尾可能是半个帧头的 0xA5。
                consumed = count > 0 && buffer[end - 1] == Head0 ? count - 1 : count;
                return false;
            }

            int skip = headIdx - offset;
            int remaining = count - skip;
            if (remaining < MinFrameLength)
            {
                consumed = skip;
                return false;
            }

            frameVersion = buffer[headIdx + 2];
            if (frameVersion != Version && frameVersion != FragmentVersion)
            {
                consumed = skip + 2;
                return false;
            }

            int payloadLen = (buffer[headIdx + 3] << 8) | buffer[headIdx + 4];
            int maxPayload = frameVersion == FragmentVersion ? MaxPayload : NormalPayload;
            if (payloadLen <= 0 || payloadLen > maxPayload)
            {
                consumed = skip + 2;
                return false;
            }

            int frameLen = MinFrameLength + payloadLen;
            if (remaining < frameLen)
            {
                consumed = skip;
                return false;
            }

            ushort crcRecv = (ushort)((buffer[headIdx + 5 + payloadLen] << 8) |
                                      buffer[headIdx + 5 + payloadLen + 1]);
            ushort crcCalc = CalcCrc16Modbus(buffer, headIdx + 2, 3 + payloadLen);
            consumed = skip + frameLen;
            if (crcRecv != crcCalc) return false;

            payload = new byte[payloadLen];
            Buffer.BlockCopy(buffer, headIdx + 5, payload, 0, payloadLen);
            return true;
        }

        /// <summary>
        /// 计算 CRC-16/MODBUS
        /// 多项式 0xA001，初始值 0xFFFF，低字节在前（返回值按大端写入帧）
        /// </summary>
        /// <param name="data">数据</param>
        /// <param name="offset">起始偏移</param>
        /// <param name="length">计算长度</param>
        /// <returns>CRC-16 值</returns>
        public static ushort CalcCrc16Modbus(byte[] data, int offset, int length)
        {
            ushort crc = 0xFFFF;
            for (int i = offset; i < offset + length && i < data.Length; i++)
            {
                crc ^= data[i];
                for (int j = 0; j < 8; j++)
                {
                    if ((crc & 0x0001) != 0)
                    {
                        crc >>= 1;
                        crc ^= 0xA001;
                    }
                    else
                    {
                        crc >>= 1;
                    }
                }
            }
            return crc;
        }
    }

    /// <summary>
    /// Incremental decoder for a byte stream. Serial and TCP have no message
    /// boundaries, so callers must feed bytes here instead of using ReadLine.
    /// </summary>
    public sealed class FrameStreamDecoder
    {
        private byte[] _buffer = new byte[8192];
        private int _count;
        private byte _fragmentMessageId;
        private int _fragmentTotal;
        private DateTime _fragmentStartedUtc;
        private readonly Dictionary<int, byte[]> _fragmentParts = new();

        /// <summary>
        /// 追加字节并回调完整 JSON 字符串消息（过渡兼容）。
        /// </summary>
        public void Append(byte[] data, int offset, int count, Action<string> onMessage,
            Action<byte[]>? onUnframedData = null)
        {
            if (onMessage == null) return;
            AppendBytes(data, offset, count,
                payload => onMessage(Encoding.UTF8.GetString(payload)),
                onUnframedData);
        }

        /// <summary>
        /// 追加字节并回调完整原始负载（支持普通帧与分片重组）。
        /// 新代码应使用此 API 处理二进制应用消息。
        /// </summary>
        public void AppendBytes(byte[] data, int offset, int count, Action<byte[]> onPayload,
            Action<byte[]>? onUnframedData = null)
        {
            if (data == null || count <= 0 || onPayload == null) return;
            EnsureCapacity(_count + count);
            Buffer.BlockCopy(data, offset, _buffer, _count, count);
            _count += count;

            while (_count > 0)
            {
                int headIndex = FindFrameHead();
                if (headIndex != 0)
                {
                    int noiseCount = headIndex > 0
                        ? headIndex
                        : (_buffer[_count - 1] == FrameCodec.Head0 ? _count - 1 : _count);
                    if (noiseCount > 0)
                    {
                        ReportUnframed(noiseCount, onUnframedData);
                        Remove(noiseCount);
                        continue;
                    }

                    // Only a possible first header byte remains.
                    break;
                }

                bool decoded = FrameCodec.TryDecodeFrame(_buffer, 0, _count,
                    out byte version, out byte[]? payload, out int consumed);

                if (consumed > 0)
                {
                    if (decoded && payload != null)
                    {
                        Remove(consumed);
                        if (version == FrameCodec.Version)
                        {
                            onPayload(payload);
                        }
                        else if (version == FrameCodec.FragmentVersion)
                        {
                            HandleFragment(payload, onPayload);
                        }
                    }
                    else
                    {
                        ReportUnframed(consumed, onUnframedData);
                        Remove(consumed);
                    }
                    continue;
                }

                // A partial frame remains in the buffer. Wait for more bytes.
                break;
            }
        }

        public void Reset()
        {
            _count = 0;
            ResetFragment();
        }

        private void HandleFragment(byte[] payload, Action<byte[]> onPayload)
        {
            if (payload.Length < 4) return;

            byte messageId = payload[0];
            int sequence = payload[1];
            int total = payload[2];
            if (total <= 0 || sequence >= total) return;

            if (_fragmentParts.Count > 0 &&
                (messageId != _fragmentMessageId || total != _fragmentTotal ||
                 DateTime.UtcNow - _fragmentStartedUtc > TimeSpan.FromSeconds(5)))
            {
                ResetFragment();
            }

            if (_fragmentParts.Count == 0)
            {
                _fragmentMessageId = messageId;
                _fragmentTotal = total;
                _fragmentStartedUtc = DateTime.UtcNow;
            }

            if (_fragmentParts.ContainsKey(sequence)) return;
            byte[] part = new byte[payload.Length - 4];
            Buffer.BlockCopy(payload, 4, part, 0, part.Length);
            _fragmentParts[sequence] = part;

            if (_fragmentParts.Count < _fragmentTotal) return;

            int length = 0;
            for (int i = 0; i < _fragmentTotal; i++)
            {
                if (!_fragmentParts.TryGetValue(i, out var current))
                {
                    ResetFragment();
                    return;
                }
                length += current.Length;
                if (length > FrameCodec.MaxMessagePayload)
                {
                    ResetFragment();
                    return;
                }
            }

            byte[] complete = new byte[length];
            int offset = 0;
            for (int i = 0; i < _fragmentTotal; i++)
            {
                byte[] current = _fragmentParts[i];
                Buffer.BlockCopy(current, 0, complete, offset, current.Length);
                offset += current.Length;
            }

            ResetFragment();
            onPayload(complete);
        }

        private void ResetFragment()
        {
            _fragmentMessageId = 0;
            _fragmentTotal = 0;
            _fragmentStartedUtc = default;
            _fragmentParts.Clear();
        }

        private void EnsureCapacity(int required)
        {
            if (required <= _buffer.Length) return;
            int size = _buffer.Length;
            while (size < required) size *= 2;
            Array.Resize(ref _buffer, size);
        }

        private int FindFrameHead()
        {
            for (int i = 0; i + 1 < _count; i++)
            {
                if (_buffer[i] == FrameCodec.Head0 && _buffer[i + 1] == FrameCodec.Head1)
                    return i;
            }
            return -1;
        }

        private void ReportUnframed(int count, Action<byte[]>? callback)
        {
            if (callback == null || count <= 0) return;
            byte[] bytes = new byte[count];
            Buffer.BlockCopy(_buffer, 0, bytes, 0, count);
            callback(bytes);
        }

        private void Remove(int count)
        {
            if (count >= _count)
            {
                _count = 0;
                return;
            }

            Buffer.BlockCopy(_buffer, count, _buffer, 0, _count - count);
            _count -= count;
        }
    }
}
