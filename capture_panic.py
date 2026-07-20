"""捕获根节点的 panic 信息（裸文本，绕过 LOG 帧）"""
import serial
import time
from datetime import datetime

PORT = "COM16"
BAUD = 921600
DURATION = 90

print(f"=== 监听 {PORT} {DURATION}s 捕获 panic（{datetime.now()}） ===")
s = serial.Serial(PORT, BAUD, timeout=0.1)
time.sleep(0.3)
s.reset_input_buffer()

start = time.time()
all_bytes = bytearray()
while time.time() - start < DURATION:
    n = s.in_waiting
    if n > 0:
        data = s.read(n)
        all_bytes += data
    else:
        time.sleep(0.05)

s.close()

# 打印所有可打印文本
print(f"\n总字节: {len(all_bytes)}")
text = []
for b in all_bytes:
    if 0x20 <= b < 0x7F or b in (0x0A, 0x0D, 0x09):
        text.append(chr(b))
    else:
        text.append(".")
text = "".join(text)

# 搜索 panic 关键词
keywords = ["Guru", "panic", "abort", "assert", "WDT", "Task watchdog",
            "Backtrace", "Rebooting", "elf", "0x4", "A0", "0x3F",
            "abort", "failed"]
print("\n=== 关键词搜索 ===")
for kw in keywords:
    idx = text.find(kw)
    if idx >= 0:
        print(f"\n  >>> 找到 '{kw}' 在位置 {idx}:")
        # 显示前后 500 字符
        s_idx = max(0, idx - 300)
        e_idx = min(len(text), idx + 500)
        print(text[s_idx:e_idx])
        print("---")
