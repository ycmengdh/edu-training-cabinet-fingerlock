"""抓根节点串口 30 秒原始日志，定位 Mesh 抖动原因"""
import serial
import time
from datetime import datetime

PORT = "COM16"
BAUD = 921600

FRAME_HEAD1 = 0xA5
FRAME_HEAD2 = 0x5A


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
        crc = 0xFFFF
        for b in bytes([version, (length >> 8) & 0xFF, length & 0xFF]) + payload:
            crc ^= b
            for _ in range(8):
                if crc & 1:
                    crc = (crc >> 1) ^ 0xA001
                else:
                    crc >>= 1
        if crc_recv != crc:
            i += 1
            continue
        try:
            yield payload.decode("utf-8", errors="replace")
        except Exception:
            pass
        i = end


def main():
    print(f"=== 抓根节点串口 90s ({datetime.now()}) ===\n")
    s = serial.Serial(PORT, BAUD, timeout=0.05)
    time.sleep(0.3)
    s.reset_input_buffer()

    start = time.time()
    buf = b""
    counts = {}
    while time.time() - start < 90:
        n = s.in_waiting
        if n > 0:
            chunk = s.read(n)
            buf += chunk
            # 尝试逐帧解码并消费
            new_buf = b""
            i = 0
            while i < len(buf):
                if buf[i] != FRAME_HEAD1:
                    i += 1
                    continue
                if i + 1 >= len(buf) or buf[i + 1] != FRAME_HEAD2:
                    i += 1
                    continue
                if i + 5 > len(buf):
                    break
                version = buf[i + 2]
                length = (buf[i + 3] << 8) | buf[i + 4]
                if version not in (0x01, 0x02):
                    i += 1
                    continue
                end = i + 5 + length + 2
                if end > len(buf):
                    break
                payload = buf[i + 5:i + 5 + length]
                crc_recv = (buf[i + 5 + length] << 8) | buf[i + 5 + length + 1]
                crc = 0xFFFF
                for b in bytes([version, (length >> 8) & 0xFF, length & 0xFF]) + payload:
                    crc ^= b
                    for _ in range(8):
                        if crc & 1:
                            crc = (crc >> 1) ^ 0xA001
                        else:
                            crc >>= 1
                if crc_recv == crc:
                    try:
                        text = payload.decode("utf-8", errors="replace")
                        # 提取 cmd
                        cmd = "?"
                        did = "?"
                        if '"cmd"' in text:
                            import json
                            try:
                                obj = json.loads(text)
                                cmd = obj.get("cmd", "?")
                                did = obj.get("device_id", "?")
                            except Exception:
                                pass
                        key = f"{cmd} @ {did}"
                        counts[key] = counts.get(key, 0) + 1
                        # 打印所有帧（LOG 帧打印前 200 字符内容）
                        ts = datetime.now().strftime("%H:%M:%S")
                        if cmd == "LOG":
                            try:
                                import json
                                obj = json.loads(text)
                                msg = obj.get("data", {}).get("msg", "")
                                if any(k in msg for k in ("MESH", "BRIDGE", "MAIN", "received", "route", "send", "forward", "heartbeat", "event")):
                                    print(f"[{ts}] LOG: {msg[:200]}")
                            except Exception:
                                pass
                        elif cmd == "STATUS_REPORT":
                            try:
                                import json
                                obj = json.loads(text)
                                data = obj.get("data", {})
                                print(f"[{ts}] STATUS_REPORT device={did} uptime={data.get('uptime')}s child={data.get('child_count')} route={data.get('route_count')}")
                            except Exception:
                                pass
                        elif cmd not in ("HEARTBEAT", "HEARTBEAT_ACK", "REGISTER"):
                            print(f"[{ts}] {cmd:20s} device={did} len={length}")
                    except Exception:
                        pass
                    i = end
                else:
                    i += 1
            # 保留未消费部分
            buf = buf[i:]
        else:
            time.sleep(0.02)

    s.close()
    print(f"\n=== 90s 帧统计 ===")
    for k, v in sorted(counts.items(), key=lambda x: -x[1]):
        print(f"  {v:5d}x  {k}")


if __name__ == "__main__":
    main()
