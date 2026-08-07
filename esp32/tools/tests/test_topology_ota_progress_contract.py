import pathlib
import unittest


REPO = pathlib.Path(__file__).resolve().parents[3]


class TopologyOtaProgressContractTests(unittest.TestCase):
    def setUp(self):
        self.protocol = (
            REPO / "esp32/common_components/cabinet_protocol/include/cabinet_protocol.h"
        ).read_text(encoding="utf-8")
        self.cabinet_app = (
            REPO / "esp32/cabinet_node/main/app_main.c"
        ).read_text(encoding="utf-8")
        self.root_ota = (
            REPO / "esp32/root_node/components/ota/root_ota.c"
        ).read_text(encoding="utf-8")
        self.root_controller = (
            REPO / "esp32/root_node/components/controller/root_controller.c"
        ).read_text(encoding="utf-8")

    def test_protocol_reserves_progress_and_paged_node_commands(self):
        self.assertIn("CAB_CMD_CABINET_OTA_PROGRESS = 0x0077", self.protocol)
        self.assertIn("CAB_CMD_CABINET_OTA_NODES = 0x0078", self.protocol)
        self.assertIn(
            "CAB_CMD_CABINET_OTA_NODES_RESPONSE = 0x0079", self.protocol
        )

    def test_cabinet_reports_topology_and_live_ota_progress(self):
        self.assertIn('\\"mesh_ap_mac\\":\\"%s\\"', self.cabinet_app)
        self.assertIn('\\"parent_bssid\\":\\"%s\\"', self.cabinet_app)
        self.assertIn("CAB_CMD_CABINET_OTA_PROGRESS", self.cabinet_app)
        self.assertIn('\\"progress\\":%u', self.cabinet_app)

    def test_scheduler_limits_parallelism_and_waits_for_parent_version(self):
        self.assertIn("#define OTA_PER_PARENT_CONCURRENCY 2U", self.root_ota)
        self.assertIn("#define OTA_GLOBAL_CONCURRENCY 10U", self.root_ota)
        self.assertIn("parent_ready_locked", self.root_ota)
        self.assertIn(
            "strcmp(parent->version, s_status.version) == 0", self.root_ota
        )
        self.assertIn("same_provider", self.root_ota)

    def test_active_slots_are_scoped_to_online_current_target_nodes(self):
        helper = self.root_ota.index(
            "registration_is_active_for_target_locked"
        )
        section = self.root_ota[helper : helper + 500]
        self.assertIn("registration_is_online", section)
        self.assertIn("registration_is_compatible", section)
        self.assertIn("registration->ota_version, s_status.version", section)
        self.assertIn("provider registration failed", self.root_ota)

    def test_root_exposes_bounded_paginated_node_status(self):
        start = self.root_controller.index("static void handle_ota_nodes")
        section = self.root_controller[start : start + 3000]
        self.assertIn("if (limit > 10) limit = 10", section)
        self.assertIn("root_ota_get_nodes(offset, limit", section)
        self.assertIn('"parent_device_id"', section)
        self.assertIn('"updated_ago"', section)
        self.assertIn("CAB_CMD_CABINET_OTA_NODES_RESPONSE", section)


if __name__ == "__main__":
    unittest.main()
