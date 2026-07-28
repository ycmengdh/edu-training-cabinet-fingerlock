"""
深度诊断 Mesh 数据传输
1. 重启根节点
2. 监听 USB 串口，捕获所有 LOG/REGISTER 消息
3. 统计柜子节点主动上报的消息数量
"""
import serial
import time
import json
from datetime import datetime

ROOT_PORT = "COM16"
CAB_PORT = "COM10"
BAUD = 921600
DURATION = 60  # 60 秒

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


def decode_frames_iter(buf: bytes):
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


def reset_device(port):
    """Hardware reset via esptool-like DTR/RTS sequence."""
    try:
        s = serial.Serial(port, BAUD, timeout=0.1)
        s.dtr = False
        s.rts = True   # EN low
        time.sleep(0.1)
        s.rts = False  # release reset
        s.dtr = False
        s.close()
        print(f"[{port}] reset sent")
    except Exception as e:
        print(f"[{port}] reset failed: {e}")


def monitor(port, label, results, duration):
    try:
        s = serial.Serial(port, BAUD, timeout=0.1)
        time.sleep(0.3)
        s.reset_input_buffer()

        start = time.time()
        all_text = []
        cabinet_msgs = 0
        root_msgs = 0
        other_msgs = 0
        mesh_logs = []
        register_msgs = []

        while time.time() - start < duration:
            n = s.in_waiting
            if n > 0:
                data = s.read(n)
                all_text.append(data)
            else:
                time.sleep(0.05)

            # try decode accumulated buffer
            buf = b"".join(all_text)
            new_frames = list(decode_frames_iter(buf))
            if new_frames:
                # clear buffer after decoding
                all_text = []
                for text in new_frames:
                    try:
                        obj = json.loads(text)
                        did = obj.get("device_id", "")
                        cmd = obj.get("cmd", "")
                        if cmd == "LOG":
                            msg = obj.get("data", {}).get("msg", "")
                            if any(k in msg for k in ["MESH", "BRIDGE", "MSG", "MAIN"]):
                                mesh_logs.append(msg)
                        elif cmd in ("REGISTER", "HEARTBEAT", "STATUS_REPORT", "STATUS_RESPONSE"):
                            register_msgs.append((did, cmd, obj.get("data", {})))
                            if did == "CABINET_001":
                                cabinet_msgs += 1
                            elif did == "ROOT_001":
                                root_msgs += 1
                            else:
                                other_msgs += 1
                    except Exception:
                        pass
        s.close()
        results[label] = {
            "cabinet_msgs": cabinet_msgs,
            "root_msgs": root_msgs,
            "other_msgs": other_msgs,
            "mesh_logs": mesh_logs,
            "register_msgs": register_msgs,
        }
    except Exception as e:
        print(f"[{label}] monitor error: {e}")
        results[label] = None


def main():
    print(f"=== Mesh 深度诊断 ({DURATION}s) ===")
    print(f"时间: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")

    # Reset both
    reset_device(ROOT_PORT)
    reset_device(CAB_PORT)
    time.sleep(1.0)

    results = {}
    import threading
    tr = threading.Thread(target=monitor, args=(ROOT_PORT, "ROOT", results, DURATION))
    tc = threading.Thread(target=monitor, args=(CAB_PORT, "CAB", results, DURATION))
    tr.start()
    tc.start()
    tr.join()
    tc.join()

    for label in ["ROOT", "CAB"]:
        r = results.get(label)
        if r is None:
            print(f"\n========== {label}: NO DATA ==========")
            continue
        print(f"\n========== {label} ==========")
        print(f"  柜子消息: {r['cabinet_msgs']}")
        print(f"  根节点消息: {r['root_msgs']}")
        print(f"  其他: {r['other_msgs']}")
        print(f"  Mesh 相关日志 (前 30 条):")
        for msg in r["mesh_logs"][:30]:
            print(f"    [LOG] {msg}")
        if r["register_msgs"]:
            print(f"  主动上报消息 (前 5 条):")
            for did, cmd, data in r["register_msgs"][:5]:
                data_str = json.dumps(data, ensure_ascii=False)[:80]
                print(f"    [{did}] {cmd}: {data_str}")


if __name__ == "__main__":
    main()
