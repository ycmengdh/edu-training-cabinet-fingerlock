#include "cabinet_fingerprint.h"

#include <stdio.h>
#include <string.h>

#include "driver/gpio.h"
#include "driver/uart.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/semphr.h"
#include "freertos/task.h"

#define FP_UART UART_NUM_2
#define FP_TX GPIO_NUM_17
#define FP_RX GPIO_NUM_18
#define FP_PACKET_COMMAND 0x01
#define FP_PACKET_DATA 0x02
#define FP_PACKET_ACK 0x07
#define FP_PACKET_END 0x08
#define FP_OK 0x00
#define FP_NO_FINGER 0x02
#define FP_NOT_FOUND 0x09
#define FP_CMD_GET_IMAGE 0x01
#define FP_CMD_IMAGE_TO_TZ 0x02
#define FP_CMD_SEARCH 0x04
#define FP_CMD_CREATE_MODEL 0x05
#define FP_CMD_STORE_MODEL 0x06
#define FP_CMD_LOAD_MODEL 0x07
#define FP_CMD_UPLOAD_CHAR 0x08
#define FP_CMD_DOWNLOAD_CHAR 0x09
#define FP_CMD_DELETE_MODEL 0x0C
#define FP_CMD_EMPTY_DATABASE 0x0D
#define FP_CMD_VERIFY_PASSWORD 0x13
#define FP_CMD_TEMPLATE_COUNT 0x1D
#define FP_CMD_READ_INDEX_TABLE 0x1F
#define FP_HANDSHAKE_BYTE 0x55
#define FP_PASSWORD_ERROR 0x13
#define FP_POWER_OFF_MS 500
#define FP_POWER_PRELOW_MS 10
#define FP_POWER_STABLE_MS 300
#define FP_ARDUINO_BOOT_DELAY_MS 1000
#define FP_HANDSHAKE_TIMEOUT_MS 500
#define FP_PROBE_ATTEMPTS 3
#define FP_PROBE_TIMEOUT_MS 1000
#define FP_PROBE_RETRY_MS 200
#define FP_RECOVERY_INTERVAL_MS 5000
#define FP_COMM_FAILURE_LIMIT 5

typedef struct {
    uint8_t type;
    uint16_t length;
    uint8_t data[300];
} fp_packet_t;

typedef struct {
    gpio_num_t power_pin;
    gpio_num_t status_pin;
    uint8_t power_on_level;
    uint8_t status_on_level;
    const char *name;
} fp_power_profile_t;

typedef struct {
    uart_stop_bits_t stop_bits;
    uart_sclk_t source_clk;
    const char *name;
} fp_uart_profile_t;

static const fp_power_profile_t s_power_profiles[] = {
    {GPIO_NUM_21, GPIO_NUM_42, 0, 1, "p21-low"},
};

static const fp_uart_profile_t s_uart_profiles[] = {
    {UART_STOP_BITS_1, UART_SCLK_XTAL, "8n1-xtal"},
    {UART_STOP_BITS_1, UART_SCLK_APB, "8n1-apb"},
};

static SemaphoreHandle_t s_mutex;
static QueueHandle_t s_result_queue;
static volatile bool s_ready;
static bool s_power_detected;
static int s_power_off_feedback_level = -1;
static int s_power_on_feedback_level = -1;
static bool s_handshake_seen;
static int s_probe_result = -1;
static volatile bool s_background_enabled;
static volatile bool s_background_requested;
static bool s_uart_installed;
static bool s_scan_task_started;
static bool s_power_profile_configured;
static size_t s_power_profile_index;
static size_t s_uart_profile_index;
static char s_error[128];
static uint32_t s_rx_bytes_seen;
static uint8_t s_first_rx_byte;
static bool s_first_rx_byte_valid;
static uint32_t s_poll_max_ms;
static uint32_t s_error_count;
static cab_fp_enroll_phase_t s_phase;
static int s_enroll_id = -1;
static int64_t s_phase_started_us;
static bool s_verify_released;

static void set_error(const char *message) {
    snprintf(s_error, sizeof(s_error), "%s", message == NULL ? "" : message);
}

static void record_rx(const uint8_t *data, size_t length) {
    if (length > 0 && !s_first_rx_byte_valid) {
        s_first_rx_byte = data[0];
        s_first_rx_byte_valid = true;
    }
    s_rx_bytes_seen += (uint32_t)length;
}

static int read_exact(uint8_t *output, size_t length, uint32_t timeout_ms) {
    size_t received = 0;
    int64_t deadline = esp_timer_get_time() + (int64_t)timeout_ms * 1000;
    while (received < length) {
        int64_t remaining_us = deadline - esp_timer_get_time();
        if (remaining_us <= 0) break;
        TickType_t wait = pdMS_TO_TICKS((remaining_us + 999) / 1000);
        if (wait == 0) wait = 1;
        int count = uart_read_bytes(FP_UART, output + received,
                                    length - received, wait);
        if (count > 0) {
            record_rx(output + received, (size_t)count);
            received += (size_t)count;
        }
    }
    return received == length ? (int)received : -1;
}

static bool read_packet(fp_packet_t *packet, uint32_t timeout_ms) {
    int64_t deadline = esp_timer_get_time() + (int64_t)timeout_ms * 1000;
    uint8_t previous = 0;
    bool found = false;
    while (esp_timer_get_time() < deadline) {
        uint8_t value;
        if (read_exact(&value, 1, 20) < 0) continue;
        if (previous == 0xEF && value == 0x01) {
            found = true;
            break;
        }
        previous = value;
    }
    if (!found) return false;
    uint8_t header[7];
    uint32_t remaining_ms = (uint32_t)((deadline - esp_timer_get_time()) /
                                       1000);
    if (read_exact(header, sizeof(header), remaining_ms) < 0) return false;
    packet->type = header[4];
    uint16_t wire_length = ((uint16_t)header[5] << 8) | header[6];
    if (wire_length < 2 || wire_length - 2 > sizeof(packet->data)) return false;
    packet->length = wire_length - 2;
    remaining_ms = (uint32_t)((deadline - esp_timer_get_time()) / 1000);
    if (read_exact(packet->data, packet->length, remaining_ms) < 0) return false;
    uint8_t checksum_bytes[2];
    remaining_ms = (uint32_t)((deadline - esp_timer_get_time()) / 1000);
    if (read_exact(checksum_bytes, 2, remaining_ms) < 0) return false;
    uint16_t checksum = packet->type + header[5] + header[6];
    for (uint16_t index = 0; index < packet->length; ++index) {
        checksum = (uint16_t)(checksum + packet->data[index]);
    }
    return checksum == (((uint16_t)checksum_bytes[0] << 8) |
                         checksum_bytes[1]);
}

static bool write_packet(uint8_t type, const uint8_t *data, uint16_t length) {
    if (length > 300) return false;
    uint8_t output[311];
    uint16_t wire_length = length + 2;
    output[0] = 0xEF;
    output[1] = 0x01;
    memset(output + 2, 0xFF, 4);
    output[6] = type;
    output[7] = (uint8_t)(wire_length >> 8);
    output[8] = (uint8_t)wire_length;
    if (length > 0) memcpy(output + 9, data, length);
    uint16_t checksum = type + output[7] + output[8];
    for (uint16_t index = 0; index < length; ++index) {
        checksum = (uint16_t)(checksum + data[index]);
    }
    output[9 + length] = (uint8_t)(checksum >> 8);
    output[10 + length] = (uint8_t)checksum;
    for (uint16_t index = 0; index < length + 11; ++index) {
        if (uart_write_bytes(FP_UART, &output[index], 1) != 1) return false;
    }
    return uart_wait_tx_done(FP_UART, pdMS_TO_TICKS(1000)) == ESP_OK;
}

static int command(const uint8_t *data, uint16_t length, fp_packet_t *ack,
                   uint32_t timeout_ms) {
    uart_flush_input(FP_UART);
    if (!write_packet(FP_PACKET_COMMAND, data, length) ||
        !read_packet(ack, timeout_ms) || ack->type != FP_PACKET_ACK ||
        ack->length < 1) return -1;
    return ack->data[0];
}

static int simple_command(const uint8_t *data, uint16_t length,
                          uint32_t timeout_ms) {
    fp_packet_t ack;
    return command(data, length, &ack, timeout_ms);
}

static bool wait_handshake(uint32_t timeout_ms) {
    int64_t deadline = esp_timer_get_time() + (int64_t)timeout_ms * 1000;
    while (esp_timer_get_time() < deadline) {
        uint8_t value = 0;
        if (uart_read_bytes(FP_UART, &value, 1, pdMS_TO_TICKS(20)) == 1) {
            record_rx(&value, 1);
            if (value == FP_HANDSHAKE_BYTE) return true;
        }
    }
    return false;
}

static int get_image(void) {
    const uint8_t data[] = {FP_CMD_GET_IMAGE};
    return simple_command(data, sizeof(data), 350);
}

static int image_to_buffer(uint8_t slot) {
    const uint8_t data[] = {FP_CMD_IMAGE_TO_TZ, slot};
    return simple_command(data, sizeof(data), 500);
}

static int create_model(void) {
    const uint8_t data[] = {FP_CMD_CREATE_MODEL};
    return simple_command(data, sizeof(data), 700);
}

static int store_model(int id) {
    uint8_t data[] = {FP_CMD_STORE_MODEL, 1,
                      (uint8_t)((uint16_t)id >> 8), (uint8_t)id};
    return simple_command(data, sizeof(data), 800);
}

static int load_model(int id) {
    uint8_t data[] = {FP_CMD_LOAD_MODEL, 1,
                      (uint8_t)((uint16_t)id >> 8), (uint8_t)id};
    return simple_command(data, sizeof(data), 800);
}

static int search(int start_id, int count, int *matched_id, int *confidence) {
    uint8_t data[] = {
        FP_CMD_SEARCH, 1,
        (uint8_t)((uint16_t)start_id >> 8), (uint8_t)start_id,
        (uint8_t)((uint16_t)count >> 8), (uint8_t)count,
    };
    fp_packet_t ack;
    int result = command(data, sizeof(data), &ack, 1000);
    if (result == FP_OK && ack.length >= 5) {
        if (matched_id != NULL) {
            *matched_id = ((uint16_t)ack.data[1] << 8) | ack.data[2];
        }
        if (confidence != NULL) {
            *confidence = ((uint16_t)ack.data[3] << 8) | ack.data[4];
        }
    }
    return result;
}

static bool take_sensor(uint32_t timeout_ms) {
    return s_mutex != NULL &&
           xSemaphoreTake(s_mutex, pdMS_TO_TICKS(timeout_ms)) == pdTRUE;
}

static void give_sensor(void) { xSemaphoreGive(s_mutex); }

static bool drive_uart_low(void) {
    if (s_uart_installed) {
        uart_wait_tx_done(FP_UART, pdMS_TO_TICKS(100));
        if (uart_driver_delete(FP_UART) != ESP_OK) {
            set_error("fingerprint uart reset failed");
            return false;
        }
        s_uart_installed = false;
    }
    gpio_reset_pin(FP_TX);
    gpio_reset_pin(FP_RX);
    gpio_config_t pins = {
        .pin_bit_mask = (1ULL << FP_TX) | (1ULL << FP_RX),
        .mode = GPIO_MODE_OUTPUT,
        .pull_up_en = GPIO_PULLUP_DISABLE,
        .pull_down_en = GPIO_PULLDOWN_DISABLE,
        .intr_type = GPIO_INTR_DISABLE,
    };
    return gpio_config(&pins) == ESP_OK &&
           gpio_set_level(FP_TX, 0) == ESP_OK &&
           gpio_set_level(FP_RX, 0) == ESP_OK;
}

static bool install_uart(uart_stop_bits_t stop_bits, uart_sclk_t source_clk) {
    uart_config_t config = {
        .baud_rate = 57600,
        .data_bits = UART_DATA_8_BITS,
        .parity = UART_PARITY_DISABLE,
        .stop_bits = stop_bits,
        .flow_ctrl = UART_HW_FLOWCTRL_DISABLE,
        .rx_flow_ctrl_thresh = 0,
        .source_clk = source_clk,
        .flags = {0},
    };
    if (uart_driver_install(FP_UART, 2048, 0, 0, NULL, 0) != ESP_OK) {
        return false;
    }
    s_uart_installed = true;
    if (uart_param_config(FP_UART, &config) == ESP_OK &&
        uart_set_line_inverse(FP_UART, UART_SIGNAL_INV_DISABLE) == ESP_OK &&
        gpio_set_level(FP_TX, 1) == ESP_OK &&
        gpio_set_direction(FP_TX, GPIO_MODE_OUTPUT) == ESP_OK &&
        gpio_set_pull_mode(FP_RX, GPIO_PULLUP_ONLY) == ESP_OK &&
        gpio_set_direction(FP_RX, GPIO_MODE_INPUT) == ESP_OK &&
        uart_set_pin(FP_UART, FP_TX, FP_RX,
                     UART_PIN_NO_CHANGE, UART_PIN_NO_CHANGE) == ESP_OK) {
        return true;
    }
    uart_driver_delete(FP_UART);
    s_uart_installed = false;
    return false;
}

static bool configure_power_profile(size_t index) {
    if (index >= sizeof(s_power_profiles) / sizeof(s_power_profiles[0])) {
        return false;
    }
    if (s_power_profile_configured) {
        const fp_power_profile_t *previous =
            &s_power_profiles[s_power_profile_index];
        gpio_set_level(previous->power_pin,
                       previous->power_on_level == 0 ? 1 : 0);
        vTaskDelay(pdMS_TO_TICKS(50));
        gpio_reset_pin(previous->power_pin);
        gpio_reset_pin(previous->status_pin);
    }

    const fp_power_profile_t *profile = &s_power_profiles[index];
    uint8_t power_off_level = profile->power_on_level == 0 ? 1 : 0;
    gpio_reset_pin(profile->power_pin);
    gpio_reset_pin(profile->status_pin);
    gpio_set_level(profile->power_pin, power_off_level);
    gpio_config_t power = {
        .pin_bit_mask = (1ULL << profile->power_pin),
        .mode = GPIO_MODE_OUTPUT,
        .pull_up_en = GPIO_PULLUP_DISABLE,
        .pull_down_en = GPIO_PULLDOWN_DISABLE,
        .intr_type = GPIO_INTR_DISABLE,
    };
    gpio_config_t status = {
        .pin_bit_mask = (1ULL << profile->status_pin),
        .mode = GPIO_MODE_INPUT,
        .pull_up_en = GPIO_PULLUP_DISABLE,
        .pull_down_en = GPIO_PULLDOWN_ENABLE,
        .intr_type = GPIO_INTR_DISABLE,
    };
    if (gpio_config(&power) != ESP_OK ||
        gpio_set_level(profile->power_pin, power_off_level) != ESP_OK ||
        gpio_config(&status) != ESP_OK) {
        set_error("fingerprint power profile setup failed");
        return false;
    }
    s_power_profile_index = index;
    s_power_profile_configured = true;
    return true;
}

static bool power_cycle_and_probe(void) {
    if (!s_power_profile_configured) return false;
    const fp_power_profile_t *profile =
        &s_power_profiles[s_power_profile_index];
    uint8_t power_off_level = profile->power_on_level == 0 ? 1 : 0;
    s_ready = false;
    s_background_enabled = false;
    s_power_detected = false;
    s_handshake_seen = false;
    s_probe_result = -1;
    s_rx_bytes_seen = 0;
    s_first_rx_byte = 0;
    s_first_rx_byte_valid = false;

    if (gpio_set_level(profile->power_pin, power_off_level) != ESP_OK ||
        !drive_uart_low()) {
        set_error("fingerprint power-cycle preparation failed");
        return false;
    }
    vTaskDelay(pdMS_TO_TICKS(FP_POWER_OFF_MS));
    s_power_off_feedback_level = gpio_get_level(profile->status_pin);
    if (gpio_set_level(profile->power_pin, profile->power_on_level) != ESP_OK) {
        set_error("fingerprint power enable failed");
        return false;
    }
    vTaskDelay(pdMS_TO_TICKS(FP_POWER_PRELOW_MS));
    // The working Arduino firmware listens for the boot byte as 8N2, then
    // Adafruit_Fingerprint::begin() waits one second and changes to 8N1.
    if (!install_uart(UART_STOP_BITS_2,
                      s_uart_profiles[s_uart_profile_index].source_clk)) {
        set_error("fingerprint uart init failed");
        return false;
    }

    vTaskDelay(pdMS_TO_TICKS(FP_ARDUINO_BOOT_DELAY_MS));
    if (uart_set_stop_bits(
            FP_UART, s_uart_profiles[s_uart_profile_index].stop_bits) !=
        ESP_OK) {
        set_error("fingerprint uart format setup failed");
        return false;
    }
    vTaskDelay(pdMS_TO_TICKS(FP_POWER_STABLE_MS));
    s_power_on_feedback_level = gpio_get_level(profile->status_pin);
    s_power_detected = s_power_on_feedback_level == profile->status_on_level;
    s_handshake_seen = wait_handshake(FP_HANDSHAKE_TIMEOUT_MS);
    uart_flush_input(FP_UART);
    uint8_t verify[] = {FP_CMD_VERIFY_PASSWORD, 0, 0, 0, 0};
    fp_packet_t ack;
    for (int attempt = 0; attempt < FP_PROBE_ATTEMPTS; ++attempt) {
        s_probe_result = command(verify, sizeof(verify), &ack,
                                 FP_PROBE_TIMEOUT_MS);
        if (s_probe_result == FP_OK) {
            s_ready = true;
            set_error("");
            return true;
        }
        if (s_probe_result == FP_PASSWORD_ERROR) break;
        vTaskDelay(pdMS_TO_TICKS(FP_PROBE_RETRY_MS));
    }
    if (s_probe_result == FP_PASSWORD_ERROR) {
        set_error("fingerprint password rejected (probe=19)");
    } else {
        snprintf(s_error, sizeof(s_error),
                 "fingerprint unavailable (%s %s power=%d handshake=%d "
                 "probe=%d rx=%lu first=%02X)",
                 profile->name, s_uart_profiles[s_uart_profile_index].name,
                 s_power_detected ? 1 : 0, s_handshake_seen ? 1 : 0,
                 s_probe_result, (unsigned long)s_rx_bytes_seen,
                 s_first_rx_byte_valid ? s_first_rx_byte : 0);
    }
    return false;
}

static bool probe_uart_profiles(void) {
    size_t count = sizeof(s_uart_profiles) / sizeof(s_uart_profiles[0]);
    size_t first = s_uart_profile_index;
    for (size_t offset = 0; offset < count; ++offset) {
        s_uart_profile_index = (first + offset) % count;
        if (power_cycle_and_probe()) return true;
        if (s_probe_result >= 0) return false;
    }
    return false;
}

static bool probe_power_profiles(void) {
    size_t count = sizeof(s_power_profiles) / sizeof(s_power_profiles[0]);
    size_t first = s_power_profile_configured ? s_power_profile_index : 0;
    for (size_t offset = 0; offset < count; ++offset) {
        size_t index = (first + offset) % count;
        if (!configure_power_profile(index)) continue;
        if (probe_uart_profiles()) return true;
        if (s_power_detected || s_probe_result >= 0) return false;
    }
    return false;
}

static void recovery_task(void *argument) {
    (void)argument;
    while (true) {
        vTaskDelay(pdMS_TO_TICKS(FP_RECOVERY_INTERVAL_MS));
        if (s_ready || !take_sensor(1000)) continue;
        bool recovered = probe_power_profiles();
        give_sensor();
        if (recovered) {
            cab_fp_set_background_enabled(s_background_requested);
        }
    }
}

bool cab_fp_init(void) {
    s_mutex = xSemaphoreCreateMutex();
    s_result_queue = xQueueCreate(4, sizeof(int));
    if (s_mutex == NULL || s_result_queue == NULL) {
        set_error("fingerprint task resources unavailable");
        return false;
    }
    bool ready = probe_power_profiles();
    if (xTaskCreate(recovery_task, "fp_recover", 4096, NULL, 5, NULL) !=
        pdPASS && !ready) {
        set_error("fingerprint unavailable; recovery task not started");
    }
    return ready;
}

bool cab_fp_ready(void) { return s_ready; }
bool cab_fp_power_detected(void) {
    if (!s_power_profile_configured) return false;
    const fp_power_profile_t *profile =
        &s_power_profiles[s_power_profile_index];
    return gpio_get_level(profile->status_pin) == profile->status_on_level;
}
int cab_fp_power_off_feedback_level(void) {
    return s_power_off_feedback_level;
}
int cab_fp_power_on_feedback_level(void) {
    return s_power_on_feedback_level;
}
bool cab_fp_handshake_seen(void) { return s_handshake_seen; }
int cab_fp_probe_result(void) { return s_probe_result; }
const char *cab_fp_last_error(void) { return s_error; }

int cab_fp_verify_once(void) {
    if (!s_ready || !take_sensor(1200)) return -2;
    int result = get_image();
    if (result != FP_OK) {
        give_sensor();
        return result < 0 ? -2 : -1;
    }
    result = image_to_buffer(1);
    if (result != FP_OK) {
        give_sensor();
        return result < 0 ? -2 : -1;
    }
    int matched = -1;
    result = search(0, CAB_FP_MAX_SLOTS, &matched, NULL);
    give_sensor();
    if (result == FP_OK) return matched;
    return result < 0 ? -2 : -1;
}

static void background_task(void *argument) {
    (void)argument;
    unsigned communication_failures = 0;
    while (true) {
        if (!s_background_enabled) {
            vTaskDelay(pdMS_TO_TICKS(50));
            continue;
        }
        int64_t started = esp_timer_get_time();
        int result = cab_fp_verify_once();
        uint32_t elapsed = (uint32_t)((esp_timer_get_time() - started) / 1000);
        if (elapsed > s_poll_max_ms) s_poll_max_ms = elapsed;
        if (result >= 0) {
            communication_failures = 0;
            xQueueSend(s_result_queue, &result, 0);
        } else if (result == -1) {
            communication_failures = 0;
        } else if (++communication_failures >= FP_COMM_FAILURE_LIMIT) {
            ++s_error_count;
            s_ready = false;
            s_background_enabled = false;
            communication_failures = 0;
            set_error("fingerprint communication lost; recovering");
        }
        vTaskDelay(pdMS_TO_TICKS(result >= 0 ? 300 : 80));
    }
}

void cab_fp_set_background_enabled(bool enabled) {
    s_background_requested = enabled;
    if (!s_scan_task_started && enabled && s_ready) {
        if (xTaskCreate(background_task, "fp_scan", 4096, NULL, 4, NULL) ==
            pdPASS) s_scan_task_started = true;
    }
    s_background_enabled = enabled && s_ready && s_scan_task_started;
    if (s_result_queue != NULL) xQueueReset(s_result_queue);
}

bool cab_fp_take_background_result(int *fingerprint_id) {
    return s_result_queue != NULL && fingerprint_id != NULL &&
           xQueueReceive(s_result_queue, fingerprint_id, 0) == pdTRUE;
}

uint32_t cab_fp_poll_max_ms(void) { return s_poll_max_ms; }
uint32_t cab_fp_error_count(void) { return s_error_count; }

static void set_phase(cab_fp_enroll_phase_t phase) {
    s_phase = phase;
    s_phase_started_us = esp_timer_get_time();
}

void cab_fp_enroll_begin(int fingerprint_id) {
    s_enroll_id = fingerprint_id;
    s_verify_released = false;
    set_error("");
    set_phase(CAB_FP_ENROLL_PLACE_1);
}

void cab_fp_enroll_abort(const char *reason) {
    set_error(reason == NULL ? "cancelled" : reason);
    set_phase(CAB_FP_ENROLL_DONE_FAIL);
}

cab_fp_enroll_phase_t cab_fp_enroll_phase(void) { return s_phase; }

const char *cab_fp_enroll_phase_code(void) {
    static const char *codes[] = {
        "idle", "place_1", "lift_1", "place_2", "lift_2", "place_3",
        "lift_3", "place_4", "store", "verify_1", "verify_2",
        "done", "failed"
    };
    return codes[s_phase <= CAB_FP_ENROLL_DONE_FAIL ? s_phase : 0];
}

const char *cab_fp_enroll_hint(void) {
    static const char *hints[] = {
        "", "place finger (1/4)", "lift finger", "place finger (2/4)",
        "lift finger", "place finger (3/4)", "lift finger",
        "place finger (4/4)", "saving", "verify finger (1/2)",
        "verify finger (2/2)", "complete", "failed"
    };
    return hints[s_phase <= CAB_FP_ENROLL_DONE_FAIL ? s_phase : 0];
}

int cab_fp_enroll_step(void) {
    switch (s_phase) {
        case CAB_FP_ENROLL_PLACE_1:
        case CAB_FP_ENROLL_LIFT_1: return 1;
        case CAB_FP_ENROLL_PLACE_2:
        case CAB_FP_ENROLL_LIFT_2: return 2;
        case CAB_FP_ENROLL_PLACE_3:
        case CAB_FP_ENROLL_LIFT_3: return 3;
        case CAB_FP_ENROLL_PLACE_4:
        case CAB_FP_ENROLL_STORE: return 4;
        case CAB_FP_ENROLL_VERIFY_1: return 5;
        case CAB_FP_ENROLL_VERIFY_2: return 6;
        case CAB_FP_ENROLL_DONE_OK: return 6;
        default: return 0;
    }
}

bool cab_fp_enroll_tick(void) {
    if (s_phase == CAB_FP_ENROLL_IDLE ||
        s_phase == CAB_FP_ENROLL_DONE_OK ||
        s_phase == CAB_FP_ENROLL_DONE_FAIL || !s_ready) return false;
    cab_fp_enroll_phase_t before = s_phase;
    if (esp_timer_get_time() - s_phase_started_us > 45000000LL) {
        set_error("fingerprint enrollment timeout");
        set_phase(CAB_FP_ENROLL_DONE_FAIL);
        return true;
    }
    if (!take_sensor(1200)) return false;
    int result = FP_OK;
    switch (s_phase) {
        case CAB_FP_ENROLL_PLACE_1:
            if (get_image() == FP_OK && image_to_buffer(1) == FP_OK)
                set_phase(CAB_FP_ENROLL_LIFT_1);
            break;
        case CAB_FP_ENROLL_LIFT_1:
            if (get_image() == FP_NO_FINGER) set_phase(CAB_FP_ENROLL_PLACE_2);
            break;
        case CAB_FP_ENROLL_PLACE_2:
            if (get_image() == FP_OK && image_to_buffer(2) == FP_OK) {
                result = create_model();
                if (result == FP_OK) set_phase(CAB_FP_ENROLL_LIFT_2);
                else { set_error("fingerprints did not match");
                       set_phase(CAB_FP_ENROLL_DONE_FAIL); }
            }
            break;
        case CAB_FP_ENROLL_LIFT_2:
            if (get_image() == FP_NO_FINGER) set_phase(CAB_FP_ENROLL_PLACE_3);
            break;
        case CAB_FP_ENROLL_PLACE_3:
            if (get_image() == FP_OK && image_to_buffer(1) == FP_OK)
                set_phase(CAB_FP_ENROLL_LIFT_3);
            break;
        case CAB_FP_ENROLL_LIFT_3:
            if (get_image() == FP_NO_FINGER) set_phase(CAB_FP_ENROLL_PLACE_4);
            break;
        case CAB_FP_ENROLL_PLACE_4:
            if (get_image() == FP_OK && image_to_buffer(2) == FP_OK)
                set_phase(CAB_FP_ENROLL_STORE);
            break;
        case CAB_FP_ENROLL_STORE:
            if (create_model() == FP_OK && store_model(s_enroll_id) == FP_OK) {
                s_verify_released = false;
                set_phase(CAB_FP_ENROLL_VERIFY_1);
            } else {
                set_error("fingerprint store failed");
                set_phase(CAB_FP_ENROLL_DONE_FAIL);
            }
            break;
        case CAB_FP_ENROLL_VERIFY_1:
        case CAB_FP_ENROLL_VERIFY_2:
            result = get_image();
            if (!s_verify_released) {
                if (result == FP_NO_FINGER) s_verify_released = true;
            } else if (result == FP_OK && image_to_buffer(1) == FP_OK) {
                result = search(s_enroll_id, 1, NULL, NULL);
                if (result == FP_OK) {
                    s_verify_released = false;
                    set_phase(s_phase == CAB_FP_ENROLL_VERIFY_1
                        ? CAB_FP_ENROLL_VERIFY_2 : CAB_FP_ENROLL_DONE_OK);
                } else if (result == FP_NOT_FOUND) {
                    uint8_t data[] = {FP_CMD_DELETE_MODEL,
                        (uint8_t)((uint16_t)s_enroll_id >> 8),
                        (uint8_t)s_enroll_id, 0, 1};
                    simple_command(data, sizeof(data), 800);
                    set_error("fingerprint verification failed");
                    set_phase(CAB_FP_ENROLL_DONE_FAIL);
                }
            }
            break;
        default: break;
    }
    give_sensor();
    return before != s_phase;
}

int cab_fp_verify_slot(int fingerprint_id, bool *finger_detected,
                       int *confidence) {
    if (finger_detected != NULL) *finger_detected = false;
    if (confidence != NULL) *confidence = 0;
    if (!s_ready || !take_sensor(1200)) return -1;
    int result = get_image();
    if (result == FP_NO_FINGER) { give_sensor(); return 0; }
    if (finger_detected != NULL) *finger_detected = true;
    if (result != FP_OK || image_to_buffer(1) != FP_OK) {
        give_sensor(); return -1;
    }
    result = search(fingerprint_id, 1, NULL, confidence);
    give_sensor();
    return result == FP_OK ? 1 : (result == FP_NOT_FOUND ? 0 : -1);
}

bool cab_fp_delete(int fingerprint_id) {
    if (!s_ready || !take_sensor(1200)) return false;
    uint8_t data[] = {FP_CMD_DELETE_MODEL,
                      (uint8_t)((uint16_t)fingerprint_id >> 8),
                      (uint8_t)fingerprint_id, 0, 1};
    bool ok = simple_command(data, sizeof(data), 800) == FP_OK;
    give_sensor();
    return ok;
}

bool cab_fp_delete_all(void) {
    if (!s_ready || !take_sensor(1200)) return false;
    const uint8_t data[] = {FP_CMD_EMPTY_DATABASE};
    bool ok = simple_command(data, sizeof(data), 1500) == FP_OK;
    give_sensor();
    return ok;
}

bool cab_fp_template_exists(int fingerprint_id) {
    if (!s_ready || !take_sensor(1200)) return false;
    bool exists = load_model(fingerprint_id) == FP_OK;
    give_sensor();
    return exists;
}

int cab_fp_template_count(void) {
    if (!s_ready || !take_sensor(1200)) return 0;
    const uint8_t data[] = {FP_CMD_TEMPLATE_COUNT};
    fp_packet_t ack;
    int result = command(data, sizeof(data), &ack, 800);
    int count = result == FP_OK && ack.length >= 3
        ? (((uint16_t)ack.data[1] << 8) | ack.data[2]) : 0;
    give_sensor();
    return count;
}

bool cab_fp_list_slots(uint16_t *slots, size_t capacity, size_t *slot_count) {
    if (slot_count != NULL) *slot_count = 0;
    if (!s_ready || slots == NULL || slot_count == NULL || capacity == 0 ||
        !take_sensor(1200)) return false;

    bool ok = true;
    size_t count = 0;
    size_t limit = capacity < CAB_FP_MAX_SLOTS ? capacity : CAB_FP_MAX_SLOTS;
    size_t page_count = (limit + 255U) / 256U;
    for (size_t page = 0; ok && page < page_count; ++page) {
        uint8_t data[] = {FP_CMD_READ_INDEX_TABLE, (uint8_t)page};
        fp_packet_t ack;
        int result = command(data, sizeof(data), &ack, 1000);
        ok = result == FP_OK && ack.length >= 33;
        if (!ok) break;
        size_t page_start = page * 256U;
        size_t page_end = page_start + 256U;
        if (page_end > limit) page_end = limit;
        for (size_t slot = page_start; slot < page_end; ++slot) {
            size_t page_slot = slot - page_start;
            size_t byte_index = page_slot / 8U;
            uint8_t bit_mask = (uint8_t)(1U << (page_slot % 8U));
            if ((ack.data[1 + byte_index] & bit_mask) != 0)
                slots[count++] = (uint16_t)slot;
        }
    }
    give_sensor();
    *slot_count = count;
    return ok;
}

bool cab_fp_read_template(int fingerprint_id, uint8_t *output,
                          size_t output_size, size_t *output_length) {
    if (output_length != NULL) *output_length = 0;
    if (!s_ready || output == NULL || output_size < CAB_FP_TEMPLATE_SIZE ||
        !take_sensor(1500)) return false;
    bool ok = load_model(fingerprint_id) == FP_OK;
    if (ok) {
        uint8_t upload[] = {FP_CMD_UPLOAD_CHAR, 1};
        fp_packet_t ack;
        ok = command(upload, sizeof(upload), &ack, 1000) == FP_OK;
    }
    size_t filled = 0;
    while (ok && filled < CAB_FP_TEMPLATE_SIZE) {
        fp_packet_t packet;
        if (!read_packet(&packet, 2000) ||
            (packet.type != FP_PACKET_DATA && packet.type != FP_PACKET_END)) {
            ok = false;
            break;
        }
        size_t copy = packet.length;
        if (copy > CAB_FP_TEMPLATE_SIZE - filled)
            copy = CAB_FP_TEMPLATE_SIZE - filled;
        memcpy(output + filled, packet.data, copy);
        filled += copy;
        if (packet.type == FP_PACKET_END) break;
    }
    give_sensor();
    ok = ok && filled == CAB_FP_TEMPLATE_SIZE;
    if (ok && output_length != NULL) *output_length = filled;
    return ok;
}

bool cab_fp_write_template(int fingerprint_id, const uint8_t *data,
                           size_t length) {
    if (!s_ready || data == NULL || length < CAB_FP_TEMPLATE_SIZE ||
        fingerprint_id < 0 || fingerprint_id >= CAB_FP_MAX_SLOTS ||
        !take_sensor(1500)) return false;
    uint8_t download[] = {FP_CMD_DOWNLOAD_CHAR, 1};
    fp_packet_t ack;
    bool ok = command(download, sizeof(download), &ack, 1000) == FP_OK;
    for (size_t offset = 0; ok && offset < CAB_FP_TEMPLATE_SIZE; offset += 128) {
        ok = write_packet(offset + 128 >= CAB_FP_TEMPLATE_SIZE
                          ? FP_PACKET_END : FP_PACKET_DATA,
                          data + offset, 128);
        vTaskDelay(pdMS_TO_TICKS(5));
    }
    if (ok) {
        fp_packet_t optional_ack;
        read_packet(&optional_ack, 300);
        ok = store_model(fingerprint_id) == FP_OK;
    }
    give_sensor();
    return ok;
}

bool cab_fp_copy_template(int source_id, int destination_id) {
    if (!s_ready || !take_sensor(1200)) return false;
    bool ok = load_model(source_id) == FP_OK &&
              store_model(destination_id) == FP_OK;
    give_sensor();
    return ok;
}
