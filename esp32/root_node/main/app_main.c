#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "cabinet_mesh.h"
#include "cabinet_protocol.h"
#include "cabinet_serial.h"
#include "root_controller.h"
#include "root_display.h"
#include "root_ota.h"
#include "root_storage.h"
#include "driver/usb_serial_jtag.h"
#include "esp_heap_caps.h"
#include "esp_mac.h"
#include "esp_system.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "nvs_flash.h"

#define ROUTE_CAPACITY 100
#define HOST_SERIAL_TASK_STACK_SIZE (12 * 1024)
#define MESH_INCOMING_QUEUE_DEPTH 64
#define MESH_INCOMING_TASK_STACK_SIZE (8 * 1024)
#define STATUS_DISPLAY_TASK_STACK_SIZE (7 * 1024)
#define ROOT_STATUS_INTERVAL_MS 60000U

typedef struct {
    char device_id[CAB_APP_ID_MAX + 1];
    uint8_t mac[6];
    uint32_t seen_at;
} route_entry_t;

typedef struct {
    uint8_t from[6];
    size_t length;
    uint8_t data[];
} mesh_incoming_message_t;

static char s_root_id[CAB_APP_ID_MAX + 1];
static route_entry_t s_routes[ROUTE_CAPACITY];
static cab_frame_parser_t s_parser;
static SemaphoreHandle_t s_serial_mutex;
static SemaphoreHandle_t s_route_mutex;
static SemaphoreHandle_t s_controller_mutex;
static QueueHandle_t s_mesh_queue;
static uint32_t s_first_status_report_ms;

static uint32_t now_ms(void) {
    return (uint32_t)(xTaskGetTickCount() * portTICK_PERIOD_MS);
}

static void make_device_id(char *output, size_t size, const char *prefix,
                           const uint8_t mac[6]) {
    snprintf(output, size, "%s_%02X%02X%02X%02X%02X%02X", prefix,
             mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
}

static void remember_route(const char *device_id, const uint8_t mac[6]) {
    if (device_id == NULL || device_id[0] == '\0') return;
    if (xSemaphoreTake(s_route_mutex, pdMS_TO_TICKS(100)) != pdTRUE) return;
    int target = -1;
    int oldest = 0;
    for (int i = 0; i < ROUTE_CAPACITY; ++i) {
        if (strcmp(s_routes[i].device_id, device_id) == 0 ||
            s_routes[i].device_id[0] == '\0') {
            target = i;
            break;
        }
        if (s_routes[i].seen_at < s_routes[oldest].seen_at) oldest = i;
    }
    if (target < 0) target = oldest;
    snprintf(s_routes[target].device_id,
             sizeof(s_routes[target].device_id), "%s", device_id);
    memcpy(s_routes[target].mac, mac, 6);
    s_routes[target].seen_at = now_ms();
    xSemaphoreGive(s_route_mutex);
}

static bool find_route(const char *device_id, uint8_t output[6]) {
    bool found = false;
    if (xSemaphoreTake(s_route_mutex, pdMS_TO_TICKS(100)) != pdTRUE) {
        return false;
    }
    for (int i = 0; i < ROUTE_CAPACITY; ++i) {
        if (strcmp(s_routes[i].device_id, device_id) == 0) {
            memcpy(output, s_routes[i].mac, 6);
            found = true;
            break;
        }
    }
    xSemaphoreGive(s_route_mutex);
    return found;
}

static int hex_nibble(char value) {
    if (value >= '0' && value <= '9') return value - '0';
    if (value >= 'A' && value <= 'F') return value - 'A' + 10;
    if (value >= 'a' && value <= 'f') return value - 'a' + 10;
    return -1;
}

static bool parse_cabinet_mac(const char *device_id, uint8_t output[6]) {
    if (device_id == NULL || strncmp(device_id, "CAB_", 4) != 0 ||
        strlen(device_id) != 16) return false;
    for (int index = 0; index < 6; ++index) {
        int high = hex_nibble(device_id[4 + index * 2]);
        int low = hex_nibble(device_id[5 + index * 2]);
        if (high < 0 || low < 0) return false;
        output[index] = (uint8_t)((high << 4) | low);
    }
    return true;
}

static void serial_send_app(const uint8_t *data, size_t length) {
    if (xSemaphoreTake(s_serial_mutex, pdMS_TO_TICKS(1000)) == pdTRUE) {
        cab_frame_send(data, length, cab_serial_frame_writer, NULL);
        xSemaphoreGive(s_serial_mutex);
    }
}

static void send_root_response(const cab_app_view_t *request,
                               uint16_t command, const uint8_t *payload,
                               uint16_t payload_len, uint8_t flags) {
    uint8_t output[1500];
    int length = cab_app_encode(output, sizeof(output), command,
                                request->message_id, request->correlation_id,
                                flags, s_root_id, s_root_id, payload,
                                payload_len, 0);
    if (length > 0) serial_send_app(output, length);
}

static void controller_transmit(const uint8_t *data, size_t length,
                                void *context) {
    (void)context;
    serial_send_app(data, length);
}

static void host_frame(const uint8_t *data, size_t length, void *context) {
    (void)context;
    cab_app_view_t view;
    if (!cab_app_decode(data, length, &view)) return;
    char target[CAB_APP_ID_MAX + 1];
    cab_app_copy_id(target, sizeof(target), view.device_id, view.device_id_len);
    if (target[0] == '\0' || strcmp(target, s_root_id) == 0) {
        if (xSemaphoreTake(s_controller_mutex, pdMS_TO_TICKS(1000)) ==
            pdTRUE) {
            root_controller_handle(&view);
            xSemaphoreGive(s_controller_mutex);
        }
        return;
    }
    uint8_t destination[6];
    bool route_found = find_route(target, destination);
    if (!route_found) route_found = parse_cabinet_mac(target, destination);
    if (route_found && cab_mesh_send_node(destination, data, length) == ESP_OK) {
        return;
    }
    uint8_t error_payload[32];
    int error_len = cab_pack_ack(error_payload, sizeof(error_payload),
                                 view.message_id, 3001, "route_not_found");
    send_root_response(&view, CAB_CMD_ERROR, error_payload, error_len,
                       CAB_APP_FLAG_IS_ERROR);
}

static void mesh_receive(const uint8_t from[6], const uint8_t *data,
                         size_t length, void *context) {
    (void)context;
    if (data == NULL || length == 0 || length > CAB_FRAME_REASSEMBLY_MAX) {
        cab_mesh_note_receive_drop();
        return;
    }
    mesh_incoming_message_t *message = heap_caps_malloc(
        sizeof(*message) + length, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    if (message == NULL) message = malloc(sizeof(*message) + length);
    if (message == NULL) {
        cab_mesh_note_receive_drop();
        return;
    }
    memcpy(message->from, from, 6);
    message->length = length;
    memcpy(message->data, data, length);
    if (xQueueSend(s_mesh_queue, &message, 0) != pdTRUE) {
        cab_mesh_note_receive_drop();
        free(message);
    }
}

static void process_mesh_message(const uint8_t from[6], const uint8_t *data,
                                 size_t length) {
    bool internal_only = false;
    cab_app_view_t view;
    if (cab_app_decode(data, length, &view)) {
        char source[CAB_APP_ID_MAX + 1];
        cab_app_copy_id(source, sizeof(source), view.source_id,
                        view.source_id_len);
        if (source[0] == '\0') {
            cab_app_copy_id(source, sizeof(source), view.device_id,
                            view.device_id_len);
        }
        remember_route(source, from);
        if (view.command == CAB_CMD_REGISTER && source[0] != '\0') {
            root_ota_note_registration(source, view.payload,
                                       view.payload_len);
        }
        if (view.command == CAB_CMD_CABINET_OTA_PROGRESS &&
            source[0] != '\0') {
            root_ota_note_progress(source, view.payload, view.payload_len);
            internal_only = true;
        }
        if (view.command == CAB_CMD_HEARTBEAT && source[0] != '\0') {
            uint8_t payload[8];
            int payload_length = cab_pack_ack(
                payload, sizeof(payload), view.message_id, 0, "ok");
            uint8_t output[128];
            int output_length = cab_app_encode(
                output, sizeof(output), CAB_CMD_HEARTBEAT_ACK,
                view.message_id, 0, CAB_APP_FLAG_IS_ACK,
                source, s_root_id, payload,
                payload_length > 0 ? (uint16_t)payload_length : 0, 0);
            if (output_length > 0) {
                cab_mesh_send_node(from, output, (size_t)output_length);
            }
        }
    }
    if (!internal_only) serial_send_app(data, length);
}

static void mesh_task(void *argument) {
    (void)argument;
    while (true) {
        mesh_incoming_message_t *message = NULL;
        if (xQueueReceive(s_mesh_queue, &message, portMAX_DELAY) == pdTRUE) {
            process_mesh_message(message->from, message->data, message->length);
            free(message);
        }
    }
}

static void serial_task(void *argument) {
    (void)argument;
    uint8_t buffer[1024];
    while (true) {
        int length = cab_serial_read(buffer, sizeof(buffer), 20);
        if (length > 0) cab_frame_parser_feed(&s_parser, buffer, length);
    }
}

static void status_display_task(void *argument) {
    (void)argument;
    uint32_t last_status_report = 0;
    while (true) {
        uint32_t now = now_ms();
        cab_mesh_stats_t stats = cab_mesh_stats();
        root_display_status_t display = {
            .uptime_seconds = now / 1000U,
            .host_connected = usb_serial_jtag_is_connected(),
            .mesh_connected = cab_mesh_is_connected(),
            .sd_ready = root_storage_ready(),
            .mesh_layer = cab_mesh_layer(),
            .child_count = cab_mesh_child_count(),
            .route_count = cab_mesh_route_count(),
            .send_failures = stats.send_failures,
            .receive_drops = stats.receive_drops,
            .heartbeat_acks = stats.receives,
            .heartbeat_timeouts = 0,
        };
        root_display_update(&display);
        root_ota_maintain();

        bool first_due = last_status_report == 0 &&
                         now >= s_first_status_report_ms;
        bool periodic_due = last_status_report != 0 &&
                            now - last_status_report >=
                                ROOT_STATUS_INTERVAL_MS;
        if ((first_due || periodic_due) &&
            xSemaphoreTake(s_controller_mutex, pdMS_TO_TICKS(1000)) ==
                pdTRUE) {
            root_controller_report_status();
            last_status_report = now;
            xSemaphoreGive(s_controller_mutex);
        }
        vTaskDelay(pdMS_TO_TICKS(1000));
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
    make_device_id(s_root_id, sizeof(s_root_id), "ROOT", mac);
    s_first_status_report_ms = 2000U +
        ((((uint32_t)mac[4] << 8) | mac[5]) % 3000U);
    ESP_ERROR_CHECK(cab_serial_init(CAB_SERIAL_USB_JTAG));
    s_serial_mutex = xSemaphoreCreateMutex();
    s_route_mutex = xSemaphoreCreateMutex();
    s_controller_mutex = xSemaphoreCreateMutex();
    s_mesh_queue = xQueueCreate(MESH_INCOMING_QUEUE_DEPTH,
                                sizeof(mesh_incoming_message_t *));
    ESP_ERROR_CHECK(s_serial_mutex == NULL || s_route_mutex == NULL ||
                    s_controller_mutex == NULL || s_mesh_queue == NULL
                        ? ESP_ERR_NO_MEM : ESP_OK);
    cab_frame_parser_init(&s_parser, host_frame, NULL);
    BaseType_t mesh_task_created = xTaskCreate(
        mesh_task, "mesh_ingress", MESH_INCOMING_TASK_STACK_SIZE,
        NULL, 8, NULL);
    ESP_ERROR_CHECK(mesh_task_created == pdPASS ? ESP_OK : ESP_ERR_NO_MEM);
    ESP_ERROR_CHECK(cab_mesh_init(CAB_MESH_ROOT, mesh_receive, NULL, NULL));
    ESP_ERROR_CHECK(root_controller_init(s_root_id, controller_transmit, NULL)
        ? ESP_OK : ESP_FAIL);
    ESP_ERROR_CHECK(root_ota_init() ? ESP_OK : ESP_FAIL);
    // The TFT is a diagnostic surface. A missing or faulty panel is never a
    // reason to stop Mesh routing or the host serial bridge.
    root_display_init(s_root_id);
    // Status responses nest the protocol encoder and frame writer, whose
    // measured stack frames exceed 6 KB before USB driver overhead.
    BaseType_t task_created = xTaskCreate(serial_task, "host_serial",
                                          HOST_SERIAL_TASK_STACK_SIZE,
                                          NULL, 7, NULL);
    BaseType_t status_created = xTaskCreate(
        status_display_task, "root_status", STATUS_DISPLAY_TASK_STACK_SIZE,
        NULL, 5, NULL);
    ESP_ERROR_CHECK(task_created == pdPASS && status_created == pdPASS
        ? ESP_OK : ESP_ERR_NO_MEM);
}
