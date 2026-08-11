#include "cabinet_mesh.h"

#include <string.h>

#include "esp_bridge.h"
#include "esp_event.h"
#include "esp_idf_version.h"
#include "esp_log.h"
#include "esp_mac.h"
#include "esp_mesh_lite.h"
#include "esp_netif.h"
#include "esp_wifi.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"

#define CAB_MESH_CHANNEL 6
#define CAB_MESH_PASSWORD "Mesh@2026"
#define CAB_MESH_SSID_PREFIX "CABINET_MESH"
#define CAB_MESH_MAX_PACKET 1500
#define CAB_MESH_WIRE_HEADER 18
#define CAB_MESH_MAX_ROUTES 100
#define CAB_MESH_RAW_UP 0x43414201U
#define CAB_MESH_RAW_DOWN 0x43414202U
#define CAB_MESH_DIRECTION_UP 1
#define CAB_MESH_DIRECTION_DOWN 2
#define CAB_MESH_PARENT_RETRY_INTERVAL_SECONDS 2U
#define CAB_MESH_PARENT_RETRY_COUNT 1U
#define CAB_MESH_RESCAN_INTERVAL_SECONDS 3U
#define CAB_MESH_SEARCH_WATCHDOG_INTERVAL_MS 5000U
#define CAB_MESH_SEARCH_START_JITTER_MS 3000U

static const uint8_t CAB_MESH_BROADCAST_DESTINATION[6] = {
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
};

static const char *TAG = "cab_mesh";
static cab_mesh_role_t s_role;
static cab_mesh_receive_t s_receive;
static cab_mesh_state_t s_state;
static void *s_context;
static volatile bool s_connected;
static volatile int s_layer;
static uint8_t s_self_mac[6];
static uint8_t s_ap_mac[6];
static char s_softap_ssid[33];
static cab_mesh_stats_t s_stats;
static uint8_t s_tx_buffer[CAB_MESH_WIRE_HEADER + CAB_MESH_MAX_PACKET];
static SemaphoreHandle_t s_send_mutex;
static portMUX_TYPE s_stats_lock = portMUX_INITIALIZER_UNLOCKED;
static bool s_parent_search_requested;

#if ESP_IDF_VERSION >= ESP_IDF_VERSION_VAL(6, 0, 0)
/* Mesh-Lite 1.0.2's prebuilt core uses the deprecated IDF 5 symbol. IDF 6
   removed only the wrapper; the replacement has the same contract. Keeping
   this in the main object also satisfies the prebuilt library's link order. */
esp_netif_t *esp_netif_next(esp_netif_t *netif) {
    return esp_netif_next_unsafe(netif);
}
#endif

static void stats_increment(uint32_t *counter) {
    portENTER_CRITICAL(&s_stats_lock);
    (*counter)++;
    portEXIT_CRITICAL(&s_stats_lock);
}

static void notify_state(bool connected, int layer) {
    const bool changed = connected != s_connected || layer != s_layer;
    if (!changed) return;
    if (s_role == CAB_MESH_CABINET && connected && !s_connected) {
        stats_increment(&s_stats.reconnects);
    }
    s_connected = connected;
    s_layer = layer;
    if (s_state != NULL) s_state(connected, layer, s_context);
}

static void wifi_event(void *argument, esp_event_base_t base, int32_t event_id,
                       void *event_data) {
    (void)argument;
    (void)base;
    (void)event_data;
    if (event_id == WIFI_EVENT_SCAN_DONE && s_role == CAB_MESH_CABINET) {
        stats_increment(&s_stats.scan_cycles);
    }
}

static bool take_parent_search_request(void) {
    bool requested;
    portENTER_CRITICAL(&s_stats_lock);
    requested = s_parent_search_requested;
    s_parent_search_requested = false;
    portEXIT_CRITICAL(&s_stats_lock);
    return requested;
}

static bool tick_reached(TickType_t now, TickType_t target) {
    return (int32_t)(now - target) >= 0;
}

static void state_task(void *argument) {
    (void)argument;
    const TickType_t watchdog_interval =
        pdMS_TO_TICKS(CAB_MESH_SEARCH_WATCHDOG_INTERVAL_MS);
    const uint32_t jitter_seed = ((uint32_t)s_self_mac[4] << 8) |
                                 s_self_mac[5];
    TickType_t next_parent_search = xTaskGetTickCount() + pdMS_TO_TICKS(
        1000U + jitter_seed % CAB_MESH_SEARCH_START_JITTER_MS);
    while (true) {
        const int level = esp_mesh_lite_get_level();
        bool connected = false;
        if (s_role == CAB_MESH_ROOT) {
            connected = level == ROOT;
        } else if (level > ROOT) {
            wifi_ap_record_t parent = {0};
            connected = esp_wifi_sta_get_ap_info(&parent) == ESP_OK;
        }
        notify_state(connected, connected ? level : 0);

        if (s_role == CAB_MESH_CABINET) {
            const TickType_t now = xTaskGetTickCount();
            const bool requested = take_parent_search_request();
            if (connected) {
                next_parent_search = now + watchdog_interval;
            }
            if (requested ||
                (!connected && tick_reached(now, next_parent_search))) {
                esp_mesh_lite_connect();
                next_parent_search = now + watchdog_interval;
            }
        }
        vTaskDelay(pdMS_TO_TICKS(250));
    }
}

static bool decode_packet(const uint8_t *data, uint32_t length,
                          uint8_t direction, const uint8_t **source,
                          const uint8_t **destination,
                          const uint8_t **payload, size_t *payload_length) {
    if (data == NULL || length < CAB_MESH_WIRE_HEADER || data[0] != 'C' ||
        data[1] != 'M' || data[2] != 1 || data[3] != direction) {
        return false;
    }
    const size_t encoded_length = (size_t)data[16] | ((size_t)data[17] << 8);
    if (encoded_length == 0 || encoded_length >= CAB_MESH_MAX_PACKET ||
        encoded_length + CAB_MESH_WIRE_HEADER != length) {
        return false;
    }
    *source = data + 4;
    *destination = data + 10;
    *payload = data + CAB_MESH_WIRE_HEADER;
    *payload_length = encoded_length;
    return true;
}

static esp_err_t send_raw(uint32_t message_id, const uint8_t *data,
                          size_t length,
                          esp_err_t (*sender)(const uint8_t *, size_t)) {
    esp_mesh_lite_msg_config_t config = {
        .raw_msg = {
            .msg_id = message_id,
            .expect_resp_msg_id = 0,
            .max_retry = 0,
            .retry_interval = 0,
            .data = data,
            .size = length,
            .raw_resend = sender,
            .raw_send_fail = NULL,
        },
    };
    return esp_mesh_lite_send_msg(ESP_MESH_LITE_RAW_MSG, &config);
}

static esp_err_t upstream_message(uint8_t *data, uint32_t length,
                                  uint8_t **out_data, uint32_t *out_length,
                                  uint32_t sequence) {
    (void)sequence;
    *out_data = NULL;
    *out_length = 0;
    if (s_role != CAB_MESH_ROOT) return ESP_OK;

    const uint8_t *source;
    const uint8_t *destination;
    const uint8_t *payload;
    size_t payload_length;
    if (!decode_packet(data, length, CAB_MESH_DIRECTION_UP, &source,
                       &destination, &payload, &payload_length)) {
        return ESP_ERR_INVALID_SIZE;
    }
    const uint8_t root_destination[6] = {0};
    if (memcmp(destination, root_destination, sizeof(root_destination)) != 0 &&
        memcmp(destination, s_self_mac, sizeof(s_self_mac)) != 0) {
        return ESP_OK;
    }
    stats_increment(&s_stats.receives);
    if (s_receive != NULL) {
        s_receive(source, payload, payload_length, s_context);
    }
    return ESP_OK;
}

static esp_err_t downstream_message(uint8_t *data, uint32_t length,
                                    uint8_t **out_data, uint32_t *out_length,
                                    uint32_t sequence) {
    (void)sequence;
    *out_data = NULL;
    *out_length = 0;
    if (s_role != CAB_MESH_CABINET) return ESP_OK;

    const uint8_t *source;
    const uint8_t *destination;
    const uint8_t *payload;
    size_t payload_length;
    if (!decode_packet(data, length, CAB_MESH_DIRECTION_DOWN, &source,
                       &destination, &payload, &payload_length)) {
        return ESP_ERR_INVALID_SIZE;
    }

    /* Mesh-Lite broadcasts to direct children. Every cabinet relays the raw
       packet so a target at any supported level can receive it. */
    esp_err_t forward_error = send_raw(
        CAB_MESH_RAW_DOWN, data, length,
        esp_mesh_lite_send_broadcast_raw_msg_to_child);
    if (forward_error != ESP_OK) {
        ESP_LOGD(TAG, "downstream relay failed: %s",
                 esp_err_to_name(forward_error));
    }

    if (memcmp(destination, s_self_mac, sizeof(s_self_mac)) != 0 &&
        memcmp(destination, CAB_MESH_BROADCAST_DESTINATION,
               sizeof(CAB_MESH_BROADCAST_DESTINATION)) != 0) {
        return ESP_OK;
    }
    stats_increment(&s_stats.receives);
    if (s_receive != NULL) {
        s_receive(source, payload, payload_length, s_context);
    }
    return ESP_OK;
}

static const esp_mesh_lite_raw_msg_action_t RAW_ACTIONS[] = {
    {CAB_MESH_RAW_UP, 0, upstream_message},
    {CAB_MESH_RAW_DOWN, 0, downstream_message},
    {0, 0, NULL},
};

static void configure_wifi(void) {
    wifi_config_t station = {0};
    ESP_ERROR_CHECK(esp_bridge_wifi_set_config(WIFI_IF_STA, &station));

    ESP_ERROR_CHECK(esp_read_mac(s_ap_mac, ESP_MAC_WIFI_SOFTAP));
    snprintf(s_softap_ssid, sizeof(s_softap_ssid), "%s_%02X%02X%02X",
             CAB_MESH_SSID_PREFIX, s_ap_mac[3], s_ap_mac[4], s_ap_mac[5]);

    wifi_config_t access_point = {0};
    access_point.ap.ssid_len = strlen(s_softap_ssid);
    memcpy(access_point.ap.ssid, s_softap_ssid, access_point.ap.ssid_len);
    snprintf((char *)access_point.ap.password,
             sizeof(access_point.ap.password), "%s", CAB_MESH_PASSWORD);
    access_point.ap.channel = CAB_MESH_CHANNEL;
    access_point.ap.authmode = WIFI_AUTH_WPA2_PSK;
    access_point.ap.max_connection = 6;
    ESP_ERROR_CHECK(esp_bridge_wifi_set_config(WIFI_IF_AP, &access_point));
}

esp_err_t cab_mesh_init(cab_mesh_role_t role, cab_mesh_receive_t receive,
                        cab_mesh_state_t state, void *context) {
    s_role = role;
    s_receive = receive;
    s_state = state;
    s_context = context;
    s_connected = false;
    s_layer = 0;
    s_parent_search_requested = false;
    memset(&s_stats, 0, sizeof(s_stats));
    ESP_ERROR_CHECK(esp_read_mac(s_self_mac, ESP_MAC_WIFI_STA));

    s_send_mutex = xSemaphoreCreateMutex();
    if (s_send_mutex == NULL) return ESP_ERR_NO_MEM;

    ESP_ERROR_CHECK(esp_netif_init());
    esp_err_t error = esp_event_loop_create_default();
    if (error != ESP_OK && error != ESP_ERR_INVALID_STATE) return error;
    ESP_ERROR_CHECK(esp_event_handler_register(WIFI_EVENT, WIFI_EVENT_SCAN_DONE,
                                               wifi_event, NULL));

    esp_bridge_create_all_netif();
    configure_wifi();
    ESP_ERROR_CHECK(esp_wifi_set_ps(WIFI_PS_NONE));

    esp_mesh_lite_config_t config = ESP_MESH_LITE_DEFAULT_INIT();
    config.vendor_id[0] = 0x43;
    config.vendor_id[1] = 0x42;
    config.mesh_id = 0x31;
    config.max_connect_number = 6;
    config.max_router_number = 5;
    config.max_level = 6;
    config.max_node_number = CAB_MESH_MAX_ROUTES;
    config.join_mesh_ignore_router_status = true;
    config.join_mesh_without_configured_wifi = role == CAB_MESH_CABINET;
    config.leaf_node = false;
    config.softap_ssid = s_softap_ssid;
    config.softap_password = CAB_MESH_PASSWORD;
    /* LAN OTA only serves requests between nodes in the same category. The
       root provides cabinet images from SD, so both roles must match here. */
    config.device_category = "cabinet-node";
    esp_mesh_lite_init(&config);
    if (role == CAB_MESH_CABINET) {
        esp_mesh_lite_set_wifi_reconnect_interval(
            CAB_MESH_PARENT_RETRY_INTERVAL_SECONDS,
            CAB_MESH_PARENT_RETRY_COUNT,
            CAB_MESH_RESCAN_INTERVAL_SECONDS);
    }
    ESP_ERROR_CHECK(esp_mesh_lite_raw_msg_action_list_register(RAW_ACTIONS));
    ESP_ERROR_CHECK(esp_mesh_lite_set_softap_info(s_softap_ssid,
                                                  CAB_MESH_PASSWORD));
    if (role == CAB_MESH_ROOT) {
        ESP_ERROR_CHECK(esp_mesh_lite_set_allowed_level(ROOT));
    } else {
        ESP_ERROR_CHECK(esp_mesh_lite_set_disallowed_level(ROOT));
    }
    esp_mesh_lite_start();

    if (xTaskCreate(state_task, "mesh_state", 4096, NULL, 5, NULL) != pdPASS) {
        return ESP_ERR_NO_MEM;
    }
    return ESP_OK;
}

bool cab_mesh_is_connected(void) { return s_connected; }
bool cab_mesh_is_root(void) { return s_role == CAB_MESH_ROOT; }
int cab_mesh_layer(void) { return s_layer; }

int cab_mesh_child_count(void) {
    wifi_sta_list_t children = {0};
    return esp_wifi_ap_get_sta_list(&children) == ESP_OK ? children.num : 0;
}

int cab_mesh_route_count(void) {
    if (s_role != CAB_MESH_ROOT) return 0;
    uint32_t total = esp_mesh_lite_get_mesh_node_number();
    return total > 0 ? (int)(total - 1) : 0;
}

int cab_mesh_link_rssi(void) {
    if (!s_connected || s_role == CAB_MESH_ROOT) return 0;
    wifi_ap_record_t parent = {0};
    return esp_wifi_sta_get_ap_info(&parent) == ESP_OK ? parent.rssi : -127;
}

void cab_mesh_self_mac(uint8_t output[6]) { memcpy(output, s_self_mac, 6); }

void cab_mesh_ap_mac(uint8_t output[6]) { memcpy(output, s_ap_mac, 6); }

bool cab_mesh_parent_bssid(uint8_t output[6]) {
    if (output == NULL || !s_connected || s_role == CAB_MESH_ROOT) return false;
    wifi_ap_record_t parent = {0};
    if (esp_wifi_sta_get_ap_info(&parent) != ESP_OK) return false;
    memcpy(output, parent.bssid, 6);
    return true;
}

void cab_mesh_request_parent_search(void) {
    if (s_role != CAB_MESH_CABINET) return;
    portENTER_CRITICAL(&s_stats_lock);
    s_parent_search_requested = true;
    portEXIT_CRITICAL(&s_stats_lock);
}

static esp_err_t send_packet(uint32_t message_id, uint8_t direction,
                             const uint8_t destination[6],
                             const uint8_t *payload, size_t length,
                             esp_err_t (*sender)(const uint8_t *, size_t),
                             TickType_t mutex_wait) {
    if (!s_connected) return ESP_ERR_INVALID_STATE;
    if (payload == NULL || destination == NULL || length == 0 ||
        length >= CAB_MESH_MAX_PACKET) {
        return ESP_ERR_INVALID_ARG;
    }
    stats_increment(&s_stats.sends);
    if (xSemaphoreTake(s_send_mutex, mutex_wait) != pdTRUE) {
        stats_increment(&s_stats.send_failures);
        return ESP_ERR_TIMEOUT;
    }
    s_tx_buffer[0] = 'C';
    s_tx_buffer[1] = 'M';
    s_tx_buffer[2] = 1;
    s_tx_buffer[3] = direction;
    memcpy(s_tx_buffer + 4, s_self_mac, 6);
    memcpy(s_tx_buffer + 10, destination, 6);
    s_tx_buffer[16] = (uint8_t)(length & 0xFF);
    s_tx_buffer[17] = (uint8_t)((length >> 8) & 0xFF);
    memcpy(s_tx_buffer + CAB_MESH_WIRE_HEADER, payload, length);
    esp_err_t error = send_raw(message_id, s_tx_buffer,
                               CAB_MESH_WIRE_HEADER + length, sender);
    xSemaphoreGive(s_send_mutex);
    if (error != ESP_OK) stats_increment(&s_stats.send_failures);
    return error;
}

esp_err_t cab_mesh_send_root(const uint8_t *data, size_t length) {
    if (s_role == CAB_MESH_ROOT) return ESP_ERR_INVALID_STATE;
    const uint8_t root_destination[6] = {0};
    return send_packet(CAB_MESH_RAW_UP, CAB_MESH_DIRECTION_UP,
                       root_destination, data, length,
                       esp_mesh_lite_send_raw_msg_to_root,
                       pdMS_TO_TICKS(6000));
}

esp_err_t cab_mesh_send_root_best_effort(const uint8_t *data, size_t length) {
    if (s_role == CAB_MESH_ROOT) return ESP_ERR_INVALID_STATE;
    const uint8_t root_destination[6] = {0};
    return send_packet(CAB_MESH_RAW_UP, CAB_MESH_DIRECTION_UP,
                       root_destination, data, length,
                       esp_mesh_lite_send_raw_msg_to_root, 0);
}

esp_err_t cab_mesh_send_node(const uint8_t destination[6], const uint8_t *data,
                             size_t length) {
    if (s_role != CAB_MESH_ROOT || destination == NULL) {
        return ESP_ERR_INVALID_STATE;
    }
    return send_packet(CAB_MESH_RAW_DOWN, CAB_MESH_DIRECTION_DOWN, destination,
                       data, length,
                       esp_mesh_lite_send_broadcast_raw_msg_to_child,
                       pdMS_TO_TICKS(6000));
}

esp_err_t cab_mesh_send_all(const uint8_t *data, size_t length) {
    if (s_role != CAB_MESH_ROOT) return ESP_ERR_INVALID_STATE;
    return send_packet(CAB_MESH_RAW_DOWN, CAB_MESH_DIRECTION_DOWN,
                       CAB_MESH_BROADCAST_DESTINATION, data, length,
                       esp_mesh_lite_send_broadcast_raw_msg_to_child,
                       pdMS_TO_TICKS(6000));
}

int cab_mesh_routes(uint8_t (*output)[6], size_t capacity) {
    if (s_role != CAB_MESH_ROOT || output == NULL || capacity == 0) return 0;
    if (capacity > CAB_MESH_MAX_ROUTES) capacity = CAB_MESH_MAX_ROUTES;
    uint32_t total = 0;
    const node_info_list_t *node = esp_mesh_lite_get_nodes_list(&total);
    int copied = 0;
    while (node != NULL && (size_t)copied < capacity && copied < (int)total) {
        if (memcmp(node->node->mac_addr, s_self_mac, 6) != 0) {
            memcpy(output[copied++], node->node->mac_addr, 6);
        }
        node = node->next;
    }
    return copied;
}

cab_mesh_stats_t cab_mesh_stats(void) {
    cab_mesh_stats_t copy;
    portENTER_CRITICAL(&s_stats_lock);
    copy = s_stats;
    portEXIT_CRITICAL(&s_stats_lock);
    return copy;
}

void cab_mesh_note_root_response(bool heartbeat_ack) {
    stats_increment(&s_stats.root_responses);
    if (heartbeat_ack) stats_increment(&s_stats.heartbeat_acks);
}

void cab_mesh_note_heartbeat_timeout(void) {
    stats_increment(&s_stats.heartbeat_timeouts);
}

void cab_mesh_note_receive_drop(void) {
    stats_increment(&s_stats.receive_drops);
}
