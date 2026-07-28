"""Safely set the Root and cabinet Mesh channel through the binary protocol."""

from __future__ import annotations

import argparse
import json
import time

from communication_stress import FrameReader, encode_app, encode_frame, open_port_safely


CMD_WRITE_CONFIG = 0x0031
CMD_CONFIG_SAVED = 0x0033
CMD_REBOOT = 0x0038
CMD_REBOOT_ACK = 0x0039


def request(port, reader, command, expected, message_id, device_id, payload, timeout):
    port.write(
        encode_frame(
            encode_app(
                command,
                message_id,
                device_id,
                json.dumps(payload, separators=(",", ":")).encode("utf-8"),
            )
        )
    )
    port.flush()
    deadline = time.perf_counter() + timeout
    while time.perf_counter() < deadline:
        for message in reader.read_messages():
            if message.command == expected and message.message_id == message_id:
                return True
        time.sleep(0.002)
    return False


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--channel", type=int, required=True)
    parser.add_argument("--root-port", default="COM16")
    parser.add_argument("--cabinet-port", default="COM12")
    parser.add_argument("--root-id", default="ROOT_B81F3FA9F404")
    parser.add_argument("--cabinet-id", default="CAB_ACA704E38558")
    args = parser.parse_args()
    if not 1 <= args.channel <= 13:
        parser.error("channel must be between 1 and 13")

    root_port = open_port_safely(args.root_port)
    cabinet_port = open_port_safely(args.cabinet_port)
    root_reader = FrameReader(root_port)
    cabinet_reader = FrameReader(cabinet_port)
    try:
        time.sleep(0.2)
        root_port.reset_input_buffer()
        cabinet_port.reset_input_buffer()

        targets = (
            ("ROOT", root_port, root_reader, args.root_id, 64000),
            ("CAB", cabinet_port, cabinet_reader, args.cabinet_id, 64010),
        )
        for label, port, reader, device_id, message_id in targets:
            ok = request(
                port,
                reader,
                CMD_WRITE_CONFIG,
                CMD_CONFIG_SAVED,
                message_id,
                device_id,
                {"mesh_channel": args.channel},
                3.0,
            )
            print(f"[{label}] channel save {'OK' if ok else 'FAILED'}")
            if not ok:
                return 1

        for label, port, reader, device_id, message_id in reversed(targets):
            ok = request(
                port,
                reader,
                CMD_REBOOT,
                CMD_REBOOT_ACK,
                message_id + 1,
                device_id,
                {"mode": "mesh"},
                3.0,
            )
            print(f"[{label}] software reboot {'ACK' if ok else 'NO ACK'}")
            if not ok:
                return 1
        print(f"Mesh channel {args.channel} saved on both devices")
        return 0
    finally:
        root_port.close()
        cabinet_port.close()


if __name__ == "__main__":
    raise SystemExit(main())
