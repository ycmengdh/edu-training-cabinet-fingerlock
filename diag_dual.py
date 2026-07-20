"""同时监听根节点 COM16 和柜子 COM10，120s。

把两边所有 LOG/命令带时间戳记下来，对齐分析柜子 boot loop
与根节点 route add/expire 的对应关系。
"""
import serial
import time
import json
import threading
from datetime import datetime

ROOT_PORT = "COM16"
CAB_PORT = "COM10"
BAUD = 921600
DURATION = 120.0

FRAME_HEAD1 = 0xA5
FRAME_HEAD2 = 0x5A


def crc16_modbus(data: bytes) -> int:
    crc = 0xFFFF
    for b in data:
        crc ^= b
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc


def parse_frames(buf: bytes):
    """从 buf 头部尽可能解析完整帧，返回 (events, consumed)。"""
    events = []
    i = 0
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
        events.append(payload)
        i = end
    return events, i


def extract_text(payload: bytes):
    """返回 (cmd, did, msg_or_payload_text)。"""
    # 二进制信封
    if len(payload) >= 18 and payload[0] == 0xB1 and payload[1] == 0x0F:
        cmd_id = payload[4] | (payload[5] << 8)
        did_len = payload[10]
        src_len = payload[11]
        plen = payload[12] | (payload[13] << 8)
        pos = 18
        if pos + did_len + src_len + plen > len(payload):
            return (f"0x{cmd_id:04X}", "", "")
        did = payload[pos:pos + did_len].decode("utf-8", "replace")
        pos += did_len + src_len
        pl = payload[pos:pos + plen].decode("utf-8", "replace")
        names = {0x0001:"REGISTER",0x0002:"HEARTBEAT",0x0003:"HEARTBEAT_ACK",
                 0x0036:"STATUS_REPORT",0x0035:"STATUS_RESPONSE"}
        return (names.get(cmd_id, f"0x{cmd_id:04X}"), did, pl[:200])
    # JSON
    try:
        obj = json.loads(payload.decode("utf-8", "replace"))
        cmd = obj.get("cmd", "?")
        did = obj.get("device_id", "")
        data = obj.get("data", {})
        if isinstance(data, dict):
            return (cmd, did, data.get("msg", json.dumps(data, ensure_ascii=False))[:200])
        return (cmd, did, str(data)[:200])
    except Exception:
        return ("raw", "", payload.decode("latin-1", "replace")[:200])


def monitor(port: str, label: str, results: list, stop_flag):
    try:
        s = serial.Serial(port, BAUD, timeout=0.05)
    except Exception as e:
        results.append((0, label, f"OPEN_FAIL: {e}"))
        return
    time.sleep(0.2)
    s.reset_input_buffer()
    buf = b""
    start = time.time()
    while not stop_flag["stop"] and time.time() - start < DURATION:
        n = s.in_waiting
        if n:
            buf += s.read(n)
        else:
            time.sleep(0.02)
            continue
        events, consumed = parse_frames(buf)
        buf = buf[consumed:]
        for payload in events:
            cmd, did, msg = extract_text(payload)
            t = time.time() - start
            results.append((t, label, cmd, did, msg))
    s.close()


def main():
    print(f"=== 双端口联合监听 {DURATION:.0f}s ({datetime.now()}) ===\n")
    results = []
    stop_flag = {"stop": False}
    t_root = threading.Thread(target=monitor, args=(ROOT_PORT, "ROOT", results, stop_flag))
    t_cab = threading.Thread(target=monitor, args=(CAB_PORT, "CAB", results, stop_flag))
    t_root.start()
    t_cab.start()
    t_root.join()
    t_cab.join()

    # 按时间排序输出
    results.sort(key=lambda x: x[0])
    print(f"共捕获 {len(results)} 条事件\n")
    print(f"{'t(s)':>7}  {'src':>4}  {'cmd':>16}  {'did':<20}  msg")
    print("-" * 110)
    for r in results:
        if len(r) == 3:
            t, label, err = r
            print(f"{t:7.2f}  {label:>4}  {err}")
            continue
        t, label, cmd, did, msg = r
        # 过滤掉根节点每 3s 的 REGISTER 噪声，只保留关键事件
        if label == "ROOT" and cmd == "REGISTER" and "ROOT_" in str(did):
            continue
        if label == "ROOT" and cmd == "LOG" and "uplink receive" in msg:
            continue
        print(f"{t:7.2f}  {label:>4}  {cmd:>16}  {str(did):<20}  {msg}")

    # 汇总
    print(f"\n{'='*60}")
    cab_events = [r for r in results if len(r) > 3 and r[1] == "CAB"]
    root_events = [r for r in results if len(r) > 3 and r[1] == "ROOT"]
    print(f"柜子侧事件: {len(cab_events)}")
    print(f"根节点侧事件: {len(root_events)}")

    # 柜子 boot 次数（同一句 STORAGE Config 重复出现 = boot 次数）
    cab_boots = [r for r in cab_events if len(r) > 4 and "STORAGE] Config:" in str(r[4])]
    print(f"\n柜子 boot 次数（看 [STORAGE] Config 日志）: {len(cab_boots)}")
    if cab_boots:
        print("  boot 时刻(s):", [round(r[0], 1) for r in cab_boots])

    # 根节点 route add/expire
    route_add = [r for r in root_events if len(r) > 4 and "route added" in str(r[4])]
    route_exp = [r for r in root_events if len(r) > 4 and "route expired" in str(r[4])]
    print(f"\n根节点 route added: {len(route_add)}")
    for r in route_add:
        print(f"  t={r[0]:.1f}s  {r[4]}")
    print(f"根节点 route expired: {len(route_exp)}")
    for r in route_exp:
        print(f"  t={r[0]:.1f}s  {r[4]}")

    # 柜子 HEARTBEAT
    cab_hb = [r for r in cab_events if len(r) > 3 and r[2] == "HEARTBEAT"]
    print(f"\n柜子 HEARTBEAT 发送: {len(cab_hb)}")
    if cab_hb:
        print("  时刻(s):", [round(r[0], 1) for r in cab_hb])

    # 柜子 panic/abort 关键字
    panic_kw = ("panic", "abort", "Guru", "Stack canary", "CORRUPT",
                "Backtrace", "Rebooting", "rst", "RESET_REASON")
    panics = [r for r in results if len(r) > 4 and any(k in str(r[4]) for k in panic_kw)]
    print(f"\npanic/重启相关事件: {len(panics)}")
    for r in panics:
        print(f"  t={r[0]:.1f}s [{r[1]}] {r[4]}")


if __name__ == "__main__":
    main()
