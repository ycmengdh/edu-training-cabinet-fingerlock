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


if __name__ == "__main__":
    unittest.main()
