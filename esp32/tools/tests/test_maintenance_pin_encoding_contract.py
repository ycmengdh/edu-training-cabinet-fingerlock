import pathlib
import unittest


REPO = pathlib.Path(__file__).resolve().parents[3]


class MaintenancePinEncodingContractTests(unittest.TestCase):
    def setUp(self):
        self.settings = (
            REPO / "CabinetLock/CabinetLock/Models/MaintenanceSettings.cs"
        ).read_text(encoding="utf-8")
        self.service = (
            REPO / "CabinetLock/CabinetLock/Services/MaintenanceService.cs"
        ).read_text(encoding="utf-8")
        self.controller = (
            REPO / "esp32/cabinet_node/components/controller/cabinet_controller.c"
        ).read_text(encoding="utf-8")
        self.cmake = (
            REPO / "esp32/cabinet_node/CMakeLists.txt"
        ).read_text(encoding="utf-8")

    def test_upper_computer_offsets_each_digit_before_sending(self):
        self.assertIn("encoded[index] = (char)(encoded[index] + 1);",
                      self.settings)
        self.assertIn("pin = MaintenanceSettings.EncodeForDevice(settings.Pin)",
                      self.service)
        self.assertIn("pin_encoding = MaintenanceSettings.DevicePinEncoding",
                      self.service)

    def test_cabinet_decodes_the_wire_pin_before_saving(self):
        self.assertIn("wire_pin[index] < '2' || wire_pin[index] > '5'",
                      self.controller)
        self.assertIn("output[index] = (char)(wire_pin[index] - 1);",
                      self.controller)
        self.assertIn("snprintf(config.maintenance_pin", self.controller)

    def test_new_firmware_remains_compatible_with_plain_legacy_pin(self):
        self.assertIn("encoding == NULL || encoding[0] == '\\0'",
                      self.controller)
        self.assertIn("maintenance_pin_valid(wire_pin)", self.controller)

    def test_protocol_change_has_a_new_cabinet_firmware_version(self):
        self.assertIn('set(PROJECT_VER "26081007-cab")', self.cmake)


if __name__ == "__main__":
    unittest.main()
