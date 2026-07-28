"""监控根节点串口通讯稳定性。

被动嗅探 COM16 90 秒，解码 A5 5A 协议帧 + 二进制 App 信封，
统计：
  - 各 cmd 的帧数
  - HEARTBEAT / HEARTBEAT_ACK 到达时间序列与间隔
  - REGISTER / 路由过期 / ACK 发送失败 / Mesh 事件等关键日志
  - STATUS_REPORT 中的 route_count / child_count 时间序列

输出每一条关键事件的时间戳，便于定位 CAB 从 1->0 的时刻。
"""
import serial
import time
import json
from datetime import datetime

PORT = "COM16"
BAUD = 921600
DURATION = 90.0

FRAME_HEAD1 = 0xA5
FRAME_HEAD2 = 0x5A

# App 信封魔数 B1 0F（config_common.h: APP_MAGIC_0/1）
APP_MAGIC_LO = 0xB1
APP_MAGIC_HI = 0x0F
APP_ENVELOPE_MIN = 18

# cmd_ids.h 子集（够诊断用）
CMD_NAMES = {
    0x0001: "REGISTER", 0x0002: "HEARTBEAT", 0x0003: "HEARTBEAT_ACK",
    0x0004: "ACK", 0x0005: "ERROR", 0x0010: "STATUS_REPORT",
    0x0011: "STATUS_RESPONSE", 0x0012: "READ_STATUS", 0x0013: "READ_CONFIG",
    0x0014: "CONFIG_RESPONSE", 0x0015: "WRITE_CONFIG", 0x0016: "CONFIG_SAVED",
    0x0017: "TIME_SYNC", 0x0018: "REBOOT", 0x0019: "REBOOT_ACK",
    0x0020: "SD_QUERY", 0x0021: "SD_QUERY_RESPONSE", 0x0022: "SD_SAVE",
    0x0023: "SD_SAVE_RESPONSE", 0x0024: "SD_QUERY_VERSION",
    0x0025: "SD_VERSION_RESPONSE", 0x0026: "SD_QUERY_PART",
    0x0027: "SD_QUERY_PART_ACK",
    0x0030: "LOG_REPORT", 0x0031: "LOG_REPORT_ACK",
    0x0040: "CONTROL_LOCK", 0x0041: "LOCK_STATUS",
    0x0050: "SYNC_PERMISSION", 0x0051: "SYNC_ACK",
    0x0052: "BEGIN_PERMISSION_SYNC", 0x0053: "COMMIT_PERMISSION_SYNC",
    0x0060: "UPLOAD_FP_TEMPLATE", 0x0061: "FP_TEMPLATE_UPLOAD_RESPONSE",
    0x0062: "DOWNLOAD_FP_TEMPLATE", 0x0063: "FP_TEMPLATE_DOWNLOAD_RESPONSE",
    0x0064: "DELETE_FP_TEMPLATE", 0x0065: "FP_TEMPLATE_DELETE_RESPONSE",
    0x0070: "BRIDGE_READY",
}


def crc16_modbus(data: bytes) -> int:
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc


def decode_outer_frames(buf: bytes):
    """从 A5 5A 外层帧里逐个吐出 payload (bytes)。"""
    i = 0
    n = len(buf)
    while i < n:
        if buf[i] != FRAME_HEAD1:
            i += 1
            continue
        if i + 1 >= n or buf[i + 1] != FRAME_HEAD2:
            i += 1
            continue
        if i + 5 > n:
            return  # 不完整，等更多字节
        version = buf[i + 2]
        length = (buf[i + 3] << 8) | buf[i + 4]
        if version not in (0x01, 0x02):
            i += 1
            continue
        end = i + 5 + length + 2
        if end > n:
            return  # 不完整
        payload = buf[i + 5:i + 5 + length]
        crc_recv = (buf[i + 5 + length] << 8) | buf[i + 5 + length + 1]
        if crc_recv == crc16_modbus(bytes([version, (length >> 8) & 0xFF, length & 0xFF]) + payload):
            yield payload
            i = end
        else:
            i += 1


def decode_app_envelope(payload: bytes):
    """解二进制 App 信封（little-endian）。返回 dict 或 None。

    Wire layout (app_protocol.cpp):
      [0] magic0=0xB1  [1] magic1=0x0F  [2] proto_ver  [3] flags
      [4-5] cmd_id u16 LE  [6-7] msg_id u16 LE  [8-9] corr_id u16 LE
      [10] device_id_len  [11] source_id_len
      [12-13] payload_len u16 LE  [14-17] timestamp u32 LE
      [18..] device_id[N] source_id[M] payload[P]
    """
    if len(payload) < APP_ENVELOPE_MIN:
        return None
    if payload[0] != APP_MAGIC_LO or payload[1] != APP_MAGIC_HI:
        return None
    flags = payload[3]
    cmd_id = payload[4] | (payload[5] << 8)
    msg_id = payload[6] | (payload[7] << 8)
    corr_id = payload[8] | (payload[9] << 8)
    did_len = payload[10]
    src_len = payload[11]
    plen = payload[12] | (payload[13] << 8)
    ts = payload[14] | (payload[15] << 8) | (payload[16] << 16) | (payload[17] << 24)

    pos = APP_ENVELOPE_MIN  # 18
    if pos + did_len + src_len + plen > len(payload):
        return None
    did = payload[pos:pos + did_len].decode("utf-8", errors="replace")
    pos += did_len
    src = payload[pos:pos + src_len].decode("utf-8", errors="replace")
    pos += src_len
    app_payload = payload[pos:pos + plen] if plen > 0 else b""
    return {
        "cmd_id": cmd_id, "cmd": CMD_NAMES.get(cmd_id, f"0x{cmd_id:04X}"),
        "msg_id": msg_id, "corr_id": corr_id, "flags": flags,
        "device_id": did, "source_id": src,
        "payload": app_payload, "ts": ts,
    }


def main():
    print(f"=== 根节点串口稳定性监控 {DURATION:.0f}s ({datetime.now()}) ===")
    print(f"PORT={PORT} BAUD={BAUD}\n")

    s = serial.Serial(PORT, BAUD, timeout=0.05)
    time.sleep(0.3)
    s.reset_input_buffer()

    start = time.time()
    buf = b""
    counts = {}                 # cmd -> count
    heartbeat_times = []        # 收到柜子 HEARTBEAT 的时刻
    ack_times = []              # 收到柜子 HEARTBEAT_ACK 的时刻（其实柜子不发，这里记录 Root 发出去的方向不可见）
    register_times = []         # REGISTER 时刻
    status_reports = []         # (时刻, route_count, child_count, uptime)
    key_events = []             # (时刻, 描述)
    last_payload_text = None

    while time.time() - start < DURATION:
        n = s.in_waiting
        if n > 0:
            chunk = s.read(n)
            buf += chunk
        else:
            time.sleep(0.02)
            continue

        # 逐帧解析并从 buf 头部消费。
        new_buf = buf
        i = 0
        progress = 0
        while i < len(new_buf):
            if new_buf[i] != FRAME_HEAD1:
                i += 1
                continue
            if i + 1 >= len(new_buf) or new_buf[i + 1] != FRAME_HEAD2:
                i += 1
                continue
            if i + 5 > len(new_buf):
                break
            version = new_buf[i + 2]
            length = (new_buf[i + 3] << 8) | new_buf[i + 4]
            if version not in (0x01, 0x02):
                i += 1
                continue
            end = i + 5 + length + 2
            if end > len(new_buf):
                break
            payload = new_buf[i + 5:i + 5 + length]
            crc_recv = (new_buf[i + 5 + length] << 8) | new_buf[i + 5 + length + 1]
            if crc_recv != crc16_modbus(bytes([version, (length >> 8) & 0xFF, length & 0xFF]) + payload):
                i += 1
                continue
            # 成功解出一帧
            now = time.time() - start
            ts_str = datetime.now().strftime("%H:%M:%S.")
            ts_str += f"{int((time.time() * 1000) % 1000):03d}"

            app = decode_app_envelope(payload)
            if app is not None:
                cmd = app["cmd"]
                did = app["device_id"]
                src = app["source_id"]
                counts[cmd] = counts.get(cmd, 0) + 1

                if cmd == "HEARTBEAT":
                    heartbeat_times.append(now)
                elif cmd == "REGISTER":
                    register_times.append(now)
                elif cmd == "STATUS_REPORT":
                    try:
                        pl = json.loads(app["payload"].decode("utf-8", errors="replace"))
                        status_reports.append((now, pl.get("route_count"),
                                               pl.get("child_count"), pl.get("uptime")))
                    except Exception:
                        pass
                # 关键命令实时打印
                if cmd in ("REGISTER", "STATUS_REPORT", "ERROR"):
                    extra = ""
                    if cmd == "STATUS_REPORT":
                        try:
                            pl = json.loads(app["payload"].decode("utf-8", errors="replace"))
                            extra = f" route={pl.get('route_count')} child={pl.get('child_count')} up={pl.get('uptime')}s"
                        except Exception:
                            pass
                    print(f"[{ts_str}] {cmd:16s} did={did} src={src}{extra}")
            else:
                # 遗留 JSON
                try:
                    text = payload.decode("utf-8", errors="replace")
                    try:
                        obj = json.loads(text)
                        cmd = obj.get("cmd", "?")
                        did = obj.get("device_id", "?")
                        counts[cmd] = counts.get(cmd, 0) + 1
                        if cmd == "REGISTER":
                            register_times.append(now)
                        # LOG 帧抓关键诊断信息
                        if cmd == "LOG":
                            msg = obj.get("data", {}).get("msg", "")
                            keywords = ("route expired", "route added", "HEARTBEAT_ACK",
                                        "sendToNodeApp failed", "sendToNode failed",
                                        "PARENT_DISCONNECTED", "PARENT_CONNECTED",
                                        "CHILD_DISCONNECTED", "CHILD_CONNECTED",
                                        "Root heartbeat ACK timeout", "Root link recovery",
                                        "parent unavailable", "no memory", "QUEUE_FULL",
                                        "binary broadcast", "forward to", "not found, dropped")
                            if any(k in msg for k in keywords):
                                key_events.append((now, msg))
                                print(f"[{ts_str}] LOG: {msg[:200]}")
                        elif cmd == "STATUS_REPORT":
                            d = obj.get("data", {})
                            status_reports.append((now, d.get("route_count"),
                                                   d.get("child_count"), d.get("uptime")))
                            print(f"[{ts_str}] STATUS_REPORT did={did} route={d.get('route_count')} child={d.get('child_count')} up={d.get('uptime')}s")
                    except Exception:
                        # 非 JSON 文本（如裸日志）
                        if text.strip():
                            counts["<raw_text>"] = counts.get("<raw_text>", 0) + 1
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
    print(f"=== HEARTBEAT 到达序列（间隔，s）===")
    print(f"{'='*60}")
    print(f"共 {len(heartbeat_times)} 次 HEARTBEAT")
    if len(heartbeat_times) >= 2:
        intervals = [heartbeat_times[i+1] - heartbeat_times[i] for i in range(len(heartbeat_times)-1)]
        print(f"间隔序列: {[round(x,1) for x in intervals]}")
        print(f"平均间隔: {sum(intervals)/len(intervals):.1f}s")
        print(f"最小/最大: {min(intervals):.1f}s / {max(intervals):.1f}s")
        # 找间隔异常大的 gap（>15s 视为可疑）
        gaps = [(i, intervals[i]) for i in range(len(intervals)) if intervals[i] > 15]
        if gaps:
            print(f"\n⚠ 发现 {len(gaps)} 处 HEARTBEAT gap > 15s（疑似掉线）：")
            for idx, gap in gaps:
                print(f"  第 {idx}→{idx+1} 次: gap={gap:.1f}s (时刻 {heartbeat_times[idx]:.1f}s → {heartbeat_times[idx+1]:.1f}s)")
        else:
            print("✓ 未发现 >15s 的 HEARTBEAT gap")
    elif len(heartbeat_times) == 1:
        print("仅收到 1 次 HEARTBEAT，无法计算间隔")
    else:
        print("⚠ 90s 内未收到任何 HEARTBEAT — 柜子可能根本没在线，或心跳被吞")

    print(f"\n{'='*60}")
    print(f"=== REGISTER 时刻序列 ===")
    print(f"{'='*60}")
    print(f"共 {len(register_times)} 次 REGISTER")
    if register_times:
        print(f"时刻(s): {[round(x,1) for x in register_times]}")
        if len(register_times) >= 2:
            reg_intervals = [register_times[i+1]-register_times[i] for i in range(len(register_times)-1)]
            print(f"间隔(s): {[round(x,1) for x in reg_intervals]}")

    print(f"\n{'='*60}")
    print(f"=== STATUS_REPORT route_count 时间序列 ===")
    print(f"{'='*60}")
    print(f"共 {len(status_reports)} 次 STATUS_REPORT")
    if status_reports:
        print(f"{'时刻':>6} {'route':>6} {'child':>6} {'uptime':>7}")
        for t, r, c, u in status_reports:
            print(f"{t:6.1f} {str(r):>6} {str(c):>6} {str(u):>7}")
        # route_count 变化点
        print("\nroute_count 变化点：")
        prev = None
        for t, r, c, u in status_reports:
            if prev is not None and r != prev:
                print(f"  t={t:.1f}s: route {prev} -> {r}")
            prev = r
    else:
        print("⚠ 未收到 STATUS_REPORT（根节点每 60s 才发一次，90s 窗口可能只看到 1 次）")

    print(f"\n{'='*60}")
    print(f"=== 关键诊断事件（{len(key_events)} 条）===")
    print(f"{'='*60}")
    for t, msg in key_events:
        print(f"  t={t:6.1f}s  {msg[:200]}")


if __name__ == "__main__":
    main()
