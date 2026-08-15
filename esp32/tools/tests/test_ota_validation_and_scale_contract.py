import pathlib
import unittest


REPO = pathlib.Path(__file__).resolve().parents[3]


class OtaValidationAndScaleContractTests(unittest.TestCase):
    def setUp(self):
        self.cabinet_app = (
            REPO / "esp32/cabinet_node/main/app_main.c"
        ).read_text(encoding="utf-8")
        self.cabinet_ota = (
            REPO / "esp32/cabinet_node/components/ota/cabinet_ota.c"
        ).read_text(encoding="utf-8")
        self.root_ota = (
            REPO / "esp32/root_node/components/ota/root_ota.c"
        ).read_text(encoding="utf-8")
        self.root_controller = (
            REPO / "esp32/root_node/components/controller/root_controller.c"
        ).read_text(encoding="utf-8")

    def test_cabinet_reports_and_confirms_rollback_validation(self):
        self.assertIn(r'\"ota_validated\":%s', self.cabinet_app)
        health = self.cabinet_ota.index("static void health_validation_task")
        section = self.cabinet_ota[health : health + 2500]
        self.assertLess(
            section.index("esp_ota_mark_app_valid_cancel_rollback"),
            section.index('result == ESP_OK ? "complete"'),
        )
        self.assertIn("cabinet_ota_running_image_validated", self.cabinet_ota)

    def test_root_counts_only_validated_target_images(self):
        self.assertIn("registration->ota_validated", self.root_ota)
        self.assertIn("firmware rollback detected", self.root_ota)
        self.assertIn("parent->ota_validated", self.root_ota)
        self.assertIn("elapsed_seconds", self.root_controller)
        self.assertIn("if (s_status.started_at_seconds == 0)", self.root_ota)

    def test_lost_notifications_use_fast_bounded_retry(self):
        self.assertIn("#define OTA_NOTIFY_STALE_SECONDS 12U", self.root_ota)
        self.assertIn("#define OTA_NOTIFY_RETRY_SECONDS 5U", self.root_ota)
        self.assertIn("notification timeout", self.root_ota)
        self.assertIn("if (retrying && registration->retry_count", self.root_ota)

    def test_unchanged_progress_reports_do_not_hide_a_stalled_download(self):
        self.assertIn("bool changed =", self.root_ota)
        self.assertIn(
            "if (changed) registration->ota_updated_seconds = now_seconds();",
            self.root_ota,
        )
        self.assertIn('sizeof(registration->ota_phase), "repairing"', self.root_ota)
        self.assertIn("CAB_CMD_REBOOT", self.root_ota)
        self.assertIn("download stalled; rebooting", self.root_ota)


if __name__ == "__main__":
    unittest.main()
