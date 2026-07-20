"""检查根节点是否在反复重启"""
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
    print(f"=== 检查根节点是否在重启 ({datetime.now()}) ===")
    s = serial.Serial(PORT, BAUD, timeout=0.05)
    time.sleep(0.3)
    s.reset_input_buffer()

    # 每 5 秒查询一次 uptime，看是否在重启
    for i in range(6):
        cmd = '{"cmd":"READ_STATUS","device_id":"ROOT_001","data":{},"timestamp":"2026-07-19 00:00:00"}'
        s.write(encode_frame(cmd))
        s.flush()

        start = time.time()
        buf = b""
        uptime = None
        while time.time() - start < 2:
            n = s.in_waiting
            if n > 0:
                buf += s.read(n)
            else:
                time.sleep(0.05)
            for text in decode_frames(buf):
                try:
                    obj = json.loads(text)
                    if obj.get("cmd") == "STATUS_RESPONSE":
                        uptime = obj.get("data", {}).get("uptime")
                        cc = obj.get("data", {}).get("child_count")
                        rc = obj.get("data", {}).get("route_count")
                        print(f"  [{i+1}/6] uptime={uptime}s, child_count={cc}, route_count={rc}")
                        break
                except Exception:
                    pass
            if uptime is not None:
                break
        time.sleep(3)

    s.close()


if __name__ == "__main__":
    main()
