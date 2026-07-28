"""验证根节点重启后立即（不等 HEARTBEAT）能稳定通讯"""
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


def send_and_wait(s, cmd_json, expect_cmd, timeout=5.0):
    s.reset_input_buffer()
    frame = encode_frame(cmd_json)
    t0 = time.time()
    s.write(frame)
    s.flush()
    buf = b""
    while time.time() - t0 < timeout:
        n = s.in_waiting
        if n > 0:
            buf += s.read(n)
        else:
            time.sleep(0.01)
        for text in decode_frames(buf):
            try:
                obj = json.loads(text)
                if obj.get("cmd") == expect_cmd:
                    return obj, (time.time() - t0) * 1000
            except Exception:
                pass
        buf = b""
    return None, (time.time() - t0) * 1000


def main():
    print(f"=== 根节点重启后立即通讯验证 ({datetime.now()}) ===")
    s = serial.Serial(PORT, BAUD, timeout=0.05)

    # 等 12 秒：让根节点重启 + 柜子 PARENT_CONNECTED + REGISTER 重发
    print("等 12 秒让 Mesh 重建 + REGISTER 重发...")
    time.sleep(12)
    s.reset_input_buffer()

    # 连续 20 次 READ_STATUS to CABINET_001，统计成功率
    success = 0
    fail = 0
    latencies = []
    print("\n连续 20 次 READ_STATUS -> CABINET_001:")
    for i in range(20):
        cmd = '{"cmd":"READ_STATUS","device_id":"CABINET_001","data":{},"timestamp":"2026-07-19 15:00:00"}'
        matched, elapsed = send_and_wait(s, cmd, "STATUS_RESPONSE", timeout=3.0)
        if matched:
            success += 1
            latencies.append(elapsed)
            data = matched.get("data", {})
            print(f"  [{i+1:2d}/20] OK   {elapsed:6.0f}ms  uptime={data.get('uptime')}s")
        else:
            fail += 1
            print(f"  [{i+1:2d}/20] FAIL timeout")
        time.sleep(0.3)  # 间隔 300ms 避免压测

    print(f"\n=== 统计 ===")
    print(f"  成功率: {success}/20 = {success/20*100:.0f}%")
    if latencies:
        print(f"  平均延迟: {sum(latencies)/len(latencies):.0f}ms")
        print(f"  最小延迟: {min(latencies):.0f}ms")
        print(f"  最大延迟: {max(latencies):.0f}ms")

    s.close()


if __name__ == "__main__":
    main()
