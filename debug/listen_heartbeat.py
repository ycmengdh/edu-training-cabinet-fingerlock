"""Listen for heartbeat/ack/route health on COM16."""
import json
import serial
import time

PORT = "COM16"
BAUD = 921600


def crc16(data):
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc


def parse(buf):
    ev = []
    i = 0
    while i < len(buf):
        if buf[i] != 0xA5:
            i += 1
            continue
        if i + 1 >= len(buf) or buf[i + 1] != 0x5A:
            i += 1
            continue
        if i + 5 > len(buf):
            break
        ver = buf[i + 2]
        length = (buf[i + 3] << 8) | buf[i + 4]
        if ver not in (1, 2):
            i += 1
            continue
        end = i + 5 + length + 2
        if end > len(buf):
            break
        pl = buf[i + 5:i + 5 + length]
        cr = (buf[i + 5 + length] << 8) | buf[i + 5 + length + 1]
        if cr == crc16(bytes([ver, (length >> 8) & 0xFF, length & 0xFF]) + pl):
            ev.append(pl)
        i = end
    return ev, i


def main():
    s = serial.Serial(PORT, BAUD, timeout=0.05)
    time.sleep(0.2)
    s.reset_input_buffer()
    t0 = time.time()
    buf = b""
    cmds = {}
    while time.time() - t0 < 30:
        n = s.in_waiting
        if n:
            buf += s.read(n)
        else:
            time.sleep(0.01)
        ev, c = parse(buf)
        if c:
            buf = buf[c:]
        for p in ev:
            if len(p) < 18 or p[0] != 0xB1 or p[1] != 0x0F:
                continue
            cid = p[4] | (p[5] << 8)
            dl = p[10]
            sl = p[11]
            plen = p[12] | (p[13] << 8)
            pos = 18
            did = p[pos:pos + dl].decode("utf-8", "replace") if pos + dl <= len(p) else ""
            pos2 = pos + dl + sl
            pl = p[pos2:pos2 + plen] if pos2 + plen <= len(p) else b""
            name = {
                1: "REG", 2: "HB", 3: "HB_ACK", 0x35: "ST_R", 0x36: "ST_P",
                6: "LOG", 0xFE: "ERR",
            }.get(cid, hex(cid))
            cmds[name] = cmds.get(name, 0) + 1
            if name == "LOG":
                try:
                    msg = json.loads(pl.decode()).get("msg", "")
                except Exception:
                    msg = pl[:80].decode("latin-1", "replace")
                keys = ("route", "HEARTBEAT", "ACK", "send", "queue", "fail",
                        "recovery", "forward", "received app", "REGISTER",
                        "Root", "MESH", "permission", "Permission", "process command",
                        "STORAGE", "restored fingerprint")
                if any(k in str(msg) for k in keys):
                    print(f"{time.time()-t0:5.1f} LOG {msg[:170]}")
            elif name == "HB":
                print(f"{time.time()-t0:5.1f} HB     {did} hex={pl[:24].hex()}")
            elif name not in ("REG",):
                text = pl.decode("utf-8", "replace")[:80]
                print(f"{time.time()-t0:5.1f} {name:6} {did} {text}")
    print("counts", cmds)
    s.close()


if __name__ == "__main__":
    main()
