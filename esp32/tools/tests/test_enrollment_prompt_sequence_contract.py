import os
import unittest


REPOSITORY_DIR = os.path.dirname(os.path.dirname(os.path.dirname(
    os.path.dirname(os.path.abspath(__file__)))))
FINGERPRINT_SOURCE = os.path.join(
    REPOSITORY_DIR, "esp32", "cabinet_node", "components",
    "fingerprint", "cabinet_fingerprint.c")
FINGERPRINT_HEADER = os.path.join(
    REPOSITORY_DIR, "esp32", "cabinet_node", "components",
    "fingerprint", "include", "cabinet_fingerprint.h")
CONTROLLER_SOURCE = os.path.join(
    REPOSITORY_DIR, "esp32", "cabinet_node", "components",
    "controller", "cabinet_controller.c")


class EnrollmentPromptSequenceContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        with open(FINGERPRINT_SOURCE, "r", encoding="utf-8") as source:
            cls.fingerprint = source.read()
        with open(FINGERPRINT_HEADER, "r", encoding="utf-8") as source:
            cls.header = source.read()
        with open(CONTROLLER_SOURCE, "r", encoding="utf-8") as source:
            cls.controller = source.read()

    def test_verification_has_explicit_release_and_press_phases(self):
        phases = [
            "CAB_FP_ENROLL_VERIFY_LIFT_1",
            "CAB_FP_ENROLL_VERIFY_PLACE_1",
            "CAB_FP_ENROLL_VERIFY_RETRY_LIFT_1",
            "CAB_FP_ENROLL_VERIFY_LIFT_2",
            "CAB_FP_ENROLL_VERIFY_PLACE_2",
            "CAB_FP_ENROLL_VERIFY_RETRY_LIFT_2",
        ]
        positions = [self.header.index(phase) for phase in phases]
        self.assertEqual(positions, sorted(positions))

    def test_sensor_drives_each_release_before_next_press(self):
        self.assertIn("case CAB_FP_ENROLL_VERIFY_LIFT_1:", self.fingerprint)
        self.assertIn("case CAB_FP_ENROLL_VERIFY_RETRY_LIFT_1:", self.fingerprint)
        self.assertIn("set_phase(CAB_FP_ENROLL_VERIFY_PLACE_1);", self.fingerprint)
        self.assertIn("case CAB_FP_ENROLL_VERIFY_LIFT_2:", self.fingerprint)
        self.assertIn("case CAB_FP_ENROLL_VERIFY_RETRY_LIFT_2:", self.fingerprint)
        self.assertIn("set_phase(CAB_FP_ENROLL_VERIFY_PLACE_2);", self.fingerprint)

    def test_each_verification_allows_three_attempts_before_failure(self):
        self.assertIn("#define FP_ENROLL_VERIFY_MAX_ATTEMPTS 3",
                      self.fingerprint)
        self.assertIn("s_verify_attempt_1 < FP_ENROLL_VERIFY_MAX_ATTEMPTS",
                      self.fingerprint)
        self.assertIn("s_verify_attempt_2 < FP_ENROLL_VERIFY_MAX_ATTEMPTS",
                      self.fingerprint)
        self.assertIn("CAB_FP_ENROLL_VERIFY_RETRY_LIFT_1", self.fingerprint)
        self.assertIn("CAB_FP_ENROLL_VERIFY_RETRY_LIFT_2", self.fingerprint)

    def test_progress_protocol_reports_all_four_verification_prompts(self):
        for phase in (
                '"verify_lift_1"', '"verify_place_1"',
                '"verify_retry_lift_1"', '"verify_lift_2"',
                '"verify_place_2"', '"verify_retry_lift_2"'):
            self.assertIn(phase, self.fingerprint)
        self.assertIn("send_enroll_progress();", self.controller)

    def test_final_verification_reports_in_progress_until_success(self):
        self.assertRegex(
            self.fingerprint,
            r"case CAB_FP_ENROLL_VERIFY_LIFT_2:\s*"
            r"case CAB_FP_ENROLL_VERIFY_PLACE_2:\s*"
            r"case CAB_FP_ENROLL_VERIFY_RETRY_LIFT_2: return 5;")
        self.assertIn("case CAB_FP_ENROLL_DONE_OK: return 6;",
                      self.fingerprint)

    def test_enrollment_failure_returns_sensor_reason(self):
        self.assertIn("const char *failure_message = cab_fp_last_error();",
                      self.controller)


if __name__ == "__main__":
    unittest.main()
