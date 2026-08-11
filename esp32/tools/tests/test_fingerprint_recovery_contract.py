import os
import re
import unittest


REPOSITORY_DIR = os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.dirname(os.path.abspath(__file__)))))
FINGERPRINT_SOURCE = os.path.join(
    REPOSITORY_DIR,
    "esp32",
    "cabinet_node",
    "components",
    "fingerprint",
    "cabinet_fingerprint.c",
)
CONTROLLER_SOURCE = os.path.join(
    REPOSITORY_DIR,
    "esp32",
    "cabinet_node",
    "components",
    "controller",
    "cabinet_controller.c",
)
CMAKE_SOURCE = os.path.join(
    REPOSITORY_DIR, "esp32", "cabinet_node", "CMakeLists.txt")


class FingerprintRecoveryContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        with open(FINGERPRINT_SOURCE, "r", encoding="utf-8") as source:
            cls.fingerprint = source.read()
        with open(CONTROLLER_SOURCE, "r", encoding="utf-8") as source:
            cls.controller = source.read()
        with open(CMAKE_SOURCE, "r", encoding="utf-8") as source:
            cls.cmake = source.read()

    def test_power_cycle_has_a_real_off_interval(self):
        match = re.search(r"#define FP_POWER_OFF_MS\s+(\d+)", self.fingerprint)
        self.assertIsNotNone(match)
        self.assertGreaterEqual(int(match.group(1)), 200)
        self.assertIn("vTaskDelay(pdMS_TO_TICKS(FP_POWER_OFF_MS))",
                      self.fingerprint)

    def test_failed_sensor_recovers_without_rebooting_the_cabinet(self):
        self.assertIn("static void recovery_task", self.fingerprint)
        self.assertIn('xTaskCreate(recovery_task, "fp_recover"',
                      self.fingerprint)
        self.assertIn("power_cycle_and_probe()", self.fingerprint)

    def test_power_detection_never_swaps_control_and_status_pins(self):
        self.assertIn(
            '{GPIO_NUM_21, GPIO_NUM_42, 0, 1, "p21-low"}',
            self.fingerprint,
        )
        self.assertNotIn(
            '{GPIO_NUM_21, GPIO_NUM_42, 1, 1, "p21-high"}',
            self.fingerprint,
        )
        self.assertNotIn("{GPIO_NUM_42, GPIO_NUM_21", self.fingerprint)
        self.assertIn("s_power_detected = s_power_on_feedback_level ==",
                      self.fingerprint)
        self.assertIn("profile->status_on_level", self.fingerprint)
        self.assertIn('"fingerprint_power_off_level\\\":%d,\"',
                      self.controller)
        self.assertIn('"fingerprint_power_on_level\\\":%d,\"',
                      self.controller)
        self.assertIn("char json[768]", self.controller)
        self.assertIn("probe_power_profiles()", self.fingerprint)

    def test_working_uart_format_is_used_for_commands(self):
        self.assertIn(
            '{UART_STOP_BITS_1, UART_SCLK_XTAL, "8n1-xtal"}',
            self.fingerprint,
        )
        self.assertIn(
            '{UART_STOP_BITS_1, UART_SCLK_APB, "8n1-apb"}',
            self.fingerprint,
        )
        self.assertNotIn('{UART_STOP_BITS_2, "8n2"}', self.fingerprint)
        self.assertIn("probe_uart_profiles()", self.fingerprint)

    def test_uart_boot_sequence_matches_working_arduino_firmware(self):
        self.assertIn("#define FP_ARDUINO_BOOT_DELAY_MS 1000",
                      self.fingerprint)
        self.assertIn("install_uart(UART_STOP_BITS_2,", self.fingerprint)
        self.assertIn("UART_SCLK_XTAL", self.fingerprint)
        self.assertIn("uart_set_stop_bits(", self.fingerprint)
        self.assertIn("gpio_set_level(FP_TX, 1)", self.fingerprint)
        self.assertIn("gpio_set_pull_mode(FP_RX, GPIO_PULLUP_ONLY)",
                      self.fingerprint)
        self.assertIn("gpio_set_direction(FP_RX, GPIO_MODE_INPUT)",
                      self.fingerprint)

    def test_uart_failure_reports_received_byte_count(self):
        self.assertIn('"probe=%d rx=%lu first=%02X)"', self.fingerprint)

    def test_uart_send_matches_adafruit_blocking_byte_writes(self):
        self.assertIn(
            "uart_driver_install(FP_UART, 2048, 0, 0, NULL, 0)",
            self.fingerprint,
        )
        self.assertIn("uart_write_bytes(FP_UART, &output[index], 1)",
                      self.fingerprint)

    def test_controller_refreshes_sensor_state_after_recovery(self):
        self.assertIn("fingerprint_ready && !s_fingerprint_was_ready",
                      self.controller)
        self.assertIn("update_config_fingerprint_count();", self.controller)

    def test_valid_sensor_rejections_are_not_transport_failures(self):
        self.assertGreaterEqual(
            self.fingerprint.count("return result < 0 ? -2 : -1;"), 3)
        self.assertIn("++s_error_count;\n            s_ready = false;",
                      self.fingerprint)

    def test_fixed_image_has_a_new_ota_version(self):
        version = re.search(
            r'set\(PROJECT_VER "(?P<version>\d{8,}-cab)"\)', self.cmake
        )
        self.assertIsNotNone(version)

    def test_normal_enrollment_uses_and_clears_temporary_slot_zero(self):
        self.assertIn(
            "cab_fp_enroll_begin(backup ? target : CAB_FP_TEMP_SLOT);",
            self.controller,
        )
        self.assertIn(
            "int template_slot = s_enroll_backup ? s_enroll_target : CAB_FP_TEMP_SLOT;",
            self.controller,
        )
        self.assertIn(
            "if (!s_enroll_backup && cab_fp_template_exists(CAB_FP_TEMP_SLOT))\n"
            "        cab_fp_delete(CAB_FP_TEMP_SLOT);",
            self.controller,
        )
        finish = self.controller.split("static void finish_enrollment", 1)[1]
        finish = finish.split("static bool parse_mac_text", 1)[0]
        self.assertNotIn("cab_fp_write_template(s_enroll_target", finish)
        self.assertNotIn("cab_storage_save_permission", finish.split(
            "if (success && s_enroll_backup)", 1)[0])

    def test_slot_listing_reads_every_sensor_index_page(self):
        self.assertIn("size_t page_count = (limit + 255U) / 256U;",
                      self.fingerprint)
        self.assertIn(
            "{FP_CMD_READ_INDEX_TABLE, (uint8_t)page}",
            self.fingerprint,
        )
        self.assertIn('cJSON_AddNumberToObject(item, "slot", slot);',
                      self.controller)

    def test_template_restore_honors_sensor_packet_size(self):
        self.assertIn("FP_CMD_READ_SYSTEM_PARAMETERS", self.fingerprint)
        self.assertIn("static size_t data_packet_size(void)", self.fingerprint)
        self.assertIn("case 0: return 32;", self.fingerprint)
        self.assertIn("case 3: return 256;", self.fingerprint)
        self.assertIn(
            "attempt == 0 ? preferred_packet_size : 32",
            self.fingerprint,
        )
        self.assertNotIn("fp_packet_t optional_ack", self.fingerprint)

    def test_template_restore_reports_sensor_failure_stage(self):
        self.assertIn("template download rejected", self.fingerprint)
        self.assertIn("template packet send failed", self.fingerprint)
        self.assertIn("template store failed", self.fingerprint)
        restore = self.controller.split(
            "static void handle_restore_fingerprint", 1)[1]
        restore = restore.split("static void handle_delete_fingerprint", 1)[0]
        self.assertIn("cab_fp_last_error()", restore)


if __name__ == "__main__":
    unittest.main()
