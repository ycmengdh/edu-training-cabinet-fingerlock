"""Probe with actual MAC-based device IDs + dump routes/logs."""
import json
import serial
import time
from datetime import datetime

PORT = "COM16"
BAUD = 921600
ROOT_ID = "ROOT_B81F3FA9F404"
CAB_ID = "CAB_ACA704E38558"

FRAME_HEAD1 = 0xA5
FRAME_HEAD2 = 0x5A


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
            if i + 1 >= len(buf):
                break
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


def extract(payload: bytes):
    if len(payload) >= 18 and payload[0] == 0xB1 and payload[1] == 0x0F:
        cmd_id = payload[4] | (payload[5] << 8)
        did_len = payload[10]
        src_len = payload[11]
        plen = payload[12] | (payload[13] << 8)
        pos = 18
        if pos + did_len + src_len + plen > len(payload):
            return f"BIN 0x{cmd_id:04X}", "", ""
        did = payload[pos:pos + did_len].decode("utf-8", "replace")
        pos += did_len
        src = payload[pos:pos + src_len].decode("utf-8", "replace")
        pos += src_len
        pl = payload[pos:pos + plen]
        # try utf8 else hex
        try:
            text = pl.decode("utf-8")
        except Exception:
            text = pl.hex()
        names = {1: "REGISTER", 2: "HEARTBEAT", 3: "HEARTBEAT_ACK",
                 0x10: "READ_STATUS", 0x35: "STATUS_RESPONSE", 0x36: "STATUS_REPORT",
                 0xF0: "LOG", 0x11: "REGISTER_ACK"}
        return names.get(cmd_id, f"0x{cmd_id:04X}"), did, text[:300]
    try:
        obj = json.loads(payload.decode("utf-8", "replace"))
        cmd = obj.get("cmd", "?")
        did = obj.get("device_id", "")
        data = obj.get("data", {})
        return cmd, did, json.dumps(data, ensure_ascii=False)[:300]
    except Exception:
        return "raw", "", payload[:60].hex()


def send_and_collect(s, cmd, device_id, timeout=6.0, data=None):
    payload = {
        "cmd": cmd,
        "device_id": device_id,
        "data": data or {},
        "timestamp": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
    }
    frame = encode_frame(json.dumps(payload, ensure_ascii=False))
    t0 = time.time()
    s.write(frame)
    s.flush()
    buf = b""
    events = []
    while time.time() - t0 < timeout:
        n = s.in_waiting
        if n:
            buf += s.read(n)
        else:
            time.sleep(0.01)
        parsed, consumed = parse_frames(buf)
        if consumed:
            buf = buf[consumed:]
        for p in parsed:
            c, d, m = extract(p)
            events.append((time.time() - t0, c, d, m))
    return events


def main():
    print(f"=== Real-ID probe {datetime.now()} ===\n")
    s = serial.Serial()
    s.port = PORT
    s.baudrate = BAUD
    s.timeout = 0.05
    s.rtscts = False
    s.dsrdtr = False
    s.xonxoff = False
    s.dtr = False
    s.rts = False
    s.open()
    time.sleep(0.3)

    # Passive 12s listen first
    print("--- Passive 12s ---")
    t0 = time.time()
    buf = b""
    while time.time() - t0 < 12:
        n = s.in_waiting
        if n:
            buf += s.read(n)
        else:
            time.sleep(0.02)
        parsed, consumed = parse_frames(buf)
        if consumed:
            buf = buf[consumed:]
        for p in parsed:
            c, d, m = extract(p)
            if c == "REGISTER" and d.startswith("ROOT"):
                continue
            print(f"  {time.time()-t0:5.1f}s {c:16} {d:22} {m[:120]}")

    for did in [ROOT_ID, CAB_ID, "ROOT_001", "CABINET_001"]:
        print(f"\n--- READ_STATUS -> {did} ---")
        events = send_and_collect(s, "READ_STATUS", did, timeout=5.0)
        status = [e for e in events if e[1] in ("STATUS_RESPONSE", "ERROR")]
        logs = [e for e in events if e[1] == "LOG" and any(k in e[3] for k in ("BRIDGE", "MESH", "route", "forward", "send"))]
        if status:
            for e in status:
                print(f"  OK t={e[0]*1000:.0f}ms {e[1]} {e[2]} {e[3][:160]}")
        else:
            print("  FAIL no STATUS_RESPONSE")
        for e in logs[:12]:
            print(f"  LOG t={e[0]*1000:.0f}ms {e[3][:160]}")

    # stability: 15x READ_STATUS to real cab id
    print(f"\n--- Stability 15x READ_STATUS -> {CAB_ID} ---")
    ok = 0
    fails = 0
    lats = []
    for i in range(15):
        events = send_and_collect(s, "READ_STATUS", CAB_ID, timeout=4.0)
        status = [e for e in events if e[1] == "STATUS_RESPONSE"]
        if status:
            ok += 1
            lats.append(status[0][0] * 1000)
            print(f"  [{i+1:02d}] OK {lats[-1]:6.0f}ms")
        else:
            fails += 1
            print(f"  [{i+1:02d}] FAIL")
        time.sleep(0.25)
    print(f"\n  success={ok}/15 fail={fails}")
    if lats:
        print(f"  avg={sum(lats)/len(lats):.0f}ms min={min(lats):.0f} max={max(lats):.0f}")

    s.close()


if __name__ == "__main__":
    main()
