"""
直接测试 Mesh 上行链路：
1. 通过 UART0 (COM10) 给柜子发 READ_STATUS 命令
2. 同时监听根节点 USB (COM16)，看柜子的 STATUS_RESPONSE 是否能到达
3. 也监听柜子 UART0，看柜子是否发出 STATUS_RESPONSE
"""
import serial
import time
import json
import threading
from datetime import datetime

ROOT_PORT = "COM16"   # 根节点 USB
CAB_PORT = "COM10"    # 柜子 UART0
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


def monitor_root(results, duration):
    """监听根节点 USB，统计柜子主动上报的消息数"""
    try:
        s = serial.Serial(ROOT_PORT, BAUD, timeout=0.1)
        time.sleep(0.2)
        s.reset_input_buffer()
        start = time.time()
        buf = b""
        cabinet_msg_count = 0
        cabinet_msgs = []
        while time.time() - start < duration:
            n = s.in_waiting
            if n > 0:
                buf += s.read(n)
            else:
                time.sleep(0.05)
            # try decode
            new_texts = list(decode_frames(buf))
            if new_texts:
                buf = b""
                for text in new_texts:
                    try:
                        obj = json.loads(text)
                        if obj.get("device_id") == "CABINET_001":
                            cabinet_msg_count += 1
                            cabinet_msgs.append((time.time(), obj.get("cmd"), obj.get("data", {})))
                    except Exception:
                        pass
        s.close()
        results["root"] = {
            "count": cabinet_msg_count,
            "msgs": cabinet_msgs,
        }
    except Exception as e:
        print(f"[ROOT] error: {e}")
        results["root"] = None


def query_cabinet_via_uart(results):
    """通过柜子 UART0 发 READ_STATUS，看柜子是否回复"""
    try:
        s = serial.Serial(CAB_PORT, BAUD, timeout=0.1)
        time.sleep(0.3)
        s.reset_input_buffer()

        # 发 READ_STATUS 给柜子
        cmd = '{"cmd":"READ_STATUS","device_id":"CABINET_001","data":{},"timestamp":"2026-07-19 00:00:00"}'
        s.write(encode_frame(cmd))
        s.flush()
        print(f"[CAB] sent READ_STATUS via UART0")

        # 读回复
        start = time.time()
        buf = b""
        cabinet_responded = False
        cabinet_response = None
        while time.time() - start < 3:
            n = s.in_waiting
            if n > 0:
                buf += s.read(n)
            else:
                time.sleep(0.05)
            for text in decode_frames(buf):
                try:
                    obj = json.loads(text)
                    if obj.get("cmd") == "STATUS_RESPONSE" and obj.get("device_id") == "CABINET_001":
                        cabinet_responded = True
                        cabinet_response = obj
                        break
                except Exception:
                    pass
            if cabinet_responded:
                break

        s.close()
        results["cabinet"] = {
            "responded": cabinet_responded,
            "response": cabinet_response,
        }
    except Exception as e:
        print(f"[CAB] error: {e}")
        results["cabinet"] = None


def main():
    print(f"=== Mesh 上行链路测试 ===")
    print(f"时间: {datetime.now().strftime('%Y-%m-%d %H:%M:%S')}\n")

    # 启动根节点监听（持续 10 秒）
    DURATION = 10
    results = {}

    # 启动根节点监听线程
    rt = threading.Thread(target=monitor_root, args=(results, DURATION))
    rt.start()
    print(f"[ROOT] 开始监听根节点 USB ({DURATION}s)...")

    # 等 1 秒让根节点监听启动
    time.sleep(1)

    # 主线程：通过柜子 UART0 发 READ_STATUS（多次）
    print("\n[CAB] 通过柜子 UART0 发 READ_STATUS（柜子会通过 Mesh 回复到根节点 USB）")
    for i in range(5):
        query_cabinet_via_uart(results)
        cab = results.get("cabinet")
        if cab and cab["responded"]:
            print(f"  [{i+1}/5] 柜子 UART0 收到回复 ✓")
            data = cab["response"].get("data", {})
            print(f"    uptime={data.get('uptime')}s, mesh_layer={data.get('mesh_layer')}, "
                  f"fingerprint_count={data.get('fingerprint_count')}")
        else:
            print(f"  [{i+1}/5] 柜子 UART0 未回复 ✗")
        time.sleep(1.5)

    # 等监听线程结束
    rt.join()

    # 输出根节点统计
    r = results.get("root")
    if r:
        print(f"\n[ROOT] {DURATION}s 内收到柜子消息: {r['count']} 条")
        for t, cmd, data in r["msgs"]:
            t_str = datetime.fromtimestamp(t).strftime("%H:%M:%S.%f")[:-3]
            data_str = json.dumps(data, ensure_ascii=False)[:80]
            print(f"  {t_str} [{cmd}] {data_str}")


if __name__ == "__main__":
    main()
