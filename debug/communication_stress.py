"""Safe binary-protocol latency test for Root, Mesh, and cabinet UART links."""

from __future__ import annotations

import argparse
import json
import statistics
import time
from dataclasses import dataclass

import serial


BAUD = 921600
CMD_READ_STATUS = 0x0034
CMD_STATUS_RESPONSE = 0x0035
CMD_HEARTBEAT = 0x0002
CMD_ACK = 0x0004
CMD_CONTROL_LOCK = 0x0010
FRAME_VERSION = 0x01


def crc16_modbus(data: bytes) -> int:
    crc = 0xFFFF
    for value in data:
        crc ^= value
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc & 0xFFFF


def encode_app(command: int, message_id: int, device_id: str, payload: bytes) -> bytes:
    device = device_id.encode("utf-8")
    if len(device) > 255 or len(payload) > 65535:
        raise ValueError("application envelope is too large")
    header = bytearray((0xB1, 0x0F, 0x01, 0x00))
    header.extend(command.to_bytes(2, "little"))
    header.extend(message_id.to_bytes(2, "little"))
    header.extend((0).to_bytes(2, "little"))
    header.extend((len(device), 0))
    header.extend(len(payload).to_bytes(2, "little"))
    header.extend((0).to_bytes(4, "little"))
    return bytes(header) + device + payload


def encode_frame(payload: bytes) -> bytes:
    body = bytes((FRAME_VERSION,)) + len(payload).to_bytes(2, "big") + payload
    crc = crc16_modbus(body)
    return b"\xA5\x5A" + body + crc.to_bytes(2, "big")


def open_port_safely(port_name: str) -> serial.Serial:
    port = serial.Serial()
    port.port = port_name
    port.baudrate = BAUD
    port.bytesize = serial.EIGHTBITS
    port.parity = serial.PARITY_NONE
    port.stopbits = serial.STOPBITS_ONE
    port.timeout = 0
    port.write_timeout = 1
    port.dtr = False
    port.rts = False
    port.open()
    port.dtr = False
    port.rts = False
    return port


@dataclass
class AppMessage:
    command: int
    message_id: int
    device_id: str
    payload: bytes


class FrameReader:
    def __init__(self, port: serial.Serial):
        self.port = port
        self.buffer = bytearray()
        self.crc_errors = 0

    def read_messages(self) -> list[AppMessage]:
        available = self.port.in_waiting
        if available:
            self.buffer.extend(self.port.read(available))

        messages: list[AppMessage] = []
        while True:
            marker = self.buffer.find(b"\xA5\x5A")
            if marker < 0:
                if len(self.buffer) > 1:
                    del self.buffer[:-1]
                break
            if marker:
                del self.buffer[:marker]
            if len(self.buffer) < 7:
                break

            length = int.from_bytes(self.buffer[3:5], "big")
            frame_size = 7 + length
            if length <= 0 or frame_size > 131072:
                del self.buffer[0]
                continue
            if len(self.buffer) < frame_size:
                break

            body = bytes(self.buffer[2 : 5 + length])
            received_crc = int.from_bytes(self.buffer[5 + length : frame_size], "big")
            if self.buffer[2] != FRAME_VERSION or crc16_modbus(body) != received_crc:
                self.crc_errors += 1
                del self.buffer[0]
                continue

            payload = bytes(self.buffer[5 : 5 + length])
            del self.buffer[:frame_size]
            message = decode_app(payload)
            if message is not None:
                messages.append(message)
        return messages


def decode_app(data: bytes) -> AppMessage | None:
    if len(data) < 18 or data[:2] != b"\xB1\x0F":
        return None
    command = int.from_bytes(data[4:6], "little")
    message_id = int.from_bytes(data[6:8], "little")
    device_length = data[10]
    source_length = data[11]
    payload_length = int.from_bytes(data[12:14], "little")
    payload_offset = 18 + device_length + source_length
    payload_end = payload_offset + payload_length
    if payload_end > len(data):
        return None
    device_id = data[18 : 18 + device_length].decode("utf-8", "replace")
    return AppMessage(command, message_id, device_id, data[payload_offset:payload_end])


def percentile(values: list[float], fraction: float) -> float:
    ordered = sorted(values)
    index = max(0, min(len(ordered) - 1, int((len(ordered) - 1) * fraction + 0.5)))
    return ordered[index]


def decode_status_payload(payload: bytes) -> dict:
    try:
        value = json.loads(payload.decode("utf-8"))
        return value if isinstance(value, dict) else {}
    except (UnicodeDecodeError, json.JSONDecodeError):
        pass
    if len(payload) < 24 or payload[0] != 1:
        return {}
    lock_mask = payload[1]
    flags = payload[3]
    u16 = lambda offset: int.from_bytes(payload[offset:offset + 2], "little")
    u32 = lambda offset: int.from_bytes(payload[offset:offset + 4], "little")
    return {
        "uptime": u32(4),
        "lock_status": [(lock_mask >> bit) & 1 for bit in range(4)],
        "fingerprint_count": u16(8),
        "perm_count": u16(10),
        "perm_version": u32(12),
        "mesh_layer": payload[2],
        "mesh_send_failures": u16(16),
        "mesh_queue_full": u16(18),
        "mesh_link_rssi": int.from_bytes(payload[20:21], "little", signed=True),
        "mesh_assoc_expire": payload[21],
        "fp_poll_max_ms": u16(22),
        "work_mode": "mesh" if flags & 0x02 else "debug",
        "time_synced": bool(flags & 0x01),
        "fingerprint_ready": bool(flags & 0x04),
    }


def run_link(
    label: str,
    port_name: str,
    device_id: str,
    count: int,
    timeout_seconds: float,
    message_base: int,
    observer_port_name: str | None = None,
    probe: str = "status",
    retry_ms: int = 0,
    pace_ms: int = 50,
) -> bool:
    latencies: list[float] = []
    uptimes: list[int] = []
    duplicate_replays: list[int] = []
    link_events: list[str] = []
    failures = 0
    port = open_port_safely(port_name)
    reader = FrameReader(port)
    observer_port = (
        open_port_safely(observer_port_name) if observer_port_name is not None else None
    )
    observer_reader = FrameReader(observer_port) if observer_port is not None else None
    try:
        time.sleep(0.2)
        port.reset_input_buffer()
        if observer_port is not None:
            observer_port.reset_input_buffer()
        for request_index in range(count):
            message_id = (message_base + request_index) & 0xFFFF
            command = CMD_READ_STATUS if probe == "status" else CMD_CONTROL_LOCK
            expected_command = CMD_STATUS_RESPONSE if probe == "status" else CMD_ACK
            payload = (
                b"{}"
                if probe == "status"
                else bytes((request_index % 4, 0))
            )
            request = encode_frame(
                encode_app(command, message_id, device_id, payload)
            )
            started = time.perf_counter()
            port.write(request)
            port.flush()

            response = None
            device_process_ms = None
            cabinet_receive_ms = None
            cabinet_process_count = 0
            heartbeat_count = 0
            retries_sent = 0
            retry_deadlines = (
                [
                    started + retry_ms / 1000.0,
                    started + retry_ms / 1000.0 + 0.5,
                    started + retry_ms / 1000.0 + 1.5,
                ]
                if retry_ms > 0
                else []
            )
            deadline = started + timeout_seconds
            while time.perf_counter() < deadline:
                now = time.perf_counter()
                if retries_sent < len(retry_deadlines) and now >= retry_deadlines[retries_sent]:
                    port.write(request)
                    port.flush()
                    retries_sent += 1
                if observer_reader is not None:
                    for observed in observer_reader.read_messages():
                        if observed.command != 0x0006:
                            continue
                        try:
                            log_message = json.loads(
                                observed.payload.decode("utf-8")
                            ).get("msg", "")
                        except (UnicodeDecodeError, json.JSONDecodeError):
                            continue
                        if any(
                            marker in log_message
                            for marker in (
                                "PARENT_DISCONNECTED",
                                "PARENT_CONNECTED",
                                "heartbeat ACK timeout",
                                "Root link recovery",
                                "send failed err=",
                                "rebuilding cabinet Mesh stack",
                                "cabinet Mesh stack rebuild",
                            )
                        ):
                            link_events.append(log_message)
                        if (
                            f"process command: {'READ_STATUS' if probe == 'status' else 'CONTROL_LOCK'}"
                            in log_message
                            and f"msg_id={message_id}" in log_message
                        ):
                            cabinet_process_count += 1
                            if cabinet_receive_ms is None:
                                cabinet_receive_ms = (
                                    time.perf_counter() - started
                                ) * 1000.0
                for message in reader.read_messages():
                    if message.command == CMD_HEARTBEAT:
                        heartbeat_count += 1
                    if message.command == 0x0006:
                        try:
                            log_message = json.loads(
                                message.payload.decode("utf-8")
                            ).get("msg", "")
                        except (UnicodeDecodeError, json.JSONDecodeError):
                            log_message = ""
                        if (
                            f"process command: {'READ_STATUS' if probe == 'status' else 'CONTROL_LOCK'}"
                            in log_message
                            and f"msg_id={message_id}" in log_message
                            and device_process_ms is None
                        ):
                            device_process_ms = (
                                time.perf_counter() - started
                            ) * 1000.0
                    if (
                        message.command == expected_command
                        and message.message_id == message_id
                    ):
                        response = message
                        break
                if response is not None:
                    break
                time.sleep(0.002)

            if response is None:
                failures += 1
                print(
                    f"[{label}] {request_index + 1:02d}/{count:02d} timeout"
                    + (f" hb_during={heartbeat_count}" if observer_reader else "")
                    + (f" cab_exec={cabinet_process_count}" if observer_reader else "")
                    + f" retries={retries_sent}"
                )
            else:
                latency_ms = (time.perf_counter() - started) * 1000.0
                latencies.append(latency_ms)
                status = {}
                if probe == "status":
                    status = decode_status_payload(response.payload)
                    if isinstance(status.get("uptime"), int):
                        uptimes.append(status["uptime"])
                    if isinstance(status.get("duplicate_replays"), int):
                        duplicate_replays.append(status["duplicate_replays"])
                tx_details = ""
                if "mesh_link_rssi" in status:
                    tx_details = (
                        f" rssi={status.get('mesh_link_rssi')}dBm"
                        f" assoc={status.get('mesh_assoc_expire')}s"
                    )
                if "fp_poll_max_ms" in status:
                    tx_details += f" fp_poll_max={status.get('fp_poll_max_ms')}ms"
                if "duplicate_replays" in status:
                    tx_details += f" dup={status.get('duplicate_replays')}"
                print(
                    f"[{label}] {request_index + 1:02d}/{count:02d} "
                    f"{latency_ms:7.1f} ms"
                    + (
                        f" device_rx={device_process_ms:7.1f}ms"
                        if device_process_ms is not None
                        else ""
                    )
                    + (
                        f" cabinet_rx={cabinet_receive_ms:7.1f}ms"
                        if cabinet_receive_ms is not None
                        else ""
                    )
                    + tx_details
                    + (f" hb_during={heartbeat_count}" if observer_reader else "")
                    + (f" cab_exec={cabinet_process_count}" if observer_reader else "")
                    + f" retries={retries_sent}"
                )
            time.sleep(max(0, pace_ms) / 1000.0)
    finally:
        port.close()
        if observer_port is not None:
            observer_port.close()

    success = len(latencies)
    uptime_monotonic = all(left <= right for left, right in zip(uptimes, uptimes[1:]))
    duplicate_replays_monotonic = all(
        left <= right
        for left, right in zip(duplicate_replays, duplicate_replays[1:])
    )
    if latencies:
        print(
            f"[{label}] success={success}/{count} failures={failures} "
            f"avg={statistics.fmean(latencies):.1f}ms "
            f"p50={percentile(latencies, 0.50):.1f}ms "
            f"p95={percentile(latencies, 0.95):.1f}ms "
            f"max={max(latencies):.1f}ms crc_bad={reader.crc_errors} "
            f"observer_crc_bad={observer_reader.crc_errors if observer_reader else 0} "
            f"uptime_monotonic={uptime_monotonic} "
            f"duplicate_replays="
            f"{duplicate_replays[-1] if duplicate_replays else 'n/a'} "
            f"duplicate_replays_monotonic={duplicate_replays_monotonic}"
        )
    else:
        print(f"[{label}] success=0/{count} crc_bad={reader.crc_errors}")
    if link_events:
        print(f"[{label}] link_events={len(link_events)}")
        for event in link_events:
            print(f"[{label}]   {event}")
    return (
        failures == 0
        and reader.crc_errors == 0
        and uptime_monotonic
        and duplicate_replays_monotonic
    )


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root-port", default="COM16")
    parser.add_argument("--cabinet-port", default="COM12")
    parser.add_argument("--root-id", default="ROOT_B81F3FA9F404")
    parser.add_argument("--cabinet-id", default="CAB_ACA704E38558")
    parser.add_argument("--count", type=int, default=30)
    parser.add_argument("--timeout", type=float, default=4.0)
    parser.add_argument(
        "--links",
        choices=("all", "root", "mesh", "uart"),
        default="all",
    )
    parser.add_argument(
        "--probe", choices=("status", "lock", "ack"), default="status"
    )
    parser.add_argument("--retry-ms", type=int, default=0)
    parser.add_argument("--pace-ms", type=int, default=50)
    args = parser.parse_args()
    message_seed = 1000 + (int(time.time() * 1000) % 50000)
    print(f"message_seed={message_seed}")

    checks: list[bool] = []
    if args.links in ("all", "root"):
        checks.append(
            run_link(
                "ROOT", args.root_port, args.root_id, args.count, args.timeout,
                message_seed, probe=args.probe, retry_ms=args.retry_ms,
                pace_ms=args.pace_ms
            )
        )
    if args.links in ("all", "mesh"):
        checks.append(
            run_link(
                "MESH",
                args.root_port,
                args.cabinet_id,
                args.count,
                args.timeout,
                message_seed + 1000,
                args.cabinet_port,
                args.probe,
                args.retry_ms,
                args.pace_ms,
            )
        )
    if args.links in ("all", "uart"):
        checks.append(
            run_link(
                "UART", args.cabinet_port, args.cabinet_id, args.count,
                args.timeout, message_seed + 2000,
                probe=args.probe, retry_ms=args.retry_ms,
                pace_ms=args.pace_ms
            )
        )
    return 0 if checks and all(checks) else 1


if __name__ == "__main__":
    raise SystemExit(main())
