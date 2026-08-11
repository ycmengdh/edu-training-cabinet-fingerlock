#include "root_ota.h"

#include <ctype.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

#include "cJSON.h"
#include "cabinet_mesh.h"
#include "cabinet_protocol.h"
#include "esp_app_desc.h"
#include "esp_app_format.h"
#include "esp_event.h"
#include "esp_log.h"
#include "esp_mesh_lite.h"
#include "esp_mesh_lite_core.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "psa/crypto.h"
#include "root_storage.h"

#define OTA_DIRECTORY "/sdcard/ota"
#define OTA_TEMP_PATH OTA_DIRECTORY "/cabinet.bin.part"
#define OTA_IMAGE_PATH OTA_DIRECTORY "/cabinet.bin"
#define OTA_POLICY_PATH OTA_DIRECTORY "/cabinet-policy.json"
#define OTA_POLICY_TEMP_PATH OTA_DIRECTORY "/cabinet-policy.json.part"
#define OTA_MIN_IMAGE_SIZE (128U * 1024U)
#define OTA_MAX_IMAGE_SIZE 0x300000U
#define OTA_REGISTRATION_CAPACITY 100
#define OTA_ONLINE_WINDOW_SECONDS 90U
#define OTA_JOIN_DEBOUNCE_SECONDS 5U
#define OTA_RETRY_SECONDS 30U
#define OTA_SCHEDULER_SECONDS 5U
#define OTA_TRANSFER_STALE_SECONDS 45U
#define OTA_NOTIFY_STALE_SECONDS 12U
#define OTA_VALIDATION_STALE_SECONDS 100U
#define OTA_NOTIFY_RETRY_SECONDS 5U
#define OTA_PER_PARENT_CONCURRENCY 2U
#define OTA_GLOBAL_CONCURRENCY 10U
#define OTA_HASH_BUFFER_SIZE 4096U

static const char *TAG = "root_ota";

typedef struct {
    char device_id[25];
    char version[ROOT_OTA_VERSION_MAX + 1];
    char hardware_version[ROOT_OTA_HARDWARE_VERSION_MAX + 1];
    char ota_version[ROOT_OTA_VERSION_MAX + 1];
    char ota_phase[20];
    char ota_error[ROOT_OTA_NODE_ERROR_MAX + 1];
    uint8_t ap_mac[6];
    uint8_t parent_bssid[6];
    uint32_t last_seen_seconds;
    uint32_t ota_updated_seconds;
    uint8_t mesh_layer;
    uint8_t ota_progress;
    uint8_t retry_count;
    bool has_ap_mac;
    bool has_parent_bssid;
    bool ota_validated;
} registration_t;

typedef struct {
    char device_id[CAB_APP_ID_MAX + 1];
    uint8_t mac[6];
    size_t registration_index;
} notification_target_t;

static SemaphoreHandle_t s_mutex;
static root_ota_status_t s_status;
static registration_t s_registrations[OTA_REGISTRATION_CAPACITY];
static FILE *s_provider_file;
static FILE *s_upload_file;
static uint32_t s_next_distribution_at;
static char s_provider_version[ROOT_OTA_VERSION_MAX + 1];
static uint32_t s_distribution_completed_at_seconds;

static esp_err_t inspect_image(
    const char *path, char actual_version[32], char actual_hardware[32],
    char actual_sha256[65], uint32_t *actual_size,
    char *error, size_t error_size);
static esp_err_t start_distribution(char *error, size_t error_size);

static uint32_t now_seconds(void) {
    return (uint32_t)(esp_timer_get_time() / 1000000ULL);
}


static void copy_error(char *output, size_t output_size, const char *value) {
    if (output == NULL || output_size == 0) return;
    snprintf(output, output_size, "%s", value == NULL ? "" : value);
}

static void copy_text(char *output, size_t output_size, const char *value) {
    if (output == NULL || output_size == 0) return;
    if (value == NULL) {
        output[0] = '\0';
        return;
    }
    size_t length = 0;
    while (length + 1 < output_size && value[length] != '\0') ++length;
    memcpy(output, value, length);
    output[length] = '\0';
}

static bool valid_text(const char *value, size_t maximum) {
    if (value == NULL || value[0] == '\0' || strlen(value) > maximum) {
        return false;
    }
    for (const unsigned char *cursor = (const unsigned char *)value;
         *cursor != '\0'; ++cursor) {
        if (*cursor < 0x21 || *cursor > 0x7E) return false;
    }
    return true;
}

static bool valid_sha256(const char *value) {
    if (value == NULL || strlen(value) != ROOT_OTA_SHA256_HEX_LENGTH) {
        return false;
    }
    for (size_t index = 0; index < ROOT_OTA_SHA256_HEX_LENGTH; ++index) {
        if (!isxdigit((unsigned char)value[index])) return false;
    }
    return true;
}

static void sha256_hex(const uint8_t digest[32], char output[65]) {
    static const char digits[] = "0123456789abcdef";
    for (size_t index = 0; index < 32; ++index) {
        output[index * 2] = digits[digest[index] >> 4];
        output[index * 2 + 1] = digits[digest[index] & 0x0F];
    }
    output[64] = '\0';
}

static bool strings_equal_ignore_case(const char *left, const char *right) {
    if (left == NULL || right == NULL) return false;
    while (*left != '\0' && *right != '\0') {
        if (tolower((unsigned char)*left) != tolower((unsigned char)*right)) {
            return false;
        }
        ++left;
        ++right;
    }
    return *left == '\0' && *right == '\0';
}

static bool registration_is_online(const registration_t *registration,
                                   uint32_t now) {
    return registration->device_id[0] != '\0' &&
           now - registration->last_seen_seconds <= OTA_ONLINE_WINDOW_SECONDS;
}

static bool registration_is_compatible(const registration_t *registration) {
    return s_status.hardware_version[0] == '\0' ||
           (registration->hardware_version[0] != '\0' &&
            strcmp(registration->hardware_version,
                   s_status.hardware_version) == 0);
}

static bool phase_is_active(const char *phase) {
    return strcmp(phase, "notified") == 0 ||
           strcmp(phase, "starting") == 0 ||
           strcmp(phase, "downloading") == 0 ||
           strcmp(phase, "verifying") == 0 ||
           strcmp(phase, "rebooting") == 0 ||
           strcmp(phase, "validating") == 0 ||
           strcmp(phase, "preparing") == 0 ||
           strcmp(phase, "broadcasting") == 0 ||
           strcmp(phase, "repairing") == 0 ||
           strcmp(phase, "ready") == 0;
}

static bool registration_is_active_for_target_locked(
    const registration_t *registration, uint32_t now) {
    return registration_is_online(registration, now) &&
           registration_is_compatible(registration) &&
           strcmp(registration->ota_version, s_status.version) == 0 &&
           phase_is_active(registration->ota_phase);
}

static registration_t *find_by_ap_mac_locked(const uint8_t mac[6]) {
    if (mac == NULL) return NULL;
    for (size_t index = 0; index < OTA_REGISTRATION_CAPACITY; ++index) {
        registration_t *registration = &s_registrations[index];
        if (registration->has_ap_mac &&
            memcmp(registration->ap_mac, mac, 6) == 0) return registration;
    }
    return NULL;
}

static bool parent_ready_locked(const registration_t *registration,
                                uint32_t now) {
    if (registration->mesh_layer <= ROOT + 1) return true;
    if (!registration->has_parent_bssid) return false;
    registration_t *parent = find_by_ap_mac_locked(
        registration->parent_bssid);
    return parent != NULL && registration_is_online(parent, now) &&
           strcmp(parent->version, s_status.version) == 0 &&
           parent->ota_validated;
}

static bool same_provider(const registration_t *left,
                          const registration_t *right) {
    if (left->has_parent_bssid && right->has_parent_bssid) {
        return memcmp(left->parent_bssid, right->parent_bssid, 6) == 0;
    }
    return left->mesh_layer == right->mesh_layer;
}

static uint32_t retry_delay_seconds(uint8_t retry_count) {
    if (retry_count <= 1) return 5U;
    if (retry_count == 2) return 15U;
    if (retry_count == 3) return 30U;
    return 60U;
}

static bool notification_delivery_failed(const registration_t *registration) {
    return strcmp(registration->ota_error, "notification timeout") == 0 ||
           strcmp(registration->ota_error, "notify failed") == 0;
}

static uint32_t registration_retry_delay_seconds(
    const registration_t *registration) {
    return notification_delivery_failed(registration)
        ? OTA_NOTIFY_RETRY_SECONDS
        : retry_delay_seconds(registration->retry_count);
}

static uint32_t transfer_stale_seconds(const registration_t *registration) {
    if (strcmp(registration->ota_phase, "notified") == 0) {
        return OTA_NOTIFY_STALE_SECONDS;
    }
    if (strcmp(registration->ota_phase, "validating") == 0) {
        return OTA_VALIDATION_STALE_SECONDS;
    }
    return OTA_TRANSFER_STALE_SECONDS;
}

static int hex_nibble(char value) {
    if (value >= '0' && value <= '9') return value - '0';
    if (value >= 'A' && value <= 'F') return value - 'A' + 10;
    if (value >= 'a' && value <= 'f') return value - 'a' + 10;
    return -1;
}

static bool parse_cabinet_mac(const char *device_id, uint8_t output[6]) {
    if (device_id == NULL || output == NULL ||
        strncmp(device_id, "CAB_", 4) != 0 || strlen(device_id) != 16) {
        return false;
    }
    for (int index = 0; index < 6; ++index) {
        int high = hex_nibble(device_id[4 + index * 2]);
        int low = hex_nibble(device_id[5 + index * 2]);
        if (high < 0 || low < 0) return false;
        output[index] = (uint8_t)((high << 4) | low);
    }
    return true;
}

static bool parse_mac_text(const char *text, uint8_t output[6]) {
    if (text == NULL || output == NULL || strlen(text) != 12) return false;
    for (int index = 0; index < 6; ++index) {
        int high = hex_nibble(text[index * 2]);
        int low = hex_nibble(text[index * 2 + 1]);
        if (high < 0 || low < 0) return false;
        output[index] = (uint8_t)((high << 4) | low);
    }
    return true;
}

static void refresh_counts_locked(void) {
    uint32_t now = now_seconds();
    s_status.known_nodes = 0;
    s_status.compatible_nodes = 0;
    s_status.completed_nodes = 0;
    s_status.pending_nodes = 0;
    s_status.incompatible_nodes = 0;
    s_status.unknown_hardware_nodes = 0;
    for (size_t index = 0; index < OTA_REGISTRATION_CAPACITY; ++index) {
        registration_t *registration = &s_registrations[index];
        if (!registration_is_online(registration, now)) continue;
        ++s_status.known_nodes;
        if (registration->hardware_version[0] == '\0') {
            ++s_status.unknown_hardware_nodes;
            if (s_status.hardware_version[0] != '\0') continue;
        }
        if (!registration_is_compatible(registration)) {
            ++s_status.incompatible_nodes;
            continue;
        }
        ++s_status.compatible_nodes;
        if (s_status.version[0] != '\0' && registration->ota_validated &&
            strcmp(registration->version, s_status.version) == 0) {
            ++s_status.completed_nodes;
        } else if (s_status.active) {
            ++s_status.pending_nodes;
        }
    }
    // Keep the legacy fields populated for old upper-computer builds.
    s_status.expected_nodes = s_status.compatible_nodes;
    uint32_t progress_total = 0;
    for (size_t index = 0; index < OTA_REGISTRATION_CAPACITY; ++index) {
        registration_t *registration = &s_registrations[index];
        if (!registration_is_online(registration, now) ||
            !registration_is_compatible(registration)) continue;
        if (registration->ota_validated &&
            strcmp(registration->version, s_status.version) == 0) {
            progress_total += 100U;
        } else if (strcmp(registration->ota_version, s_status.version) == 0) {
            progress_total += registration->ota_progress;
        }
    }
    s_status.mesh_progress = s_status.compatible_nodes == 0 ? 0 :
        (uint8_t)(progress_total / s_status.compatible_nodes);
    if (s_status.started_at_seconds == 0) {
        s_status.elapsed_seconds = 0;
        s_distribution_completed_at_seconds = 0;
    } else if (s_status.compatible_nodes > 0 &&
               s_status.pending_nodes == 0) {
        if (s_distribution_completed_at_seconds == 0) {
            s_distribution_completed_at_seconds = now;
        }
        s_status.elapsed_seconds = s_distribution_completed_at_seconds -
                                   s_status.started_at_seconds;
    } else {
        s_distribution_completed_at_seconds = 0;
        s_status.elapsed_seconds = now - s_status.started_at_seconds;
    }
}

static bool persist_policy_locked(char *error, size_t error_size) {
    cJSON *json = cJSON_CreateObject();
    if (json == NULL) {
        copy_error(error, error_size, "create ota policy failed");
        return false;
    }
    cJSON_AddBoolToObject(json, "active", s_status.active);
    cJSON_AddStringToObject(json, "version", s_status.version);
    cJSON_AddStringToObject(json, "hardware_version",
                           s_status.hardware_version);
    cJSON_AddStringToObject(json, "sha256", s_status.sha256);
    cJSON_AddNumberToObject(json, "image_size", s_status.image_size);
    cJSON_AddNumberToObject(json, "published_at",
                           (double)s_status.published_at);
    char *content = cJSON_PrintUnformatted(json);
    cJSON_Delete(json);
    if (content == NULL) {
        copy_error(error, error_size, "encode ota policy failed");
        return false;
    }
    FILE *file = fopen(OTA_POLICY_TEMP_PATH, "wb");
    bool ok = file != NULL;
    if (ok) {
        size_t length = strlen(content);
        ok = fwrite(content, 1, length, file) == length &&
             fflush(file) == 0 && fsync(fileno(file)) == 0;
        ok = fclose(file) == 0 && ok;
        file = NULL;
    }
    free(content);
    if (!ok) {
        if (file != NULL) fclose(file);
        remove(OTA_POLICY_TEMP_PATH);
        copy_error(error, error_size, "write ota policy failed");
        return false;
    }
    remove(OTA_POLICY_PATH);
    if (rename(OTA_POLICY_TEMP_PATH, OTA_POLICY_PATH) != 0) {
        remove(OTA_POLICY_TEMP_PATH);
        copy_error(error, error_size, "commit ota policy failed");
        return false;
    }
    return true;
}

static bool load_policy_locked(void) {
    FILE *file = fopen(OTA_POLICY_PATH, "rb");
    if (file == NULL) return false;
    if (fseek(file, 0, SEEK_END) != 0) {
        fclose(file);
        return false;
    }
    long length = ftell(file);
    rewind(file);
    if (length <= 0 || length > 2048) {
        fclose(file);
        return false;
    }
    char *content = calloc(1, (size_t)length + 1);
    bool read_ok = content != NULL &&
                   fread(content, 1, (size_t)length, file) == (size_t)length;
    fclose(file);
    cJSON *json = read_ok ? cJSON_Parse(content) : NULL;
    free(content);
    if (json == NULL) return false;

    const cJSON *active = cJSON_GetObjectItemCaseSensitive(json, "active");
    const cJSON *version = cJSON_GetObjectItemCaseSensitive(json, "version");
    const cJSON *hardware =
        cJSON_GetObjectItemCaseSensitive(json, "hardware_version");
    const cJSON *sha256 = cJSON_GetObjectItemCaseSensitive(json, "sha256");
    const cJSON *image_size =
        cJSON_GetObjectItemCaseSensitive(json, "image_size");
    const cJSON *published_at =
        cJSON_GetObjectItemCaseSensitive(json, "published_at");
    bool metadata_ok = cJSON_IsTrue(active) && cJSON_IsString(version) &&
        version->valuestring != NULL &&
        (hardware == NULL || cJSON_IsString(hardware)) &&
        cJSON_IsString(sha256) && sha256->valuestring != NULL &&
        cJSON_IsNumber(image_size);
    if (!metadata_ok) {
        cJSON_Delete(json);
        return false;
    }

    char actual_version[32] = {0};
    char actual_hardware[32] = {0};
    char actual_sha256[65] = {0};
    uint32_t actual_size = 0;
    char validation_error[128] = {0};
    esp_err_t validation = inspect_image(
        OTA_IMAGE_PATH, actual_version, actual_hardware, actual_sha256,
        &actual_size, validation_error, sizeof(validation_error));
    const char *hardware_value = hardware != NULL && hardware->valuestring != NULL
        ? hardware->valuestring : "";
    if (validation != ESP_OK ||
        strcmp(actual_version, version->valuestring) != 0 ||
        (hardware_value[0] != '\0' &&
         strcmp(actual_hardware, hardware_value) != 0) ||
        !strings_equal_ignore_case(actual_sha256, sha256->valuestring) ||
        actual_size != (uint32_t)image_size->valuedouble) {
        snprintf(s_status.error, sizeof(s_status.error),
                 "stored ota policy validation failed: %s",
                 validation_error[0] == '\0' ? "metadata mismatch" :
                 validation_error);
        cJSON_Delete(json);
        return false;
    }

    snprintf(s_status.version, sizeof(s_status.version), "%s", actual_version);
    snprintf(s_status.hardware_version, sizeof(s_status.hardware_version),
             "%s", hardware_value);
    snprintf(s_status.sha256, sizeof(s_status.sha256), "%s", actual_sha256);
    s_status.image_size = actual_size;
    s_status.received_bytes = actual_size;
    s_status.published_at = cJSON_IsNumber(published_at)
        ? (uint64_t)published_at->valuedouble : 0;
    s_status.active = true;
    s_status.finish_reason = -1;
    snprintf(s_status.phase, sizeof(s_status.phase), "published");
    s_next_distribution_at = now_seconds() + OTA_JOIN_DEBOUNCE_SECONDS;
    cJSON_Delete(json);
    return true;
}

static esp_err_t provide_file(
    esp_mesh_lite_lan_ota_file_transfer_param_t *param) {
    if (param == NULL || param->data == NULL || param->data_size == 0 ||
        s_mutex == NULL) return ESP_ERR_INVALID_ARG;
    if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(5000)) != pdTRUE) {
        return ESP_ERR_TIMEOUT;
    }
    esp_err_t result = ESP_OK;
    if (s_provider_file == NULL ||
        (param->fw_version != NULL &&
         strcmp(param->fw_version, s_status.version) != 0)) {
        result = ESP_ERR_INVALID_STATE;
    } else {
        memset(param->data, 0xFF, param->data_size);
        if (param->offset < s_status.image_size) {
            size_t remaining = s_status.image_size - param->offset;
            size_t requested = remaining < param->data_size
                ? remaining : param->data_size;
            if (fseek(s_provider_file, (long)param->offset, SEEK_SET) != 0 ||
                fread(param->data, 1, requested, s_provider_file) != requested) {
                result = ESP_FAIL;
            }
        }
    }
    xSemaphoreGive(s_mutex);
    return result;
}

static esp_err_t reject_file(
    esp_mesh_lite_lan_ota_file_transfer_param_t *param) {
    (void)param;
    return ESP_ERR_NOT_SUPPORTED;
}

static esp_err_t reject_file_done(void) {
    return ESP_ERR_NOT_SUPPORTED;
}

static void ota_event(void *argument, esp_event_base_t base,
                      int32_t event_id, void *event_data) {
    (void)argument;
    (void)base;
    if (s_mutex == NULL ||
        xSemaphoreTake(s_mutex, pdMS_TO_TICKS(100)) != pdTRUE) return;
    if (event_id == ESP_MESH_LITE_EVENT_OTA_FINISH &&
               event_data != NULL) {
        const esp_mesh_lite_event_ota_finish_t *finish = event_data;
        s_status.finish_reason = (int)finish->reason;
        refresh_counts_locked();
        s_next_distribution_at = s_status.pending_nodes > 0
            ? now_seconds() + OTA_RETRY_SECONDS : 0;
        if (finish->reason != ESP_MESH_LITE_EVENT_OTA_SUCCESS) {
            snprintf(s_status.error, sizeof(s_status.error),
                     "mesh ota receiver reported reason %d",
                     (int)finish->reason);
        }
    }
    xSemaphoreGive(s_mutex);
}

bool root_ota_init(void) {
    if (s_mutex != NULL) return true;
    s_mutex = xSemaphoreCreateMutex();
    if (s_mutex == NULL) return false;
    memset(&s_status, 0, sizeof(s_status));
    s_distribution_completed_at_seconds = 0;
    snprintf(s_status.phase, sizeof(s_status.phase), "idle");
    s_status.finish_reason = -1;
    if (root_storage_ready() &&
        mkdir(OTA_DIRECTORY, 0775) != 0 && errno != EEXIST) {
        snprintf(s_status.error, sizeof(s_status.error),
                 "create ota directory failed");
    }
    static esp_mesh_lite_lan_ota_file_transfer_cb_t callbacks = {
        .provide_file_cb = provide_file,
        .get_file_cb = reject_file,
        .get_file_done = reject_file_done,
    };
    esp_mesh_lite_ota_register_file_transfer_cb(&callbacks);
    bool loaded = root_storage_ready() && load_policy_locked();
    if (loaded) {
        ESP_LOGI(TAG, "Loaded cabinet release %s for %s",
                 s_status.version,
                 s_status.hardware_version[0] == '\0'
                     ? "all hardware" : s_status.hardware_version);
    }
    return esp_event_handler_register(ESP_MESH_LITE_EVENT,
                                      ESP_EVENT_ANY_ID, ota_event, NULL) ==
           ESP_OK;
}

esp_err_t root_ota_upload_begin(const char *upload_id, const char *version,
                                const char *hardware_version,
                                const char *sha256, uint32_t image_size,
                                uint64_t published_at,
                                char *error, size_t error_size) {
    if (!root_storage_ready()) {
        copy_error(error, error_size, "sd card not ready");
        return ESP_ERR_INVALID_STATE;
    }
    if (!valid_text(upload_id, ROOT_OTA_UPLOAD_ID_MAX) ||
        !valid_text(version, ROOT_OTA_VERSION_MAX) ||
        (hardware_version != NULL && hardware_version[0] != '\0' &&
         !valid_text(hardware_version, ROOT_OTA_HARDWARE_VERSION_MAX)) ||
        !valid_sha256(sha256) || image_size < OTA_MIN_IMAGE_SIZE ||
        image_size > OTA_MAX_IMAGE_SIZE) {
        copy_error(error, error_size, "invalid ota metadata");
        return ESP_ERR_INVALID_ARG;
    }
    if (mkdir(OTA_DIRECTORY, 0775) != 0 && errno != EEXIST) {
        copy_error(error, error_size, "create ota directory failed");
        return ESP_FAIL;
    }
    if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(5000)) != pdTRUE) {
        copy_error(error, error_size, "ota state busy");
        return ESP_ERR_TIMEOUT;
    }
    if (s_provider_file != NULL) {
        fclose(s_provider_file);
        s_provider_file = NULL;
    }
    if (s_upload_file != NULL) {
        fclose(s_upload_file);
        s_upload_file = NULL;
    }
    remove(OTA_TEMP_PATH);
    s_upload_file = fopen(OTA_TEMP_PATH, "wb");
    if (s_upload_file == NULL) {
        xSemaphoreGive(s_mutex);
        copy_error(error, error_size, "open ota staging file failed");
        return ESP_FAIL;
    }
    memset(&s_status, 0, sizeof(s_status));
    s_distribution_completed_at_seconds = 0;
    snprintf(s_status.phase, sizeof(s_status.phase), "uploading");
    snprintf(s_status.upload_id, sizeof(s_status.upload_id), "%s", upload_id);
    snprintf(s_status.version, sizeof(s_status.version), "%s", version);
    snprintf(s_status.hardware_version, sizeof(s_status.hardware_version),
             "%s", hardware_version == NULL ? "" : hardware_version);
    snprintf(s_status.sha256, sizeof(s_status.sha256), "%s", sha256);
    for (char *cursor = s_status.sha256; *cursor != '\0'; ++cursor) {
        *cursor = (char)tolower((unsigned char)*cursor);
    }
    s_status.image_size = image_size;
    s_status.published_at = published_at;
    s_status.active = false;
    s_status.finish_reason = -1;
    xSemaphoreGive(s_mutex);
    copy_error(error, error_size, "");
    return ESP_OK;
}

esp_err_t root_ota_upload_chunk(const char *upload_id, uint32_t offset,
                                const uint8_t *data, size_t length,
                                uint32_t *next_offset, bool *duplicate,
                                char *error, size_t error_size) {
    if (next_offset != NULL) *next_offset = 0;
    if (duplicate != NULL) *duplicate = false;
    if (data == NULL || length == 0 || length > 3072 ||
        !valid_text(upload_id, ROOT_OTA_UPLOAD_ID_MAX)) {
        copy_error(error, error_size, "invalid ota chunk");
        return ESP_ERR_INVALID_ARG;
    }
    if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(5000)) != pdTRUE) {
        copy_error(error, error_size, "ota state busy");
        return ESP_ERR_TIMEOUT;
    }
    if (strcmp(s_status.phase, "uploading") != 0 ||
        strcmp(s_status.upload_id, upload_id) != 0) {
        xSemaphoreGive(s_mutex);
        copy_error(error, error_size, "ota upload session mismatch");
        return ESP_ERR_INVALID_STATE;
    }
    if (offset < s_status.received_bytes) {
        if (next_offset != NULL) *next_offset = s_status.received_bytes;
        if (duplicate != NULL) *duplicate = true;
        xSemaphoreGive(s_mutex);
        copy_error(error, error_size, "");
        return ESP_OK;
    }
    if (offset != s_status.received_bytes ||
        offset + length > s_status.image_size) {
        if (next_offset != NULL) *next_offset = s_status.received_bytes;
        xSemaphoreGive(s_mutex);
        copy_error(error, error_size, "ota chunk offset mismatch");
        return ESP_ERR_INVALID_STATE;
    }
    bool ok = s_upload_file != NULL &&
              fwrite(data, 1, length, s_upload_file) == length &&
              fflush(s_upload_file) == 0;
    if (!ok) {
        if (s_upload_file != NULL) {
            fclose(s_upload_file);
            s_upload_file = NULL;
        }
        xSemaphoreGive(s_mutex);
        copy_error(error, error_size, "write ota staging file failed");
        return ESP_FAIL;
    }
    s_status.received_bytes += (uint32_t)length;
    if (next_offset != NULL) *next_offset = s_status.received_bytes;
    xSemaphoreGive(s_mutex);
    copy_error(error, error_size, "");
    return ESP_OK;
}

static esp_err_t inspect_image(const char *path, char actual_version[32],
                               char actual_hardware[32],
                               char actual_sha256[65],
                               uint32_t *actual_size,
                               char *error, size_t error_size) {
    struct stat image_stat;
    if (path == NULL || actual_version == NULL || actual_hardware == NULL ||
        actual_sha256 == NULL || actual_size == NULL ||
        stat(path, &image_stat) != 0 ||
        image_stat.st_size < OTA_MIN_IMAGE_SIZE ||
        image_stat.st_size > OTA_MAX_IMAGE_SIZE) {
        copy_error(error, error_size, "stored ota image size is invalid");
        return ESP_ERR_INVALID_SIZE;
    }
    FILE *file = fopen(path, "rb");
    if (file == NULL) {
        copy_error(error, error_size, "open ota image failed");
        return ESP_FAIL;
    }
    esp_image_header_t image_header;
    esp_image_segment_header_t segment_header;
    esp_app_desc_t descriptor;
    bool valid = fread(&image_header, 1, sizeof(image_header), file) ==
                     sizeof(image_header) &&
                 fread(&segment_header, 1, sizeof(segment_header), file) ==
                     sizeof(segment_header) &&
                 fread(&descriptor, 1, sizeof(descriptor), file) ==
                     sizeof(descriptor);
    if (!valid || image_header.magic != ESP_IMAGE_HEADER_MAGIC ||
        image_header.chip_id != ESP_CHIP_ID_ESP32S3 ||
        descriptor.magic_word != ESP_APP_DESC_MAGIC_WORD ||
        strncmp(descriptor.project_name, "cabinet_node_idf",
                sizeof(descriptor.project_name)) != 0) {
        fclose(file);
        copy_error(error, error_size, "image is not cabinet_node_idf for ESP32-S3");
        return ESP_ERR_INVALID_ARG;
    }
    snprintf(actual_version, 32, "%.*s", (int)sizeof(descriptor.version),
             descriptor.version);
    // The ESP-IDF project name is the immutable receiver-side hardware
    // compatibility boundary. Future cabinet hardware must use a new project
    // name and add its mapping here.
    snprintf(actual_hardware, 32, "cabinet-v1");
    rewind(file);
    uint8_t *buffer = malloc(OTA_HASH_BUFFER_SIZE);
    if (buffer == NULL) {
        fclose(file);
        copy_error(error, error_size, "allocate image hash buffer failed");
        return ESP_ERR_NO_MEM;
    }
    psa_hash_operation_t context = PSA_HASH_OPERATION_INIT;
    psa_status_t sha_status = psa_hash_setup(&context, PSA_ALG_SHA_256);
    while (sha_status == PSA_SUCCESS) {
        size_t length = fread(buffer, 1, OTA_HASH_BUFFER_SIZE, file);
        if (length > 0) sha_status = psa_hash_update(&context, buffer, length);
        if (length < OTA_HASH_BUFFER_SIZE) {
            if (ferror(file)) sha_status = PSA_ERROR_GENERIC_ERROR;
            break;
        }
    }
    uint8_t digest[32];
    size_t digest_length = 0;
    if (sha_status == PSA_SUCCESS) {
        sha_status = psa_hash_finish(&context, digest, sizeof(digest),
                                     &digest_length);
    }
    psa_hash_abort(&context);
    free(buffer);
    fclose(file);
    if (sha_status != PSA_SUCCESS || digest_length != sizeof(digest)) {
        copy_error(error, error_size, "calculate image sha256 failed");
        return ESP_FAIL;
    }
    sha256_hex(digest, actual_sha256);
    *actual_size = (uint32_t)image_stat.st_size;
    return ESP_OK;
}

static esp_err_t validate_staged_image(char actual_version[32],
                                       char *error, size_t error_size) {
    char actual_hardware[32] = {0};
    char computed[65] = {0};
    uint32_t actual_size = 0;
    esp_err_t result = inspect_image(OTA_TEMP_PATH, actual_version,
                                     actual_hardware, computed, &actual_size,
                                     error, error_size);
    if (result != ESP_OK) return result;
    if (actual_size != s_status.image_size) {
        copy_error(error, error_size, "image size mismatch");
        return ESP_ERR_INVALID_SIZE;
    }
    if (!strings_equal_ignore_case(computed, s_status.sha256)) {
        copy_error(error, error_size, "image sha256 mismatch");
        return ESP_ERR_INVALID_CRC;
    }
    if (strcmp(actual_version, s_status.version) != 0) {
        copy_error(error, error_size, "image version mismatch");
        return ESP_ERR_INVALID_VERSION;
    }
    if (s_status.hardware_version[0] != '\0' &&
        strcmp(actual_hardware, s_status.hardware_version) != 0) {
        copy_error(error, error_size, "image hardware version mismatch");
        return ESP_ERR_INVALID_VERSION;
    }
    return ESP_OK;
}

esp_err_t root_ota_upload_commit(const char *upload_id,
                                 char *actual_version,
                                 size_t actual_version_size,
                                 char *error, size_t error_size) {
    if (!valid_text(upload_id, ROOT_OTA_UPLOAD_ID_MAX) ||
        xSemaphoreTake(s_mutex, pdMS_TO_TICKS(10000)) != pdTRUE) {
        copy_error(error, error_size, "ota state busy");
        return ESP_ERR_INVALID_ARG;
    }
    if (strcmp(s_status.phase, "uploading") != 0 ||
        strcmp(s_status.upload_id, upload_id) != 0 ||
        s_status.received_bytes != s_status.image_size) {
        xSemaphoreGive(s_mutex);
        copy_error(error, error_size, "ota image upload is incomplete");
        return ESP_ERR_INVALID_STATE;
    }
    bool staging_closed = false;
    if (s_upload_file != NULL) {
        bool flushed = fflush(s_upload_file) == 0;
        bool closed = fclose(s_upload_file) == 0;
        s_upload_file = NULL;
        staging_closed = flushed && closed;
    }
    if (!staging_closed) {
        xSemaphoreGive(s_mutex);
        copy_error(error, error_size, "close ota staging file failed");
        return ESP_FAIL;
    }
    char version[32] = {0};
    esp_err_t result = validate_staged_image(version, error, error_size);
    if (result == ESP_OK) {
        remove(OTA_IMAGE_PATH);
        if (rename(OTA_TEMP_PATH, OTA_IMAGE_PATH) != 0) {
            result = ESP_FAIL;
            copy_error(error, error_size, "commit ota image failed");
        } else {
            s_status.active = true;
            s_status.received_bytes = s_status.image_size;
            snprintf(s_status.phase, sizeof(s_status.phase), "published");
            if (!persist_policy_locked(error, error_size)) {
                result = ESP_FAIL;
            } else {
                s_status.error[0] = '\0';
                s_next_distribution_at = now_seconds();
            }
        }
    }
    if (result != ESP_OK) {
        snprintf(s_status.error, sizeof(s_status.error), "%s",
                 error == NULL ? "image validation failed" : error);
    }
    if (actual_version != NULL && actual_version_size > 0) {
        snprintf(actual_version, actual_version_size, "%s", version);
    }
    xSemaphoreGive(s_mutex);
    return result;
}

static esp_err_t start_distribution(char *error, size_t error_size) {
    if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(5000)) != pdTRUE) {
        copy_error(error, error_size, "ota state busy");
        return ESP_ERR_TIMEOUT;
    }
    if (!s_status.active || strcmp(s_status.phase, "uploading") == 0) {
        xSemaphoreGive(s_mutex);
        copy_error(error, error_size, "ota release is not published");
        return ESP_ERR_INVALID_STATE;
    }
    refresh_counts_locked();
    if (s_status.pending_nodes == 0) {
        snprintf(s_status.phase, sizeof(s_status.phase), "published");
        s_status.mesh_progress = s_status.compatible_nodes > 0 ? 100 : 0;
        s_status.error[0] = '\0';
        s_next_distribution_at = 0;
        if (s_provider_file != NULL) {
            fclose(s_provider_file);
            s_provider_file = NULL;
        }
        xSemaphoreGive(s_mutex);
        copy_error(error, error_size, "");
        return ESP_OK;
    }
    if (s_provider_file == NULL) {
        s_provider_file = fopen(OTA_IMAGE_PATH, "rb");
    }
    if (s_provider_file == NULL) {
        xSemaphoreGive(s_mutex);
        copy_error(error, error_size, "open committed ota image failed");
        return ESP_FAIL;
    }

    size_t target_capacity = s_status.pending_nodes;
    notification_target_t *targets = calloc(target_capacity,
                                             sizeof(*targets));
    if (targets == NULL) {
        xSemaphoreGive(s_mutex);
        copy_error(error, error_size, "allocate ota notification list failed");
        return ESP_ERR_NO_MEM;
    }
    size_t target_count = 0;
    uint32_t now = now_seconds();
    size_t global_active = 0;
    for (size_t index = 0; index < OTA_REGISTRATION_CAPACITY; ++index) {
        registration_t *registration = &s_registrations[index];
        if (strcmp(registration->ota_version, s_status.version) == 0 &&
            phase_is_active(registration->ota_phase) &&
            now - registration->ota_updated_seconds >
                transfer_stale_seconds(registration)) {
            bool notification_timeout =
                strcmp(registration->ota_phase, "notified") == 0;
            snprintf(registration->ota_phase,
                     sizeof(registration->ota_phase), "failed");
            snprintf(registration->ota_error,
                     sizeof(registration->ota_error), "%s",
                     notification_timeout ? "notification timeout" :
                     "progress timeout");
            registration->ota_updated_seconds = now;
        }
        if (registration_is_active_for_target_locked(registration, now)) {
            ++global_active;
        }
    }
    for (size_t index = 0; index < OTA_REGISTRATION_CAPACITY &&
                           target_count < target_capacity &&
                           global_active < OTA_GLOBAL_CONCURRENCY; ++index) {
        registration_t *registration = &s_registrations[index];
        if (!registration_is_online(registration, now) ||
            !registration_is_compatible(registration) ||
            strcmp(registration->version, s_status.version) == 0 ||
            phase_is_active(registration->ota_phase)) {
            continue;
        }
        if (strcmp(registration->ota_version, s_status.version) != 0) {
            snprintf(registration->ota_version,
                     sizeof(registration->ota_version), "%s",
                     s_status.version);
            snprintf(registration->ota_phase,
                     sizeof(registration->ota_phase), "pending");
            registration->ota_progress = 0;
            registration->retry_count = 0;
            registration->ota_error[0] = '\0';
            registration->ota_updated_seconds = now;
        }
        if (!parent_ready_locked(registration, now)) {
            snprintf(registration->ota_phase,
                     sizeof(registration->ota_phase), "waiting_parent");
            continue;
        }
        bool retrying = strcmp(registration->ota_phase, "failed") == 0;
        if (retrying &&
            now - registration->ota_updated_seconds <
                registration_retry_delay_seconds(registration)) continue;
        size_t provider_active = 0;
        for (size_t other = 0; other < OTA_REGISTRATION_CAPACITY; ++other) {
            if (registration_is_active_for_target_locked(
                    &s_registrations[other], now) &&
                same_provider(registration, &s_registrations[other])) {
                ++provider_active;
            }
        }
        if (provider_active >= OTA_PER_PARENT_CONCURRENCY ||
            !parse_cabinet_mac(registration->device_id,
                               targets[target_count].mac)) continue;
        copy_text(targets[target_count].device_id,
                  sizeof(targets[target_count].device_id),
                  registration->device_id);
        targets[target_count].registration_index = index;
        snprintf(registration->ota_phase,
                 sizeof(registration->ota_phase), "notified");
        registration->ota_progress = 0;
        registration->ota_error[0] = '\0';
        registration->ota_updated_seconds = now;
        if (retrying && registration->retry_count < UINT8_MAX) {
            ++registration->retry_count;
        }
        ++target_count;
        ++global_active;
    }
    char version[ROOT_OTA_VERSION_MAX + 1];
    snprintf(version, sizeof(version), "%s", s_status.version);
    uint32_t image_size = s_status.image_size;
    bool register_provider = strcmp(s_provider_version, version) != 0;
    s_status.finish_reason = -1;
    if (s_status.started_at_seconds == 0) {
        s_status.started_at_seconds = now;
    }
    s_status.error[0] = '\0';
    snprintf(s_status.phase, sizeof(s_status.phase), "distributing");
    s_next_distribution_at = now + OTA_SCHEDULER_SECONDS;
    xSemaphoreGive(s_mutex);

    if (register_provider) {
        esp_err_t result = esp_mesh_lite_lan_ota_set_file_name(version);
        if (result != ESP_OK) {
            char provider_error[128];
            snprintf(provider_error, sizeof(provider_error),
                     "register cabinet ota provider failed: %s (0x%x)",
                     esp_err_to_name(result), (unsigned int)result);
            if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(1000)) == pdTRUE) {
                uint32_t failed_at = now_seconds();
                for (size_t index = 0; index < target_count; ++index) {
                    registration_t *registration =
                        &s_registrations[targets[index].registration_index];
                    if (strcmp(registration->device_id,
                               targets[index].device_id) != 0 ||
                        strcmp(registration->ota_version, version) != 0 ||
                        strcmp(registration->ota_phase, "notified") != 0) {
                        continue;
                    }
                    snprintf(registration->ota_phase,
                             sizeof(registration->ota_phase), "failed");
                    snprintf(registration->ota_error,
                             sizeof(registration->ota_error),
                             "provider registration failed");
                    registration->ota_updated_seconds = failed_at;
                }
                snprintf(s_status.error, sizeof(s_status.error), "%s",
                         provider_error);
                s_next_distribution_at = failed_at +
                    retry_delay_seconds(1);
                xSemaphoreGive(s_mutex);
            }
            free(targets);
            copy_error(error, error_size, provider_error);
            return result;
        }
        if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(1000)) == pdTRUE) {
            snprintf(s_provider_version, sizeof(s_provider_version), "%s",
                     version);
            xSemaphoreGive(s_mutex);
        }
    }

    if (target_count == 0) {
        free(targets);
        copy_error(error, error_size, "");
        return ESP_OK;
    }

    size_t sent = 0;
    for (size_t index = 0; index < target_count; ++index) {
        char payload[128];
        int payload_length = snprintf(payload, sizeof(payload),
            "{\"version\":\"%s\",\"image_size\":%lu}", version,
            (unsigned long)image_size);
        uint8_t message[256];
        int message_length = payload_length <= 0 ||
            payload_length >= (int)sizeof(payload) ? -1 :
            cab_app_encode(message, sizeof(message),
                           CAB_CMD_CABINET_OTA_NOTIFY,
                           cab_next_message_id(), 0, 0,
                           targets[index].device_id, "ROOT_OTA",
                           (const uint8_t *)payload,
                           (uint16_t)payload_length, 0);
        bool delivered = message_length > 0 &&
            cab_mesh_send_node(targets[index].mac, message,
                               (size_t)message_length) == ESP_OK;
        if (delivered) {
            ++sent;
        } else if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(1000)) == pdTRUE) {
            registration_t *registration =
                &s_registrations[targets[index].registration_index];
            snprintf(registration->ota_phase,
                     sizeof(registration->ota_phase), "failed");
            snprintf(registration->ota_error,
                     sizeof(registration->ota_error), "notify failed");
            registration->ota_updated_seconds = now_seconds();
            xSemaphoreGive(s_mutex);
        }
    }
    free(targets);
    if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(1000)) == pdTRUE) {
        if (sent < target_count) {
            snprintf(s_status.error, sizeof(s_status.error),
                     "ota notify reached %u of %u pending cabinets",
                     (unsigned)sent, (unsigned)target_count);
        } else {
            s_status.error[0] = '\0';
        }
        xSemaphoreGive(s_mutex);
    }
    if (sent == 0) {
        copy_error(error, error_size, "cabinet ota notifications failed");
        return ESP_FAIL;
    }
    copy_error(error, error_size, "");
    return ESP_OK;
}

esp_err_t root_ota_start(char *error, size_t error_size) {
    return start_distribution(error, error_size);
}

void root_ota_maintain(void) {
    if (s_mutex == NULL) return;
    bool due = false;
    if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(100)) == pdTRUE) {
        refresh_counts_locked();
        uint32_t now = now_seconds();
        if (s_status.active && s_status.pending_nodes == 0 &&
            strcmp(s_status.phase, "uploading") != 0) {
            snprintf(s_status.phase, sizeof(s_status.phase), "published");
            s_next_distribution_at = 0;
            if (s_provider_file != NULL) {
                fclose(s_provider_file);
                s_provider_file = NULL;
            }
        }
        due = s_status.active && s_status.pending_nodes > 0 &&
              strcmp(s_status.phase, "uploading") != 0 &&
              (s_next_distribution_at == 0 || now >= s_next_distribution_at);
        if (due) s_next_distribution_at = now + OTA_SCHEDULER_SECONDS;
        xSemaphoreGive(s_mutex);
    }
    if (!due) return;
    char error[128] = {0};
    esp_err_t result = start_distribution(error, sizeof(error));
    if (result != ESP_OK) {
        ESP_LOGW(TAG, "Automatic cabinet OTA retry deferred: %s", error);
    }
}

void root_ota_note_registration(const char *device_id,
                                const uint8_t *payload, size_t payload_len) {
    if (device_id == NULL || device_id[0] == '\0' || payload == NULL ||
        payload_len == 0 || s_mutex == NULL) return;
    cJSON *json = cJSON_ParseWithLength((const char *)payload, payload_len);
    const cJSON *version_item = json == NULL ? NULL :
        cJSON_GetObjectItemCaseSensitive(json, "firmware_version");
    const cJSON *hardware_item = json == NULL ? NULL :
        cJSON_GetObjectItemCaseSensitive(json, "hardware_version");
    const cJSON *layer_item = json == NULL ? NULL :
        cJSON_GetObjectItemCaseSensitive(json, "mesh_layer");
    const cJSON *ap_mac_item = json == NULL ? NULL :
        cJSON_GetObjectItemCaseSensitive(json, "mesh_ap_mac");
    const cJSON *parent_item = json == NULL ? NULL :
        cJSON_GetObjectItemCaseSensitive(json, "parent_bssid");
    const cJSON *validated_item = json == NULL ? NULL :
        cJSON_GetObjectItemCaseSensitive(json, "ota_validated");
    if (!cJSON_IsString(version_item) || version_item->valuestring == NULL) {
        cJSON_Delete(json);
        return;
    }
    if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(100)) == pdTRUE) {
        size_t target = OTA_REGISTRATION_CAPACITY;
        for (size_t index = 0; index < OTA_REGISTRATION_CAPACITY; ++index) {
            if (strcmp(s_registrations[index].device_id, device_id) == 0) {
                target = index;
                break;
            }
            if (target == OTA_REGISTRATION_CAPACITY &&
                s_registrations[index].device_id[0] == '\0') target = index;
        }
        if (target < OTA_REGISTRATION_CAPACITY) {
            registration_t *registration = &s_registrations[target];
            bool previously_reported_target = s_status.version[0] != '\0' &&
                strcmp(registration->version, s_status.version) == 0;
            snprintf(registration->device_id,
                     sizeof(registration->device_id), "%s", device_id);
            snprintf(registration->version,
                     sizeof(registration->version), "%s",
                     version_item->valuestring);
            snprintf(registration->hardware_version,
                     sizeof(registration->hardware_version), "%s",
                     cJSON_IsString(hardware_item) &&
                          hardware_item->valuestring != NULL
                          ? hardware_item->valuestring : "");
            registration->mesh_layer = cJSON_IsNumber(layer_item) &&
                layer_item->valuedouble >= 0 && layer_item->valuedouble < 256
                ? (uint8_t)layer_item->valuedouble : 0;
            registration->has_ap_mac = cJSON_IsString(ap_mac_item) &&
                parse_mac_text(ap_mac_item->valuestring,
                               registration->ap_mac);
            registration->has_parent_bssid = cJSON_IsString(parent_item) &&
                parse_mac_text(parent_item->valuestring,
                               registration->parent_bssid);
            registration->ota_validated = !cJSON_IsBool(validated_item) ||
                cJSON_IsTrue(validated_item);
            registration->last_seen_seconds = now_seconds();
            if (s_status.version[0] != '\0' &&
                strcmp(registration->version, s_status.version) == 0) {
                snprintf(registration->ota_version,
                         sizeof(registration->ota_version), "%s",
                         s_status.version);
                snprintf(registration->ota_phase,
                         sizeof(registration->ota_phase), "%s",
                         registration->ota_validated ? "completed" :
                         "validating");
                registration->ota_progress = 100;
                registration->ota_error[0] = '\0';
                registration->ota_updated_seconds = now_seconds();
            } else if (s_status.active && previously_reported_target) {
                snprintf(registration->ota_version,
                         sizeof(registration->ota_version), "%s",
                         s_status.version);
                snprintf(registration->ota_phase,
                         sizeof(registration->ota_phase), "failed");
                snprintf(registration->ota_error,
                         sizeof(registration->ota_error),
                         "firmware rollback detected");
                registration->ota_progress = 0;
                registration->ota_updated_seconds = now_seconds();
            } else if (s_status.active &&
                       strcmp(registration->ota_version,
                              s_status.version) != 0) {
                snprintf(registration->ota_version,
                         sizeof(registration->ota_version), "%s",
                         s_status.version);
                snprintf(registration->ota_phase,
                         sizeof(registration->ota_phase), "pending");
                registration->ota_progress = 0;
                registration->retry_count = 0;
                registration->ota_error[0] = '\0';
                registration->ota_updated_seconds = now_seconds();
            }
        }
        refresh_counts_locked();
        if (s_status.active && s_status.pending_nodes > 0) {
            uint32_t due = now_seconds() + OTA_JOIN_DEBOUNCE_SECONDS;
            if (s_next_distribution_at == 0 || due < s_next_distribution_at) {
                s_next_distribution_at = due;
            }
        }
        xSemaphoreGive(s_mutex);
    }
    cJSON_Delete(json);
}

void root_ota_note_progress(const char *device_id,
                            const uint8_t *payload, size_t payload_len) {
    if (device_id == NULL || device_id[0] == '\0' || payload == NULL ||
        payload_len == 0 || s_mutex == NULL) return;
    cJSON *json = cJSON_ParseWithLength((const char *)payload, payload_len);
    const cJSON *version = json == NULL ? NULL :
        cJSON_GetObjectItemCaseSensitive(json, "version");
    const cJSON *phase = json == NULL ? NULL :
        cJSON_GetObjectItemCaseSensitive(json, "phase");
    const cJSON *progress = json == NULL ? NULL :
        cJSON_GetObjectItemCaseSensitive(json, "progress");
    const cJSON *error = json == NULL ? NULL :
        cJSON_GetObjectItemCaseSensitive(json, "error");
    if (!cJSON_IsString(version) || !cJSON_IsString(phase) ||
        !cJSON_IsNumber(progress)) {
        cJSON_Delete(json);
        return;
    }
    if (xSemaphoreTake(s_mutex, pdMS_TO_TICKS(100)) == pdTRUE) {
        for (size_t index = 0; index < OTA_REGISTRATION_CAPACITY; ++index) {
            registration_t *registration = &s_registrations[index];
            if (strcmp(registration->device_id, device_id) != 0) continue;
            if (s_status.version[0] != '\0' &&
                strcmp(version->valuestring, s_status.version) != 0) break;
            snprintf(registration->ota_version,
                     sizeof(registration->ota_version), "%s",
                     version->valuestring);
            snprintf(registration->ota_phase,
                     sizeof(registration->ota_phase), "%s",
                     phase->valuestring);
            snprintf(registration->ota_error,
                     sizeof(registration->ota_error), "%s",
                     cJSON_IsString(error) && error->valuestring != NULL
                         ? error->valuestring : "");
            double value = progress->valuedouble;
            registration->ota_progress = value <= 0 ? 0 :
                value >= 100 ? 100 : (uint8_t)value;
            if (strcmp(registration->ota_phase, "complete") == 0 ||
                strcmp(registration->ota_phase, "completed") == 0) {
                registration->ota_validated = true;
            } else if (strcmp(registration->ota_phase, "validating") == 0) {
                registration->ota_validated = false;
            }
            registration->ota_updated_seconds = now_seconds();
            if (strcmp(registration->ota_phase, "failed") == 0) {
                uint32_t due = now_seconds() +
                    registration_retry_delay_seconds(registration);
                if (s_next_distribution_at == 0 ||
                    due < s_next_distribution_at) s_next_distribution_at = due;
            }
            break;
        }
        refresh_counts_locked();
        xSemaphoreGive(s_mutex);
    }
    cJSON_Delete(json);
}

void root_ota_get_status(root_ota_status_t *status) {
    if (status == NULL) return;
    memset(status, 0, sizeof(*status));
    if (s_mutex == NULL ||
        xSemaphoreTake(s_mutex, pdMS_TO_TICKS(1000)) != pdTRUE) {
        snprintf(status->phase, sizeof(status->phase), "unavailable");
        return;
    }
    refresh_counts_locked();
    *status = s_status;
    xSemaphoreGive(s_mutex);
}

size_t root_ota_get_nodes(size_t offset, size_t limit,
                          root_ota_node_status_t *nodes, size_t *total) {
    if (total != NULL) *total = 0;
    if (nodes == NULL || limit == 0 || s_mutex == NULL ||
        xSemaphoreTake(s_mutex, pdMS_TO_TICKS(1000)) != pdTRUE) return 0;
    uint32_t now = now_seconds();
    size_t known = 0;
    for (size_t index = 0; index < OTA_REGISTRATION_CAPACITY; ++index) {
        if (s_registrations[index].device_id[0] != '\0') ++known;
    }
    if (total != NULL) *total = known;
    size_t logical_index = 0;
    size_t copied = 0;
    for (size_t index = 0; index < OTA_REGISTRATION_CAPACITY &&
                           copied < limit; ++index) {
        registration_t *registration = &s_registrations[index];
        if (registration->device_id[0] == '\0') continue;
        if (logical_index++ < offset) continue;
        root_ota_node_status_t *node = &nodes[copied++];
        memset(node, 0, sizeof(*node));
        copy_text(node->device_id, sizeof(node->device_id),
                  registration->device_id);
        copy_text(node->version, sizeof(node->version),
                  registration->version);
        node->mesh_layer = registration->mesh_layer;
        node->online = registration_is_online(registration, now);
        node->compatible = registration_is_compatible(registration);
        node->retry_count = registration->retry_count;
        node->updated_ago_seconds = registration->ota_updated_seconds == 0
            ? now - registration->last_seen_seconds
            : now - registration->ota_updated_seconds;
        if (registration->mesh_layer <= ROOT + 1) {
            copy_text(node->parent_device_id,
                      sizeof(node->parent_device_id), "ROOT");
        } else if (registration->has_parent_bssid) {
            registration_t *parent = find_by_ap_mac_locked(
                registration->parent_bssid);
            if (parent != NULL) {
                copy_text(node->parent_device_id,
                          sizeof(node->parent_device_id), parent->device_id);
            }
        }
        if (!node->online) {
            copy_text(node->phase, sizeof(node->phase), "offline");
        } else if (!node->compatible) {
            copy_text(node->phase, sizeof(node->phase), "incompatible");
        } else if (s_status.version[0] != '\0' &&
                   registration->ota_validated &&
                   strcmp(registration->version, s_status.version) == 0) {
            copy_text(node->phase, sizeof(node->phase), "completed");
            node->progress = 100;
        } else if (!s_status.active) {
            copy_text(node->phase, sizeof(node->phase), "idle");
        } else if (strcmp(registration->ota_version,
                          s_status.version) == 0 &&
                   registration->ota_phase[0] != '\0') {
            copy_text(node->phase, sizeof(node->phase),
                      registration->ota_phase);
            node->progress = registration->ota_progress;
            copy_text(node->error, sizeof(node->error),
                      registration->ota_error);
        } else {
            copy_text(node->phase, sizeof(node->phase),
                      parent_ready_locked(registration, now)
                          ? "pending" : "waiting_parent");
        }
    }
    xSemaphoreGive(s_mutex);
    return copied;
}
