"""
直连根节点 COM16 测试 Mesh 链路稳定性。
自动发送 CONTROL_LOCK 命令到 CABINET_001，等待 ACK，验证：
1. USB 串口通讯是否正常
2. 协议帧编解码是否正确
3. Mesh 链路转发是否成功
4. 柜子是否能响应 ACK

用法：
  python test_unlock.py --port COM16 --baud 921600
  python test_unlock.py --port COM16 --baud 115200
"""
import argparse
import serial
import struct
import time
import json
import uuid
from datetime import datetime

# 协议帧格式：0xA5 0x5A + version(1B) + length(2B BE) + JSON + CRC16(2B LE)
FRAME_HEAD = b'\xA5\x5A'
FRAME_VERSION = 0x01


def crc16_modbus(data: bytes) -> int:
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            if crc & 1:
                crc = (crc >> 1) ^ 0xA001
            else:
                crc >>= 1
    return crc & 0xFFFF


def encode_frame(json_str: str) -> bytes:
    payload = json_str.encode('utf-8')
    length = len(payload)
    # CRC 计算范围：版本+长度+负载（不含帧头 0xA5 0x5A）
    crc_body = bytes([FRAME_VERSION]) + struct.pack('>H', length) + payload
    crc = crc16_modbus(crc_body)
    # CRC 字节序：大端（与固件一致：高字节在前）
    return FRAME_HEAD + crc_body + struct.pack('>H', crc)


def decode_frames_from_buffer(buf: bytes):
    """从缓冲区解析所有完整帧，返回 (frames, remaining_bytes)"""
    frames = []
    pos = 0
    while pos < len(buf):
        # 找帧头
        head_idx = buf.find(FRAME_HEAD, pos)
        if head_idx < 0:
            break
        if head_idx + 5 > len(buf):
            break
        version = buf[head_idx + 2]
        length = struct.unpack('>H', buf[head_idx + 3:head_idx + 5])[0]
        if length > 8192:
            pos = head_idx + 2
            continue
        frame_end = head_idx + 5 + length + 2
        if frame_end > len(buf):
            break
        payload = buf[head_idx + 5:head_idx + 5 + length]
        crc_recv = struct.unpack('>H', buf[head_idx + 5 + length:frame_end])[0]
        # CRC 计算范围：版本+长度+负载（不含帧头 0xA5 0x5A）
        crc_calc = crc16_modbus(buf[head_idx + 2:frame_end - 2])
        if crc_recv != crc_calc:
            pos = head_idx + 2
            continue
        try:
            json_obj = json.loads(payload.decode('utf-8'))
            frames.append(json_obj)
        except Exception as e:
            pass
        pos = frame_end
    return frames, buf[pos:]


def send_command_and_wait_response(ser, json_cmd, timeout=8.0):
    """发送命令并等待响应帧"""
    msg_id = str(uuid.uuid4())[:8]
    if 'msg_id' not in json_cmd:
        json_cmd['msg_id'] = msg_id

    frame = encode_frame(json.dumps(json_cmd, ensure_ascii=False))
    print(f"\n[{datetime.now().strftime('%H:%M:%S.%f')[:-3]}] >>> SEND: {json.dumps(json_cmd, ensure_ascii=False)}")
    print(f"  Frame bytes ({len(frame)}B): {frame.hex()}")

    # 不要调用 reset_input_buffer()！会触发 USB CDC 丢字节
    # 等待 200ms 让设备完成上一次发送，再写入新帧
    time.sleep(0.2)

    # 发送
    ser.write(frame)
    ser.flush()

    # 等待响应
    start = time.time()
    buf = b''
    responses = []
    while time.time() - start < timeout:
        if ser.in_waiting > 0:
            chunk = ser.read(ser.in_waiting)
            buf += chunk
            frames, buf = decode_frames_from_buffer(buf)
            for f in frames:
                print(f"[{datetime.now().strftime('%H:%M:%S.%f')[:-3]}] <<< RECV: {json.dumps(f, ensure_ascii=False)}")
                responses.append(f)
                # 收到 ACK 或 ERROR 就返回
                if f.get('cmd') in ('ACK', 'ERROR', 'CONTROL_LOCK_RESULT'):
                    return responses
        time.sleep(0.05)
    return responses


def test_unlock(ser, device_id="CABINET_001", lock_id=1):
    """测试开锁命令"""
    cmd = {
        "cmd": "CONTROL_LOCK",
        "device_id": device_id,
        "msg_id": str(uuid.uuid4())[:8],
        "data": {
            "lock_id": lock_id,
            "action": "open",
            "operator": "python_test"
        },
        "timestamp": datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    }
    return send_command_and_wait_response(ser, cmd, timeout=10.0)


def test_read_status(ser, device_id="CABINET_001"):
    """测试读取状态命令"""
    cmd = {
        "cmd": "READ_STATUS",
        "device_id": device_id,
        "msg_id": str(uuid.uuid4())[:8],
        "data": {},
        "timestamp": datetime.now().strftime("%Y-%m-%d %H:%M:%S")
    }
    return send_command_and_wait_response(ser, cmd, timeout=8.0)


def listen_background(ser, duration=10.0):
    """监听后台收到的所有帧（不发送命令）"""
    print(f"\n[监听模式] {duration}秒，只接收不发送...")
    start = time.time()
    buf = b''
    count = 0
    total_bytes = 0
    while time.time() - start < duration:
        if ser.in_waiting > 0:
            chunk = ser.read(ser.in_waiting)
            total_bytes += len(chunk)
            buf += chunk
            frames, buf = decode_frames_from_buffer(buf)
            for f in frames:
                count += 1
                cmd = f.get('cmd', '?')
                did = f.get('device_id', '?')
                print(f"  [{count}] cmd={cmd} device_id={did}")
        else:
            time.sleep(0.1)
    print(f"[监听结束] 共收到 {count} 帧，原始字节 {total_bytes}B")
    if total_bytes > 0:
        # 打印前 300 字节用于调试
        print(f"  原始数据前 300B: {buf[:300].hex()}")
        print(f"  原始数据前 100B ASCII: {buf[:100]}")


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--port', default='COM16', help='根节点串口名')
    parser.add_argument('--baud', type=int, default=921600, help='波特率')
    parser.add_argument('--mode', default='all',
                       choices=['all', 'listen', 'status', 'unlock'],
                       help='all=监听+读状态+开锁; listen=只监听; status=只读状态; unlock=只开锁')
    parser.add_argument('--device', default='CABINET_001', help='目标柜子 ID')
    parser.add_argument('--lock', type=int, default=1, help='锁号')
    args = parser.parse_args()

    print(f"=== Mesh 链路测试 ===")
    print(f"端口: {args.port}, 波特率: {args.baud}")
    print(f"目标设备: {args.device}, 锁号: {args.lock}")

    try:
        ser = serial.Serial(args.port, args.baud, timeout=0.1)
        print(f"串口已打开: {ser.name}")
    except Exception as e:
        print(f"打开串口失败: {e}")
        return

    try:
        # USB CDC 打开后需要时间稳定
        # 重要：不要调用 reset_input_buffer()！会触发 ESP32-S3 USB CDC 丢弃下个发送帧的前 2 字节
        time.sleep(1.0)

        if args.mode in ('all', 'listen'):
            listen_background(ser, duration=10.0)

        if args.mode in ('all', 'status'):
            responses = test_read_status(ser, args.device)
            if not responses:
                print("⚠️ 未收到 READ_STATUS 响应！")

        if args.mode in ('all', 'unlock'):
            responses = test_unlock(ser, args.device, args.lock)
            if not responses:
                print("⚠️ 未收到开锁响应！链路可能不通")
            else:
                ack_found = any(r.get('cmd') == 'ACK' for r in responses)
                if ack_found:
                    print("✅ 开锁成功！Mesh 链路稳定")
                else:
                    print(f"⚠️ 收到响应但无 ACK: {responses}")

    finally:
        ser.close()
        print("\n串口已关闭")


if __name__ == '__main__':
    main()
