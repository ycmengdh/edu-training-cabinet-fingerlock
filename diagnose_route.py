"""快速诊断根节点路由表为何为 0"""
import serial
import time
import json
from datetime import datetime

PORT = "COM16"
BAUD = 921600

FRAME_HEAD1 = 0xA5
FRAME_HEAD2 = 0x5A


def crc16_modbus(data):
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            if crc & 1:
                crc = (crc >> 1) ^ 0xA001
            else:
                crc >>= 1
    return crc


def encode_frame(payload_json: str) -> bytes:
    payload = payload_json.encode("utf-8")
    version = 0x01
    length = len(payload)
    crc_input = bytes([version, (length >> 8) & 0xFF, length & 0xFF]) + payload
    crc = crc16_modbus(crc_input)
    return bytes([FRAME_HEAD1, FRAME_HEAD2, version,
                 (length >> 8) & 0xFF, length & 0xFF]) + payload + \
           bytes([(crc >> 8) & 0xFF, crc & 0xFF])


def decode_frames(buf: bytes):
    i = 0
    while i < len(buf):
        if buf[i] != FRAME_HEAD1:
            i += 1
            continue
        if i + 1 >= len(buf) or buf[i + 1] != FRAME_HEAD2:
            i += 1
            continue
        if i + 5 > len(buf):
            return
        version = buf[i + 2]
        length = (buf[i + 3] << 8) | buf[i + 4]
        if version not in (0x01, 0x02):
            i += 1
            continue
        end = i + 5 + length + 2
        if end > len(buf):
            return
        payload = buf[i + 5:i + 5 + length]
        crc_recv = (buf[i + 5 + length] << 8) | buf[i + 5 + length + 1]
        crc_calc = crc16_modbus(bytes([version, (length >> 8) & 0xFF, length & 0xFF]) + payload)
        if crc_recv != crc_calc:
            i += 1
            continue
        try:
            yield payload.decode("utf-8", errors="replace")
        except Exception:
            pass
        i = end


def main():
    print(f"=== 路由表诊断 ({datetime.now()}) ===")
    s = serial.Serial(PORT, BAUD, timeout=0.05)
    time.sleep(0.3)
    s.reset_input_buffer()

    # 监听 15 秒，过滤 BRIDGE 相关日志
    print("监听根节点日志 15 秒，过滤 BRIDGE 相关条目...\n")
    start = time.time()
    buf = b""
    route_logs = []
    other_bridge_logs = []
    while time.time() - start < 15:
        n = s.in_waiting
        if n > 0:
            buf += s.read(n)
        else:
            time.sleep(0.05)
        for text in decode_frames(buf):
            try:
                obj = json.loads(text)
                if obj.get("cmd") == "LOG":
                    msg = obj.get("data", {}).get("msg", "")
                    if "BRIDGE" in msg:
                        if "route" in msg.lower() or "child" in msg.lower():
                            route_logs.append(msg)
                        else:
                            other_bridge_logs.append(msg)
            except Exception:
                pass
        if len(buf) > 16384:
            buf = buf[-8192:]

    print(f"=== 路由相关日志 ({len(route_logs)} 条) ===")
    for msg in route_logs[:15]:
        print(f"  {msg}")

    print(f"\n=== 其他 BRIDGE 日志 (前 10 条) ===")
    for msg in other_bridge_logs[:10]:
        print(f"  {msg}")

    # 查询根节点状态
    print("\n=== 查询根节点状态 ===")
    s.reset_input_buffer()
    cmd = '{"cmd":"READ_STATUS","device_id":"ROOT_001","data":{},"timestamp":"2026-07-19 00:00:00"}'
    s.write(encode_frame(cmd))
    s.flush()
    start = time.time()
    buf = b""
    while time.time() - start < 3:
        n = s.in_waiting
        if n > 0:
            buf += s.read(n)
        else:
            time.sleep(0.05)
        for text in decode_frames(buf):
            try:
                obj = json.loads(text)
                if obj.get("cmd") == "STATUS_RESPONSE":
                    data = obj.get("data", {})
                    print(f"  uptime={data.get('uptime')}s")
                    print(f"  mesh_layer={data.get('mesh_layer')}")
                    print(f"  child_count={data.get('child_count')}")
                    print(f"  route_count={data.get('route_count')}")
                    break
            except Exception:
                pass

    s.close()


if __name__ == "__main__":
    main()
