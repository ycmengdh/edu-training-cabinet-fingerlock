import pathlib
import re
import unittest


REPO = pathlib.Path(__file__).resolve().parents[3]


class MeshOtaPullContractTests(unittest.TestCase):
    def setUp(self):
        self.root_ota = (
            REPO / "esp32/root_node/components/ota/root_ota.c"
        ).read_text(encoding="utf-8")
        self.cabinet_ota = (
            REPO / "esp32/cabinet_node/components/ota/cabinet_ota.c"
        ).read_text(encoding="utf-8")
        self.controller = (
            REPO
            / "esp32/cabinet_node/components/controller/cabinet_controller.c"
        ).read_text(encoding="utf-8")
        self.mesh = (
            REPO / "esp32/common_components/cabinet_mesh/cabinet_mesh.c"
        ).read_text(encoding="utf-8")
        self.protocol = (
            REPO
            / "esp32/common_components/cabinet_protocol/include/cabinet_protocol.h"
        ).read_text(encoding="utf-8")
        self.cabinet_app = (
            REPO / "esp32/cabinet_node/main/app_main.c"
        ).read_text(encoding="utf-8")
        self.root_main = (
            REPO / "esp32/root_node/main/app_main.c"
        ).read_text(encoding="utf-8")

    def test_root_notifies_and_cabinet_starts_the_pull(self):
        self.assertIn("CAB_CMD_CABINET_OTA_NOTIFY", self.root_ota)
        self.assertIn("esp_mesh_lite_lan_ota_set_file_name", self.root_ota)
        self.assertNotIn("esp_mesh_lite_transmit_file_start", self.root_ota)
        self.assertIn("CAB_CMD_CABINET_OTA_NOTIFY", self.controller)
        self.assertIn("cabinet_ota_request(version, image_size)", self.controller)
        self.assertIn("esp_mesh_lite_transmit_file_start", self.cabinet_ota)

    def test_root_and_cabinets_share_the_ota_device_category(self):
        self.assertIn('config.device_category = "cabinet-node";', self.mesh)
        self.assertNotIn('"cabinet-root"', self.mesh)

    def test_receiver_uses_real_size_instead_of_aligned_mesh_size(self):
        self.assertIn("s_expected_size = s_requested_size", self.cabinet_ota)
        self.assertIn("s_expected_size - param->offset", self.cabinet_ota)
        self.assertIn("esp_ota_write(s_update_handle, param->data,", self.cabinet_ota)
        self.assertIn("write_size);", self.cabinet_ota)

    def test_raw_broadcast_ota_protocol_is_removed(self):
        self.assertNotIn("CAB_OTA_BROADCAST", self.protocol)
        self.assertNotIn("CAB_CMD_CABINET_OTA_REPAIR_REQUEST", self.protocol)
        self.assertNotIn("OTA_BROADCAST", self.root_ota)
        self.assertNotIn("ota_broadcast_version", self.cabinet_app)
        self.assertNotIn("cabinet_ota_broadcast", self.cabinet_ota)
        self.assertNotIn("CAB_CMD_CABINET_OTA_BROADCAST", self.controller)
        self.assertNotIn("root_ota_note_repair", self.root_main)

    def test_deep_nodes_wait_for_a_validated_parent_provider(self):
        self.assertIn("if (!registration->has_parent_bssid) return false;",
                      self.root_ota)
        self.assertIn("parent->ota_validated", self.root_ota)
        self.assertIn("OTA_PER_PARENT_CONCURRENCY 2U", self.root_ota)
        self.assertIn("OTA_GLOBAL_CONCURRENCY 10U", self.root_ota)

    def test_pull_start_failure_retains_request_context(self):
        pull_start = self.cabinet_ota.index(
            "esp_mesh_lite_transmit_file_start(&config)"
        )
        failure_path = self.cabinet_ota[pull_start:]
        self.assertNotIn("s_requested_size = 0;", failure_path)
        self.assertNotIn("s_requested_version[0] = '\\0';", failure_path)

    def test_role_suffix_versions_are_distinct(self):
        root_cmake = (REPO / "esp32/root_node/CMakeLists.txt").read_text(
            encoding="utf-8"
        )
        cabinet_cmake = (REPO / "esp32/cabinet_node/CMakeLists.txt").read_text(
            encoding="utf-8"
        )
        root_version = re.search(
            r'set\(PROJECT_VER "(?P<version>[^"]+-root)"\)', root_cmake
        )
        cabinet_version = re.search(
            r'set\(PROJECT_VER "(?P<version>[^"]+-cab)"\)', cabinet_cmake
        )
        self.assertIsNotNone(root_version)
        self.assertIsNotNone(cabinet_version)
        self.assertNotEqual(
            root_version.group("version"), cabinet_version.group("version")
        )


if __name__ == "__main__":
    unittest.main()
