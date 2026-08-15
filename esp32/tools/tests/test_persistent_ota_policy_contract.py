import pathlib
import unittest


REPO = pathlib.Path(__file__).resolve().parents[3]


class PersistentOtaPolicyContractTests(unittest.TestCase):
    def test_root_persists_and_reloads_active_release(self):
        source = (REPO / "esp32/root_node/components/ota/root_ota.c").read_text(
            encoding="utf-8"
        )

        self.assertIn('OTA_POLICY_PATH OTA_DIRECTORY "/cabinet-policy.json"', source)
        self.assertIn("persist_policy_locked", source)
        self.assertIn("load_policy_locked", source)
        self.assertIn("root_ota_maintain", source)

    def test_persisted_image_validation_does_not_overflow_main_stack(self):
        source = (REPO / "esp32/root_node/components/ota/root_ota.c").read_text(
            encoding="utf-8"
        )
        defaults = (REPO / "esp32/root_node/sdkconfig.defaults").read_text(
            encoding="utf-8"
        )

        self.assertNotIn("uint8_t buffer[4096]", source)
        self.assertIn("uint8_t *buffer = malloc(OTA_HASH_BUFFER_SIZE)", source)
        self.assertIn("free(buffer)", source)
        self.assertIn("CONFIG_ESP_MAIN_TASK_STACK_SIZE=8192", defaults)

    def test_registration_carries_hardware_compatibility_version(self):
        app = (REPO / "esp32/cabinet_node/main/app_main.c").read_text(
            encoding="utf-8"
        )
        controller = (
            REPO / "esp32/cabinet_node/components/controller/cabinet_controller.c"
        ).read_text(encoding="utf-8")
        cmake = (REPO / "esp32/cabinet_node/CMakeLists.txt").read_text(
            encoding="utf-8"
        )

        self.assertIn('CABINET_HARDWARE_VERSION="cabinet-v1"', cmake)
        self.assertIn('\\"hardware_version\\":\\"%s\\"', app)
        self.assertIn('\\"hardware_version\\":\\"%s\\"', controller)

    def test_root_no_longer_uses_route_snapshot_as_completion_target(self):
        controller = (
            REPO / "esp32/root_node/components/controller/root_controller.c"
        ).read_text(encoding="utf-8")

        start = controller.index("static void handle_ota_start")
        section = controller[start : start + 500]
        self.assertNotIn("cab_mesh_route_count", section)
        self.assertIn("root_ota_start(error", section)

    def test_release_never_resumes_automatically_after_commit_or_reboot(self):
        source = (REPO / "esp32/root_node/components/ota/root_ota.c").read_text(
            encoding="utf-8"
        )

        load = source[source.index("static bool load_policy_locked") :]
        load = load[: load.index("static esp_err_t reject_file")]
        self.assertIn("s_status.active = false;", load)
        self.assertIn('s_status.phase), "paused"', load)

        commit = source[source.index("esp_err_t root_ota_upload_commit") :]
        commit = commit[: commit.index("static esp_err_t start_distribution")]
        self.assertIn("s_status.active = false;", commit)
        self.assertIn('s_status.phase), "ready"', commit)

    def test_pause_stops_scheduler_and_resets_per_node_sessions(self):
        source = (REPO / "esp32/root_node/components/ota/root_ota.c").read_text(
            encoding="utf-8"
        )
        pause = source[source.index("esp_err_t root_ota_pause") :]
        pause = pause[: pause.index("void root_ota_maintain")]

        self.assertIn("s_status.active = false;", pause)
        self.assertIn("s_next_distribution_at = 0;", pause)
        self.assertIn('sizeof(registration->ota_phase), "paused"', pause)
        self.assertIn("CAB_CMD_CABINET_OTA_PAUSE", pause)

    def test_completed_distribution_does_not_remain_automatic(self):
        source = (REPO / "esp32/root_node/components/ota/root_ota.c").read_text(
            encoding="utf-8"
        )
        start = source[source.index("static esp_err_t start_distribution") :]
        start = start[: start.index("esp_err_t root_ota_start")]

        completed = start[start.index("if (s_status.pending_nodes == 0)") :]
        self.assertIn("s_status.active = false;", completed)
        self.assertIn('s_status.phase), "complete"', completed)
        self.assertIn("persist_policy_locked", completed)


if __name__ == "__main__":
    unittest.main()
