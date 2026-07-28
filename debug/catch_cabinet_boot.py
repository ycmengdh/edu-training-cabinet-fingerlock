"""柜子烧录后立即抓 60 秒启动日志"""
import serial
import time
import sys

PORT = "COM10"
BAUD = 921600

s = serial.Serial(PORT, BAUD, timeout=0.05)
time.sleep(0.3)
s.reset_input_buffer()

print("抓柜子 60 秒输出：")
print("-" * 60)
start = time.time()
last_print = start
while time.time() - start < 60:
    n = s.in_waiting
    if n > 0:
        data = s.read(n)
        try:
            text = data.decode("utf-8", errors="replace")
            elapsed = time.time() - start
            # 只打印可读字符
            for line in text.split("\n"):
                if line.strip():
                    # 去掉协议帧二进制
                    printable = "".join(c if 32 <= ord(c) < 127 or c in "\r\t" else "." for c in line)
                    if len(printable) > 5:
                        print(f"  [{elapsed:6.1f}s] {printable[:200]}")
        except Exception:
            pass
    else:
        time.sleep(0.01)

s.close()
print("\n完成")
