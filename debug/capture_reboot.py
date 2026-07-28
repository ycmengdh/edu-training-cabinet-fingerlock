"""捕获根节点重启前的最后日志"""
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
    print(f"=== 捕获根节点重启前日志 ({datetime.now()}) ===")
    s = serial.Serial(PORT, BAUD, timeout=0.05)
    time.sleep(0.3)
    s.reset_input_buffer()

    # 监听 60 秒，记录所有日志
    print("监听 60 秒...\n")
    start = time.time()
    all_logs = []
    raw_text = bytearray()
    while time.time() - start < 60:
        n = s.in_waiting
        if n > 0:
            data = s.read(n)
            raw_text += data
        else:
            time.sleep(0.05)
        # 尝试解码
        new_frames = list(decode_frames(bytes(raw_text)))
        if new_frames:
            raw_text = bytearray()
            for text in new_frames:
                try:
                    obj = json.loads(text)
                    ts = time.time()
                    all_logs.append((ts, obj))
                except Exception:
                    pass

    s.close()
    print(f"=== 总共收到 {len(all_logs)} 个帧 ===\n")

    # 找到所有 "init complete" 或 "Init done" 的时间点（每次重启的标志）
    reboot_points = []
    for ts, obj in all_logs:
        if obj.get("cmd") == "LOG":
            msg = obj.get("data", {}).get("msg", "")
            if "Init done" in msg or "init complete" in msg:
                reboot_points.append(ts)
                print(f"  *** 重启点: {datetime.fromtimestamp(ts).strftime('%H:%M:%S')} - {msg}")

    if not reboot_points:
        print("  没有发现重启")
    else:
        # 显示每次重启前的最后 20 条日志
        for i, reboot_ts in enumerate(reboot_points):
            print(f"\n=== 第 {i+1} 次重启（{datetime.fromtimestamp(reboot_ts).strftime('%H:%M:%S')}）前 20 条日志 ===")
            prev_logs = [(ts, obj) for ts, obj in all_logs if ts < reboot_ts]
            for ts, obj in prev_logs[-20:]:
                cmd = obj.get("cmd", "?")
                if cmd == "LOG":
                    msg = obj.get("data", {}).get("msg", "")
                    ts_str = datetime.fromtimestamp(ts).strftime("%H:%M:%S.%f")[:-3]
                    print(f"  {ts_str} [LOG] {msg}")
                elif cmd in ("REGISTER", "STATUS_RESPONSE"):
                    did = obj.get("device_id", "")
                    ts_str = datetime.fromtimestamp(ts).strftime("%H:%M:%S.%f")[:-3]
                    print(f"  {ts_str} [{cmd}] device={did}")


if __name__ == "__main__":
    main()
