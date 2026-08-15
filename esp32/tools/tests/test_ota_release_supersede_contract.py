import pathlib
import unittest


REPO = pathlib.Path(__file__).resolve().parents[3]


class OtaReleaseSupersedeContractTests(unittest.TestCase):
    def setUp(self):
        self.root_ota = (
            REPO / "esp32/root_node/components/ota/root_ota.c"
        ).read_text(encoding="utf-8")
        self.cabinet_ota = (
            REPO / "esp32/cabinet_node/components/ota/cabinet_ota.c"
        ).read_text(encoding="utf-8")

    def test_new_root_release_resets_old_per_node_sessions(self):
        upload_begin = self.root_ota.index("esp_err_t root_ota_upload_begin")
        upload_chunk = self.root_ota.index("esp_err_t root_ota_upload_chunk")
        section = self.root_ota[upload_begin:upload_chunk]

        self.assertIn("reset_registration_release_locked", section)
        self.assertIn("s_provider_version[0] = '\\0';", section)

    def test_scheduler_normalizes_release_before_active_phase_filter(self):
        scheduler = self.root_ota.index("static esp_err_t start_distribution")
        notify = self.root_ota.index("CAB_CMD_CABINET_OTA_NOTIFY", scheduler)
        section = self.root_ota[scheduler:notify]
        normalize = section.rindex("reset_registration_release_locked")
        active_filter = section.rindex("phase_is_active")

        self.assertLess(normalize, active_filter)

    def test_cabinet_releases_failed_partition_and_supersedes_old_request(self):
        receive = self.cabinet_ota.index("static esp_err_t receive_file")
        receive_done = self.cabinet_ota.index("static esp_err_t receive_file_done")
        receive_section = self.cabinet_ota[receive:receive_done]
        request = self.cabinet_ota.index("esp_err_t cabinet_ota_request")
        status = self.cabinet_ota.index("bool cabinet_ota_get_status")
        request_section = self.cabinet_ota[request:status]

        self.assertIn("abort_update_locked();", receive_section)
        self.assertIn("Superseding cabinet OTA", request_section)
        self.assertIn("abort_update_locked();", request_section)


if __name__ == "__main__":
    unittest.main()
