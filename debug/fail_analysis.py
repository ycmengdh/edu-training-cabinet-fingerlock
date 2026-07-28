"""Capture failure reasons for READ_STATUS to cabinet with full event dump."""
import json
import serial
import time
from datetime import datetime
from collections import Counter

PORT = "COM16"
BAUD = 921600
CAB_ID = "CAB_ACA704E38558"
ROOT_ID = "ROOT_B81F3FA9F404"

FRAME_HEAD1 = 0xA5
FRAME_HEAD2 = 0x5A
CMD_NAMES = {
    0x0001: "REGISTER", 0x0002: "HEARTBEAT", 0x0003: "HEARTBEAT_ACK",
    0x0010: "READ_STATUS", 0x0035: "STATUS_RESPONSE", 0x0036: "STATUS_REPORT",
    0x00F0: "LOG", 0x00FE: "ERROR", 0x0004: "ACK",
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


def extract(payload: bytes):
    if len(payload) >= 18 and payload[0] == 0xB1 and payload[1] == 0x0F:
        cmd_id = payload[4] | (payload[5] << 8)
        flags = payload[6]
        msg_id = payload[2] | (payload[3] << 8) if False else (payload[6] if False else 0)
        # layout from app_protocol: after magic
        # check actual layout
        msg_id = payload[2] | (payload[3] << 8)  # may be wrong - re-read
        # From earlier: cmd at 4-5, did_len at 10...
        # Actually from app_protocol.cpp comments:
        # Let's parse carefully from known structure used in diag_dual
        cmd_id = payload[4] | (payload[5] << 8)
        # flags might be at 6
        did_len = payload[10]
        src_len = payload[11]
        plen = payload[12] | (payload[13] << 8)
        pos = 18
        if pos + did_len + src_len + plen > len(payload):
            return CMD_NAMES.get(cmd_id, f"0x{cmd_id:04X}"), "", f"trunc len={len(payload)}", cmd_id
        did = payload[pos:pos + did_len].decode("utf-8", "replace")
        pos += did_len
        src = payload[pos:pos + src_len].decode("utf-8", "replace")
        pos += src_len
        pl = payload[pos:pos + plen]
        try:
            text = pl.decode("utf-8")
        except Exception:
            text = pl.hex()
        return CMD_NAMES.get(cmd_id, f"0x{cmd_id:04X}"), did, text[:250], cmd_id
    try:
        obj = json.loads(payload.decode("utf-8", "replace"))
        return obj.get("cmd", "?"), obj.get("device_id", ""), json.dumps(obj.get("data", {}), ensure_ascii=False)[:250], -1
    except Exception:
        return "raw", "", payload[:40].hex(), -2


def one_shot(s, device_id, timeout=5.0):
    payload = {
        "cmd": "READ_STATUS",
        "device_id": device_id,
        "data": {},
        "timestamp": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
    }
    frame = encode_frame(json.dumps(payload, ensure_ascii=False))
    s.reset_input_buffer()
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
            time.sleep(0.005)
        parsed, consumed = parse_frames(buf)
        if consumed:
            buf = buf[consumed:]
        for p in parsed:
            c, d, m, cid = extract(p)
            events.append((time.time() - t0, c, d, m, cid))
            if c in ("STATUS_RESPONSE", "ERROR") and (d == device_id or c == "ERROR" or d.startswith("ROOT") or d.startswith("CAB")):
                # keep collecting a bit more for related logs? break on match
                if c == "STATUS_RESPONSE" and (d == device_id or device_id in d):
                    return True, (time.time() - t0) * 1000, events
                if c == "ERROR":
                    return False, (time.time() - t0) * 1000, events
    return False, timeout * 1000, events


def main():
    print(f"=== Fail analysis {datetime.now()} ===\n")
    s = serial.Serial(PORT, BAUD, timeout=0.05)
    time.sleep(0.3)

    ok = fail = 0
    lats = []
    fail_details = []
    for i in range(30):
        success, ms, events = one_shot(s, CAB_ID, timeout=5.0)
        if success:
            ok += 1
            lats.append(ms)
            print(f"[{i+1:02d}] OK   {ms:7.0f}ms  events={len(events)}")
        else:
            fail += 1
            print(f"[{i+1:02d}] FAIL {ms:7.0f}ms  events={len(events)}")
            # dump interesting events
            cmds = Counter(e[1] for e in events)
            print(f"      cmds={dict(cmds)}")
            for e in events:
                if e[1] in ("ERROR", "LOG", "STATUS_RESPONSE", "HEARTBEAT", "ACK") or "route" in e[3] or "BRIDGE" in e[3] or "MESH" in e[3]:
                    print(f"      t={e[0]*1000:6.0f}ms {e[1]:16} {e[2]:22} {e[3][:140]}")
            fail_details.append(events)
        time.sleep(0.2)

    print(f"\n=== Result: {ok}/30 ok, {fail} fail = {ok/30*100:.0f}% ===")
    if lats:
        print(f"latency avg={sum(lats)/len(lats):.0f} min={min(lats):.0f} max={max(lats):.0f}")
        buckets = Counter()
        for ms in lats:
            if ms < 300: buckets["<300"] += 1
            elif ms < 700: buckets["300-700"] += 1
            elif ms < 1200: buckets["700-1200"] += 1
            else: buckets[">1200"] += 1
        print(f"buckets={dict(buckets)}")

    # Also check root continuously
    print("\n--- Root 10x ---")
    rok = 0
    for i in range(10):
        success, ms, _ = one_shot(s, ROOT_ID, timeout=2.0)
        print(f"  [{i+1:02d}] {'OK' if success else 'FAIL'} {ms:.0f}ms")
        if success:
            rok += 1
        time.sleep(0.15)
    print(f"Root {rok}/10")

    s.close()


if __name__ == "__main__":
    main()
