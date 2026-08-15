import pathlib
import unittest


REPO = pathlib.Path(__file__).resolve().parents[3]


class HeartbeatBroadcastContractTests(unittest.TestCase):
    def setUp(self):
        self.root_app = (
            REPO / "esp32/root_node/main/app_main.c"
        ).read_text(encoding="utf-8")

    def test_root_coalesces_heartbeat_acks_into_one_mesh_broadcast(self):
        start = self.root_app.index("if (view.command == CAB_CMD_HEARTBEAT")
        section = self.root_app[start : start + 1800]
        self.assertIn("HEARTBEAT_BROADCAST_MIN_INTERVAL_MS", section)
        self.assertIn('"", s_root_id', section)
        self.assertIn("cab_mesh_send_all", section)
        self.assertNotIn("cab_mesh_send_node(from", section)


if __name__ == "__main__":
    unittest.main()
