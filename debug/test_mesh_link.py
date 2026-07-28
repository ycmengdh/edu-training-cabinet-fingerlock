"""
Mesh 三方通讯链路测试脚本
测试链路: 上位机(USB) <-> 根节点 <-> Mesh <-> 柜子节点

测试项目:
  1. 链路 1: 上位机 -> 根节点 -> 上位机 (READ_STATUS to ROOT_001)
  2. 链路 2: 上位机 -> 根节点 -> 柜子节点 -> 根节点 -> 上位机 (READ_STATUS to CABINET_001)
  3. 链路 3: 多次往返测试，验证稳定性和延迟
"""
import serial
import time
import json
import statistics
from datetime import datetime

PORT = "COM16"     # 根节点 USB 串口
BAUD = 921600
TIMEOUT = 5.0      # 单次响应超时
ROOT_ID = "ROOT_001"
CAB_ID = "CABINET_001"

FRAME_HEAD1 = 0xA5
FRAME_HEAD2 = 0x5A


def crc16_modbus(data):
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            if crc & 1:
                crc = (crc >> 1) ^ 0xA001
            else:
                crc >>= 1
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


def decode_frames(buf: bytes):
    """Yield decoded JSON strings from buffer."""
    i = 0
    while i < len(buf):
        if buf[i] != FRAME_HEAD1:
            i += 1
            continue
        if i + 1 >= len(buf) or buf[i + 1] != FRAME_HEAD2:
            i += 1
            continue
        if i + 5 > len(buf):
            return
        version = buf[i + 2]
        length = (buf[i + 3] << 8) | buf[i + 4]
        if version not in (0x01, 0x02):
            i += 1
            continue
        end = i + 5 + length + 2
        if end > len(buf):
            return
        payload = buf[i + 5:i + 5 + length]
        crc_recv = (buf[i + 5 + length] << 8) | buf[i + 5 + length + 1]
        crc_calc = crc16_modbus(bytes([version, (length >> 8) & 0xFF, length & 0xFF]) + payload)
        if crc_recv != crc_calc:
            i += 1
            continue
        try:
            yield payload.decode("utf-8", errors="replace")
        except Exception:
            pass
        i = end


def build_cmd(cmd: str, device_id: str, data: dict = None) -> str:
    payload = {
        "cmd": cmd,
        "device_id": device_id,
        "data": data or {},
        "timestamp": datetime.now().strftime("%Y-%m-%d %H:%M:%S"),
    }
    return json.dumps(payload, ensure_ascii=False)


def send_and_wait(s: serial.Serial, cmd_json: str, expect_cmd: str = None,
                  timeout: float = TIMEOUT) -> tuple:
    """Send a command and wait for matching response frame.
    Returns (matched_obj_or_None, all_frames, elapsed_ms)."""
    s.reset_input_buffer()
    frame = encode_frame(cmd_json)
    t0 = time.time()
    s.write(frame)
    s.flush()

    buf = b""
    matched = None
    all_frames = []
    while time.time() - t0 < timeout:
        n = s.in_waiting
        if n > 0:
            buf += s.read(n)
        else:
            time.sleep(0.02)

        for text in decode_frames(buf):
            try:
                obj = json.loads(text)
                all_frames.append(obj)
                if expect_cmd is None:
                    if obj.get("cmd") not in ("LOG", "REGISTER"):
                        matched = obj
                        break
                else:
                    if obj.get("cmd") == expect_cmd:
                        matched = obj
                        break
            except Exception:
                pass
        if matched:
            break
    elapsed_ms = (time.time() - t0) * 1000
    return matched, all_frames, elapsed_ms


def main():
    print(f"=== Mesh 三方通讯测试 ===")
    print(f"端口: {PORT}, 波特率: {BAUD}")
    print(f"时间: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")

    s = serial.Serial(PORT, BAUD, timeout=0.05)
    time.sleep(0.5)
    s.reset_input_buffer()

    # ========================
    # 阶段 1: 链路 1 测试 - 上位机 <-> 根节点
    # ========================
    print("=" * 60)
    print("[阶段 1] 链路 1: 上位机 -> 根节点 -> 上位机")
    print("=" * 60)
    print(f"  发送: READ_STATUS -> {ROOT_ID}")

    cmd = build_cmd("READ_STATUS", ROOT_ID)
    matched, frames, elapsed = send_and_wait(s, cmd, "STATUS_RESPONSE")

    if matched:
        print(f"  ✓ 收到响应 ({elapsed:.0f}ms)")
        data = matched.get("data", {})
        print(f"    uptime={data.get('uptime')}s, mesh_layer={data.get('mesh_layer')}, "
              f"child_count={data.get('child_count')}, route_count={data.get('route_count')}")
    else:
        print(f"  ✗ 未收到响应 ({elapsed:.0f}ms, 收到 {len(frames)} 帧)")
        for f in frames[:5]:
            print(f"    - {f.get('cmd')}: {json.dumps(f.get('data', {}), ensure_ascii=False)[:100]}")

    # ========================
    # 阶段 2: 链路 2 测试 - 三方转发
    # ========================
    print("\n" + "=" * 60)
    print("[阶段 2] 链路 2: 上位机 -> 根节点 -> Mesh -> 柜子节点 -> 根节点 -> 上位机")
    print("=" * 60)
    print(f"  发送: READ_STATUS -> {CAB_ID}")

    cmd = build_cmd("READ_STATUS", CAB_ID)
    matched, frames, elapsed = send_and_wait(s, cmd, "STATUS_RESPONSE", timeout=8.0)

    if matched:
        print(f"  ✓ 收到柜子响应 ({elapsed:.0f}ms)")
        data = matched.get("data", {})
        print(f"    uptime={data.get('uptime')}s, mesh_layer={data.get('mesh_layer')}, "
              f"child_count={data.get('child_count')}")
        print(f"    lock_status={data.get('lock_status')}, "
              f"fingerprint_count={data.get('fingerprint_count')}, "
              f"perm_count={data.get('perm_count')}")
    else:
        print(f"  ✗ 未收到柜子响应 ({elapsed:.0f}ms, 收到 {len(frames)} 帧)")
        for f in frames[:5]:
            print(f"    - {f.get('cmd')}: {json.dumps(f.get('data', {}), ensure_ascii=False)[:100]}")

    # ========================
    # 阶段 3: 稳定性测试 - 多次往返
    # ========================
    print("\n" + "=" * 60)
    print("[阶段 3] 稳定性测试: 10 次往返延迟测试")
    print("=" * 60)

    targets = [("根节点", ROOT_ID), ("柜子节点", CAB_ID)]
    for label, did in targets:
        print(f"\n  >> 目标: {label} ({did})")
        delays = []
        successes = 0
        for i in range(10):
            cmd = build_cmd("READ_STATUS", did)
            matched, _, elapsed = send_and_wait(s, cmd, "STATUS_RESPONSE", timeout=8.0)
            ok = matched is not None
            if ok:
                successes += 1
                delays.append(elapsed)
                print(f"    [{i+1:2d}/10] OK  {elapsed:6.0f}ms")
            else:
                print(f"    [{i+1:2d}/10] FAIL (timeout)")
            time.sleep(0.3)

        print(f"\n  >> {label} 统计:")
        print(f"     成功率: {successes}/10 = {successes*10}%")
        if delays:
            print(f"     平均延迟: {statistics.mean(delays):.0f}ms")
            print(f"     最小延迟: {min(delays):.0f}ms")
            print(f"     最大延迟: {max(delays):.0f}ms")
            if len(delays) > 1:
                print(f"     抖动(标准差): {statistics.stdev(delays):.0f}ms")

    # ========================
    # 阶段 4: 验证柜子节点主动上报消息（HEARTBEAT/REGISTER）
    # ========================
    print("\n" + "=" * 60)
    print("[阶段 4] 监听柜子主动上报消息（10秒）")
    print("=" * 60)

    s.reset_input_buffer()
    start = time.time()
    buf = b""
    cabinet_msgs = 0
    root_msgs = 0
    other_msgs = 0
    while time.time() - start < 10:
        n = s.in_waiting
        if n > 0:
            buf += s.read(n)
        else:
            time.sleep(0.05)
        # try decode
        for text in decode_frames(buf):
            try:
                obj = json.loads(text)
                did = obj.get("device_id", "")
                cmd = obj.get("cmd", "")
                if did == CAB_ID:
                    cabinet_msgs += 1
                    if cmd in ("REGISTER", "HEARTBEAT", "STATUS_REPORT"):
                        print(f"    [CAB] {cmd}")
                elif did == ROOT_ID:
                    root_msgs += 1
                else:
                    other_msgs += 1
            except Exception:
                pass
        # truncate processed bytes (simplistic: just clear)
        if len(buf) > 8192:
            buf = buf[-8192:]

    print(f"\n  10秒内收到: 柜子消息={cabinet_msgs}, 根节点消息={root_msgs}, 其他={other_msgs}")
    print(f"  → 柜子主动消息透传到上位机的链路: {'✓ 通畅' if cabinet_msgs > 0 else '✗ 未收到'}")

    s.close()
    print("\n=== 测试完成 ===")


if __name__ == "__main__":
    main()
