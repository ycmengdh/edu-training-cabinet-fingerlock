"""最小化测试：不复位缓冲区，直接读取并解析"""
import serial
import struct
import time
import json

def crc16_modbus(data: bytes) -> int:
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            if crc & 1:
                crc = (crc >> 1) ^ 0xA001
            else:
                crc >>= 1
    return crc & 0xFFFF

def decode_one_frame(buf, start):
    """从 buf[start] 开始尝试解析一帧"""
    if start + 7 > len(buf):
        return None, start
    # 期望帧头 a5 5a
    if buf[start] != 0xA5 or buf[start + 1] != 0x5A:
        return None, start + 1
    version = buf[start + 2]
    length = (buf[start + 3] << 8) | buf[start + 4]
    if length > 8192:
        return None, start + 2
    frame_end = start + 5 + length + 2
    if frame_end > len(buf):
        return None, start  # 数据不足
    payload = buf[start + 5:start + 5 + length]
    crc_recv = (buf[start + 5 + length] << 8) | buf[start + 5 + length + 1]
    # CRC 范围：版本+长度+负载（不含帧头）
    crc_calc = crc16_modbus(buf[start + 2:start + 5 + length])
    if crc_recv != crc_calc:
        return None, start + 2
    try:
        return json.loads(payload.decode('utf-8')), frame_end
    except:
        return None, frame_end


ser = serial.Serial('COM16', 921600, timeout=0.5)
print(f"串口已打开: {ser.name}")
# 不调用 reset_input_buffer，直接读取
print("等待 30 秒收数据...")

start = time.time()
buf = b''
count = 0
while time.time() - start < 30:
    if ser.in_waiting > 0:
        chunk = ser.read(ser.in_waiting)
        buf += chunk
        # 尝试解析所有完整帧
        pos = 0
        while pos < len(buf):
            frame, new_pos = decode_one_frame(buf, pos)
            if frame is None:
                if new_pos == pos:
                    break  # 数据不足，等待更多
                pos = new_pos
                continue
            count += 1
            cmd = frame.get('cmd', '?')
            did = frame.get('device_id', '?')
            print(f"  [{count}] cmd={cmd} device_id={did} ts={frame.get('timestamp','?')}")
            pos = new_pos
        # 移除已处理的数据
        buf = buf[pos:]
    else:
        time.sleep(0.05)

print(f"\n30 秒内共收到 {count} 帧")
print(f"剩余未解析字节: {len(buf)}B")
if buf:
    print(f"剩余数据前 50B: {buf[:50].hex()}")
ser.close()
