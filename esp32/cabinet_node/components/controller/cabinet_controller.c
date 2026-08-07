#include "cabinet_controller.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "cJSON.h"
#include "cabinet_fingerprint.h"
#include "cabinet_hardware.h"
#include "cabinet_mesh.h"
#include "cabinet_ota.h"
#include "cabinet_storage.h"
#include "esp_app_desc.h"
#include "esp_heap_caps.h"
#include "esp_system.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

#define RESPONSE_CACHE_COUNT 8
#define RESPONSE_CACHE_SIZE 1500
#define VERIFY_WINDOW_MS 10000U
#define PERMISSION_SYNC_TIMEOUT_MS 30000U
#define FP_TEST_IDLE_TIMEOUT_MS 60000U
#define FP_TEST_POLL_MS 180U
#define UNIX_2000_01_01 946684800U

typedef enum {
    STATE_WAIT_FINGER,
    STATE_VERIFIED_WINDOW,
    STATE_ENROLLING,
    STATE_FINGERPRINT_TEST,
} controller_state_t;

typedef struct {
    bool valid;
    bool ingress;
    uint16_t command;
    uint16_t message_id;
    uint16_t correlation_id;
    uint16_t length;
    uint32_t stored_at;
    uint32_t sequence;
    uint8_t data[RESPONSE_CACHE_SIZE];
} response_cache_t;

static char s_device_id[CAB_APP_ID_MAX + 1];
static cab_controller_tx_t s_transmit;
static void *s_transmit_context;
static bool s_mesh_connected;
static response_cache_t s_cache[RESPONSE_CACHE_COUNT];
static uint8_t s_cache_next;
static uint32_t s_cache_sequence;
static const cab_app_view_t *s_current_request;
static bool s_current_ingress;

static controller_state_t s_state;
static uint32_t s_state_entered;
static cab_permission_t s_verified_permission;
static bool s_verified_valid;

static cab_permission_t s_staged[CAB_STORAGE_PERMISSION_MAX];
static bool s_staged_received[CAB_STORAGE_PERMISSION_MAX];
static bool s_sync_active;
static size_t s_sync_expected;
static size_t s_sync_received;
static uint32_t s_sync_version;
static uint32_t s_sync_started;

static int s_enroll_target = -1;
static char s_enroll_user[CAB_STORAGE_USER_ID_MAX + 1];
static uint16_t s_enroll_message_id;
static uint16_t s_enroll_correlation_id;
static bool s_enroll_ingress;
static bool s_enroll_backup;
static cab_fp_enroll_phase_t s_last_enroll_phase;

static char s_test_token[65];
static int s_test_source_id = -1;
static uint32_t s_test_last_activity;
static uint32_t s_test_last_poll;
static bool s_test_finger_down;
static uint32_t s_last_permission_lost_report;
static bool s_permission_lost_pending;
static bool s_fingerprint_was_ready;

static uint32_t now_ms(void) {
    return (uint32_t)(xTaskGetTickCount() * portTICK_PERIOD_MS);
}

static void set_state(controller_state_t state) {
    s_state = state;
    s_state_entered = now_ms();
    cab_fp_set_background_enabled(state == STATE_WAIT_FINGER);
}

static bool response_matches(const response_cache_t *entry,
                             const cab_app_view_t *request,
                             bool mesh_ingress) {
    return entry->valid && entry->ingress == mesh_ingress &&
           entry->command == request->command &&
           entry->message_id == request->message_id &&
           entry->correlation_id == request->correlation_id &&
           now_ms() - entry->stored_at <= 30000U;
}

static bool replay_cached(const cab_app_view_t *request,
                          bool mesh_ingress) {
    const response_cache_t *matches[RESPONSE_CACHE_COUNT];
    size_t count = 0;
    for (size_t index = 0; index < RESPONSE_CACHE_COUNT; ++index) {
        if (response_matches(&s_cache[index], request, mesh_ingress)) {
            matches[count++] = &s_cache[index];
        }
    }
    for (size_t index = 1; index < count; ++index) {
        const response_cache_t *entry = matches[index];
        size_t position = index;
        while (position > 0 &&
               matches[position - 1]->sequence > entry->sequence) {
            matches[position] = matches[position - 1];
            --position;
        }
        matches[position] = entry;
    }
    for (size_t index = 0; index < count; ++index) {
        s_transmit(matches[index]->data, matches[index]->length,
                   mesh_ingress, s_transmit_context);
    }
    return count > 0;
}

static void maybe_cache(const uint8_t *data, size_t length) {
    if (s_current_request == NULL || length > RESPONSE_CACHE_SIZE) return;
    response_cache_t *entry = &s_cache[s_cache_next++ % RESPONSE_CACHE_COUNT];
    entry->valid = true;
    entry->ingress = s_current_ingress;
    entry->command = s_current_request->command;
    entry->message_id = s_current_request->message_id;
    entry->correlation_id = s_current_request->correlation_id;
    entry->length = (uint16_t)length;
    entry->stored_at = now_ms();
    entry->sequence = ++s_cache_sequence;
    memcpy(entry->data, data, length);
}

static void send_payload(uint16_t command, uint16_t message_id,
                         uint16_t correlation_id, uint8_t flags,
                         const uint8_t *payload, uint16_t payload_length,
                         bool ingress, bool cache) {
    uint8_t output[RESPONSE_CACHE_SIZE];
    int length = cab_app_encode(output, sizeof(output), command, message_id,
                                correlation_id, flags, s_device_id,
                                s_device_id, payload, payload_length,
                                cab_storage_unix_time());
    if (length <= 0) return;
    if (cache) maybe_cache(output, (size_t)length);
    s_transmit(output, (size_t)length, ingress, s_transmit_context);
}

static void send_json(uint16_t command, uint16_t message_id,
                      uint16_t correlation_id, const char *json,
                      bool ingress, bool cache) {
    if (json == NULL) json = "{}";
    size_t length = strlen(json);
    if (length > CAB_APP_MAX_PAYLOAD) return;
    send_payload(command, message_id, correlation_id, 0,
                 (const uint8_t *)json, (uint16_t)length, ingress, cache);
}

static void send_ack(const cab_app_view_t *request, const char *result,
                     bool ingress) {
    uint8_t payload[96];
    int length = cab_pack_ack(payload, sizeof(payload), request->message_id,
                              0, result);
    if (length > 0) {
        send_payload(CAB_CMD_ACK, request->message_id,
                     request->correlation_id, CAB_APP_FLAG_IS_ACK,
                     payload, (uint16_t)length, ingress, true);
    }
}

static void send_error(const cab_app_view_t *request, uint16_t code,
                       const char *message, bool ingress) {
    uint8_t payload[192];
    int length = cab_pack_ack(payload, sizeof(payload), request->message_id,
                              code, message);
    if (length > 0) {
        send_payload(CAB_CMD_ERROR, request->message_id,
                     request->correlation_id, CAB_APP_FLAG_IS_ERROR,
                     payload, (uint16_t)length, ingress, true);
    }
}

static void send_async_json(uint16_t command, uint16_t message_id,
                            uint16_t correlation_id, const char *json,
                            bool ingress) {
    send_json(command, message_id, correlation_id, json, ingress, false);
}

static cJSON *parse_json_payload(const cab_app_view_t *request) {
    if (request->payload_len == 0) return cJSON_CreateObject();
    return cJSON_ParseWithLength((const char *)request->payload,
                                 request->payload_len);
}

static const char *json_string(const cJSON *object, const char *name,
                               const char *fallback) {
    const cJSON *item = cJSON_GetObjectItemCaseSensitive(object, name);
    return cJSON_IsString(item) && item->valuestring != NULL
        ? item->valuestring : fallback;
}

static int json_int(const cJSON *object, const char *name, int fallback) {
    const cJSON *item = cJSON_GetObjectItemCaseSensitive(object, name);
    return cJSON_IsNumber(item) ? item->valueint : fallback;
}

static uint32_t json_u32(const cJSON *object, const char *name,
                         uint32_t fallback) {
    const cJSON *item = cJSON_GetObjectItemCaseSensitive(object, name);
    return cJSON_IsNumber(item) && item->valuedouble >= 0
        ? (uint32_t)item->valuedouble : fallback;
}

static bool json_bool(const cJSON *object, const char *name, bool fallback) {
    const cJSON *item = cJSON_GetObjectItemCaseSensitive(object, name);
    return cJSON_IsBool(item) ? cJSON_IsTrue(item) : fallback;
}

static bool parse_permission(const cJSON *json, cab_permission_t *permission) {
    memset(permission, 0, sizeof(*permission));
    permission->fingerprint_id = (int16_t)json_int(json, "fingerprint_id", -1);
    permission->local_fingerprint_id = (int16_t)json_int(
        json, "local_fp_id", permission->fingerprint_id);
    const char *user_id = json_string(json, "user_id", "");
    if (permission->fingerprint_id < 0 || user_id[0] == '\0') return false;
    snprintf(permission->user_id, sizeof(permission->user_id), "%s", user_id);
    snprintf(permission->name, sizeof(permission->name), "%s",
             json_string(json, "name", ""));
    permission->user_id_number = cab_storage_user_id_to_number(user_id);
    permission->role = (cab_role_t)json_int(json, "role", CAB_ROLE_STUDENT);
    permission->is_backup = json_bool(json, "is_backup", false);
    const cJSON *locks = cJSON_GetObjectItemCaseSensitive(json,
                                                         "lock_permissions");
    for (int lock = 0; lock < CAB_LOCK_COUNT; ++lock) {
        char key[8];
        snprintf(key, sizeof(key), "lock_%d", lock);
        if (json_bool(locks, key, false)) permission->lock_mask |= 1U << lock;
    }
    if (permission->role != CAB_ROLE_ADMIN) permission->lock_mask &= 0x0E;
    const char *expire = json_string(json, "expire_date", "");
    permission->expire_days = expire[0] == '\0'
        ? UINT32_MAX : cab_storage_date_to_days(expire);
    return true;
}

static uint8_t hex_value(char value) {
    if (value >= '0' && value <= '9') return (uint8_t)(value - '0');
    if (value >= 'a' && value <= 'f') return (uint8_t)(value - 'a' + 10);
    if (value >= 'A' && value <= 'F') return (uint8_t)(value - 'A' + 10);
    return 0xFF;
}

static bool decode_template_hex(const char *hex,
                                uint8_t output[CAB_FP_TEMPLATE_SIZE]) {
    if (hex == NULL || strlen(hex) != CAB_FP_TEMPLATE_SIZE * 2) return false;
    for (size_t index = 0; index < CAB_FP_TEMPLATE_SIZE; ++index) {
        uint8_t high = hex_value(hex[index * 2]);
        uint8_t low = hex_value(hex[index * 2 + 1]);
        if (high == 0xFF || low == 0xFF) return false;
        output[index] = (uint8_t)((high << 4) | low);
    }
    return true;
}

static uint32_t template_crc32(const uint8_t *data, size_t length) {
    uint32_t crc = 0xFFFFFFFFU;
    for (size_t index = 0; index < length; ++index) {
        crc ^= data[index];
        for (int bit = 0; bit < 8; ++bit) {
            crc = (crc & 1U) ? (crc >> 1) ^ 0xEDB88320U : crc >> 1;
        }
    }
    return crc ^ 0xFFFFFFFFU;
}

static bool permission_expired(const cab_permission_t *permission) {
    if (permission->expire_days == UINT32_MAX) return false;
    uint32_t timestamp = cab_storage_unix_time();
    return timestamp >= UNIX_2000_01_01 &&
           (timestamp - UNIX_2000_01_01) / 86400U > permission->expire_days;
}

static void send_event(uint16_t command, const char *json) {
    send_async_json(command, cab_next_message_id(), 0, json,
                    s_mesh_connected);
}

static void send_log(const char *user_id, int fingerprint_id, int lock_id,
                     const char *result, const char *reason) {
    char json[384];
    snprintf(json, sizeof(json),
        "{\"logs\":[{\"user_id\":\"%s\",\"fingerprint_id\":%d,"
        "\"lock_id\":%d,\"action\":\"open\",\"result\":\"%s\","
        "\"reason\":\"%s\",\"timestamp\":%lu}]}",
        user_id == NULL ? "" : user_id, fingerprint_id, lock_id,
        result, reason, (unsigned long)cab_storage_unix_time());
    send_event(CAB_CMD_LOG_REPORT, json);
}

static void send_verify_event(const char *event, int lock_id) {
    char json[256];
    snprintf(json, sizeof(json),
        "{\"event\":\"%s\",\"user_id\":\"%s\","
        "\"fingerprint_id\":%d%s%s}",
        event, s_verified_valid ? s_verified_permission.user_id : "",
        s_verified_valid ? s_verified_permission.local_fingerprint_id : -1,
        lock_id >= 0 ? ",\"lock_id\":" : "",
        lock_id >= 0 ? "0" : "");
    if (lock_id >= 0) {
        snprintf(json, sizeof(json),
            "{\"event\":\"%s\",\"user_id\":\"%s\","
            "\"fingerprint_id\":%d,\"lock_id\":%d}",
            event, s_verified_valid ? s_verified_permission.user_id : "",
            s_verified_valid ? s_verified_permission.local_fingerprint_id : -1,
            lock_id);
    }
    send_event(CAB_CMD_VERIFY_WINDOW_EVENT, json);
}

static void end_verified_window(const char *event) {
    if (event != NULL) send_verify_event(event, -1);
    s_verified_valid = false;
    cab_lock_clear_permission_hint();
    set_state(STATE_WAIT_FINGER);
}

static void update_config_fingerprint_count(void) {
    cab_device_config_t config;
    if (!cab_storage_load_config(&config)) return;
    config.fingerprint_count = (uint8_t)cab_fp_template_count();
    config.permission_version = cab_storage_permission_version();
    cab_storage_save_config(&config);
}

static void handle_register(const cab_app_view_t *request, bool ingress) {
    cab_device_config_t config;
    cab_storage_load_config(&config);
    char json[768];
    cab_mesh_stats_t stats = cab_mesh_stats();
    snprintf(json, sizeof(json),
        "{\"device_id\":\"%s\",\"device_name\":\"%s\","
        "\"is_root\":false,\"firmware_version\":\"%s\","
        "\"hardware_version\":\"%s\","
        "\"mesh_layer\":%d,\"mesh_node_type\":2,\"child_count\":%d,"
        "\"free_heap\":%lu,\"mesh_send_failures\":%lu,"
        "\"mesh_queue_full\":%lu,\"mesh_recoveries\":%lu,"
        "\"mesh_root_responses\":%lu,\"mesh_heartbeat_acks\":%lu,"
        "\"mesh_heartbeat_timeouts\":%lu,"
        "\"fingerprint_ready\":%s,\"fingerprint_error\":\"%s\","
        "\"fingerprint_error_count\":%lu}",
        s_device_id, config.device_name, esp_app_get_description()->version,
        CABINET_HARDWARE_VERSION,
        cab_mesh_layer(),
        cab_mesh_child_count(), (unsigned long)esp_get_free_heap_size(),
        (unsigned long)stats.send_failures,
        (unsigned long)stats.receive_drops,
        (unsigned long)stats.reconnects,
        (unsigned long)stats.root_responses,
        (unsigned long)stats.heartbeat_acks,
        (unsigned long)stats.heartbeat_timeouts,
        cab_fp_ready() ? "true" : "false", cab_fp_last_error(),
        (unsigned long)cab_fp_error_count());
    send_json(CAB_CMD_REGISTER, request->message_id,
              request->correlation_id, json, ingress, true);
}

static int build_status_payload(uint8_t payload[24]) {
    cab_mesh_stats_t stats = cab_mesh_stats();
    cab_device_config_t config;
    cab_storage_load_config(&config);
    uint8_t flags = 0x02;
    if (cab_storage_time_is_synced()) flags |= 0x01;
    if (cab_fp_ready()) flags |= 0x04;
    return cab_pack_status(
        payload, 24, now_ms() / 1000, cab_lock_active_mask(),
        (uint8_t)cab_mesh_layer(), flags, config.fingerprint_count,
        (uint16_t)cab_storage_permission_count(),
        cab_storage_permission_version(), (uint16_t)stats.send_failures,
        (uint16_t)stats.receive_drops, (int8_t)cab_mesh_link_rssi(), 120,
        (uint16_t)cab_fp_poll_max_ms());
}

static void handle_read_status(const cab_app_view_t *request, bool ingress) {
    uint8_t payload[24];
    int length = build_status_payload(payload);
    if (length <= 0) {
        send_error(request, CAB_ERR_INTERNAL, "status encode failed", ingress);
        return;
    }
    send_payload(CAB_CMD_STATUS_RESPONSE, request->message_id,
                 request->correlation_id, 0, payload, (uint16_t)length,
                 ingress, true);
}

static void handle_read_config(const cab_app_view_t *request, bool ingress) {
    cab_device_config_t config;
    cab_storage_load_config(&config);
    char json[512];
    snprintf(json, sizeof(json),
        "{\"device_id\":\"%s\",\"device_name\":\"%s\","
        "\"is_root\":false,\"work_mode\":\"mesh\","
        "\"uplink_mode\":0,\"mesh_channel\":6,"
        "\"fingerprint_count\":%u,\"perm_version\":%lu,"
        "\"fingerprint_ready\":%s,\"fingerprint_error\":\"%s\","
        "\"fingerprint_error_count\":%lu,\"fingerprint_power\":%s,"
        "\"fingerprint_power_off_level\":%d,"
        "\"fingerprint_power_on_level\":%d,"
        "\"fingerprint_handshake\":%s,\"fingerprint_probe_result\":%d,"
        "\"firmware_version\":\"%s\",\"hardware_version\":\"%s\"}",
        s_device_id, config.device_name, config.fingerprint_count,
        (unsigned long)cab_storage_permission_version(),
        cab_fp_ready() ? "true" : "false", cab_fp_last_error(),
        (unsigned long)cab_fp_error_count(),
        cab_fp_power_detected() ? "true" : "false",
        cab_fp_power_off_feedback_level(),
        cab_fp_power_on_feedback_level(),
        cab_fp_handshake_seen() ? "true" : "false",
        cab_fp_probe_result(), esp_app_get_description()->version,
        CABINET_HARDWARE_VERSION);
    send_json(CAB_CMD_CONFIG_RESPONSE, request->message_id,
              request->correlation_id, json, ingress, true);
}

static void handle_write_config(const cab_app_view_t *request, cJSON *json,
                                bool ingress) {
    cab_device_config_t config;
    cab_storage_load_config(&config);
    const char *name = json_string(json, "device_name", NULL);
    if (name != NULL) snprintf(config.device_name, sizeof(config.device_name),
                               "%s", name);
    // Channel and work mode stay fixed; changing either would split the mesh.
    config.work_mode = 0;
    config.mesh_channel = 6;
    if (!cab_storage_save_config(&config)) {
        send_error(request, CAB_ERR_FLASH_WRITE, "config save failed", ingress);
        return;
    }
    send_json(CAB_CMD_CONFIG_SAVED, request->message_id,
              request->correlation_id, "{\"result\":\"success\"}",
              ingress, true);
}

static void handle_control_lock(const cab_app_view_t *request, bool ingress) {
    if (request->payload_len < 2 || request->payload[0] >= CAB_LOCK_COUNT) {
        send_error(request, CAB_ERR_LOCK_ID_RANGE, "lock id out of range",
                   ingress);
        return;
    }
    uint8_t lock_id = request->payload[0];
    if (request->payload[1] == 1) {
        cab_lock_close(lock_id);
        send_ack(request, "close", ingress);
    } else if (cab_lock_open(lock_id)) {
        send_ack(request, "open", ingress);
        send_log("remote", -1, lock_id, "success", "remote_control");
    } else {
        send_error(request, CAB_ERR_LOCK_HARDWARE, "lock open failed", ingress);
    }
}

static void reset_permission_sync(void) {
    s_sync_active = false;
    s_sync_expected = 0;
    s_sync_received = 0;
    s_sync_version = 0;
    s_sync_started = 0;
    memset(s_staged_received, 0, sizeof(s_staged_received));
}

static void handle_sync_permissions(const cab_app_view_t *request, cJSON *json,
                                    bool ingress) {
    const cJSON *users = cJSON_GetObjectItemCaseSensitive(json, "users");
    if (!cJSON_IsArray(users) ||
        cJSON_GetArraySize(users) > CAB_STORAGE_PERMISSION_MAX) {
        send_error(request, CAB_ERR_BAD_REQUEST, "invalid permission list",
                   ingress);
        return;
    }
    size_t count = 0;
    const cJSON *row = NULL;
    cJSON_ArrayForEach(row, users) {
        if (!cJSON_IsObject(row) || !parse_permission(row, &s_staged[count])) {
            send_error(request, CAB_ERR_BAD_REQUEST,
                       "invalid permission record", ingress);
            return;
        }
        ++count;
    }
    uint32_t version = json_u32(json, "version", 0);
    bool ok = cab_storage_replace_permissions(s_staged, count, version);
    char response[128];
    snprintf(response, sizeof(response),
             "{\"count\":%u,\"version\":%lu,\"result\":\"%s\"}",
             (unsigned)count, (unsigned long)version,
             ok ? "success" : "fail");
    send_json(CAB_CMD_SYNC_ACK, request->message_id,
              request->correlation_id, response, ingress, true);
}

static void handle_begin_permission_sync(const cab_app_view_t *request,
                                         cJSON *json, bool ingress) {
    int total = json_int(json, "total", -1);
    if (total < 0 || total > CAB_STORAGE_PERMISSION_MAX) {
        send_error(request, CAB_ERR_BAD_REQUEST,
                   "invalid permission sync total", ingress);
        return;
    }
    reset_permission_sync();
    s_sync_active = true;
    s_sync_expected = (size_t)total;
    s_sync_version = json_u32(json, "version", 0);
    s_sync_started = now_ms();
    send_ack(request, "permission_sync_started", ingress);
}

static void handle_sync_permission(const cab_app_view_t *request, cJSON *json,
                                   bool ingress) {
    cab_permission_t permission;
    if (!parse_permission(json, &permission)) {
        send_error(request, CAB_ERR_BAD_REQUEST, "invalid permission record",
                   ingress);
        return;
    }
    uint32_t version = json_u32(json, "version", 0);
    if (!s_sync_active) {
        if (cab_storage_save_permission(&permission, version))
            send_ack(request, "permission_synced", ingress);
        else
            send_error(request, CAB_ERR_FLASH_WRITE,
                       "permission save failed", ingress);
        return;
    }
    int sequence = json_int(json, "sequence", -1);
    int total = json_int(json, "total", -1);
    if (version != s_sync_version || total != (int)s_sync_expected ||
        sequence < 0 || sequence >= (int)s_sync_expected) {
        send_error(request, CAB_ERR_BAD_REQUEST,
                   "permission sync sequence mismatch", ingress);
        return;
    }
    s_staged[sequence] = permission;
    if (!s_staged_received[sequence]) {
        s_staged_received[sequence] = true;
        ++s_sync_received;
    }
    s_sync_started = now_ms();
    send_ack(request, "permission_staged", ingress);
}

static void handle_commit_permission_sync(const cab_app_view_t *request,
                                          cJSON *json, bool ingress) {
    uint32_t version = json_u32(json, "version", 0);
    int total = json_int(json, "total", -1);
    if (!s_sync_active || version != s_sync_version ||
        total != (int)s_sync_expected || s_sync_received != s_sync_expected) {
        reset_permission_sync();
        send_error(request, CAB_ERR_BAD_REQUEST,
                   "permission sync incomplete", ingress);
        return;
    }
    size_t count = s_sync_expected;
    bool ok = cab_storage_replace_permissions(s_staged, count, version);
    reset_permission_sync();
    char response[128];
    snprintf(response, sizeof(response),
             "{\"count\":%u,\"version\":%lu,\"result\":\"%s\"}",
             (unsigned)count, (unsigned long)version,
             ok ? "success" : "fail");
    send_json(CAB_CMD_SYNC_ACK, request->message_id,
              request->correlation_id, response, ingress, true);
}

static const cab_permission_t *find_permission(const char *user_id,
                                               int fingerprint_id) {
    uint32_t user_number = user_id == NULL || user_id[0] == '\0'
        ? 0 : cab_storage_user_id_to_number(user_id);
    for (size_t index = 0; index < cab_storage_permission_count(); ++index) {
        const cab_permission_t *permission = cab_storage_permission_at(index);
        if (permission == NULL) continue;
        if (fingerprint_id >= 0 &&
            permission->fingerprint_id != fingerprint_id &&
            permission->local_fingerprint_id != fingerprint_id) continue;
        if (user_number != 0 && permission->user_id_number != user_number)
            continue;
        return permission;
    }
    return NULL;
}

static void handle_read_permissions(const cab_app_view_t *request, cJSON *json,
                                    bool ingress) {
    const char *user_id = json_string(json, "user_id", "");
    int fingerprint_id = json_int(json, "fingerprint_id", -1);
    const cab_permission_t *permission = find_permission(user_id,
                                                         fingerprint_id);
    char response[512];
    snprintf(response, sizeof(response),
        "{\"count\":%u,\"version\":%lu,\"user_id\":\"%s\","
        "\"found\":%s,\"fingerprint_id\":%d,\"role\":%d,"
        "\"lock_0\":%s,\"lock_1\":%s,\"lock_2\":%s,\"lock_3\":%s}",
        (unsigned)cab_storage_permission_count(),
        (unsigned long)cab_storage_permission_version(), user_id,
        permission != NULL ? "true" : "false",
        permission != NULL ? permission->fingerprint_id : -1,
        permission != NULL ? (int)permission->role : CAB_ROLE_STUDENT,
        permission != NULL && (permission->lock_mask & 1) ? "true" : "false",
        permission != NULL && (permission->lock_mask & 2) ? "true" : "false",
        permission != NULL && (permission->lock_mask & 4) ? "true" : "false",
        permission != NULL && (permission->lock_mask & 8) ? "true" : "false");
    send_json(CAB_CMD_PERMISSIONS_RESPONSE, request->message_id,
              request->correlation_id, response, ingress, true);
}

static void handle_delete_user(const cab_app_view_t *request, cJSON *json,
                               bool ingress) {
    const char *user_id = json_string(json, "user_id", "");
    if (user_id[0] == '\0' || s_sync_active) {
        send_error(request, CAB_ERR_BAD_REQUEST,
                   user_id[0] == '\0' ? "missing user_id" :
                                        "permission sync active", ingress);
        return;
    }
    if (cab_storage_delete_user(user_id, json_u32(json, "version", 0)))
        send_ack(request, "user_permission_deleted", ingress);
    else
        send_error(request, CAB_ERR_FLASH_WRITE,
                   "user permission delete failed", ingress);
}

static void handle_clear_permissions(const cab_app_view_t *request,
                                     cJSON *json, bool ingress) {
    bool ok = cab_storage_replace_permissions(NULL, 0,
                                               json_u32(json, "version", 0));
    if (ok) send_ack(request, "permissions_cleared", ingress);
    else send_error(request, CAB_ERR_FLASH_WRITE,
                    "permissions clear failed", ingress);
}

static void send_enroll_progress(void) {
    char json[320];
    snprintf(json, sizeof(json),
        "{\"phase\":\"%s\",\"step\":%d,\"total\":6,"
        "\"hint\":\"%s\",\"fingerprint_id\":%d,\"is_backup\":%s}",
        cab_fp_enroll_phase_code(), cab_fp_enroll_step(),
        cab_fp_enroll_hint(), s_enroll_backup ? s_enroll_target : 0,
        s_enroll_backup ? "true" : "false");
    send_async_json(CAB_CMD_ENROLL_PROGRESS, s_enroll_message_id,
                    s_enroll_correlation_id, json, s_enroll_ingress);
}

static void start_enrollment(const cab_app_view_t *request, int target,
                             const char *user_id, bool backup, bool ingress) {
    s_enroll_target = target;
    snprintf(s_enroll_user, sizeof(s_enroll_user), "%s",
             user_id == NULL ? "" : user_id);
    s_enroll_message_id = request->message_id;
    s_enroll_correlation_id = request->correlation_id;
    s_enroll_ingress = ingress;
    s_enroll_backup = backup;
    cab_fp_enroll_begin(backup ? target : CAB_FP_TEMP_SLOT);
    s_last_enroll_phase = cab_fp_enroll_phase();
    set_state(STATE_ENROLLING);
    send_enroll_progress();
}

static void handle_add_fingerprint(const cab_app_view_t *request, cJSON *json,
                                   bool ingress) {
    int target = json_int(json, "fingerprint_id", 0);
    if (!cab_fp_ready()) {
        send_error(request, CAB_ERR_FP_COMM_FAILED,
                   cab_fp_last_error(), ingress);
    } else if (target <= CAB_FP_TEMP_SLOT || target >= CAB_FP_MAX_SLOTS) {
        send_error(request, CAB_ERR_FP_TEMPLATE_FORMAT,
                   "invalid target fingerprint id", ingress);
    } else if (s_state != STATE_WAIT_FINGER) {
        send_error(request, CAB_ERR_INTERNAL, "device busy", ingress);
    } else {
        if (cab_fp_template_exists(CAB_FP_TEMP_SLOT))
            cab_fp_delete(CAB_FP_TEMP_SLOT);
        send_ack(request, "enrolling", ingress);
        start_enrollment(request, target, json_string(json, "user_id", ""),
                         false, ingress);
    }
}

static void handle_add_backup(const cab_app_view_t *request, cJSON *json,
                              bool ingress) {
    const char *user_id = json_string(json, "user_id", "");
    if (user_id[0] == '\0') {
        send_error(request, CAB_ERR_BAD_REQUEST, "missing user_id", ingress);
        return;
    }
    if (!cab_fp_ready()) {
        send_error(request, CAB_ERR_FP_COMM_FAILED,
                   cab_fp_last_error(), ingress);
        return;
    }
    if (s_state != STATE_WAIT_FINGER) {
        send_error(request, CAB_ERR_INTERNAL, "device busy", ingress);
        return;
    }
    uint32_t user_number = cab_storage_user_id_to_number(user_id);
    for (size_t index = 0; index < cab_storage_permission_count(); ++index) {
        const cab_permission_t *permission = cab_storage_permission_at(index);
        if (permission != NULL && permission->is_backup &&
            permission->user_id_number == user_number) {
            send_error(request, CAB_ERR_FP_BACKUP_EXISTS,
                       "backup fingerprint already exists", ingress);
            return;
        }
    }
    int target = cab_storage_allocate_fingerprint_id();
    if (target < 1) {
        send_error(request, CAB_ERR_FP_BACKUP_LIMIT,
                   "no free fingerprint slot", ingress);
        return;
    }
    if (cab_fp_template_exists(target)) cab_fp_delete(target);
    send_ack(request, "enrolling_backup", ingress);
    start_enrollment(request, target, user_id, true, ingress);
}

static void handle_restore_fingerprint(const cab_app_view_t *request,
                                       cJSON *json, bool ingress) {
    int fingerprint_id = json_int(json, "fingerprint_id", -1);
    bool replace = json_bool(json, "replace", true);
    if (fingerprint_id < 0 || fingerprint_id >= CAB_FP_MAX_SLOTS) {
        send_error(request, CAB_ERR_FP_TEMPLATE_FORMAT,
                   "invalid fingerprint id", ingress);
        return;
    }
    if (s_state != STATE_WAIT_FINGER) {
        send_error(request, CAB_ERR_INTERNAL, "device busy", ingress);
        return;
    }
    if (!replace && cab_fp_template_exists(fingerprint_id)) {
        send_error(request, CAB_ERR_FP_ID_EXISTS,
                   "fingerprint id already exists", ingress);
        return;
    }
    uint8_t template_data[CAB_FP_TEMPLATE_SIZE];
    if (!decode_template_hex(json_string(json, "template_hex", ""),
                             template_data)) {
        send_error(request, CAB_ERR_FP_TEMPLATE_FORMAT,
                   "invalid template hex", ingress);
        return;
    }
    if (!cab_fp_write_template(fingerprint_id, template_data,
                               sizeof(template_data))) {
        send_error(request, CAB_ERR_FP_COMM_FAILED,
                   "fingerprint restore failed", ingress);
        return;
    }
    update_config_fingerprint_count();
    char response[192];
    snprintf(response, sizeof(response),
        "{\"fingerprint_id\":%d,\"user_id\":\"%s\","
        "\"result\":\"success\"}", fingerprint_id,
        json_string(json, "user_id", ""));
    send_json(CAB_CMD_RESTORE_FINGERPRINT_RESULT, request->message_id,
              request->correlation_id, response, ingress, true);
    send_ack(request, "success", ingress);
}

static void handle_delete_fingerprint(const cab_app_view_t *request,
                                      cJSON *json, bool ingress) {
    int fingerprint_id = json_int(json, "fingerprint_id", -1);
    if (fingerprint_id < 0 || fingerprint_id >= CAB_FP_MAX_SLOTS) {
        send_error(request, CAB_ERR_BAD_REQUEST,
                   "invalid fingerprint id", ingress);
        return;
    }
    bool ok = !cab_fp_template_exists(fingerprint_id) ||
              cab_fp_delete(fingerprint_id);
    cab_storage_delete_permission(fingerprint_id);
    if (!ok) {
        send_error(request, CAB_ERR_FP_COMM_FAILED,
                   "delete fingerprint failed", ingress);
        return;
    }
    update_config_fingerprint_count();
    send_ack(request, "fingerprint_deleted", ingress);
}

static void handle_delete_all(const cab_app_view_t *request, bool ingress) {
    bool ok = cab_fp_delete_all();
    cab_storage_clear_permissions();
    update_config_fingerprint_count();
    if (ok) send_ack(request, "fingerprints_deleted", ingress);
    else send_error(request, CAB_ERR_FP_COMM_FAILED,
                    "delete all fingerprints failed", ingress);
}

static void send_test_event(const char *event, int confidence) {
    char json[320];
    snprintf(json, sizeof(json),
        "{\"event\":\"%s\",\"test_token\":\"%s\","
        "\"fingerprint_id\":%d,\"confidence\":%d,"
        "\"idle_timeout_seconds\":60}",
        event, s_test_token, s_test_source_id, confidence);
    send_event(CAB_CMD_FINGERPRINT_TEST_EVENT, json);
}

static void finish_test(const char *event) {
    if (event != NULL) send_test_event(event, 0);
    cab_fp_delete(CAB_FP_TEMP_SLOT);
    s_test_token[0] = '\0';
    s_test_source_id = -1;
    s_test_finger_down = false;
    set_state(STATE_WAIT_FINGER);
}

static void handle_start_test(const cab_app_view_t *request, cJSON *json,
                              bool ingress) {
    if (s_state != STATE_WAIT_FINGER) {
        send_error(request, CAB_ERR_INTERNAL, "device busy", ingress);
        return;
    }
    uint8_t template_data[CAB_FP_TEMPLATE_SIZE];
    if (!decode_template_hex(json_string(json, "template_hex", ""),
                             template_data)) {
        send_error(request, CAB_ERR_FP_TEMPLATE_FORMAT,
                   "invalid fingerprint test template", ingress);
        return;
    }
    if (cab_fp_template_exists(CAB_FP_TEMP_SLOT))
        cab_fp_delete(CAB_FP_TEMP_SLOT);
    if (!cab_fp_write_template(CAB_FP_TEMP_SLOT, template_data,
                               sizeof(template_data))) {
        send_error(request, CAB_ERR_FP_COMM_FAILED,
                   "fingerprint test template write failed", ingress);
        return;
    }
    snprintf(s_test_token, sizeof(s_test_token), "%s",
             json_string(json, "test_token", ""));
    s_test_source_id = json_int(json, "fingerprint_id", -1);
    s_test_last_activity = now_ms();
    s_test_last_poll = 0;
    s_test_finger_down = false;
    set_state(STATE_FINGERPRINT_TEST);
    send_ack(request, "fingerprint_test_started", ingress);
    send_test_event("started", 0);
}

static void handle_stop_test(const cab_app_view_t *request, cJSON *json,
                             bool ingress) {
    const char *token = json_string(json, "test_token", "");
    if (s_state == STATE_FINGERPRINT_TEST && token[0] != '\0' &&
        strcmp(token, s_test_token) != 0) {
        send_error(request, CAB_ERR_BAD_REQUEST,
                   "fingerprint test token mismatch", ingress);
        return;
    }
    if (s_state == STATE_FINGERPRINT_TEST) finish_test("stopped");
    else if (cab_fp_template_exists(CAB_FP_TEMP_SLOT))
        cab_fp_delete(CAB_FP_TEMP_SLOT);
    send_ack(request, "fingerprint_test_stopped", ingress);
}

static void handle_check_fingerprint(const cab_app_view_t *request,
                                     cJSON *json, bool ingress) {
    int fingerprint_id = json_int(json, "fingerprint_id", -1);
    uint32_t expected = json_u32(json, "expected_crc32", 0);
    if (fingerprint_id <= 0 || fingerprint_id >= CAB_FP_MAX_SLOTS) {
        send_error(request, CAB_ERR_BAD_REQUEST,
                   "invalid fingerprint id", ingress);
        return;
    }
    bool exists = cab_fp_template_exists(fingerprint_id);
    bool readable = false;
    uint32_t actual = 0;
    if (exists) {
        uint8_t template_data[CAB_FP_TEMPLATE_SIZE];
        size_t length = 0;
        readable = cab_fp_read_template(fingerprint_id, template_data,
                                        sizeof(template_data), &length);
        if (readable) actual = template_crc32(template_data, length);
    }
    char response[256];
    snprintf(response, sizeof(response),
        "{\"fingerprint_id\":%d,\"exists\":%s,\"readable\":%s,"
        "\"matches\":%s,\"expected_crc32\":%lu,"
        "\"actual_crc32\":%lu}",
        fingerprint_id, exists ? "true" : "false",
        readable ? "true" : "false",
        exists && readable && expected != 0 && expected == actual
            ? "true" : "false",
        (unsigned long)expected, (unsigned long)actual);
    send_json(CAB_CMD_FINGERPRINT_CHECK_RESPONSE, request->message_id,
              request->correlation_id, response, ingress, true);
}

static void handle_fingerprint_list(const cab_app_view_t *request,
                                    cJSON *json, bool ingress) {
    int page = json_int(json, "page", 0);
    int page_size = json_int(json, "page_size", 20);
    if (page < 0 || page_size < 1 || page_size > 20) {
        send_error(request, CAB_ERR_BAD_REQUEST,
                   "invalid fingerprint list page", ingress);
        return;
    }

    uint16_t slots[CAB_FP_MAX_SLOTS];
    size_t count = 0;
    if (!cab_fp_list_slots(slots, CAB_FP_MAX_SLOTS, &count)) {
        send_error(request, CAB_ERR_FP_COMM_FAILED,
                   "fingerprint slot list failed", ingress);
        return;
    }

    size_t start = (size_t)page * (size_t)page_size;
    size_t end = start + (size_t)page_size;
    if (end > count) end = count;

    cJSON *root = cJSON_CreateObject();
    cJSON_AddNumberToObject(root, "page", page);
    cJSON_AddNumberToObject(root, "page_size", page_size);
    cJSON_AddNumberToObject(root, "total", (double)count);
    cJSON_AddNumberToObject(root, "capacity", CAB_FP_MAX_SLOTS);
    cJSON *items = cJSON_AddArrayToObject(root, "items");
    for (size_t index = start; index < end; ++index) {
        int slot = slots[index];
        cab_permission_t permission;
        bool bound = cab_storage_find_by_local_fingerprint(slot, &permission);
        cJSON *item = cJSON_CreateObject();
        cJSON_AddNumberToObject(item, "slot", slot);
        cJSON_AddBoolToObject(item, "bound", bound);
        cJSON_AddNumberToObject(item, "fingerprint_id",
                                bound ? permission.fingerprint_id : -1);
        cJSON_AddStringToObject(item, "user_id",
                                bound ? permission.user_id : "");
        cJSON_AddStringToObject(item, "name", bound ? permission.name : "");
        cJSON_AddNumberToObject(item, "role",
                                bound ? permission.role : CAB_ROLE_STUDENT);
        cJSON_AddNumberToObject(item, "lock_mask",
                                bound ? permission.lock_mask : 0);
        cJSON_AddBoolToObject(item, "is_backup",
                              bound && permission.is_backup);
        cJSON_AddItemToArray(items, item);
    }
    char *response = cJSON_PrintUnformatted(root);
    if (response == NULL) {
        cJSON_Delete(root);
        send_error(request, CAB_ERR_INTERNAL,
                   "fingerprint slot list encode failed", ingress);
        return;
    }
    send_json(CAB_CMD_FINGERPRINT_LIST_RESPONSE, request->message_id,
              request->correlation_id, response, ingress, true);
    cJSON_free(response);
    cJSON_Delete(root);
    send_ack(request, "fingerprint_list_sent", ingress);
}

static void handle_backup_list(const cab_app_view_t *request, bool ingress) {
    cJSON *root = cJSON_CreateObject();
    cJSON *array = cJSON_AddArrayToObject(root, "backups");
    int count = 0;
    for (size_t index = 0; index < cab_storage_permission_count(); ++index) {
        const cab_permission_t *permission = cab_storage_permission_at(index);
        if (permission == NULL || !permission->is_backup) continue;
        cJSON *item = cJSON_CreateObject();
        cJSON_AddStringToObject(item, "user_id", permission->user_id);
        cJSON_AddNumberToObject(item, "user_id_num",
                                permission->user_id_number);
        cJSON_AddNumberToObject(item, "local_fp_id",
                                permission->local_fingerprint_id);
        cJSON_AddNumberToObject(item, "role", permission->role);
        cJSON *locks = cJSON_AddObjectToObject(item, "lock_permissions");
        for (int lock = 0; lock < CAB_LOCK_COUNT; ++lock) {
            char key[8];
            snprintf(key, sizeof(key), "lock_%d", lock);
            cJSON_AddBoolToObject(locks, key,
                                  (permission->lock_mask & (1U << lock)) != 0);
        }
        cJSON_AddItemToArray(array, item);
        ++count;
    }
    cJSON_AddNumberToObject(root, "count", count);
    char *text = cJSON_PrintUnformatted(root);
    if (text != NULL) {
        send_json(CAB_CMD_BACKUP_FP_LIST, request->message_id,
                  request->correlation_id, text, ingress, true);
        cJSON_free(text);
    }
    cJSON_Delete(root);
    send_ack(request, "backup_list_sent", ingress);
}

static void handle_delete_backup(const cab_app_view_t *request, cJSON *json,
                                 bool ingress) {
    const char *user_id = json_string(json, "user_id", "");
    if (user_id[0] == '\0') {
        send_error(request, CAB_ERR_BAD_REQUEST, "missing user_id", ingress);
        return;
    }
    uint32_t user_number = cab_storage_user_id_to_number(user_id);
    int local_id = -1;
    for (size_t index = 0; index < cab_storage_permission_count(); ++index) {
        const cab_permission_t *permission = cab_storage_permission_at(index);
        if (permission != NULL && permission->is_backup &&
            permission->user_id_number == user_number) {
            local_id = permission->local_fingerprint_id;
            break;
        }
    }
    if (local_id < 0) {
        send_error(request, CAB_ERR_FP_BACKUP_NOT_FOUND,
                   "backup fingerprint not found", ingress);
        return;
    }
    bool sensor_ok = !cab_fp_template_exists(local_id) || cab_fp_delete(local_id);
    bool storage_ok = cab_storage_delete_permission(local_id);
    if (!storage_ok) {
        send_error(request, CAB_ERR_FLASH_WRITE,
                   "backup permission delete failed", ingress);
        return;
    }
    update_config_fingerprint_count();
    char response[256];
    snprintf(response, sizeof(response),
        "{\"user_id\":\"%s\",\"local_fp_id\":%d,"
        "\"as608_deleted\":%s,\"storage_deleted\":true,"
        "\"result\":\"success\"}", user_id, local_id,
        sensor_ok ? "true" : "false");
    send_json(CAB_CMD_DELETE_BACKUP_FINGERPRINT, request->message_id,
              request->correlation_id, response, ingress, true);
    send_ack(request, "backup_deleted", ingress);
}

static void cancel_current_operation(void) {
    if (s_state == STATE_VERIFIED_WINDOW) {
        end_verified_window("cancel");
    } else if (s_state == STATE_ENROLLING) {
        cab_fp_enroll_abort("user_cancelled");
        int slot = s_enroll_backup ? s_enroll_target : CAB_FP_TEMP_SLOT;
        if (cab_fp_template_exists(slot)) cab_fp_delete(slot);
        char response[256];
        snprintf(response, sizeof(response),
            "{\"fingerprint_id\":%d,\"user_id\":\"%s\","
            "\"is_backup\":%s,\"result\":\"fail\","
            "\"message\":\"user_cancelled\"}",
            s_enroll_target, s_enroll_user,
            s_enroll_backup ? "true" : "false");
        send_async_json(CAB_CMD_ADD_FINGERPRINT_RESULT,
                        s_enroll_message_id, s_enroll_correlation_id,
                        response, s_enroll_ingress);
        set_state(STATE_WAIT_FINGER);
    } else if (s_state == STATE_FINGERPRINT_TEST) {
        finish_test("cancelled");
    }
}

static void finish_enrollment(bool success) {
    uint8_t template_data[CAB_FP_TEMPLATE_SIZE];
    size_t template_length = 0;
    char *response = heap_caps_malloc(1400, MALLOC_CAP_SPIRAM |
                                           MALLOC_CAP_8BIT);
    if (response == NULL) response = malloc(1400);
    if (response == NULL) {
        set_state(STATE_WAIT_FINGER);
        return;
    }
    char template_hex[CAB_FP_TEMPLATE_SIZE * 2 + 1];
    template_hex[0] = '\0';
    int template_slot = s_enroll_backup ? s_enroll_target : CAB_FP_TEMP_SLOT;
    if (success && cab_fp_read_template(template_slot, template_data,
                                        sizeof(template_data),
                                        &template_length)) {
        static const char digits[] = "0123456789ABCDEF";
        for (size_t index = 0; index < template_length; ++index) {
            template_hex[index * 2] = digits[template_data[index] >> 4];
            template_hex[index * 2 + 1] = digits[template_data[index] & 0x0F];
        }
        template_hex[template_length * 2] = '\0';
    } else if (success) {
        success = false;
    }
    if (!s_enroll_backup && cab_fp_template_exists(CAB_FP_TEMP_SLOT))
        cab_fp_delete(CAB_FP_TEMP_SLOT);
    if (success && s_enroll_backup) {
        cab_permission_t backup;
        if (!cab_storage_find_primary_by_user(s_enroll_user, &backup)) {
            memset(&backup, 0, sizeof(backup));
            backup.role = CAB_ROLE_STUDENT;
            backup.expire_days = UINT32_MAX;
            backup.user_id_number = cab_storage_user_id_to_number(s_enroll_user);
            snprintf(backup.user_id, sizeof(backup.user_id), "%s",
                     s_enroll_user);
        }
        backup.fingerprint_id = s_enroll_target;
        backup.local_fingerprint_id = s_enroll_target;
        backup.is_backup = true;
        success = cab_storage_save_permission(&backup, 0);
    }
    snprintf(response, 1400,
        "{\"fingerprint_id\":%d,\"local_fp_id\":%d,"
        "\"user_id\":\"%s\",\"is_backup\":%s,"
        "\"result\":\"%s\"%s%s%s%s}",
        s_enroll_target, s_enroll_backup ? s_enroll_target : CAB_FP_TEMP_SLOT,
        s_enroll_user, s_enroll_backup ? "true" : "false",
        success ? "success" : "fail",
        template_hex[0] != '\0' ? ",\"template_hex\":\"" : "",
        template_hex[0] != '\0' ? template_hex : "",
        template_hex[0] != '\0' ? "\"" : "",
        success ? "" : ",\"message\":\"fingerprint enrollment failed\"");
    if (!success) {
        size_t used = strlen(response);
        if (used > 0 && response[used - 1] != '}')
            snprintf(response + used, 1400 - used, "}");
    }
    send_async_json(CAB_CMD_ADD_FINGERPRINT_RESULT, s_enroll_message_id,
                    s_enroll_correlation_id, response, s_enroll_ingress);
    free(response);
    update_config_fingerprint_count();
    set_state(STATE_WAIT_FINGER);
}

bool cab_controller_init(const char *device_id, cab_controller_tx_t transmit,
                         void *context) {
    if (device_id == NULL || transmit == NULL) return false;
    snprintf(s_device_id, sizeof(s_device_id), "%s", device_id);
    s_transmit = transmit;
    s_transmit_context = context;
    if (!cab_storage_init(device_id, false) || !cab_hardware_init()) {
        return false;
    }
    bool fingerprint_ready = cab_fp_init();
    s_fingerprint_was_ready = fingerprint_ready;
    if (fingerprint_ready) {
        if (cab_fp_template_exists(CAB_FP_TEMP_SLOT))
            cab_fp_delete(CAB_FP_TEMP_SLOT);
        update_config_fingerprint_count();
    }
    s_permission_lost_pending = cab_storage_permissions_lost();
    reset_permission_sync();
    set_state(STATE_WAIT_FINGER);
    return true;
}

void cab_controller_set_mesh_connected(bool connected) {
    s_mesh_connected = connected;
}

void cab_controller_handle(const cab_app_view_t *request, bool mesh_ingress) {
    if (request == NULL) return;
    if (replay_cached(request, mesh_ingress)) return;
    s_current_request = request;
    s_current_ingress = mesh_ingress;

    if (request->command == CAB_CMD_HEARTBEAT_ACK ||
        request->command == CAB_CMD_ACK ||
        request->command == CAB_CMD_LOG_REPORT_ACK) {
        s_current_request = NULL;
        return;
    }
    if (request->command == CAB_CMD_PERM_LOST_ACK) {
        s_permission_lost_pending = false;
        s_current_request = NULL;
        return;
    }
    if (request->command == CAB_CMD_REGISTER) {
        handle_register(request, mesh_ingress);
        s_current_request = NULL;
        return;
    }
    if (request->command == CAB_CMD_READ_STATUS) {
        handle_read_status(request, mesh_ingress);
        s_current_request = NULL;
        return;
    }
    if (request->command == CAB_CMD_READ_CONFIG) {
        handle_read_config(request, mesh_ingress);
        s_current_request = NULL;
        return;
    }
    if (request->command == CAB_CMD_CONTROL_LOCK) {
        handle_control_lock(request, mesh_ingress);
        s_current_request = NULL;
        return;
    }
    if (request->command == CAB_CMD_TIME_SYNC) {
        if (request->payload_len >= 4) {
            uint32_t timestamp = request->payload[0] |
                ((uint32_t)request->payload[1] << 8) |
                ((uint32_t)request->payload[2] << 16) |
                ((uint32_t)request->payload[3] << 24);
            if (timestamp != 0) {
                cab_storage_set_unix_time(timestamp);
                send_ack(request, "time_synced", mesh_ingress);
            } else {
                send_error(request, CAB_ERR_BAD_REQUEST,
                           "invalid timestamp", mesh_ingress);
            }
        } else {
            send_error(request, CAB_ERR_BAD_REQUEST,
                       "invalid timestamp", mesh_ingress);
        }
        s_current_request = NULL;
        return;
    }

    cJSON *json = parse_json_payload(request);
    if (json == NULL || !cJSON_IsObject(json)) {
        cJSON_Delete(json);
        send_error(request, CAB_ERR_JSON_PARSE, "json parse failed",
                   mesh_ingress);
        s_current_request = NULL;
        return;
    }
    switch (request->command) {
        case CAB_CMD_CABINET_OTA_NOTIFY: {
            const char *version = json_string(json, "version", "");
            uint32_t image_size = json_u32(json, "image_size", 0);
            if (mesh_ingress && version[0] != '\0' && image_size > 0) {
                /* Progress is reported by the next registration after reboot;
                   this internal notification deliberately has no ACK. */
                cabinet_ota_request(version, image_size);
            }
            break;
        }
        case CAB_CMD_WRITE_CONFIG:
            handle_write_config(request, json, mesh_ingress);
            break;
        case CAB_CMD_SYNC_PERMISSIONS:
            handle_sync_permissions(request, json, mesh_ingress);
            break;
        case CAB_CMD_BEGIN_PERMISSION_SYNC:
            handle_begin_permission_sync(request, json, mesh_ingress);
            break;
        case CAB_CMD_SYNC_PERMISSION:
            handle_sync_permission(request, json, mesh_ingress);
            break;
        case CAB_CMD_COMMIT_PERMISSION_SYNC:
            handle_commit_permission_sync(request, json, mesh_ingress);
            break;
        case CAB_CMD_CLEAR_PERMISSIONS:
            handle_clear_permissions(request, json, mesh_ingress);
            break;
        case CAB_CMD_DELETE_USER_PERMISSION:
            handle_delete_user(request, json, mesh_ingress);
            break;
        case CAB_CMD_READ_PERMISSIONS:
            handle_read_permissions(request, json, mesh_ingress);
            break;
        case CAB_CMD_ADD_FINGERPRINT:
            handle_add_fingerprint(request, json, mesh_ingress);
            break;
        case CAB_CMD_ADD_BACKUP_FINGERPRINT:
            handle_add_backup(request, json, mesh_ingress);
            break;
        case CAB_CMD_RESTORE_FINGERPRINT:
            handle_restore_fingerprint(request, json, mesh_ingress);
            break;
        case CAB_CMD_DELETE_FINGERPRINT:
            handle_delete_fingerprint(request, json, mesh_ingress);
            break;
        case CAB_CMD_DELETE_ALL_FINGERPRINTS:
            handle_delete_all(request, mesh_ingress);
            break;
        case CAB_CMD_START_FINGERPRINT_TEST:
            handle_start_test(request, json, mesh_ingress);
            break;
        case CAB_CMD_STOP_FINGERPRINT_TEST:
            handle_stop_test(request, json, mesh_ingress);
            break;
        case CAB_CMD_CHECK_FINGERPRINT:
            handle_check_fingerprint(request, json, mesh_ingress);
            break;
        case CAB_CMD_FINGERPRINT_LIST_REQUEST:
            handle_fingerprint_list(request, json, mesh_ingress);
            break;
        case CAB_CMD_BACKUP_FP_LIST_REQUEST:
            handle_backup_list(request, mesh_ingress);
            break;
        case CAB_CMD_DELETE_BACKUP_FINGERPRINT:
            handle_delete_backup(request, json, mesh_ingress);
            break;
        case CAB_CMD_CANCEL_ENROLL:
            cancel_current_operation();
            send_ack(request, "cancelled", mesh_ingress);
            break;
        case CAB_CMD_CLEAR_LOGS:
            send_ack(request, "logs_cleared", mesh_ingress);
            break;
        case CAB_CMD_REBOOT: {
            send_json(CAB_CMD_REBOOT_ACK, request->message_id,
                      request->correlation_id,
                      "{\"result\":\"rebooting\"}", mesh_ingress, false);
            send_ack(request, "rebooting", mesh_ingress);
            cJSON_Delete(json);
            s_current_request = NULL;
            vTaskDelay(pdMS_TO_TICKS(250));
            esp_restart();
            return;
        }
        case CAB_CMD_SD_QUERY:
        case CAB_CMD_SD_SAVE:
        case CAB_CMD_SD_QUERY_VERSION:
        case CAB_CMD_UPLOAD_FP_TEMPLATE:
        case CAB_CMD_DOWNLOAD_FP_TEMPLATE:
        case CAB_CMD_DELETE_FP_TEMPLATE:
            send_error(request, CAB_ERR_PERMISSION_DENIED,
                       "only root node has SD storage", mesh_ingress);
            break;
        default:
            send_error(request, CAB_ERR_UNKNOWN_COMMAND, "unknown command",
                       mesh_ingress);
            break;
    }
    cJSON_Delete(json);
    s_current_request = NULL;
}

static void update_wait_finger(void) {
    int fingerprint_id = -1;
    if (!cab_fp_take_background_result(&fingerprint_id)) return;
    if (fingerprint_id == CAB_FP_TEMP_SLOT ||
        !cab_storage_find_by_local_fingerprint(fingerprint_id,
                                               &s_verified_permission) ||
        permission_expired(&s_verified_permission) ||
        s_verified_permission.lock_mask == 0) {
        send_log("", fingerprint_id, -1, "fail",
                 "permission_not_synced");
        return;
    }
    s_verified_valid = true;
    cab_lock_set_permission_hint(s_verified_permission.lock_mask);
    set_state(STATE_VERIFIED_WINDOW);
    send_verify_event("enter", -1);
}

static void update_enrollment(void) {
    bool changed = cab_fp_enroll_tick();
    cab_fp_enroll_phase_t phase = cab_fp_enroll_phase();
    if (changed && phase != s_last_enroll_phase) {
        s_last_enroll_phase = phase;
        if (phase != CAB_FP_ENROLL_DONE_OK &&
            phase != CAB_FP_ENROLL_DONE_FAIL) send_enroll_progress();
    }
    if (phase == CAB_FP_ENROLL_DONE_OK) {
        finish_enrollment(true);
    } else if (phase == CAB_FP_ENROLL_DONE_FAIL) {
        int slot = s_enroll_backup ? s_enroll_target : CAB_FP_TEMP_SLOT;
        if (cab_fp_template_exists(slot)) cab_fp_delete(slot);
        finish_enrollment(false);
    }
}

static void update_test(void) {
    uint32_t now = now_ms();
    if (now - s_test_last_activity >= FP_TEST_IDLE_TIMEOUT_MS) {
        finish_test("timeout");
        return;
    }
    if (now - s_test_last_poll < FP_TEST_POLL_MS) return;
    s_test_last_poll = now;
    bool detected = false;
    int confidence = 0;
    int result = cab_fp_verify_slot(CAB_FP_TEMP_SLOT, &detected, &confidence);
    if (!detected) {
        s_test_finger_down = false;
        return;
    }
    s_test_last_activity = now;
    if (s_test_finger_down) return;
    s_test_finger_down = true;
    send_test_event(result == 1 ? "matched" :
                    (result == 0 ? "not_matched" : "read_error"),
                    confidence);
}

void cab_controller_update(void) {
    uint32_t now = now_ms();
    bool fingerprint_ready = cab_fp_ready();
    if (fingerprint_ready && !s_fingerprint_was_ready) {
        if (cab_fp_template_exists(CAB_FP_TEMP_SLOT))
            cab_fp_delete(CAB_FP_TEMP_SLOT);
        update_config_fingerprint_count();
        cab_fp_set_background_enabled(s_state == STATE_WAIT_FINGER);
    }
    s_fingerprint_was_ready = fingerprint_ready;
    cab_hardware_update();
    int key = cab_key_take_press();
    if (key == CAB_KEY_CANCEL) {
        cancel_current_operation();
    } else if (key >= 0 && key < CAB_LOCK_COUNT &&
               s_state == STATE_VERIFIED_WINDOW && s_verified_valid) {
        if ((s_verified_permission.lock_mask & (1U << key)) != 0) {
            cab_lock_open((uint8_t)key);
            send_log(s_verified_permission.user_id,
                     s_verified_permission.local_fingerprint_id,
                     key, "success", s_verified_permission.is_backup
                         ? "local_backup" : "local_cache");
            send_verify_event("unlocked", key);
        } else {
            send_log(s_verified_permission.user_id,
                     s_verified_permission.local_fingerprint_id,
                     key, "fail", "no_permission_in_window");
        }
    }
    int long_key;
    cab_key_take_long_press(&long_key);

    if (s_sync_active && now - s_sync_started >= PERMISSION_SYNC_TIMEOUT_MS)
        reset_permission_sync();
    if (s_permission_lost_pending && s_mesh_connected &&
        (s_last_permission_lost_report == 0 ||
         now - s_last_permission_lost_report >= 60000U)) {
        s_last_permission_lost_report = now;
        send_event(CAB_CMD_PERM_LOST, "{\"reason\":\"crc_failed\"}");
    }
    switch (s_state) {
        case STATE_WAIT_FINGER:
            update_wait_finger();
            break;
        case STATE_VERIFIED_WINDOW:
            if (now - s_state_entered >= VERIFY_WINDOW_MS) {
                send_log(s_verified_permission.user_id,
                         s_verified_permission.local_fingerprint_id,
                         -1, "fail", "window_timeout");
                end_verified_window("timeout");
            }
            break;
        case STATE_ENROLLING:
            update_enrollment();
            break;
        case STATE_FINGERPRINT_TEST:
            update_test();
            break;
    }
}

void cab_controller_report_status(bool mesh_ingress) {
    uint8_t payload[24];
    int length = build_status_payload(payload);
    if (length <= 0) return;
    send_payload(CAB_CMD_STATUS_REPORT, cab_next_message_id(), 0, 0,
                 payload, (uint16_t)length, mesh_ingress, false);
}
