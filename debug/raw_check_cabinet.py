"""抓柜子 30 秒 raw 输出，按行打印所有内容"""
import serial
import time

PORT = "COM10"
BAUD = 921600

s = serial.Serial(PORT, BAUD, timeout=0.1)
time.sleep(0.3)
s.reset_input_buffer()

print("抓柜子 30 秒 raw 输出：")
print("-" * 60)
start = time.time()
all_data = b""
while time.time() - start < 30:
    n = s.in_waiting
    if n > 0:
        data = s.read(n)
        all_data += data
    else:
        time.sleep(0.02)

try:
    text = all_data.decode("utf-8", errors="replace")
    lines = text.split("\n")
    # 打印所有非空行
    printed = 0
    for i, line in enumerate(lines):
        if line.strip():
            print(f"  L{i:3d}: {line[:200]}")
            printed += 1
            if printed >= 100:
                print(f"  ... (更多行省略)")
                break
    print(f"\n共 {len(lines)} 行, 总计 {len(all_data)} 字节")
except Exception as e:
    print(f"解码失败: {e}")

s.close()
