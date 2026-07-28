"""精确诊断：发 1 次 READ_STATUS to CABINET，统计根节点响应行为"""
import serial
import time
import json
from datetime import datetime

PORT = "COM16"
BAUD = 921600

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


def main():
    print(f"=== 精确诊断 ({datetime.now()}) ===")
    s = serial.Serial(PORT, BAUD, timeout=0.05)
    time.sleep(0.5)
    s.reset_input_buffer()

    # 等 3 秒让根节点稳定
    print("等待 3 秒让根节点稳定...")
    time.sleep(3)
    s.reset_input_buffer()

    # 发 1 次 READ_STATUS to CABINET_001
    cmd_json = '{"cmd":"READ_STATUS","device_id":"CABINET_001","data":{},"timestamp":"2026-07-19 15:00:00"}'
    print(f"\n>>> 发送 1 次: READ_STATUS -> CABINET_001")
    frame = encode_frame(cmd_json)
    t0 = time.time()
    s.write(frame)
    s.flush()
    print(f"    发送完成 (帧大小 {len(frame)} 字节)")

    # 收 8 秒，分类统计
    buf = b""
    bridge_recv_count = 0       # [BRIDGE] << uplink receive READ_STATUS 日志数
    forward_count = 0            # [BRIDGE] forward to ... 日志数
    mesh_send_fail_count = 0     # sendToNode failed 日志数
    status_response_count = 0    # STATUS_RESPONSE @ CABINET_001 帧
    cabinet_msgs_received = 0    # [MESH] received message from ... 日志数
    all_log_msgs = []            # 所有 [BRIDGE] 和 [MESH] 日志

    while time.time() - t0 < 8:
        n = s.in_waiting
        if n > 0:
            buf += s.read(n)
        else:
            time.sleep(0.005)

        for text in decode_frames(buf):
            try:
                obj = json.loads(text)
                cmd = obj.get("cmd", "")
                if cmd == "LOG":
                    msg = obj.get("data", {}).get("msg", "")
                    if "BRIDGE" in msg or "MESH" in msg:
                        all_log_msgs.append((time.time() - t0, msg[:200]))
                    if "[BRIDGE] << uplink receive" in msg and "READ_STATUS" in msg:
                        bridge_recv_count += 1
                    if "[BRIDGE] forward to" in msg:
                        forward_count += 1
                    if "sendToNode failed" in msg or "esp_mesh_send failed" in msg:
                        mesh_send_fail_count += 1
                    if "[MESH] received message from" in msg:
                        cabinet_msgs_received += 1
                elif cmd == "STATUS_RESPONSE":
                    status_response_count += 1
                    print(f"  [{time.time()-t0:.3f}s] 收到 STATUS_RESPONSE")
            except Exception:
                pass
        buf = b""  # 清空已解析的 buf，避免重复

    print(f"\n=== 8 秒统计 ===")
    print(f"  [BRIDGE] << uplink receive READ_STATUS 次数: {bridge_recv_count}")
    print(f"  [BRIDGE] forward to 次数:                  {forward_count}")
    print(f"  [MESH] received message from 次数:          {cabinet_msgs_received}")
    print(f"  sendToNode failed 次数:                      {mesh_send_fail_count}")
    print(f"  STATUS_RESPONSE @ CABINET_001 帧数:          {status_response_count}")

    print(f"\n=== 所有 BRIDGE/MESH 日志（按时间）===")
    for t, msg in all_log_msgs:
        print(f"  [{t:.3f}s] {msg}")

    # 多等几秒看根节点状态报告
    print(f"\n=== 等 5 秒继续监听 ===")
    extra_start = time.time()
    while time.time() - extra_start < 5:
        n = s.in_waiting
        if n > 0:
            buf += s.read(n)
        else:
            time.sleep(0.02)
        for text in decode_frames(buf):
            try:
                obj = json.loads(text)
                cmd = obj.get("cmd", "")
                if cmd == "STATUS_REPORT" or cmd == "LOG":
                    msg = obj.get("data", {}).get("msg", "")
                    if msg:
                        print(f"  [{time.time()-extra_start:.3f}s] LOG: {msg[:150]}")
                    else:
                        data = obj.get("data", {})
                        print(f"  [{time.time()-extra_start:.3f}s] {cmd}: uptime={data.get('uptime')}s child={data.get('child_count')} route={data.get('route_count')}")
            except Exception:
                pass
        buf = b""

    s.close()


if __name__ == "__main__":
    main()
