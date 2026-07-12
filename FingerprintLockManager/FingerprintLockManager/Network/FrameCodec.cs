using System.Text;

namespace FingerprintLockManager
{
    /// <summary>
    /// 协议帧编解码器（二进制帧，用于串口等无消息边界的链路）
    /// 帧格式：0xA5 0x5A + 版本1B + 长度2B(大端,JSON负载长度) + JSON负载 + CRC16 2B(大端,MODBUS)
    /// 当前实现单帧编解码；分片重组为可选项，暂未实现（大负载建议应用层分片）。
    /// </summary>
    public static class FrameCodec
    {
        /// <summary>帧头标记</summary>
        public const byte Head0 = 0xA5;
        public const byte Head1 = 0x5A;

        /// <summary>协议版本</summary>
        public const byte Version = 0x01;

        /// <summary>JSON 负载最大长度（65535 受 2 字节长度字段限制）</summary>
        public const int MaxPayload = 0xFFFF;

        /// <summary>帧最小长度（头2 + 版本1 + 长度2 + 负载0 + CRC2 = 7）</summary>
        public const int MinFrameLength = 7;

        /// <summary>
        /// 将 JSON 字符串编码为完整二进制帧
        /// </summary>
        /// <param name="jsonString">JSON 字符串（不含尾部换行）</param>
        /// <returns>完整帧字节数组；输入为空返回 null</returns>
        public static byte[] Encode(string jsonString)
        {
            if (string.IsNullOrEmpty(jsonString)) return null;

            byte[] payload = Encoding.UTF8.GetBytes(jsonString);
            if (payload.Length > MaxPayload) return null;

            // 帧：头2 + 版本1 + 长度2 + 负载 + CRC2
            byte[] frame = new byte[MinFrameLength + payload.Length];
            frame[0] = Head0;
            frame[1] = Head1;
            frame[2] = Version;
            // 长度大端
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
        /// 从完整二进制帧解码出 JSON 字符串
        /// </summary>
        /// <param name="frame">完整帧字节数组</param>
        /// <returns>JSON 字符串；校验失败或长度不足返回 null</returns>
        public static string Decode(byte[] frame)
        {
            if (frame == null || frame.Length < MinFrameLength) return null;

            // 校验帧头
            if (frame[0] != Head0 || frame[1] != Head1) return null;

            // 解析长度（大端）
            int payloadLen = (frame[3] << 8) | frame[4];
            if (payloadLen < 0 || payloadLen > MaxPayload) return null;
            if (frame.Length < MinFrameLength + payloadLen) return null;

            // 校验 CRC（覆盖 版本 + 长度 + 负载）
            ushort crcRecv = (ushort)((frame[5 + payloadLen] << 8) | frame[5 + payloadLen + 1]);
            ushort crcCalc = CalcCrc16Modbus(frame, 2, 3 + payloadLen);
            if (crcRecv != crcCalc) return null;

            // 提取 JSON 负载
            return Encoding.UTF8.GetString(frame, 5, payloadLen);
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
        public static bool TryDecode(byte[] buffer, int offset, int count, out string json, out int consumed)
        {
            json = null;
            consumed = 0;

            if (buffer == null || count < MinFrameLength) return false;

            // 查找帧头
            int headIdx = -1;
            for (int i = offset; i <= offset + count - 2; i++)
            {
                if (buffer[i] == Head0 && buffer[i + 1] == Head1)
                {
                    headIdx = i;
                    break;
                }
            }
            if (headIdx < 0)
            {
                // 丢弃帧头之前的字节
                consumed = count;
                return false;
            }

            // 丢弃帧头之前字节
            int skip = headIdx - offset;
            int remaining = count - skip;
            if (remaining < MinFrameLength)
            {
                consumed = skip;
                return false;
            }

            int payloadLen = (buffer[headIdx + 3] << 8) | buffer[headIdx + 4];
            if (payloadLen < 0 || payloadLen > MaxPayload)
            {
                // 长度非法，跳过帧头继续
                consumed = skip + 2;
                return false;
            }

            int frameLen = MinFrameLength + payloadLen;
            if (remaining < frameLen)
            {
                // 数据不足，等待更多
                consumed = skip;
                return false;
            }

            // 提取完整帧
            byte[] frame = new byte[frameLen];
            Buffer.BlockCopy(buffer, headIdx, frame, 0, frameLen);
            json = Decode(frame);
            consumed = skip + frameLen;
            return json != null;
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
}
