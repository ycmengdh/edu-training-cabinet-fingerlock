#include "root_controller.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "cJSON.h"
#include "cabinet_mesh.h"
#include "cabinet_storage.h"
#include "esp_app_desc.h"
#include "esp_system.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "mbedtls/base64.h"
#include "root_display.h"
#include "root_ota.h"
#include "root_storage.h"

#define CACHE_COUNT 8
#define OUTPUT_MAX 1500
#define QUERY_PART_DATA_MAX 500
#define SNAPSHOT_DOWNLOAD_DATA_MAX 3000

typedef struct {
    bool valid;
    uint16_t command;
    uint16_t message_id;
    uint16_t correlation_id;
    uint16_t length;
    uint32_t stored_at;
    uint8_t data[OUTPUT_MAX];
} response_cache_t;

static char s_root_id[CAB_APP_ID_MAX + 1];
static root_controller_tx_t s_transmit;
static void *s_transmit_context;
static response_cache_t s_cache[CACHE_COUNT];
static uint8_t s_cache_next;
static const cab_app_view_t *s_current_request;

static uint32_t now_ms(void) {
    return (uint32_t)(xTaskGetTickCount() * portTICK_PERIOD_MS);
}

static const response_cache_t *find_cached(const cab_app_view_t *request) {
    for (size_t index = 0; index < CACHE_COUNT; ++index) {
        response_cache_t *entry = &s_cache[index];
        if (entry->valid && entry->command == request->command &&
            entry->message_id == request->message_id &&
            entry->correlation_id == request->correlation_id &&
            now_ms() - entry->stored_at <= 30000U) return entry;
    }
    return NULL;
}

static void cache_response(const uint8_t *data, size_t length) {
    if (s_current_request == NULL || length > OUTPUT_MAX) return;
    response_cache_t *entry = &s_cache[s_cache_next++ % CACHE_COUNT];
    entry->valid = true;
    entry->command = s_current_request->command;
    entry->message_id = s_current_request->message_id;
    entry->correlation_id = s_current_request->correlation_id;
    entry->length = (uint16_t)length;
    entry->stored_at = now_ms();
    memcpy(entry->data, data, length);
}

static void send_payload(uint16_t command, uint16_t message_id,
                         uint16_t correlation_id, uint8_t flags,
                         const uint8_t *payload, uint16_t payload_length,
                         bool cache) {
    uint8_t stack_output[OUTPUT_MAX];
    size_t required = 18 + strlen(s_root_id) * 2 + payload_length;
    size_t capacity = required <= sizeof(stack_output)
        ? sizeof(stack_output) : required;
    uint8_t *output = required <= sizeof(stack_output)
        ? stack_output : malloc(capacity);
    if (output == NULL) return;
    int length = cab_app_encode(output, capacity, command, message_id,
                                correlation_id, flags, s_root_id, s_root_id,
                                payload, payload_length,
                                cab_storage_unix_time());
    if (length > 0) {
        if (cache) cache_response(output, (size_t)length);
        s_transmit(output, (size_t)length, s_transmit_context);
    }
    if (output != stack_output) free(output);
}

static void send_json(uint16_t command, const cab_app_view_t *request,
                      const char *json, bool cache) {
    if (json == NULL) json = "{}";
    size_t length = strlen(json);
    if (length > CAB_APP_MAX_PAYLOAD) return;
    send_payload(command, request->message_id, request->correlation_id, 0,
                 (const uint8_t *)json, (uint16_t)length, cache);
}

static void send_ack(const cab_app_view_t *request, const char *result) {
    uint8_t payload[96];
    int length = cab_pack_ack(payload, sizeof(payload), request->message_id,
                              0, result);
    if (length > 0) send_payload(CAB_CMD_ACK, request->message_id,
        request->correlation_id, CAB_APP_FLAG_IS_ACK, payload,
        (uint16_t)length, true);
}

static void send_error(const cab_app_view_t *request, uint16_t code,
                       const char *message) {
    uint8_t payload[192];
    int length = cab_pack_ack(payload, sizeof(payload), request->message_id,
                              code, message);
    if (length > 0) send_payload(CAB_CMD_ERROR, request->message_id,
        request->correlation_id, CAB_APP_FLAG_IS_ERROR, payload,
        (uint16_t)length, true);
}

static cJSON *parse_json(const cab_app_view_t *request) {
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

static uint8_t hex_value(char value) {
    if (value >= '0' && value <= '9') return (uint8_t)(value - '0');
    if (value >= 'a' && value <= 'f') return (uint8_t)(value - 'a' + 10);
    if (value >= 'A' && value <= 'F') return (uint8_t)(value - 'A' + 10);
    return 0xFF;
}

static const char *firmware_version(void) {
    return esp_app_get_description()->version;
}

static void handle_register(const cab_app_view_t *request) {
    cab_device_config_t config;
    cab_storage_load_config(&config);
    cab_mesh_stats_t stats = cab_mesh_stats();
    char json[512];
    snprintf(json, sizeof(json),
        "{\"device_id\":\"%s\",\"device_name\":\"%s\","
        "\"is_root\":true,\"firmware_version\":\"%s\","
        "\"mesh_layer\":1,\"mesh_node_type\":1,\"child_count\":%d,"
        "\"route_count\":%d,\"mesh_to_ds_ready\":true,"
        "\"mesh_rx_drops\":%lu,"
        "\"sd_ready\":%s}", s_root_id, config.device_name,
        firmware_version(),
        cab_mesh_child_count(), cab_mesh_route_count(),
        (unsigned long)stats.receive_drops,
        root_storage_ready() ? "true" : "false");
    send_json(CAB_CMD_REGISTER, request, json, true);
}

static void build_status_json(char *json, size_t json_size) {
    cab_mesh_stats_t stats = cab_mesh_stats();
    snprintf(json, json_size,
        "{\"firmware_version\":\"%s\",\"uptime\":%lu,"
        "\"reset_reason\":%d,\"mesh_layer\":1,\"mesh_node_type\":1,"
        "\"child_count\":%d,\"route_count\":%d,"
        "\"mesh_to_ds_ready\":true,\"uplink_connected\":true,"
        "\"mesh_send_failures\":%lu,\"mesh_rx_drops\":%lu,"
        "\"mesh_heartbeat_acks\":%lu,\"mesh_heartbeat_timeouts\":%lu,"
        "\"work_mode\":\"mesh\","
        "\"sd_ready\":%s,\"sd_error\":\"%s\","
        "\"display_ready\":%s,\"display_error\":\"%s\"}",
        firmware_version(), (unsigned long)(now_ms() / 1000),
        (int)esp_reset_reason(),
        cab_mesh_child_count(), cab_mesh_route_count(),
        (unsigned long)stats.send_failures,
        (unsigned long)stats.receive_drops,
        (unsigned long)stats.heartbeat_acks,
        (unsigned long)stats.heartbeat_timeouts,
        root_storage_ready() ? "true" : "false",
        root_storage_ready() ? "" : root_storage_last_error(),
        root_display_ready() ? "true" : "false",
        root_display_ready() ? "" : root_display_last_error());
}

static void handle_status(const cab_app_view_t *request) {
    char json[768];
    build_status_json(json, sizeof(json));
    send_json(CAB_CMD_STATUS_RESPONSE, request, json, true);
}

static void handle_read_config(const cab_app_view_t *request) {
    cab_device_config_t config;
    cab_storage_load_config(&config);
    char json[512];
    snprintf(json, sizeof(json),
        "{\"device_id\":\"%s\",\"device_name\":\"%s\","
        "\"is_root\":true,\"work_mode\":\"mesh\","
        "\"uplink_mode\":0,\"mesh_channel\":6,"
        "\"firmware_version\":\"%s\"}",
        s_root_id, config.device_name, firmware_version());
    send_json(CAB_CMD_CONFIG_RESPONSE, request, json, true);
}

static void handle_write_config(const cab_app_view_t *request, cJSON *json) {
    cab_device_config_t config;
    cab_storage_load_config(&config);
    const char *name = json_string(json, "device_name", NULL);
    if (name != NULL) snprintf(config.device_name, sizeof(config.device_name),
                               "%s", name);
    config.work_mode = 0;
    config.mesh_channel = 6;
    if (!cab_storage_save_config(&config)) {
        send_error(request, CAB_ERR_FLASH_WRITE, "config save failed");
        return;
    }
    send_json(CAB_CMD_CONFIG_SAVED, request,
              "{\"result\":\"success\"}", true);
}

static void broadcast_time(uint32_t timestamp) {
    uint8_t payload[4] = {(uint8_t)timestamp, (uint8_t)(timestamp >> 8),
                          (uint8_t)(timestamp >> 16),
                          (uint8_t)(timestamp >> 24)};
    uint8_t output[160];
    int length = cab_app_encode(output, sizeof(output), CAB_CMD_TIME_SYNC,
        cab_next_message_id(), 0, CAB_APP_FLAG_BROADCAST, "", s_root_id,
        payload, sizeof(payload), timestamp);
    if (length <= 0) return;
    uint8_t routes[100][6];
    int count = cab_mesh_routes(routes, 100);
    for (int index = 0; index < count; ++index)
        cab_mesh_send_node(routes[index], output, (size_t)length);
}

static bool sd_required(const cab_app_view_t *request) {
    if (root_storage_ready()) return true;
    send_error(request, CAB_ERR_SD_NOT_READY, "sd card not ready");
    return false;
}

static size_t utf8_part_length(const char *text, size_t start,
                               size_t remaining) {
    size_t length = remaining > QUERY_PART_DATA_MAX
        ? QUERY_PART_DATA_MAX : remaining;
    while (length > 0 && text[start + length] != '\0' &&
           (((uint8_t)text[start + length] & 0xC0) == 0x80)) --length;
    return length;
}

static bool send_large_query(const cab_app_view_t *request,
                             const char *response, size_t length) {
    if (length <= QUERY_PART_DATA_MAX) {
        send_json(CAB_CMD_SD_QUERY_RESPONSE, request, response, true);
        return true;
    }
    size_t offset = 0;
    int total = 0;
    while (offset < length) {
        size_t part = utf8_part_length(response, offset, length - offset);
        if (part == 0) return false;
        offset += part;
        ++total;
    }
    offset = 0;
    for (int index = 0; index < total; ++index) {
        size_t part_length = utf8_part_length(response, offset,
                                              length - offset);
        char *chunk = malloc(part_length + 1);
        if (chunk == NULL) return false;
        memcpy(chunk, response + offset, part_length);
        chunk[part_length] = '\0';
        cJSON *part = cJSON_CreateObject();
        cJSON_AddNumberToObject(part, "part", index + 1);
        cJSON_AddNumberToObject(part, "total", total);
        cJSON_AddStringToObject(part, "data", chunk);
        free(chunk);
        char *json = cJSON_PrintUnformatted(part);
        cJSON_Delete(part);
        if (json == NULL) return false;
        send_json(CAB_CMD_SD_QUERY_PART, request, json, false);
        cJSON_free(json);
        offset += part_length;
        vTaskDelay(pdMS_TO_TICKS((index + 1) % 3 == 0 ? 80 : 25));
    }
    return true;
}

static void handle_sd_query(const cab_app_view_t *request, cJSON *json) {
    if (!sd_required(request)) return;
    const char *table = json_string(json, "table", "");
    if (!root_storage_table_allowed(table)) {
        send_error(request, CAB_ERR_BAD_REQUEST, "table is not allowed");
        return;
    }
    char *table_json = NULL;
    size_t table_length = 0;
    if (!root_storage_read_table(table, &table_json, &table_length) ||
        table_length == 0) {
        free(table_json);
        table_json = strdup("[]");
        table_length = 2;
    }
    size_t capacity = table_length + strlen(table) + 96;
    char *response = malloc(capacity);
    if (response == NULL) { free(table_json); send_error(request,
        CAB_ERR_INTERNAL, "memory alloc failed"); return; }
    int prefix = snprintf(response, capacity,
        "{\"table\":\"%s\",\"version\":%lu,\"json\":",
        table, (unsigned long)root_storage_table_version(table));
    memcpy(response + prefix, table_json, table_length);
    response[prefix + table_length] = '}';
    response[prefix + table_length + 1] = '\0';
    send_large_query(request, response, prefix + table_length + 1);
    free(response);
    free(table_json);
}

static char *json_value_text(const cJSON *object, const char *name,
                             bool *must_free) {
    *must_free = false;
    const cJSON *item = cJSON_GetObjectItemCaseSensitive(object, name);
    if (cJSON_IsString(item) && item->valuestring != NULL)
        return item->valuestring;
    if (item != NULL) {
        *must_free = true;
        return cJSON_PrintUnformatted(item);
    }
    return NULL;
}

static void send_sd_save_result(const cab_app_view_t *request,
                                const char *table, const char *result,
                                const char *extra) {
    char response[512];
    snprintf(response, sizeof(response),
        "{\"table\":\"%s\",\"result\":\"%s\"%s%s}",
        table, result, extra != NULL && extra[0] != '\0' ? "," : "",
        extra == NULL ? "" : extra);
    send_json(CAB_CMD_SD_SAVE_RESPONSE, request, response, true);
}

static void handle_sd_save(const cab_app_view_t *request, cJSON *json) {
    if (!sd_required(request)) return;
    const char *table = json_string(json, "table", "");
    if (!root_storage_table_allowed(table) || strcmp(table, "version") == 0) {
        send_error(request, CAB_ERR_BAD_REQUEST, "table is not allowed");
        return;
    }
    uint32_t base_version = json_u32(json, "base_version", 0);
    bool enforce_version = json_bool(json, "enforce_version", false);
    const cJSON *chunk_item = cJSON_GetObjectItemCaseSensitive(json,
                                                               "chunk_base64");
    bool chunked = cJSON_IsString(chunk_item);
    const char *upload_id = json_string(json, "upload_id", "");
    uint32_t part_index = json_u32(json, "part_index", 0);
    uint32_t part_total = json_u32(json, "part_total", 0);
    uint32_t total_bytes = json_u32(json, "total_bytes", 0);

    bool known = chunked && root_storage_chunk_known(
        table, upload_id, part_index, part_total);
    if (!known && (enforce_version || base_version > 0) &&
        root_storage_table_version(table) != base_version) {
        char response[256];
        snprintf(response, sizeof(response),
            "{\"error\":\"version_conflict\",\"current_version\":%lu,"
            "\"base_version\":%lu}",
            (unsigned long)root_storage_table_version(table),
            (unsigned long)base_version);
        send_json(CAB_CMD_SD_SAVE_RESPONSE, request, response, true);
        return;
    }
    if (chunked) {
        const char *base64 = chunk_item->valuestring;
        if (upload_id[0] == '\0' || strlen(upload_id) > 40 ||
            part_total == 0 || part_index >= part_total ||
            total_bytes == 0 || base64[0] == '\0') {
            send_sd_save_result(request, table, "fail",
                                "\"error\":\"invalid_chunk\"");
            return;
        }
        size_t capacity = strlen(base64) / 4 * 3 + 3;
        uint8_t *decoded = malloc(capacity);
        size_t decoded_length = 0;
        int decode = decoded == NULL ? -1 : mbedtls_base64_decode(
            decoded, capacity, &decoded_length,
            (const unsigned char *)base64, strlen(base64));
        if (decode != 0 || decoded_length == 0) {
            free(decoded);
            send_sd_save_result(request, table, "fail",
                "\"error\":\"base64_decode_failed\"");
            return;
        }
        uint32_t expected = 0;
        root_sd_chunk_result_t result = root_storage_write_chunk(
            table, upload_id, part_index, part_total, total_bytes,
            decoded, decoded_length, &expected);
        free(decoded);
        char response[512];
        const char *result_text = result == ROOT_SD_CHUNK_COMPLETE
            ? "success" : ((result == ROOT_SD_CHUNK_ACCEPTED ||
                             result == ROOT_SD_CHUNK_DUPLICATE)
                            ? "part_ok" : "fail");
        const char *error = result == ROOT_SD_CHUNK_OUT_OF_ORDER
            ? "out_of_order" : (result == ROOT_SD_CHUNK_INVALID
                                 ? "invalid_chunk" :
                                 (result == ROOT_SD_CHUNK_FAILED
                                  ? "sd_write_failed" : ""));
        snprintf(response, sizeof(response),
            "{\"table\":\"%s\",\"upload_id\":\"%s\","
            "\"part_index\":%lu,\"part_total\":%lu,"
            "\"result\":\"%s\",\"expected_part\":%lu%s%s%s,"
            "\"version\":%lu}", table, upload_id,
            (unsigned long)part_index, (unsigned long)part_total,
            result_text, (unsigned long)expected,
            error[0] != '\0' ? ",\"error\":\"" : "",
            error, error[0] != '\0' ? "\"" : "",
            (unsigned long)root_storage_table_version(table));
        send_json(CAB_CMD_SD_SAVE_RESPONSE, request, response, true);
        return;
    }
    bool must_free = false;
    char *content = json_value_text(json, "json", &must_free);
    if (content == NULL || content[0] == '\0') {
        if (must_free) cJSON_free(content);
        send_error(request, CAB_ERR_BAD_REQUEST, "missing json");
        return;
    }
    bool ok = root_storage_write_table(table, (const uint8_t *)content,
                                       strlen(content));
    if (must_free) cJSON_free(content);
    if (ok) {
        char extra[160];
        root_sd_versions_t versions;
        root_storage_read_versions(&versions);
        snprintf(extra, sizeof(extra),
            "\"version\":%lu,\"global_version\":%lu",
            (unsigned long)root_storage_table_version(table),
            (unsigned long)versions.global);
        send_sd_save_result(request, table, "success", extra);
    } else {
        send_sd_save_result(request, table, "fail",
                            "\"error\":\"sd_write_failed\"");
    }
}

static void handle_sd_version(const cab_app_view_t *request) {
    if (!sd_required(request)) return;
    root_sd_versions_t versions;
    root_storage_read_versions(&versions);
    char response[512];
    snprintf(response, sizeof(response),
        "{\"global_version\":%lu,\"users_version\":%lu,"
        "\"classes_version\":%lu,\"permissions_version\":%lu,"
        "\"devices_version\":%lu,\"fp_version\":%lu,"
        "\"settings_version\":%lu,"
        "\"logs_version\":%lu,\"sd_total_bytes\":%llu,"
        "\"sd_used_bytes\":%llu}",
        (unsigned long)versions.global, (unsigned long)versions.users,
        (unsigned long)versions.classes,
        (unsigned long)versions.permissions,
        (unsigned long)versions.devices,
        (unsigned long)versions.fingerprints,
        (unsigned long)versions.settings,
        (unsigned long)versions.logs,
        (unsigned long long)root_storage_total_bytes(),
        (unsigned long long)root_storage_used_bytes());
    send_json(CAB_CMD_SD_VERSION_RESPONSE, request, response, true);
}

static uint32_t payload_le32(const uint8_t *value) {
    return (uint32_t)value[0] | ((uint32_t)value[1] << 8) |
           ((uint32_t)value[2] << 16) | ((uint32_t)value[3] << 24);
}

static void payload_write_le32(uint8_t *output, uint32_t value) {
    output[0] = (uint8_t)value;
    output[1] = (uint8_t)(value >> 8);
    output[2] = (uint8_t)(value >> 16);
    output[3] = (uint8_t)(value >> 24);
}

static void send_snapshot_response(const cab_app_view_t *request,
                                   uint8_t operation,
                                   root_snapshot_result_t result,
                                   uint32_t next_offset,
                                   uint32_t total_size,
                                   const uint8_t upload_id[16],
                                   bool cache) {
    uint8_t payload[28] = {0};
    payload[0] = 1;
    payload[1] = operation;
    payload[2] = (uint8_t)result;
    payload_write_le32(payload + 4, next_offset);
    payload_write_le32(payload + 8, total_size);
    if (upload_id != NULL) memcpy(payload + 12, upload_id, 16);
    send_payload(CAB_CMD_SD_SNAPSHOT_RESPONSE, request->message_id,
                 request->correlation_id, 0, payload, sizeof(payload), cache);
}

static void handle_snapshot_manifest(const cab_app_view_t *request) {
    if (!sd_required(request)) return;
    uint8_t payload[4 + ROOT_SNAPSHOT_HEADER_SIZE] = {0};
    payload[0] = 1;
    root_snapshot_result_t result = root_storage_snapshot_manifest(payload + 4);
    payload[1] = (uint8_t)result;
    size_t length = result == ROOT_SNAPSHOT_OK ? sizeof(payload) : 4;
    send_payload(CAB_CMD_SD_SNAPSHOT_MANIFEST_RESPONSE,
                 request->message_id, request->correlation_id, 0,
                 payload, (uint16_t)length, true);
}

static void handle_snapshot_begin(const cab_app_view_t *request) {
    if (!sd_required(request)) return;
    uint32_t next_offset = 0;
    root_snapshot_result_t result = root_storage_snapshot_begin(
        request->payload, request->payload_len, &next_offset);
    uint32_t total_size = request->payload_len >= 16
        ? payload_le32(request->payload + 12) : 0;
    const uint8_t *upload_id = request->payload_len >= 40
        ? request->payload + 24 : NULL;
    send_snapshot_response(request, 1, result, next_offset, total_size,
                           upload_id, true);
}

static void handle_snapshot_chunk(const cab_app_view_t *request) {
    if (!sd_required(request)) return;
    if (request->payload_len <= 24 || request->payload[0] != 1) {
        send_snapshot_response(request, 2, ROOT_SNAPSHOT_INVALID, 0, 0,
                               NULL, true);
        return;
    }
    bool acknowledge = (request->payload[1] & 1U) != 0;
    const uint8_t *upload_id = request->payload + 4;
    uint32_t offset = payload_le32(request->payload + 20);
    uint32_t next_offset = 0;
    root_snapshot_result_t result = root_storage_snapshot_write(
        upload_id, offset, request->payload + 24,
        request->payload_len - 24, acknowledge, &next_offset);
    if (acknowledge || result != ROOT_SNAPSHOT_OK)
        send_snapshot_response(request, 2, result, next_offset, 0,
                               upload_id, acknowledge);
}

static void handle_snapshot_commit(const cab_app_view_t *request) {
    if (!sd_required(request)) return;
    if (request->payload_len != 20 || request->payload[0] != 1) {
        send_snapshot_response(request, 3, ROOT_SNAPSHOT_INVALID, 0, 0,
                               NULL, true);
        return;
    }
    const uint8_t *upload_id = request->payload + 4;
    uint32_t size = 0;
    root_snapshot_result_t result = root_storage_snapshot_commit(upload_id,
                                                                  &size);
    send_snapshot_response(request, 3, result, size, size, upload_id, true);
}

static void handle_snapshot_download(const cab_app_view_t *request) {
    if (!sd_required(request)) return;
    if (request->payload_len < 8 || request->payload[0] != 1) {
        send_error(request, CAB_ERR_BAD_REQUEST,
                   "invalid snapshot download request");
        return;
    }
    uint32_t offset = payload_le32(request->payload + 4);
    uint8_t *payload = malloc(12 + SNAPSHOT_DOWNLOAD_DATA_MAX);
    if (payload == NULL) {
        send_error(request, CAB_ERR_INTERNAL, "memory alloc failed");
        return;
    }
    for (;;) {
        size_t length = 0;
        uint32_t total_size = 0;
        root_snapshot_result_t result = root_storage_snapshot_read(
            offset, payload + 12, SNAPSHOT_DOWNLOAD_DATA_MAX, &length,
            &total_size);
        if (result != ROOT_SNAPSHOT_OK || length == 0) {
            free(payload);
            send_error(request, result == ROOT_SNAPSHOT_NOT_FOUND
                ? CAB_ERR_NOT_FOUND : CAB_ERR_INTERNAL,
                result == ROOT_SNAPSHOT_NOT_FOUND
                    ? "business snapshot not found"
                    : "business snapshot read failed");
            return;
        }
        bool last = offset + length >= total_size;
        memset(payload, 0, 12);
        payload[0] = 1;
        payload[1] = last ? 1 : 0;
        payload_write_le32(payload + 4, offset);
        payload_write_le32(payload + 8, total_size);
        send_payload(CAB_CMD_SD_SNAPSHOT_DOWNLOAD_PART,
                     request->message_id, request->correlation_id,
                     CAB_APP_FLAG_MULTI_PART, payload,
                     (uint16_t)(12 + length), false);
        offset += (uint32_t)length;
        if (last) break;
        if ((offset / SNAPSHOT_DOWNLOAD_DATA_MAX) % 4 == 0)
            vTaskDelay(pdMS_TO_TICKS(1));
    }
    free(payload);
}

static void send_ota_response(const cab_app_view_t *request,
                              const char *operation, const char *result,
                              uint32_t next_offset, bool duplicate) {
    root_ota_status_t status;
    root_ota_get_status(&status);
    char response[1024];
    snprintf(response, sizeof(response),
        "{\"operation\":\"%s\",\"result\":\"%s\","
        "\"phase\":\"%s\",\"upload_id\":\"%s\","
        "\"version\":\"%s\",\"hardware_version\":\"%s\","
        "\"sha256\":\"%s\",\"active\":%s,"
        "\"image_size\":%lu,\"received_bytes\":%lu,"
        "\"next_offset\":%lu,\"duplicate\":%s,"
        "\"expected_nodes\":%lu,\"completed_nodes\":%lu,"
        "\"known_nodes\":%lu,\"compatible_nodes\":%lu,"
        "\"pending_nodes\":%lu,\"incompatible_nodes\":%lu,"
        "\"unknown_hardware_nodes\":%lu,"
        "\"mesh_progress\":%u,\"started_at\":%lu,"
        "\"elapsed_seconds\":%lu,"
        "\"published_at\":%llu,"
        "\"finish_reason\":%d,\"error\":\"%s\"}",
        operation == NULL ? "status" : operation,
        result == NULL ? "ok" : result, status.phase, status.upload_id,
        status.version, status.hardware_version, status.sha256,
        status.active ? "true" : "false", (unsigned long)status.image_size,
        (unsigned long)status.received_bytes, (unsigned long)next_offset,
        duplicate ? "true" : "false",
        (unsigned long)status.expected_nodes,
        (unsigned long)status.completed_nodes,
        (unsigned long)status.known_nodes,
        (unsigned long)status.compatible_nodes,
        (unsigned long)status.pending_nodes,
        (unsigned long)status.incompatible_nodes,
        (unsigned long)status.unknown_hardware_nodes, status.mesh_progress,
        (unsigned long)status.started_at_seconds,
        (unsigned long)status.elapsed_seconds,
        (unsigned long long)status.published_at, status.finish_reason,
        status.error);
    send_json(CAB_CMD_CABINET_OTA_RESPONSE, request, response, true);
}

static void handle_ota_nodes(const cab_app_view_t *request, cJSON *json) {
    size_t offset = json_u32(json, "offset", 0);
    size_t limit = json_u32(json, "limit", 10);
    if (limit == 0) limit = 10;
    if (limit > 10) limit = 10;
    root_ota_node_status_t *nodes = calloc(limit, sizeof(*nodes));
    if (nodes == NULL) {
        send_error(request, CAB_ERR_INTERNAL, "allocate ota node page failed");
        return;
    }
    size_t total = 0;
    size_t count = root_ota_get_nodes(offset, limit, nodes, &total);
    cJSON *response = cJSON_CreateObject();
    cJSON *items = cJSON_CreateArray();
    if (response == NULL || items == NULL) {
        cJSON_Delete(response);
        cJSON_Delete(items);
        free(nodes);
        send_error(request, CAB_ERR_INTERNAL, "encode ota node page failed");
        return;
    }
    cJSON_AddNumberToObject(response, "offset", (double)offset);
    cJSON_AddNumberToObject(response, "count", (double)count);
    cJSON_AddNumberToObject(response, "total", (double)total);
    cJSON_AddItemToObject(response, "nodes", items);
    for (size_t index = 0; index < count; ++index) {
        const root_ota_node_status_t *node = &nodes[index];
        cJSON *item = cJSON_CreateObject();
        if (item == NULL) continue;
        cJSON_AddStringToObject(item, "device_id", node->device_id);
        cJSON_AddStringToObject(item, "parent_device_id",
                               node->parent_device_id);
        cJSON_AddStringToObject(item, "version", node->version);
        cJSON_AddStringToObject(item, "phase", node->phase);
        cJSON_AddStringToObject(item, "error", node->error);
        cJSON_AddNumberToObject(item, "mesh_layer", node->mesh_layer);
        cJSON_AddNumberToObject(item, "progress", node->progress);
        cJSON_AddNumberToObject(item, "retry_count", node->retry_count);
        cJSON_AddNumberToObject(item, "updated_ago",
                               node->updated_ago_seconds);
        cJSON_AddBoolToObject(item, "online", node->online);
        cJSON_AddBoolToObject(item, "compatible", node->compatible);
        cJSON_AddItemToArray(items, item);
    }
    char *content = cJSON_PrintUnformatted(response);
    if (content != NULL) {
        send_json(CAB_CMD_CABINET_OTA_NODES_RESPONSE,
                  request, content, false);
        free(content);
    } else {
        send_error(request, CAB_ERR_INTERNAL, "encode ota node page failed");
    }
    cJSON_Delete(response);
    free(nodes);
}

static void handle_ota_begin(const cab_app_view_t *request, cJSON *json) {
    const char *upload_id = json_string(json, "upload_id", "");
    const char *version = json_string(json, "version", "");
    const char *hardware_version =
        json_string(json, "hardware_version", "");
    const char *sha256 = json_string(json, "sha256", "");
    uint32_t image_size = json_u32(json, "image_size", 0);
    const cJSON *published_item =
        cJSON_GetObjectItemCaseSensitive(json, "published_at");
    uint64_t published_at = cJSON_IsNumber(published_item)
        ? (uint64_t)published_item->valuedouble : 0;
    char error[128] = {0};
    esp_err_t result = root_ota_upload_begin(upload_id, version,
                                             hardware_version, sha256,
                                             image_size, published_at, error,
                                             sizeof(error));
    if (result != ESP_OK) {
        send_error(request, result == ESP_ERR_INVALID_STATE
            ? CAB_ERR_OTA_NOT_READY : CAB_ERR_BAD_REQUEST, error);
        return;
    }
    send_ota_response(request, "begin", "ok", 0, false);
}

static void handle_ota_chunk(const cab_app_view_t *request, cJSON *json) {
    const char *upload_id = json_string(json, "upload_id", "");
    const char *base64 = json_string(json, "chunk_base64", "");
    uint32_t offset = json_u32(json, "offset", UINT32_MAX);
    size_t capacity = strlen(base64) / 4 * 3 + 3;
    if (offset == UINT32_MAX || capacity == 0 || capacity > 3072) {
        send_error(request, CAB_ERR_BAD_REQUEST, "invalid ota chunk");
        return;
    }
    uint8_t *decoded = malloc(capacity);
    size_t decoded_length = 0;
    int decode_result = decoded == NULL ? -1 : mbedtls_base64_decode(
        decoded, capacity, &decoded_length,
        (const unsigned char *)base64, strlen(base64));
    if (decode_result != 0 || decoded_length == 0) {
        free(decoded);
        send_error(request, CAB_ERR_BAD_REQUEST, "ota chunk base64 invalid");
        return;
    }
    uint32_t next_offset = 0;
    bool duplicate = false;
    char error[128] = {0};
    esp_err_t result = root_ota_upload_chunk(
        upload_id, offset, decoded, decoded_length, &next_offset,
        &duplicate, error, sizeof(error));
    free(decoded);
    if (result != ESP_OK) {
        send_error(request, CAB_ERR_OTA_UPLOAD_STATE, error);
        return;
    }
    send_ota_response(request, "chunk", "ok", next_offset, duplicate);
}

static void handle_ota_commit(const cab_app_view_t *request, cJSON *json) {
    const char *upload_id = json_string(json, "upload_id", "");
    char actual_version[32] = {0};
    char error[128] = {0};
    esp_err_t result = root_ota_upload_commit(
        upload_id, actual_version, sizeof(actual_version), error,
        sizeof(error));
    if (result != ESP_OK) {
        send_error(request, CAB_ERR_OTA_INVALID_IMAGE, error);
        return;
    }
    send_ota_response(request, "commit", "ok", 0, false);
}

static void handle_ota_start(const cab_app_view_t *request) {
    char error[128] = {0};
    esp_err_t result = root_ota_start(error, sizeof(error));
    if (result != ESP_OK) {
        send_error(request, CAB_ERR_OTA_START_FAILED, error);
        return;
    }
    send_ota_response(request, "start", "ok", 0, false);
}

static void handle_upload_template(const cab_app_view_t *request,
                                   cJSON *json) {
    if (!sd_required(request)) return;
    const char *user_id = json_string(json, "user_id", "");
    int finger_index = (int)json_u32(json, "finger_index", 1);
    const char *hex = json_string(json, "template_hex", "");
    size_t hex_length = strlen(hex);
    if (user_id[0] == '\0' || finger_index < 1 || finger_index > 2 ||
        hex_length == 0 || (hex_length & 1U) != 0 || hex_length > 1152) {
        send_error(request, CAB_ERR_BAD_REQUEST, "invalid template data");
        return;
    }
    size_t length = hex_length / 2;
    uint8_t *data = malloc(length);
    if (data == NULL) { send_error(request, CAB_ERR_INTERNAL,
                                   "memory alloc failed"); return; }
    bool valid = true;
    for (size_t index = 0; index < length; ++index) {
        uint8_t high = hex_value(hex[index * 2]);
        uint8_t low = hex_value(hex[index * 2 + 1]);
        if (high == 0xFF || low == 0xFF) { valid = false; break; }
        data[index] = (uint8_t)((high << 4) | low);
    }
    bool ok = valid && root_storage_write_template(user_id, finger_index,
                                                    data, length);
    free(data);
    char response[192];
    snprintf(response, sizeof(response),
        "{\"user_id\":\"%s\",\"finger_index\":%d,"
        "\"result\":\"%s\"}", user_id, finger_index,
        ok ? "success" : "fail");
    send_json(CAB_CMD_FP_TEMPLATE_UPLOAD_RESPONSE, request, response, true);
}

static void handle_download_template(const cab_app_view_t *request,
                                     cJSON *json) {
    if (!sd_required(request)) return;
    const char *user_id = json_string(json, "user_id", "");
    int finger_index = (int)json_u32(json, "finger_index", 1);
    if (user_id[0] == '\0' || finger_index < 1 || finger_index > 2) {
        send_error(request, CAB_ERR_BAD_REQUEST, "invalid template key");
        return;
    }
    uint8_t data[576];
    size_t length = 0;
    if (!root_storage_read_template(user_id, finger_index, data,
                                    sizeof(data), &length)) {
        send_error(request, CAB_ERR_NOT_FOUND, "template not found");
        return;
    }
    char hex[1153];
    static const char digits[] = "0123456789ABCDEF";
    for (size_t index = 0; index < length; ++index) {
        hex[index * 2] = digits[data[index] >> 4];
        hex[index * 2 + 1] = digits[data[index] & 0x0F];
    }
    hex[length * 2] = '\0';
    char *response = malloc(1400);
    if (response == NULL) { send_error(request, CAB_ERR_INTERNAL,
                                       "memory alloc failed"); return; }
    snprintf(response, 1400,
        "{\"user_id\":\"%s\",\"finger_index\":%d,\"len\":%u,"
        "\"template_hex\":\"%s\"}", user_id, finger_index,
        (unsigned)length, hex);
    send_json(CAB_CMD_FP_TEMPLATE_DOWNLOAD_RESPONSE, request, response, true);
    free(response);
}

static void handle_delete_template(const cab_app_view_t *request,
                                   cJSON *json) {
    if (!sd_required(request)) return;
    const char *user_id = json_string(json, "user_id", "");
    int finger_index = (int)json_u32(json, "finger_index", 0);
    if (user_id[0] == '\0' || finger_index < 0 || finger_index > 2) {
        send_error(request, CAB_ERR_BAD_REQUEST, "invalid template key");
        return;
    }
    bool ok = root_storage_delete_template(user_id, finger_index);
    char response[192];
    snprintf(response, sizeof(response),
        "{\"user_id\":\"%s\",\"finger_index\":%d,"
        "\"result\":\"%s\"}", user_id, finger_index,
        ok ? "success" : "fail");
    send_json(CAB_CMD_FP_TEMPLATE_DELETE_RESPONSE, request, response, true);
}

bool root_controller_init(const char *root_id, root_controller_tx_t transmit,
                          void *context) {
    if (root_id == NULL || transmit == NULL) return false;
    snprintf(s_root_id, sizeof(s_root_id), "%s", root_id);
    s_transmit = transmit;
    s_transmit_context = context;
    if (!cab_storage_init(root_id, true)) return false;
    // SD failure is non-fatal: Mesh routing and cabinet management remain live.
    root_storage_init();
    return true;
}

void root_controller_handle(const cab_app_view_t *request) {
    if (request == NULL) return;
    const response_cache_t *cached = find_cached(request);
    if (cached != NULL) {
        s_transmit(cached->data, cached->length, s_transmit_context);
        return;
    }
    s_current_request = request;
    switch (request->command) {
        case CAB_CMD_HEARTBEAT_ACK:
        case CAB_CMD_ACK:
        case CAB_CMD_SD_QUERY_PART_ACK:
            s_current_request = NULL;
            return;
        case CAB_CMD_REGISTER:
            handle_register(request);
            s_current_request = NULL;
            return;
        case CAB_CMD_READ_STATUS:
            handle_status(request);
            s_current_request = NULL;
            return;
        case CAB_CMD_READ_CONFIG:
            handle_read_config(request);
            s_current_request = NULL;
            return;
        case CAB_CMD_TIME_SYNC:
            if (request->payload_len >= 4) {
                uint32_t timestamp = request->payload[0] |
                    ((uint32_t)request->payload[1] << 8) |
                    ((uint32_t)request->payload[2] << 16) |
                    ((uint32_t)request->payload[3] << 24);
                if (timestamp != 0) {
                    cab_storage_set_unix_time(timestamp);
                    broadcast_time(timestamp);
                    send_ack(request, "time_synced");
                } else send_error(request, CAB_ERR_BAD_REQUEST,
                                  "invalid timestamp");
            } else send_error(request, CAB_ERR_BAD_REQUEST,
                              "invalid timestamp");
            s_current_request = NULL;
            return;
        case CAB_CMD_SD_SNAPSHOT_MANIFEST:
            handle_snapshot_manifest(request);
            s_current_request = NULL;
            return;
        case CAB_CMD_SD_SNAPSHOT_BEGIN:
            handle_snapshot_begin(request);
            s_current_request = NULL;
            return;
        case CAB_CMD_SD_SNAPSHOT_CHUNK:
            handle_snapshot_chunk(request);
            s_current_request = NULL;
            return;
        case CAB_CMD_SD_SNAPSHOT_COMMIT:
            handle_snapshot_commit(request);
            s_current_request = NULL;
            return;
        case CAB_CMD_SD_SNAPSHOT_DOWNLOAD:
            handle_snapshot_download(request);
            s_current_request = NULL;
            return;
        default:
            break;
    }

    cJSON *json = parse_json(request);
    if (json == NULL || !cJSON_IsObject(json)) {
        cJSON_Delete(json);
        send_error(request, CAB_ERR_JSON_PARSE, "json parse failed");
        s_current_request = NULL;
        return;
    }
    switch (request->command) {
        case CAB_CMD_WRITE_CONFIG:
            handle_write_config(request, json);
            break;
        case CAB_CMD_REBOOT:
            send_json(CAB_CMD_REBOOT_ACK, request,
                      "{\"result\":\"rebooting\"}", false);
            send_ack(request, "rebooting");
            cJSON_Delete(json);
            s_current_request = NULL;
            vTaskDelay(pdMS_TO_TICKS(250));
            esp_restart();
            return;
        case CAB_CMD_SD_QUERY:
            handle_sd_query(request, json);
            break;
        case CAB_CMD_SD_SAVE:
            handle_sd_save(request, json);
            break;
        case CAB_CMD_SD_QUERY_VERSION:
            handle_sd_version(request);
            break;
        case CAB_CMD_CABINET_OTA_BEGIN:
            handle_ota_begin(request, json);
            break;
        case CAB_CMD_CABINET_OTA_CHUNK:
            handle_ota_chunk(request, json);
            break;
        case CAB_CMD_CABINET_OTA_COMMIT:
            handle_ota_commit(request, json);
            break;
        case CAB_CMD_CABINET_OTA_START:
            handle_ota_start(request);
            break;
        case CAB_CMD_CABINET_OTA_STATUS:
            send_ota_response(request, "status", "ok", 0, false);
            break;
        case CAB_CMD_CABINET_OTA_NODES:
            handle_ota_nodes(request, json);
            break;
        case CAB_CMD_UPLOAD_FP_TEMPLATE:
            handle_upload_template(request, json);
            break;
        case CAB_CMD_DOWNLOAD_FP_TEMPLATE:
            handle_download_template(request, json);
            break;
        case CAB_CMD_DELETE_FP_TEMPLATE:
            handle_delete_template(request, json);
            break;
        default:
            send_error(request, CAB_ERR_UNKNOWN_COMMAND, "unknown command");
            break;
    }
    cJSON_Delete(json);
    s_current_request = NULL;
}

void root_controller_report_status(void) {
    char json[768];
    build_status_json(json, sizeof(json));
    send_payload(CAB_CMD_STATUS_REPORT, cab_next_message_id(), 0, 0,
                 (const uint8_t *)json, (uint16_t)strlen(json), false);
}
