"""长时监听柜子 COM10，抓 boot loop / panic backtrace。

特性：
  - 持续 600s，实时写文件 cabinet_long.bin
  - 每收到字节就打印时间戳和大致内容
  - 检测到 panic 关键字立即高亮
  - 异常时重试串口打开
"""
import serial
import time
import sys
from datetime import datetime

PORT = "COM10"
BAUD = 921600
DURATION = 600.0
OUTFILE = "cabinet_long.bin"

PANIC_KW = (b"Guru", b"panic", b"abort", b"assert", b"Backtrace",
            b"Rebooting", b"Stack canary", b"CORRUPT", b"WDT",
            b"watchdog", b"rst:0x", b"RESET_REASON", b"elf",
            b"CABINET_BOOT", b"PROTOCOL READY", b"0x40", b"0x3f",
            b"0x4", b"T0")


def reopen():
    s = serial.Serial(PORT, BAUD, timeout=0.1)
    s.reset_input_buffer()
    return s


def main():
    print(f"=== 长监听 {PORT} {DURATION:.0f}s ({datetime.now()}) ===")
    s = reopen()
    t0 = time.time()
    out = open(OUTFILE, "wb")
    total = 0
    last_report = t0
    last_data_t = t0

    while time.time() - t0 < DURATION:
        try:
            n = s.in_waiting
        except Exception as e:
            print(f"[{time.time()-t0:.0f}s] 串口异常: {e}, 重开...")
            try: s.close()
            except: pass
            time.sleep(1)
            s = reopen()
            continue
        if n > 0:
            chunk = s.read(n)
            out.write(chunk); out.flush()
            total += len(chunk)
            last_data_t = time.time()
            ts = datetime.now().strftime("%H:%M:%S.")
            ts += f"{int((time.time()*1000)%1000):03d}"
            # 检测 panic 关键字
            hit = [kw.decode() for kw in PANIC_KW if kw in chunk]
            if hit:
                print(f"[{ts}] *** {total}B HIT {hit} *** {chunk[:200]!r}")
            else:
                # 显示可打印片段
                printable = bytes(b if (0x20 <= b < 0x7F or b in (10,13,9)) else 0x2e for b in chunk)
                print(f"[{ts}] +{len(chunk)}B {printable[:150]!r}")
        else:
            time.sleep(0.05)
        # 60s 心跳
        if time.time() - last_report > 60:
            idle = time.time() - last_data_t
            print(f"--- t={time.time()-t0:.0f}s 累计 {total}B 静默 {idle:.0f}s ---")
            last_report = time.time()

    out.close()
    s.close()
    print(f"\n完成，共 {total} 字节 -> {OUTFILE}")


if __name__ == "__main__":
    main()
