#include "cabinet_hardware.h"

#include <string.h>

#include "driver/gpio.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"

#define SHIFT_DS GPIO_NUM_4
#define SHIFT_LATCH GPIO_NUM_15
#define SHIFT_CLOCK GPIO_NUM_16
#define SHIFT_RESET GPIO_NUM_5
#define LOCK_OPEN_MS 200U
#define LOCK_FORCE_OFF_MS 1000U
#define HINT_HALF_PERIOD_MS 400U
#define KEY_DEBOUNCE_MS 20U
#define KEY_LONG_PRESS_MS 10000U

static const gpio_num_t s_key_pins[5] = {
    GPIO_NUM_47, GPIO_NUM_48, GPIO_NUM_45, GPIO_NUM_38, GPIO_NUM_39
};
static const uint8_t s_relay_bits[4] = {4, 5, 6, 7};
static const uint8_t s_led_bits[4] = {3, 2, 1, 0};

typedef struct {
    bool raw;
    bool stable;
    bool reported;
    bool long_reported;
    uint32_t changed_at;
    uint32_t pressed_at;
} key_state_t;

static SemaphoreHandle_t s_mutex;
static uint8_t s_lock_mask;
static uint8_t s_hint_mask;
static bool s_hint_phase;
static uint32_t s_opened_at[4];
static uint32_t s_refreshed_at[4];
static uint32_t s_hint_changed_at;
static key_state_t s_keys[5];
static int s_key_event = -1;
static int s_long_key_event = -1;

static uint32_t now_ms(void) {
    return (uint32_t)(xTaskGetTickCount() * portTICK_PERIOD_MS);
}

static void shift_write(uint8_t value) {
    gpio_set_level(SHIFT_LATCH, 0);
    for (int bit = 7; bit >= 0; --bit) {
        gpio_set_level(SHIFT_CLOCK, 0);
        gpio_set_level(SHIFT_DS, (value >> bit) & 1);
        gpio_set_level(SHIFT_CLOCK, 1);
    }
    gpio_set_level(SHIFT_LATCH, 1);
}

static void refresh_output(void) {
    uint8_t output = 0;
    for (uint8_t lock = 0; lock < 4; ++lock) {
        if ((s_lock_mask & (1U << lock)) != 0) {
            output |= (uint8_t)(1U << s_relay_bits[lock]);
            output |= (uint8_t)(1U << s_led_bits[lock]);
        } else if (s_hint_phase &&
                   (s_hint_mask & (1U << lock)) != 0) {
            output |= (uint8_t)(1U << s_led_bits[lock]);
        }
    }
    shift_write(output);
}

bool cab_hardware_init(void) {
    gpio_config_t output = {
        .pin_bit_mask = (1ULL << SHIFT_DS) | (1ULL << SHIFT_LATCH) |
                        (1ULL << SHIFT_CLOCK) | (1ULL << SHIFT_RESET),
        .mode = GPIO_MODE_OUTPUT,
        .pull_up_en = GPIO_PULLUP_DISABLE,
        .pull_down_en = GPIO_PULLDOWN_DISABLE,
        .intr_type = GPIO_INTR_DISABLE,
    };
    if (gpio_config(&output) != ESP_OK) return false;
    gpio_config_t input = {
        .pin_bit_mask = (1ULL << GPIO_NUM_47) | (1ULL << GPIO_NUM_48) |
                        (1ULL << GPIO_NUM_45) | (1ULL << GPIO_NUM_38) |
                        (1ULL << GPIO_NUM_39),
        .mode = GPIO_MODE_INPUT,
        .pull_up_en = GPIO_PULLUP_ENABLE,
        .pull_down_en = GPIO_PULLDOWN_DISABLE,
        .intr_type = GPIO_INTR_DISABLE,
    };
    if (gpio_config(&input) != ESP_OK) return false;
    s_mutex = xSemaphoreCreateMutex();
    if (s_mutex == NULL) return false;
    gpio_set_level(SHIFT_RESET, 1);
    shift_write(0);
    uint32_t now = now_ms();
    for (int index = 0; index < 5; ++index) {
        bool pressed = gpio_get_level(s_key_pins[index]) == 0;
        s_keys[index].raw = pressed;
        s_keys[index].stable = pressed;
        s_keys[index].reported = pressed;
        s_keys[index].changed_at = now;
        s_keys[index].pressed_at = now;
    }
    return true;
}

bool cab_lock_open(uint8_t lock_id) {
    if (lock_id >= 4 || s_mutex == NULL ||
        xSemaphoreTake(s_mutex, pdMS_TO_TICKS(100)) != pdTRUE) return false;
    uint32_t now = now_ms();
    if ((s_lock_mask & (1U << lock_id)) == 0) {
        s_lock_mask |= (uint8_t)(1U << lock_id);
        s_opened_at[lock_id] = now;
    }
    s_refreshed_at[lock_id] = now;
    refresh_output();
    xSemaphoreGive(s_mutex);
    return true;
}

void cab_lock_close(uint8_t lock_id) {
    if (lock_id >= 4 || s_mutex == NULL ||
        xSemaphoreTake(s_mutex, pdMS_TO_TICKS(100)) != pdTRUE) return;
    s_lock_mask &= (uint8_t)~(1U << lock_id);
    s_opened_at[lock_id] = 0;
    s_refreshed_at[lock_id] = 0;
    refresh_output();
    xSemaphoreGive(s_mutex);
}

void cab_lock_close_all(void) {
    if (s_mutex == NULL ||
        xSemaphoreTake(s_mutex, pdMS_TO_TICKS(100)) != pdTRUE) return;
    s_lock_mask = 0;
    memset(s_opened_at, 0, sizeof(s_opened_at));
    memset(s_refreshed_at, 0, sizeof(s_refreshed_at));
    refresh_output();
    xSemaphoreGive(s_mutex);
}

uint8_t cab_lock_active_mask(void) {
    uint8_t result = 0;
    if (s_mutex != NULL &&
        xSemaphoreTake(s_mutex, pdMS_TO_TICKS(100)) == pdTRUE) {
        result = s_lock_mask;
        xSemaphoreGive(s_mutex);
    }
    return result;
}

void cab_lock_set_permission_hint(uint8_t lock_mask) {
    if (s_mutex == NULL ||
        xSemaphoreTake(s_mutex, pdMS_TO_TICKS(100)) != pdTRUE) return;
    s_hint_mask = lock_mask & 0x0F;
    s_hint_phase = true;
    s_hint_changed_at = now_ms();
    refresh_output();
    xSemaphoreGive(s_mutex);
}

void cab_lock_clear_permission_hint(void) {
    if (s_mutex == NULL ||
        xSemaphoreTake(s_mutex, pdMS_TO_TICKS(100)) != pdTRUE) return;
    s_hint_mask = 0;
    s_hint_phase = false;
    refresh_output();
    xSemaphoreGive(s_mutex);
}

static void update_keys(uint32_t now) {
    for (int index = 0; index < 5; ++index) {
        key_state_t *key = &s_keys[index];
        bool raw = gpio_get_level(s_key_pins[index]) == 0;
        if (raw != key->raw) {
            key->raw = raw;
            key->changed_at = now;
        }
        if (raw != key->stable && now - key->changed_at >= KEY_DEBOUNCE_MS) {
            key->stable = raw;
            if (raw) {
                key->pressed_at = now;
                key->long_reported = false;
                if (s_key_event < 0) s_key_event = index;
                key->reported = true;
            } else {
                key->reported = false;
                key->long_reported = false;
            }
        }
        if (key->stable && !key->long_reported &&
            now - key->pressed_at >= KEY_LONG_PRESS_MS) {
            key->long_reported = true;
            if (s_long_key_event < 0) s_long_key_event = index;
        }
    }
}

void cab_hardware_update(void) {
    uint32_t now = now_ms();
    update_keys(now);
    if (s_mutex == NULL ||
        xSemaphoreTake(s_mutex, pdMS_TO_TICKS(100)) != pdTRUE) return;
    bool changed = false;
    for (uint8_t lock = 0; lock < 4; ++lock) {
        if ((s_lock_mask & (1U << lock)) != 0 &&
            (now - s_refreshed_at[lock] >= LOCK_OPEN_MS ||
             now - s_opened_at[lock] >= LOCK_FORCE_OFF_MS)) {
            s_lock_mask &= (uint8_t)~(1U << lock);
            s_opened_at[lock] = 0;
            s_refreshed_at[lock] = 0;
            changed = true;
        }
    }
    if (s_hint_mask != 0 &&
        now - s_hint_changed_at >= HINT_HALF_PERIOD_MS) {
        s_hint_phase = !s_hint_phase;
        s_hint_changed_at = now;
        changed = true;
    }
    if (changed) refresh_output();
    xSemaphoreGive(s_mutex);
}

int cab_key_take_press(void) {
    int result = s_key_event;
    s_key_event = -1;
    return result;
}

bool cab_key_take_long_press(int *key_id) {
    if (s_long_key_event < 0) return false;
    if (key_id != NULL) *key_id = s_long_key_event;
    s_long_key_event = -1;
    return true;
}
