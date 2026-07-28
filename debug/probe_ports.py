"""Quick probe of COM16 (root) and COM12 (cabinet)."""
import serial
import time

PORTS = ["COM16", "COM12"]
BAUD = 921600

for port in PORTS:
    try:
        s = serial.Serial()
        s.port = port
        s.baudrate = BAUD
        s.timeout = 0.1
        s.rtscts = False
        s.dsrdtr = False
        s.xonxoff = False
        s.dtr = False
        s.rts = False
        s.open()
        time.sleep(0.3)
        time.sleep(3.0)
        n = s.in_waiting
        data = s.read(n) if n else b""
        a55a = data.count(b"\xa5\x5a")
        print(f"{port}: open OK, bytes={len(data)}, A55A_count={a55a}")
        if data:
            print(f"  hex_head={data[:48].hex()}")
            # try ascii snippets
            text = data.decode("utf-8", errors="replace")
            printable = "".join(c if 32 <= ord(c) < 127 or c in "\r\n" else "." for c in text[:300])
            print(f"  text_head={printable!r}")
        else:
            print("  (no data in 3s)")
        s.close()
    except Exception as e:
        print(f"{port}: FAIL {e}")
