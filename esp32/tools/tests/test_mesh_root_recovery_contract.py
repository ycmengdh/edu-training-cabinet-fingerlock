import pathlib
import unittest


REPO = pathlib.Path(__file__).resolve().parents[3]


class MeshRootRecoveryContractTests(unittest.TestCase):
    def setUp(self):
        self.mesh = (
            REPO / "esp32/common_components/cabinet_mesh/cabinet_mesh.c"
        ).read_text(encoding="utf-8")
        self.mesh_header = (
            REPO / "esp32/common_components/cabinet_mesh/include/cabinet_mesh.h"
        ).read_text(encoding="utf-8")
        self.cabinet_app = (
            REPO / "esp32/cabinet_node/main/app_main.c"
        ).read_text(encoding="utf-8")

    def test_disconnected_cabinets_rescan_without_long_backoff(self):
        self.assertIn("CAB_MESH_RESCAN_INTERVAL_SECONDS 3U", self.mesh)
        self.assertIn("CAB_MESH_SEARCH_WATCHDOG_INTERVAL_MS 5000U", self.mesh)
        self.assertIn("esp_mesh_lite_set_wifi_reconnect_interval", self.mesh)
        self.assertIn("esp_mesh_lite_connect();", self.mesh)

    def test_root_silence_triggers_parent_reselection(self):
        self.assertIn("cab_mesh_request_parent_search", self.mesh_header)
        self.assertIn("ROOT_PARENT_RESELECT_TIMEOUT_COUNT 3U", self.cabinet_app)
        self.assertIn("cab_mesh_request_parent_search();", self.cabinet_app)


if __name__ == "__main__":
    unittest.main()
