#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define CAB_FP_TEMPLATE_SIZE 512
#define CAB_FP_MAX_SLOTS 200
#define CAB_FP_TEMP_SLOT 0

typedef enum {
    CAB_FP_ENROLL_IDLE = 0,
    CAB_FP_ENROLL_PLACE_1,
    CAB_FP_ENROLL_LIFT_1,
    CAB_FP_ENROLL_PLACE_2,
    CAB_FP_ENROLL_LIFT_2,
    CAB_FP_ENROLL_PLACE_3,
    CAB_FP_ENROLL_LIFT_3,
    CAB_FP_ENROLL_PLACE_4,
    CAB_FP_ENROLL_STORE,
    CAB_FP_ENROLL_VERIFY_1,
    CAB_FP_ENROLL_VERIFY_2,
    CAB_FP_ENROLL_DONE_OK,
    CAB_FP_ENROLL_DONE_FAIL,
} cab_fp_enroll_phase_t;

bool cab_fp_init(void);
bool cab_fp_ready(void);
bool cab_fp_power_detected(void);
int cab_fp_power_off_feedback_level(void);
int cab_fp_power_on_feedback_level(void);
bool cab_fp_handshake_seen(void);
int cab_fp_probe_result(void);
const char *cab_fp_last_error(void);
void cab_fp_set_background_enabled(bool enabled);
bool cab_fp_take_background_result(int *fingerprint_id);
uint32_t cab_fp_poll_max_ms(void);
uint32_t cab_fp_error_count(void);

void cab_fp_enroll_begin(int fingerprint_id);
bool cab_fp_enroll_tick(void);
void cab_fp_enroll_abort(const char *reason);
cab_fp_enroll_phase_t cab_fp_enroll_phase(void);
const char *cab_fp_enroll_phase_code(void);
const char *cab_fp_enroll_hint(void);
int cab_fp_enroll_step(void);

int cab_fp_verify_once(void);
int cab_fp_verify_slot(int fingerprint_id, bool *finger_detected,
                       int *confidence);
bool cab_fp_delete(int fingerprint_id);
bool cab_fp_delete_all(void);
bool cab_fp_template_exists(int fingerprint_id);
int cab_fp_template_count(void);
bool cab_fp_list_slots(uint16_t *slots, size_t capacity, size_t *slot_count);
bool cab_fp_read_template(int fingerprint_id, uint8_t *output,
                          size_t output_size, size_t *output_length);
bool cab_fp_write_template(int fingerprint_id, const uint8_t *data,
                           size_t length);
bool cab_fp_copy_template(int source_id, int destination_id);

#ifdef __cplusplus
}
#endif
