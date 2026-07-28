"""Verify Root SD chunk upload without changing table contents."""

from __future__ import annotations

import argparse
import base64
import json
import random
import time

import serial


BAUD = 921600
FRAME_PAYLOAD = 1400
FRAGMENT_DATA = FRAME_PAYLOAD - 4
CMD_SD_QUERY = 0x0040
CMD_SD_QUERY_RESPONSE = 0x0041
CMD_SD_QUERY_PART = 0x0042
CMD_SD_SAVE = 0x0044
CMD_SD_SAVE_RESPONSE = 0x0045
CMD_SD_QUERY_VERSION = 0x0046
CMD_SD_VERSION_RESPONSE = 0x0047


def crc16_modbus(data: bytes) -> int:
    crc = 0xFFFF
    for value in data:
        crc ^= value
        for _ in range(8):
            crc = (crc >> 1) ^ 0xA001 if crc & 1 else crc >> 1
    return crc & 0xFFFF


def encode_frame(version: int, payload: bytes) -> bytes:
    body = bytes((version,)) + len(payload).to_bytes(2, "big") + payload
    return b"\xA5\x5A" + body + crc16_modbus(body).to_bytes(2, "big")


def encode_transport(payload: bytes, fragment_id: int) -> bytes:
    if len(payload) <= FRAME_PAYLOAD:
        return encode_frame(1, payload)
    total = (len(payload) + FRAGMENT_DATA - 1) // FRAGMENT_DATA
    frames = bytearray()
    for sequence in range(total):
        start = sequence * FRAGMENT_DATA
        chunk = payload[start:start + FRAGMENT_DATA]
        header = bytes((fragment_id & 0xFF, sequence, total, 0))
        frames.extend(encode_frame(2, header + chunk))
    return bytes(frames)


def encode_app(command: int, message_id: int, device_id: str, payload: bytes) -> bytes:
    device = device_id.encode("ascii")
    header = bytearray((0xB1, 0x0F, 0x01, 0x01))
    header.extend(command.to_bytes(2, "little"))
    header.extend(message_id.to_bytes(2, "little"))
    header.extend((0).to_bytes(2, "little"))
    header.extend((len(device), 0))
    header.extend(len(payload).to_bytes(2, "little"))
    header.extend(int(time.time()).to_bytes(4, "little"))
    return bytes(header) + device + payload


def open_port_safely(port_name: str) -> serial.Serial:
    port = serial.Serial()
    port.port = port_name
    port.baudrate = BAUD
    port.timeout = 0
    port.write_timeout = 2
    port.dtr = False
    port.rts = False
    port.open()
    port.dtr = False
    port.rts = False
    return port


class Reader:
    def __init__(self, port: serial.Serial):
        self.port = port
        self.buffer = bytearray()

    def read_apps(self) -> list[tuple[int, int, bytes]]:
        waiting = self.port.in_waiting
        if waiting:
            self.buffer.extend(self.port.read(waiting))
        apps: list[tuple[int, int, bytes]] = []
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
            if len(self.buffer) < frame_size:
                break
            body = bytes(self.buffer[2:5 + length])
            received_crc = int.from_bytes(self.buffer[5 + length:frame_size], "big")
            if self.buffer[2] != 1 or crc16_modbus(body) != received_crc:
                del self.buffer[0]
                continue
            payload = bytes(self.buffer[5:5 + length])
            del self.buffer[:frame_size]
            if len(payload) < 18 or payload[:2] != b"\xB1\x0F":
                continue
            command = int.from_bytes(payload[4:6], "little")
            message_id = int.from_bytes(payload[6:8], "little")
            device_length = payload[10]
            source_length = payload[11]
            payload_length = int.from_bytes(payload[12:14], "little")
            offset = 18 + device_length + source_length
            if offset + payload_length <= len(payload):
                apps.append((command, message_id, payload[offset:offset + payload_length]))
        return apps


class Client:
    def __init__(self, port: serial.Serial, root_id: str):
        self.port = port
        self.root_id = root_id
        self.reader = Reader(port)
        self.message_id = random.randint(1000, 50000)
        self.fragment_id = random.randint(1, 250)

    def next_message_id(self) -> int:
        self.message_id = self.message_id % 65535 + 1
        return self.message_id

    def send(self, command: int, data: dict) -> int:
        message_id = self.next_message_id()
        payload = json.dumps(data, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
        app = encode_app(command, message_id, self.root_id, payload)
        frames = encode_transport(app, self.fragment_id)
        self.fragment_id = self.fragment_id % 255 + 1
        for offset in range(0, len(frames), 512):
            self.port.write(frames[offset:offset + 512])
            time.sleep(0.002)
        self.port.flush()
        return message_id

    def wait_json(self, message_id: int, commands: set[int], timeout: float) -> tuple[int, dict]:
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            for command, received_id, payload in self.reader.read_apps():
                if received_id != message_id or command not in commands:
                    continue
                return command, json.loads(payload.decode("utf-8"))
            time.sleep(0.005)
        raise TimeoutError(f"response timeout: mid={message_id}")

    def query_version(self) -> dict:
        message_id = self.send(CMD_SD_QUERY_VERSION, {})
        _, data = self.wait_json(message_id, {CMD_SD_VERSION_RESPONSE}, 8)
        return data

    def query_table(self, table: str) -> dict:
        message_id = self.send(CMD_SD_QUERY, {"table": table})
        parts: dict[int, str] = {}
        total = 0
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            for command, received_id, payload in self.reader.read_apps():
                if received_id != message_id:
                    continue
                data = json.loads(payload.decode("utf-8"))
                if command == CMD_SD_QUERY_RESPONSE:
                    return data
                if command == CMD_SD_QUERY_PART:
                    part = int(data["part"])
                    total = int(data["total"])
                    parts[part] = data["data"]
                    if len(parts) == total:
                        return json.loads("".join(parts[index] for index in range(1, total + 1)))
            time.sleep(0.005)
        raise TimeoutError(f"table query timeout: {table}, parts={len(parts)}/{total}")

    def save_part(self, data: dict) -> dict:
        message_id = self.send(CMD_SD_SAVE, data)
        _, response = self.wait_json(message_id, {CMD_SD_SAVE_RESPONSE}, 10)
        return response


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--port", default="COM16")
    parser.add_argument("--root-id", default="ROOT_B81F3FA9F404")
    parser.add_argument("--table", default="users")
    args = parser.parse_args()

    port = open_port_safely(args.port)
    try:
        client = Client(port, args.root_id)
        before_versions = client.query_version()
        snapshot = client.query_table(args.table)
        table_data = snapshot["json"]
        table_json = json.dumps(table_data, ensure_ascii=False, separators=(",", ":"))
        table_bytes = table_json.encode("utf-8")
        base_version = int(snapshot["version"])
        upload_id = f"verify{int(time.time()):x}{random.randint(0, 0xFFFF):04x}"
        chunks = [table_bytes[index:index + 2048] for index in range(0, len(table_bytes), 2048)]
        print(
            f"table={args.table} bytes={len(table_bytes)} parts={len(chunks)} "
            f"base_version={base_version}"
        )

        final_request = None
        for part_index, chunk in enumerate(chunks):
            request = {
                "table": args.table,
                "upload_id": upload_id,
                "part_index": part_index,
                "part_total": len(chunks),
                "total_bytes": len(table_bytes),
                "chunk_base64": base64.b64encode(chunk).decode("ascii"),
                "base_version": base_version,
                "enforce_version": True,
            }
            response = client.save_part(request)
            expected = "success" if part_index == len(chunks) - 1 else "part_ok"
            if response.get("result") != expected:
                raise RuntimeError(f"part {part_index + 1} failed: {response}")
            print(f"part {part_index + 1}/{len(chunks)}: {response['result']}")
            final_request = request

        duplicate_response = client.save_part(final_request)
        if duplicate_response.get("result") != "success":
            raise RuntimeError(f"final duplicate was not idempotent: {duplicate_response}")

        after_snapshot = client.query_table(args.table)
        after_versions = client.query_version()
        if after_snapshot["json"] != table_data:
            raise RuntimeError("read-back table differs from the original data")
        expected_version = base_version + 1
        if int(after_snapshot["version"]) != expected_version:
            raise RuntimeError(
                f"table version mismatch: {after_snapshot['version']} != {expected_version}"
            )
        if args.table == "users":
            permission_delta = int(after_versions["permissions_version"]) - int(
                before_versions["permissions_version"]
            )
            if permission_delta != 1:
                raise RuntimeError(f"permissions version delta is {permission_delta}, expected 1")

        print(
            "PASS: chunk upload committed once, duplicate final part was idempotent, "
            "and read-back matched"
        )
        return 0
    finally:
        port.close()


if __name__ == "__main__":
    raise SystemExit(main())
