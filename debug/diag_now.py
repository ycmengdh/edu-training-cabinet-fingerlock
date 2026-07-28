"""
Live dual-port diagnostics for COM16 (root) + COM12 (cabinet).
- Detect cabinet boot loops
- Decode protocol frames on both sides
- Probe root READ_STATUS for ROOT and CABINET
"""
import json
import serial
import threading
import time
from datetime import datetime
from collections import Counter

ROOT_PORT = "COM16"
CAB_PORT = "COM12"
BAUD = 921600
LISTEN_S = 45.0

FRAME_HEAD1 = 0xA5
FRAME_HEAD2 = 0x5A

CMD_NAMES = {
    0x0001: "REGISTER",
    0x0002: "HEARTBEAT",
    0x0003: "HEARTBEAT_ACK",
    0x0035: "STATUS_RESPONSE",
    0x0036: "STATUS_REPORT",
    0x0010: "READ_STATUS",
    0x00F0: "LOG",
}


def crc16_modbus(data: bytes) -> int:
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
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


def parse_frames(buf: bytes):
    events = []
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
        if crc_recv != crc16_modbus(bytes([version, (length >> 8) & 0xFF, length & 0xFF]) + payload):
            i += 1
            continue
        events.append(payload)
        i = end
    return events, i


def extract_event(payload: bytes):
    if len(payload) >= 18 and payload[0] == 0xB1 and payload[1] == 0x0F:
        cmd_id = payload[4] | (payload[5] << 8)
        did_len = payload[10]
        src_len = payload[11]
        plen = payload[12] | (payload[13] << 8)
        pos = 18
        if pos + did_len + src_len + plen > len(payload):
            return ("BIN", f"0x{cmd_id:04X}", "", "")
        did = payload[pos:pos + did_len].decode("utf-8", "replace")
        pos += did_len
        src = payload[pos:pos + src_len].decode("utf-8", "replace")
        pos += src_len
        pl = payload[pos:pos + plen].decode("utf-8", "replace")
        return ("BIN", CMD_NAMES.get(cmd_id, f"0x{cmd_id:04X}"), did, pl[:180])
    try:
        obj = json.loads(payload.decode("utf-8", "replace"))
        cmd = obj.get("cmd", "?")
        did = obj.get("device_id", "")
        data = obj.get("data", {})
        if isinstance(data, dict):
            msg = data.get("msg") or json.dumps(data, ensure_ascii=False)
        else:
            msg = str(data)
        return ("JSON", cmd, did, msg[:180])
    except Exception:
        return ("RAW", "raw", "", payload[:80].hex())


def monitor(port, label, results, stop_at, raw_lines):
    try:
        s = serial.Serial(port, BAUD, timeout=0.05)
    except Exception as e:
        results.append((0.0, label, "ERR", "OPEN_FAIL", "", str(e)))
        return
    time.sleep(0.2)
    s.reset_input_buffer()
    buf = b""
    ascii_acc = b""
    t0 = time.time()
    while time.time() < stop_at:
        n = s.in_waiting
        if n:
            chunk = s.read(n)
            buf += chunk
            ascii_acc += chunk
        else:
            time.sleep(0.01)
            continue

        # extract ASCII log lines for cabinet boot detection
        while b"\n" in ascii_acc:
            line, ascii_acc = ascii_acc.split(b"\n", 1)
            # skip binary-ish
            try:
                text = line.decode("utf-8", "replace").strip()
            except Exception:
                continue
            if text and (text.startswith("[") or "RESET" in text or "panic" in text
                         or "Boot" in text or "Guru" in text or "Rebooting" in text
                         or "CABINET_BOOT" in text or "MESH" in text or "MAIN" in text
                         or "FP" in text or "Storage" in text or "====" in text
                         or "ESP32" in text or "Firmware" in text or "Parent" in text
                         or "route" in text.lower() or "wifi" in text.lower()):
                raw_lines.append((time.time() - t0, label, text[:220]))

        events, consumed = parse_frames(buf)
        if consumed:
            buf = buf[consumed:]
        if len(buf) > 16384:
            buf = buf[-8192:]
        for payload in events:
            kind, cmd, did, msg = extract_event(payload)
            results.append((time.time() - t0, label, kind, cmd, did, msg))
    s.close()


def send_read_status(port, device_id, timeout=6.0):
    s = serial.Serial(port, BAUD, timeout=0.05)
    time.sleep(0.2)
    s.reset_input_buffer()
    cmd = json.dumps({
        "cmd": "READ_STATUS",
        "device_id": device_id,
        "data": {},
        "timestamp": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
    }, ensure_ascii=False)
    frame = encode_frame(cmd)
    t0 = time.time()
    s.write(frame)
    s.flush()
    buf = b""
    matched = None
    logs = []
    while time.time() - t0 < timeout:
        n = s.in_waiting
        if n:
            buf += s.read(n)
        else:
            time.sleep(0.01)
        events, consumed = parse_frames(buf)
        if consumed:
            buf = buf[consumed:]
        for payload in events:
            kind, cmd, did, msg = extract_event(payload)
            if cmd == "STATUS_RESPONSE" and (not did or device_id in did or did == device_id):
                matched = (kind, cmd, did, msg, (time.time() - t0) * 1000)
                break
            if cmd == "LOG":
                logs.append(msg[:160])
        if matched:
            break
    s.close()
    return matched, logs


def main():
    print(f"=== Dual diagnostics {datetime.now()} ===")
    print(f"ROOT={ROOT_PORT} CAB={CAB_PORT} listen={LISTEN_S}s\n")

    results = []
    raw_lines = []
    stop_at = time.time() + LISTEN_S
    t_root = threading.Thread(target=monitor, args=(ROOT_PORT, "ROOT", results, stop_at, raw_lines))
    t_cab = threading.Thread(target=monitor, args=(CAB_PORT, "CAB", results, stop_at, raw_lines))
    t_root.start()
    t_cab.start()
    t_root.join()
    t_cab.join()

    results.sort(key=lambda x: x[0])
    raw_lines.sort(key=lambda x: x[0])

    print("--- Key events (filtered) ---")
    for t, label, kind, cmd, did, msg in results:
        # filter noise
        if label == "ROOT" and cmd == "REGISTER" and ("ROOT_" in did or "ROOT" in did):
            continue
        if cmd == "LOG" and "uplink receive" in msg:
            continue
        if cmd in ("HEARTBEAT", "HEARTBEAT_ACK", "STATUS_REPORT") and label == "ROOT" and "ROOT" in did:
            # keep occasional
            pass
        print(f"{t:6.1f}s [{label:4}] {kind:4} {cmd:16} {did:22} {msg}")

    print("\n--- ASCII lines (boot/mesh) ---")
    for t, label, text in raw_lines:
        print(f"{t:6.1f}s [{label:4}] {text}")

    print("\n=== Summary ===")
    cab_cmds = Counter(r[3] for r in results if r[1] == "CAB")
    root_cmds = Counter(r[3] for r in results if r[1] == "ROOT")
    print(f"CAB frame cmds:  {dict(cab_cmds)}")
    print(f"ROOT frame cmds: {dict(root_cmds)}")

    boots = [r for r in raw_lines if r[1] == "CAB" and ("CABINET_BOOT" in r[2] or "RESET_REASON" in r[2] or "Firmware v" in r[2])]
    print(f"CAB boot markers: {len(boots)}")
    for t, _, text in boots:
        print(f"  t={t:.1f}s {text}")

    route_add = [r for r in results if "route added" in r[5]]
    route_exp = [r for r in results if "route expired" in r[5]]
    print(f"route added={len(route_add)} expired={len(route_exp)}")
    for r in route_add + route_exp:
        print(f"  t={r[0]:.1f}s {r[5]}")

    # Active probe after passive listen (ports free)
    print("\n=== Active probe ===")
    for did in ["ROOT_001", "CABINET_001"]:
        matched, logs = send_read_status(ROOT_PORT, did, timeout=6.0)
        if matched:
            kind, cmd, mid, msg, ms = matched
            print(f"READ_STATUS {did}: OK {ms:.0f}ms did={mid} data={msg[:120]}")
        else:
            print(f"READ_STATUS {did}: FAIL")
            for lg in logs[-8:]:
                print(f"  LOG: {lg}")


if __name__ == "__main__":
    main()
