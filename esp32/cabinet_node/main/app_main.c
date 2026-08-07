#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "cabinet_controller.h"
#include "cabinet_mesh.h"
#include "cabinet_ota.h"
#include "cabinet_protocol.h"
#include "cabinet_serial.h"
#include "esp_app_desc.h"
#include "esp_heap_caps.h"
#include "esp_mac.h"
#include "esp_system.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "nvs_flash.h"

#define INCOMING_QUEUE_DEPTH 16
#define SERIAL_TASK_STACK 6144
#define BUSINESS_TASK_STACK 12288
#define HEARTBEAT_INTERVAL_MS 5000U
#define ROOT_RESPONSE_TIMEOUT_MS 7000U
#define STATUS_REPORT_INTERVAL_MS 60000U

typedef struct {
    bool mesh_ingress;
    size_t length;
    uint8_t data[];
} incoming_message_t;

static char s_device_id[CAB_APP_ID_MAX + 1];
static cab_frame_parser_t s_parser;
static volatile bool s_mesh_connected;
static volatile bool s_register_pending;
static uint32_t s_first_heartbeat_ms;
static uint32_t s_first_status_report_ms;
static uint32_t s_unanswered_heartbeat_since;
static bool s_root_response_timed_out;
static QueueHandle_t s_incoming_queue;
static SemaphoreHandle_t s_serial_mutex;

static uint32_t now_ms(void) {
    return (uint32_t)(xTaskGetTickCount() * portTICK_PERIOD_MS);
}

static void make_device_id(char *output, size_t size, const uint8_t mac[6]) {
    snprintf(output, size, "CAB_%02X%02X%02X%02X%02X%02X",
             mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
}

static void format_mac(char output[13], const uint8_t mac[6]) {
    snprintf(output, 13, "%02X%02X%02X%02X%02X%02X",
             mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
}

static void serial_send(const uint8_t *data, size_t length) {
    if (xSemaphoreTake(s_serial_mutex, pdMS_TO_TICKS(1000)) == pdTRUE) {
        cab_frame_send(data, length, cab_serial_frame_writer, NULL);
        xSemaphoreGive(s_serial_mutex);
    }
}

static void controller_transmit(const uint8_t *data, size_t length,
                                bool mesh_ingress, void *context) {
    (void)context;
    if (mesh_ingress && s_mesh_connected) {
        cab_mesh_send_root(data, length);
    } else {
        serial_send(data, length);
    }
}

static void enqueue_message(const uint8_t *data, size_t length,
                            bool mesh_ingress) {
    if (data == NULL || length == 0 || length > CAB_FRAME_REASSEMBLY_MAX) {
        if (mesh_ingress) cab_mesh_note_receive_drop();
        return;
    }
    incoming_message_t *message = heap_caps_malloc(
        sizeof(*message) + length, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    if (message == NULL) message = malloc(sizeof(*message) + length);
    if (message == NULL) {
        if (mesh_ingress) cab_mesh_note_receive_drop();
        return;
    }
    message->mesh_ingress = mesh_ingress;
    message->length = length;
    memcpy(message->data, data, length);
    if (xQueueSend(s_incoming_queue, &message, 0) != pdTRUE) {
        if (mesh_ingress) cab_mesh_note_receive_drop();
        free(message);
    }
}

static void serial_frame(const uint8_t *data, size_t length, void *context) {
    (void)context;
    enqueue_message(data, length, false);
}

static void mesh_receive(const uint8_t from[6], const uint8_t *data,
                         size_t length, void *context) {
    (void)from;
    (void)context;
    enqueue_message(data, length, true);
}

static void mesh_state(bool connected, int layer, void *context) {
    (void)layer;
    (void)context;
    if (connected && !s_mesh_connected) s_register_pending = true;
    s_mesh_connected = connected;
    cab_controller_set_mesh_connected(connected);
}

static int encode_periodic(uint8_t *output, size_t output_size,
                           uint16_t command) {
    uint8_t payload[384];
    int payload_length;
    if (command == CAB_CMD_REGISTER) {
        cab_mesh_stats_t stats = cab_mesh_stats();
        uint8_t ap_mac[6];
        uint8_t parent_bssid[6] = {0};
        char ap_mac_text[13];
        char parent_text[13] = {0};
        cab_mesh_ap_mac(ap_mac);
        format_mac(ap_mac_text, ap_mac);
        if (cab_mesh_parent_bssid(parent_bssid)) {
            format_mac(parent_text, parent_bssid);
        }
        payload_length = snprintf((char *)payload, sizeof(payload),
            "{\"device_id\":\"%s\",\"device_name\":\"ESP-IDF Cabinet\","
            "\"is_root\":false,\"firmware_version\":\"%s\","
            "\"hardware_version\":\"%s\","
            "\"mesh_layer\":%d,\"mesh_mac\":\"%s\","
            "\"mesh_ap_mac\":\"%s\",\"parent_bssid\":\"%s\","
            "\"mesh_root_responses\":%lu,\"mesh_heartbeat_acks\":%lu,"
            "\"mesh_heartbeat_timeouts\":%lu}",
            s_device_id, esp_app_get_description()->version,
            CABINET_HARDWARE_VERSION,
            cab_mesh_layer(), s_device_id + 4, ap_mac_text, parent_text,
            (unsigned long)stats.root_responses,
            (unsigned long)stats.heartbeat_acks,
            (unsigned long)stats.heartbeat_timeouts);
    } else if (command == CAB_CMD_CABINET_OTA_PROGRESS) {
        cabinet_ota_status_t status;
        if (!cabinet_ota_get_status(&status)) return -1;
        payload_length = snprintf((char *)payload, sizeof(payload),
            "{\"version\":\"%s\",\"phase\":\"%s\","
            "\"progress\":%u,\"error\":\"%s\"}",
            status.version, status.phase, status.progress, status.error);
    } else {
        cab_mesh_stats_t stats = cab_mesh_stats();
        uint32_t recoveries = stats.reconnects + stats.heartbeat_timeouts;
        if (recoveries > UINT16_MAX) recoveries = UINT16_MAX;
        uint32_t queue_full = stats.receive_drops;
        if (queue_full > UINT16_MAX) queue_full = UINT16_MAX;
        payload_length = cab_pack_heartbeat(
            payload, sizeof(payload), esp_get_free_heap_size(),
            heap_caps_get_free_size(MALLOC_CAP_SPIRAM),
            (uint16_t)esp_get_minimum_free_heap_size(),
            (uint8_t)cab_mesh_layer(), 0,
            (uint16_t)stats.send_failures, (uint16_t)queue_full,
            (uint16_t)recoveries);
    }
    if (payload_length <= 0 || payload_length >= (int)sizeof(payload)) return -1;
    return cab_app_encode(output, output_size, command,
                          cab_next_message_id(), 0, 0,
                          s_device_id, s_device_id, payload,
                          (uint16_t)payload_length, 0);
}

static bool send_periodic(uint16_t command) {
    uint8_t output[1500];
    int length = encode_periodic(output, sizeof(output), command);
    return length > 0 &&
           cab_mesh_send_root_best_effort(output, (size_t)length) == ESP_OK;
}

static void serial_task(void *argument) {
    (void)argument;
    uint8_t buffer[1024];
    while (true) {
        int length = cab_serial_read(buffer, sizeof(buffer), 20);
        if (length > 0) cab_frame_parser_feed(&s_parser, buffer, length);
    }
}

static void business_task(void *argument) {
    (void)argument;
    uint32_t last_register = 0;
    uint32_t last_heartbeat = 0;
    uint32_t last_status_report = 0;
    uint32_t last_ota_report = 0;
    uint32_t last_ota_generation = 0;
    while (true) {
        incoming_message_t *message = NULL;
        if (xQueueReceive(s_incoming_queue, &message,
                          pdMS_TO_TICKS(10)) == pdTRUE) {
            cab_app_view_t view;
            if (cab_app_decode(message->data, message->length, &view)) {
                char target[CAB_APP_ID_MAX + 1];
                cab_app_copy_id(target, sizeof(target), view.device_id,
                                view.device_id_len);
                if (target[0] == '\0' || strcmp(target, s_device_id) == 0) {
                    if (message->mesh_ingress) {
                        cab_mesh_note_root_response(
                            view.command == CAB_CMD_HEARTBEAT_ACK);
                        s_unanswered_heartbeat_since = 0;
                        s_root_response_timed_out = false;
                    }
                    cab_controller_handle(&view, message->mesh_ingress);
                }
            }
            free(message);
        }
        cab_controller_update();

        uint32_t now = now_ms();
        if (s_mesh_connected &&
            ((s_register_pending && now - last_register >= 1000U) ||
             (!s_register_pending && now - last_register >= 60000U))) {
            last_register = now;
            if (send_periodic(CAB_CMD_REGISTER)) s_register_pending = false;
        }
        uint32_t heartbeat_interval = last_heartbeat == 0
            ? s_first_heartbeat_ms : HEARTBEAT_INTERVAL_MS;
        if (s_mesh_connected &&
            now - last_heartbeat >= heartbeat_interval) {
            last_heartbeat = now;
            send_periodic(CAB_CMD_HEARTBEAT);
            if (s_unanswered_heartbeat_since == 0) {
                s_unanswered_heartbeat_since = now;
            }
        }
        bool first_status_due = last_status_report == 0 &&
                                now >= s_first_status_report_ms;
        bool periodic_status_due = last_status_report != 0 &&
            now - last_status_report >= STATUS_REPORT_INTERVAL_MS;
        if (s_mesh_connected && (first_status_due || periodic_status_due)) {
            cab_controller_report_status(true);
            last_status_report = now;
        }
        cabinet_ota_status_t ota_status;
        if (s_mesh_connected && cabinet_ota_get_status(&ota_status) &&
            strcmp(ota_status.phase, "idle") != 0 &&
            (ota_status.generation != last_ota_generation ||
             (ota_status.active && now - last_ota_report >= 5000U))) {
            if (send_periodic(CAB_CMD_CABINET_OTA_PROGRESS)) {
                last_ota_generation = ota_status.generation;
                last_ota_report = now;
            }
        }
        if (!s_mesh_connected) {
            s_unanswered_heartbeat_since = 0;
            s_root_response_timed_out = false;
        } else if (s_unanswered_heartbeat_since != 0 &&
                   now - s_unanswered_heartbeat_since >=
                       ROOT_RESPONSE_TIMEOUT_MS) {
            if (!s_root_response_timed_out) {
                cab_mesh_note_heartbeat_timeout();
            }
            s_root_response_timed_out = true;
            s_register_pending = true;
            s_unanswered_heartbeat_since = now;
        }
    }
}

void app_main(void) {
    esp_err_t nvs = nvs_flash_init();
    if (nvs == ESP_ERR_NVS_NO_FREE_PAGES ||
        nvs == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        ESP_ERROR_CHECK(nvs_flash_init());
    } else {
        ESP_ERROR_CHECK(nvs);
    }
    uint8_t mac[6];
    ESP_ERROR_CHECK(esp_read_mac(mac, ESP_MAC_WIFI_STA));
    make_device_id(s_device_id, sizeof(s_device_id), mac);
    s_first_heartbeat_ms = 1000U +
        ((((uint32_t)mac[4] << 8) | mac[5]) % 2000U);
    // Spread the first report across the minute so a large installation does
    // not create a synchronized 100-node burst after a power restoration.
    s_first_status_report_ms = 5000U +
        ((((uint32_t)mac[2] << 24) | ((uint32_t)mac[3] << 16) |
          ((uint32_t)mac[4] << 8) | mac[5]) % 50000U);

    s_serial_mutex = xSemaphoreCreateMutex();
    s_incoming_queue = xQueueCreate(INCOMING_QUEUE_DEPTH,
                                    sizeof(incoming_message_t *));
    ESP_ERROR_CHECK(s_serial_mutex == NULL || s_incoming_queue == NULL
        ? ESP_ERR_NO_MEM : ESP_OK);
    ESP_ERROR_CHECK(cab_serial_init(CAB_SERIAL_UART0));
    cab_frame_parser_init(&s_parser, serial_frame, NULL);
    ESP_ERROR_CHECK(cab_controller_init(s_device_id, controller_transmit,
                                        NULL) ? ESP_OK : ESP_FAIL);
    ESP_ERROR_CHECK(cab_mesh_init(CAB_MESH_CABINET, mesh_receive,
                                  mesh_state, NULL));
    ESP_ERROR_CHECK(cabinet_ota_init() ? ESP_OK : ESP_FAIL);
    ESP_ERROR_CHECK(cabinet_ota_start_health_validation()
        ? ESP_OK : ESP_ERR_NO_MEM);

    BaseType_t serial_created = xTaskCreate(
        serial_task, "cabinet_serial", SERIAL_TASK_STACK, NULL, 8, NULL);
    BaseType_t business_created = xTaskCreate(
        business_task, "cabinet_business", BUSINESS_TASK_STACK, NULL, 7, NULL);
    ESP_ERROR_CHECK(serial_created == pdPASS && business_created == pdPASS
        ? ESP_OK : ESP_ERR_NO_MEM);
}
