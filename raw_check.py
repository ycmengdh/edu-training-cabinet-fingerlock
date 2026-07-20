"""抓根节点 raw 串口输出 15 秒，显示全部内容"""
import serial
import time

PORT = "COM16"
BAUD = 921600

s = serial.Serial(PORT, BAUD, timeout=0.1)
time.sleep(0.3)
s.reset_input_buffer()

print("抓根节点 15 秒 raw 输出：")
print("-" * 60)
start = time.time()
all_data = b""
while time.time() - start < 15:
    n = s.in_waiting
    if n > 0:
        data = s.read(n)
        all_data += data
    else:
        time.sleep(0.02)

# 按行打印
try:
    text = all_data.decode("utf-8", errors="replace")
    lines = text.split("\n")
    for i, line in enumerate(lines[:50]):
        if line.strip():
            print(f"  L{i:3d}: {line[:200]}")
    print(f"\n... 共 {len(lines)} 行")
except Exception as e:
    print(f"解码失败: {e}")
    print(f"前 500 字节: {all_data[:500]!r}")

print(f"\n总计 {len(all_data)} 字节")
s.close()
