import os
import sys
import unittest


TOOLS_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
if TOOLS_DIR not in sys.path:
    sys.path.insert(0, TOOLS_DIR)

from batch_flash_core import load_profile


CONFIG_PATH = os.path.join(TOOLS_DIR, "batch_flash_config.json")


class BatchFlashConfigTests(unittest.TestCase):
    def test_root_uses_esp_idf_build_outputs(self):
        profile = load_profile(CONFIG_PATH, "root")

        self.assertEqual(
            ["0x0", "0x8000", "0x10000"],
            [item["address"] for item in profile["files"]],
        )
        self.assertEqual("cabinet_root_idf.bin", profile["_firmware"])
        self.assertTrue(all(".pio" not in item["path"] for item in profile["files"]))

    def test_cabinet_includes_ota_partition_state(self):
        profile = load_profile(CONFIG_PATH, "cabinet")
        files = {item["address"]: os.path.basename(item["resolved_path"])
                 for item in profile["files"]}

        self.assertEqual("bootloader.bin", files["0x0"])
        self.assertEqual("partition-table.bin", files["0x8000"])
        self.assertEqual("cabinet_node_idf.bin", files["0x10000"])
        self.assertEqual("ota_data_initial.bin", files["0x610000"])
        self.assertNotIn("0xe000", files)


if __name__ == "__main__":
    unittest.main()
