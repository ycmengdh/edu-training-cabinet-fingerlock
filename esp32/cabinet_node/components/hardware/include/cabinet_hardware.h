#pragma once

#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define CAB_LOCK_COUNT 4
#define CAB_KEY_CANCEL 4

bool cab_hardware_init(void);
bool cab_lock_open(uint8_t lock_id);
void cab_lock_close(uint8_t lock_id);
void cab_lock_close_all(void);
uint8_t cab_lock_active_mask(void);
void cab_lock_set_permission_hint(uint8_t lock_mask);
void cab_lock_clear_permission_hint(void);
void cab_led_set_override(uint8_t led_mask);
void cab_led_clear_override(void);
void cab_hardware_update(void);

// Returns 0..4 once for each debounced press, or -1 when no event is pending.
int cab_key_take_press(void);
bool cab_key_take_long_press(int *key_id);
bool cab_key_is_pressed(int key_id);

#ifdef __cplusplus
}
#endif
