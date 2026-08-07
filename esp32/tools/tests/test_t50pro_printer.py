import subprocess
import tempfile
import threading
import time
import unittest
from unittest import mock

import batch_flash_core
from batch_flash_core import BatchFlashController
from t50pro_printer import T50ProClient, T50ProPrintQueue, cabinet_label_text


class FakePrinterClient:
    def __init__(self):
        self.calls = []
        self.active = 0
        self.max_active = 0
        self.lock = threading.Lock()

    def print_label(self, device_path, mac, settings):
        with self.lock:
            self.active += 1
            self.max_active = max(self.max_active, self.active)
            self.calls.append((device_path, mac, settings))
        time.sleep(0.02)
        with self.lock:
            self.active -= 1
        return {"description": "完成"}


class FlakyPrinterClient(FakePrinterClient):
    def print_label(self, device_path, mac, settings):
        if not self.calls:
            self.calls.append((device_path, mac, settings))
            raise RuntimeError("temporary failure")
        return super().print_label(device_path, mac, settings)


class FailingPrinterClient(FakePrinterClient):
    def print_label(self, device_path, mac, settings):
        self.calls.append((device_path, mac, settings))
        raise RuntimeError("print failed")


class T50ProPrinterTests(unittest.TestCase):
    def test_cabinet_label_matches_firmware_device_id(self):
        self.assertEqual("CAB_AABBCCDDEEFF", cabinet_label_text("aa:bb:cc:dd:ee:ff"))

    def test_invalid_mac_is_rejected(self):
        with self.assertRaises(ValueError):
            cabinet_label_text("not-a-mac")

    def test_vertical_label_uses_clockwise_rotation(self):
        client = T50ProClient()
        with mock.patch.object(client, "_call", return_value={}) as call:
            client.print_label(
                "device-1",
                "AA:BB:CC:DD:EE:FF",
                {"direction": 3, "margin_left_mm": 5, "margin_top_mm": -5},
            )
        payload = call.call_args.args[0]
        self.assertEqual(3, payload["direction"])
        self.assertEqual(5, payload["margin_left_mm"])
        self.assertEqual(-5, payload["margin_top_mm"])

    def test_queue_deduplicates_and_serializes_jobs(self):
        client = FakePrinterClient()
        events = []
        completed = threading.Event()

        def on_event(event):
            events.append(event)
            if len([item for item in events if item["status"] == "printed"]) == 2:
                completed.set()

        printer = T50ProPrintQueue(client, "device-1", {}, on_event)
        self.assertTrue(printer.submit("COM3", "AA:BB:CC:DD:EE:01"))
        self.assertFalse(printer.submit("COM4", "aa-bb-cc-dd-ee-01"))
        self.assertTrue(printer.submit("COM4", "AA:BB:CC:DD:EE:02"))
        self.assertTrue(completed.wait(2))
        printer.close(wait=True)

        self.assertEqual(2, len(client.calls))
        self.assertEqual(1, client.max_active)
        self.assertEqual(
            ["CAB_AABBCCDDEE01", "CAB_AABBCCDDEE02"],
            [item["label"] for item in events if item["status"] == "printed"],
        )

    def test_explicit_reprint_bypasses_batch_deduplication(self):
        client = FakePrinterClient()
        completed = threading.Event()
        printed = []

        def on_event(event):
            if event["status"] == "printed":
                printed.append(event["label"])
                if len(printed) == 2:
                    completed.set()

        printer = T50ProPrintQueue(client, "device-1", {}, on_event)
        self.assertTrue(printer.submit("COM3", "AA:BB:CC:DD:EE:01"))
        self.assertTrue(
            printer.submit(
                "COM3", "AA:BB:CC:DD:EE:01", allow_duplicate=True
            )
        )
        self.assertTrue(completed.wait(2))
        printer.close(wait=True)

        self.assertEqual(2, len(client.calls))

    def test_queue_retries_a_transient_print_failure(self):
        client = FlakyPrinterClient()
        events = []
        completed = threading.Event()

        def on_event(event):
            events.append(event)
            if event["status"] == "printed":
                completed.set()

        printer = T50ProPrintQueue(
            client,
            "device-1",
            {"retry_count": 1, "retry_delay_seconds": 0},
            on_event,
        )
        printer.submit("COM3", "AA:BB:CC:DD:EE:01")
        self.assertTrue(completed.wait(2))
        printer.close(wait=True)

        self.assertEqual(2, len(client.calls))
        self.assertIn("retrying", [event["status"] for event in events])
        self.assertNotIn("failed", [event["status"] for event in events])

    def test_queue_does_not_retry_by_default(self):
        client = FailingPrinterClient()
        events = []
        failed = threading.Event()

        def on_event(event):
            events.append(event)
            if event["status"] == "failed":
                failed.set()

        printer = T50ProPrintQueue(client, "device-1", {}, on_event)
        printer.submit("COM3", "AA:BB:CC:DD:EE:01")
        self.assertTrue(failed.wait(2))
        printer.close(wait=True)

        self.assertEqual(1, len(client.calls))
        self.assertNotIn("retrying", [event["status"] for event in events])

    def test_mac_callback_fires_only_when_flashing_will_start(self):
        identified = []
        sequence = []
        events = []
        stdout = "\n".join(
            [
                "Chip is ESP32-S3 (QFN56)",
                "Features: WiFi, BLE",
                "MAC: aa:bb:cc:dd:ee:ff",
            ]
        )
        with tempfile.TemporaryDirectory() as temp_dir:
            config = {
                "records_csv": temp_dir + "/records.csv",
                "log_dir": temp_dir + "/logs",
                "_profile": "cabinet",
                "_firmware": "firmware.bin",
                "_firmware_sha256": "test-signature",
                "settle_delay": 0,
            }
            def record_event(event):
                events.append(event)
                if event.get("type") == "device" and event.get("status") == "flashing":
                    sequence.append("start")

            controller = BatchFlashController(
                config,
                record_event,
                mac_callback=lambda port, mac: (
                    identified.append((port, mac)), sequence.append("print")
                ),
            )
            def stop_after_flash_starts(*_args):
                sequence.append("flash")
                raise RuntimeError("test stop")

            controller._flash = mock.Mock(side_effect=stop_after_flash_starts)
            command_result = subprocess.CompletedProcess([], 0, stdout, "")
            with mock.patch.object(batch_flash_core, "run_esptool", return_value=command_result):
                result = controller._process_port("COM7", 1)

        self.assertEqual([("COM7", "AA:BB:CC:DD:EE:FF")], identified)
        self.assertEqual(["start", "print", "flash"], sequence)
        self.assertEqual("FAIL", result.status)

    def test_completed_firmware_is_skipped_without_printing(self):
        identified = []
        stdout = "\n".join(
            [
                "Chip is ESP32-S3 (QFN56)",
                "Features: WiFi, BLE",
                "MAC: aa:bb:cc:dd:ee:ff",
            ]
        )
        with tempfile.TemporaryDirectory() as temp_dir:
            config = {
                "records_csv": temp_dir + "/records.csv",
                "log_dir": temp_dir + "/logs",
                "_profile": "cabinet",
                "_firmware": "firmware.bin",
                "_firmware_sha256": "test-signature",
                "settle_delay": 0,
            }
            controller = BatchFlashController(
                config,
                lambda event: None,
                mac_callback=lambda port, mac: identified.append((port, mac)),
            )
            controller.records.append(
                {
                    "mac": "aa:bb:cc:dd:ee:ff",
                    "profile": "cabinet",
                    "firmware_sha256": "test-signature",
                    "status": "OK",
                }
            )
            command_result = subprocess.CompletedProcess([], 0, stdout, "")
            with mock.patch.object(batch_flash_core, "run_esptool", return_value=command_result):
                result = controller._process_port("COM7", 1)

        self.assertEqual([], identified)
        self.assertEqual("SKIP", result.status)


if __name__ == "__main__":
    unittest.main()
