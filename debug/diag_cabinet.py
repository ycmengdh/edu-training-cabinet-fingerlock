"""监控柜子节点 COM10 (UART0) 通讯稳定性。

柜子 UART0 输出 A5 5A 帧（同根节点格式），日志里含 Mesh 事件、
HEARTBEAT 发送、REGISTER、Root ACK 接收等。抓 90s 看柜子是否在
持续发心跳，以及 Mesh 链路是否 flap。
"""
import serial
import time
import json
from datetime import datetime

PORT = "COM10"
BAUD = 921600
DURATION = 90.0

FRAME_HEAD1 = 0xA5
FRAME_HEAD2 = 0x5A
APP_MAGIC_LO = 0xB1
APP_MAGIC_HI = 0x0F
APP_ENVELOPE_MIN = 18

CMD_NAMES = {
    0x0001: "REGISTER", 0x0002: "HEARTBEAT", 0x0003: "HEARTBEAT_ACK",
    0x0004: "ACK", 0x0005: "ERROR", 0x0036: "STATUS_REPORT",
    0x0035: "STATUS_RESPONSE", 0x0034: "READ_STATUS",
    0x0020: "BEGIN_PERMISSION_SYNC", 0x0021: "SYNC_PERMISSION",
    0x0022: "COMMIT_PERMISSION_SYNC", 0x0024: "SYNC_ACK",
    0x0037: "TIME_SYNC", 0x0038: "REBOOT", 0x0039: "REBOOT_ACK",
    0x0060: "LOG_REPORT", 0x0061: "LOG_REPORT_ACK",
}


def crc16_modbus(data: bytes) -> int:
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc


def decode_app_envelope(payload: bytes):
    if len(payload) < APP_ENVELOPE_MIN:
        return None
    if payload[0] != APP_MAGIC_LO or payload[1] != APP_MAGIC_HI:
        return None
    flags = payload[3]
    cmd_id = payload[4] | (payload[5] << 8)
    msg_id = payload[6] | (payload[7] << 8)
    did_len = payload[10]
    src_len = payload[11]
    plen = payload[12] | (payload[13] << 8)
    pos = APP_ENVELOPE_MIN
    if pos + did_len + src_len + plen > len(payload):
        return None
    did = payload[pos:pos + did_len].decode("utf-8", errors="replace")
    pos += did_len
    src = payload[pos:pos + src_len].decode("utf-8", errors="replace")
    pos += src_len
    app_payload = payload[pos:pos + plen] if plen > 0 else b""
    return {
        "cmd_id": cmd_id, "cmd": CMD_NAMES.get(cmd_id, f"0x{cmd_id:04X}"),
        "msg_id": msg_id, "flags": flags,
        "device_id": did, "source_id": src,
        "payload": app_payload,
    }


def main():
    print(f"=== 柜子节点 COM10 稳定性监控 {DURATION:.0f}s ({datetime.now()}) ===\n")

    s = serial.Serial(PORT, BAUD, timeout=0.05)
    time.sleep(0.3)
    s.reset_input_buffer()

    start = time.time()
    buf = b""
    counts = {}
    heartbeat_send_times = []     # 柜子发出 HEARTBEAT 的时刻
    heartbeat_ack_times = []      # 收到 Root HEARTBEAT_ACK 的时刻
    register_times = []
    key_events = []               # (时刻, msg)

    # 关键字：Mesh 链路事件 + 心跳超时 + Root 不可达
    KEYWORDS = (
        "PARENT_CONNECTED", "PARENT_DISCONNECTED", "CHILD_CONNECTED",
        "CHILD_DISCONNECTED", "Root heartbeat ACK timeout",
        "Root link recovery", "parent unavailable", "parent recovery",
        "QUEUE_FULL", "no memory", "sendAppRaw failed", "sendToNode",
        "MESH_STARTED", "Mesh started", "registeredWithRoot",
        "cabinet REGISTER", "HEARTBEAT_ACK to", "HEARTBEAT_ACK failed",
        "rootResponseTimedOut", "reboot", "panic", "Guru Meditation",
        "Stack canary", "CORRUPT", "abort",
    )

    while time.time() - start < DURATION:
        n = s.in_waiting
        if n > 0:
            chunk = s.read(n)
            buf += chunk
        else:
            time.sleep(0.02)
            continue

        i = 0
        progress = 0
        while i < len(buf):
            if buf[i] != FRAME_HEAD1:
                i += 1
                continue
            if i + 1 >= len(buf) or buf[i + 1] != FRAME_HEAD2:
                i += 1
                continue
            if i + 5 > len(buf):
                break
            version = buf[i + 2]
            length = (buf[i + 3] << 8) | buf[i + 4]
            if version not in (0x01, 0x02):
                i += 1
                continue
            end = i + 5 + length + 2
            if end > len(buf):
                break
            payload = buf[i + 5:i + 5 + length]
            crc_recv = (buf[i + 5 + length] << 8) | buf[i + 5 + length + 1]
            if crc_recv != crc16_modbus(bytes([version, (length >> 8) & 0xFF, length & 0xFF]) + payload):
                i += 1
                continue

            now = time.time() - start
            ts_str = datetime.now().strftime("%H:%M:%S.%f")[:-3]

            app = decode_app_envelope(payload)
            if app is not None:
                cmd = app["cmd"]
                counts[cmd] = counts.get(cmd, 0) + 1
                if cmd == "HEARTBEAT":
                    heartbeat_send_times.append(now)
                elif cmd == "HEARTBEAT_ACK":
                    heartbeat_ack_times.append(now)
                    print(f"[{ts_str}] << HEARTBEAT_ACK received (Root -> 柜子)")
                elif cmd == "REGISTER":
                    register_times.append(now)
                    print(f"[{ts_str}] >> REGISTER sent did={app['device_id']}")
            else:
                try:
                    text = payload.decode("utf-8", errors="replace")
                    try:
                        obj = json.loads(text)
                        cmd = obj.get("cmd", "?")
                        counts[cmd] = counts.get(cmd, 0) + 1
                        if cmd == "LOG":
                            msg = obj.get("data", {}).get("msg", "")
                            level = obj.get("data", {}).get("level", "")
                            if any(k in msg for k in KEYWORDS):
                                key_events.append((now, msg))
                                print(f"[{ts_str}] LOG/{level}: {msg[:200]}")
                        elif cmd == "HEARTBEAT":
                            heartbeat_send_times.append(now)
                        elif cmd == "HEARTBEAT_ACK":
                            heartbeat_ack_times.append(now)
                        elif cmd == "REGISTER":
                            register_times.append(now)
                    except Exception:
                        counts["<raw_text>"] = counts.get("<raw_text>", 0) + 1
                        # 裸文本里也可能有关键字（如启动 banner）
                        if any(k in text for k in KEYWORDS):
                            key_events.append((now, text.strip()[:200]))
                            print(f"[{ts_str}] RAW: {text.strip()[:200]}")
                except Exception:
                    pass

            i = end
            progress = i
        buf = buf[progress:]

    s.close()

    # ====== 汇总 ======
    print(f"\n{'='*60}")
    print(f"=== {DURATION:.0f}s 帧统计 ===")
    print(f"{'='*60}")
    total = sum(counts.values())
    print(f"总帧数: {total}\n")
    for k, v in sorted(counts.items(), key=lambda x: -x[1]):
        print(f"  {v:5d}x  {k}")

    print(f"\n{'='*60}")
    print(f"=== 柜子发出 HEARTBEAT 序列 ===")
    print(f"{'='*60}")
    print(f"共 {len(heartbeat_send_times)} 次 HEARTBEAT 发送")
    if heartbeat_send_times:
        print(f"时刻(s): {[round(x,1) for x in heartbeat_send_times]}")
        if len(heartbeat_send_times) >= 2:
            intervals = [heartbeat_send_times[i+1]-heartbeat_send_times[i]
                         for i in range(len(heartbeat_send_times)-1)]
            print(f"间隔(s): {[round(x,1) for x in intervals]}")
            print(f"平均间隔: {sum(intervals)/len(intervals):.1f}s (期望 10s)")
    else:
        print("⚠ 90s 内柜子未发送任何 HEARTBEAT")

    print(f"\n{'='*60}")
    print(f"=== 收到 Root HEARTBEAT_ACK 序列 ===")
    print(f"{'='*60}")
    print(f"共 {len(heartbeat_ack_times)} 次 ACK")
    if heartbeat_ack_times:
        print(f"时刻(s): {[round(x,1) for x in heartbeat_ack_times]}")

    print(f"\n{'='*60}")
    print(f"=== REGISTER 时刻 ===")
    print(f"{'='*60}")
    print(f"共 {len(register_times)} 次")
    if register_times:
        print(f"时刻(s): {[round(x,1) for x in register_times]}")

    print(f"\n{'='*60}")
    print(f"=== 关键诊断事件（{len(key_events)} 条）===")
    print(f"{'='*60}")
    for t, msg in key_events:
        print(f"  t={t:6.1f}s  {msg[:200]}")


if __name__ == "__main__":
    main()
