#include "cabinet_storage.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>

#include "esp_timer.h"
#include "nvs.h"

#define STORAGE_NAMESPACE "esp32_cfg"
#define PERMISSION_MAGIC 0xA5A55A5AU
#define PERMISSION_RECORD_SIZE 16
#define PERMISSION_HEADER_SIZE 16

static nvs_handle_t s_nvs;
static bool s_initialized;
static bool s_permission_lost;
static cab_permission_t s_permissions[CAB_STORAGE_PERMISSION_MAX];
static size_t s_permission_count;
static uint32_t s_permission_version;
static uint32_t s_time_base;
static int64_t s_time_base_us;
static bool s_time_synced;

typedef struct {
    char device_id[25];
    char device_name[33];
    uint8_t work_mode;
    uint8_t mesh_channel;
    uint8_t fingerprint_count;
    uint32_t permission_version;
} legacy_device_config_t;

static bool maintenance_pin_valid(const char *pin) {
    for (size_t index = 0; index < CAB_MAINTENANCE_PIN_LENGTH; ++index) {
        if (pin[index] < '1' || pin[index] > '4') return false;
    }
    return pin[CAB_MAINTENANCE_PIN_LENGTH] == '\0';
}

static uint16_t read_be16(const uint8_t *value) {
    return ((uint16_t)value[0] << 8) | value[1];
}

static uint32_t read_be32(const uint8_t *value) {
    return ((uint32_t)value[0] << 24) | ((uint32_t)value[1] << 16) |
           ((uint32_t)value[2] << 8) | value[3];
}

static void write_be16(uint8_t *output, uint16_t value) {
    output[0] = (uint8_t)(value >> 8);
    output[1] = (uint8_t)value;
}

static void write_be32(uint8_t *output, uint32_t value) {
    output[0] = (uint8_t)(value >> 24);
    output[1] = (uint8_t)(value >> 16);
    output[2] = (uint8_t)(value >> 8);
    output[3] = (uint8_t)value;
}

static uint32_t crc32(const uint8_t *data, size_t length) {
    uint32_t crc = 0xFFFFFFFFU;
    for (size_t index = 0; index < length; ++index) {
        crc ^= data[index];
        for (int bit = 0; bit < 8; ++bit) {
            crc = (crc & 1U) ? (crc >> 1) ^ 0xEDB88320U : crc >> 1;
        }
    }
    return crc ^ 0xFFFFFFFFU;
}

uint32_t cab_storage_user_id_to_number(const char *user_id) {
    if (user_id == NULL) return 0;
    const char *digits = (user_id[0] == 'U' || user_id[0] == 'u')
        ? user_id + 1 : user_id;
    char *end = NULL;
    unsigned long parsed = strtoul(digits, &end, 10);
    if (digits[0] != '\0' && end != NULL && *end == '\0') {
        return (uint32_t)parsed;
    }
    uint32_t hash = 2166136261U;
    for (const uint8_t *p = (const uint8_t *)user_id; *p != 0; ++p) {
        hash = (hash ^ *p) * 16777619U;
    }
    return hash;
}

void cab_storage_number_to_user_id(uint32_t number, char *output,
                                   size_t output_size) {
    if (output == NULL || output_size == 0) return;
    snprintf(output, output_size, "U%03lu", (unsigned long)number);
}

static void serialize_permission(const cab_permission_t *permission,
                                 uint8_t output[PERMISSION_RECORD_SIZE]) {
    write_be16(output, (uint16_t)permission->fingerprint_id);
    write_be32(output + 2, permission->user_id_number);
    output[6] = permission->lock_mask & 0x0F;
    output[7] = (uint8_t)permission->role;
    write_be32(output + 8, permission->expire_days);
    write_be16(output + 12, (uint16_t)permission->local_fingerprint_id);
    output[14] = permission->is_backup ? 1 : 0;
    output[15] = 0;
}

static void deserialize_permission(const uint8_t input[PERMISSION_RECORD_SIZE],
                                   cab_permission_t *permission) {
    memset(permission, 0, sizeof(*permission));
    permission->fingerprint_id = (int16_t)read_be16(input);
    permission->user_id_number = read_be32(input + 2);
    permission->lock_mask = input[6] & 0x0F;
    permission->role = (cab_role_t)input[7];
    permission->expire_days = read_be32(input + 8);
    permission->local_fingerprint_id = (int16_t)read_be16(input + 12);
    permission->is_backup = (input[14] & 1) != 0;
    cab_storage_number_to_user_id(permission->user_id_number,
                                  permission->user_id,
                                  sizeof(permission->user_id));
}

static bool decode_permission_blob(const uint8_t *blob, size_t length,
                                   cab_permission_t *permissions,
                                   size_t *count, uint32_t *version) {
    if (length < PERMISSION_HEADER_SIZE ||
        read_be32(blob + 12) != PERMISSION_MAGIC) return false;
    size_t record_count = read_be16(blob + 4);
    if (record_count > CAB_STORAGE_PERMISSION_MAX ||
        length != PERMISSION_HEADER_SIZE + record_count * PERMISSION_RECORD_SIZE) {
        return false;
    }
    size_t crc_length = 8 + record_count * PERMISSION_RECORD_SIZE;
    uint8_t *crc_data = malloc(crc_length);
    if (crc_data == NULL) return false;
    memcpy(crc_data, blob, 8);
    memcpy(crc_data + 8, blob + PERMISSION_HEADER_SIZE,
           record_count * PERMISSION_RECORD_SIZE);
    uint32_t actual_crc = crc32(crc_data, crc_length);
    free(crc_data);
    if (actual_crc != read_be32(blob + 8)) return false;
    for (size_t index = 0; index < record_count; ++index) {
        deserialize_permission(blob + PERMISSION_HEADER_SIZE +
                               index * PERMISSION_RECORD_SIZE,
                               &permissions[index]);
    }
    *count = record_count;
    *version = read_be32(blob);
    return true;
}

static bool load_permission_key(const char *key, bool *present,
                                cab_permission_t *permissions,
                                size_t *count, uint32_t *version) {
    size_t length = 0;
    esp_err_t error = nvs_get_blob(s_nvs, key, NULL, &length);
    *present = error == ESP_OK;
    if (error != ESP_OK || length == 0 ||
        length > PERMISSION_HEADER_SIZE +
                 CAB_STORAGE_PERMISSION_MAX * PERMISSION_RECORD_SIZE) {
        return false;
    }
    uint8_t *blob = malloc(length);
    if (blob == NULL) return false;
    error = nvs_get_blob(s_nvs, key, blob, &length);
    bool valid = error == ESP_OK && decode_permission_blob(
        blob, length, permissions, count, version);
    free(blob);
    return valid;
}

static bool persist_permissions(void) {
    size_t length = PERMISSION_HEADER_SIZE +
                    s_permission_count * PERMISSION_RECORD_SIZE;
    uint8_t *blob = calloc(1, length);
    if (blob == NULL) return false;
    write_be32(blob, s_permission_version);
    write_be16(blob + 4, (uint16_t)s_permission_count);
    for (size_t index = 0; index < s_permission_count; ++index) {
        serialize_permission(&s_permissions[index], blob +
                             PERMISSION_HEADER_SIZE +
                             index * PERMISSION_RECORD_SIZE);
    }
    size_t crc_length = 8 + s_permission_count * PERMISSION_RECORD_SIZE;
    uint8_t *crc_data = malloc(crc_length);
    if (crc_data == NULL) {
        free(blob);
        return false;
    }
    memcpy(crc_data, blob, 8);
    memcpy(crc_data + 8, blob + PERMISSION_HEADER_SIZE,
           s_permission_count * PERMISSION_RECORD_SIZE);
    write_be32(blob + 8, crc32(crc_data, crc_length));
    free(crc_data);
    write_be32(blob + 12, PERMISSION_MAGIC);
    bool ok = nvs_set_blob(s_nvs, "perm_b", blob, length) == ESP_OK &&
              nvs_commit(s_nvs) == ESP_OK &&
              nvs_set_blob(s_nvs, "perm_a", blob, length) == ESP_OK &&
              nvs_commit(s_nvs) == ESP_OK;
    free(blob);
    if (ok) s_permission_lost = false;
    return ok;
}

bool cab_storage_init(const char *default_device_id, bool is_root) {
    if (s_initialized) return true;
    if (nvs_open(STORAGE_NAMESPACE, NVS_READWRITE, &s_nvs) != ESP_OK) {
        return false;
    }
    bool present_a = false;
    bool present_b = false;
    if (!load_permission_key("perm_a", &present_a, s_permissions,
                             &s_permission_count, &s_permission_version) &&
        !load_permission_key("perm_b", &present_b, s_permissions,
                             &s_permission_count, &s_permission_version)) {
        s_permission_count = 0;
        s_permission_version = 0;
        s_permission_lost = present_a || present_b;
    }
    s_initialized = true;
    cab_device_config_t config;
    if (!cab_storage_load_config(&config)) {
        memset(&config, 0, sizeof(config));
        snprintf(config.device_id, sizeof(config.device_id), "%s",
                 default_device_id == NULL ? "CABINET" : default_device_id);
        size_t legacy_name_length = sizeof(config.device_name);
        if (nvs_get_str(s_nvs, "device_name", config.device_name,
                        &legacy_name_length) != ESP_OK) {
            snprintf(config.device_name, sizeof(config.device_name), "%s",
                     is_root ? "ESP-IDF Root" : "ESP-IDF Cabinet");
        }
        config.work_mode = 0;
        config.mesh_channel = 6;
        nvs_get_u8(s_nvs, "fp_count", &config.fingerprint_count);
        nvs_get_u32(s_nvs, "perm_ver", &config.permission_version);
        snprintf(config.maintenance_pin, sizeof(config.maintenance_pin), "%s",
                 CAB_DEFAULT_MAINTENANCE_PIN);
        config.maintenance_config_version = 1;
        cab_storage_save_config(&config);
    }
    if (!maintenance_pin_valid(config.maintenance_pin)) {
        snprintf(config.maintenance_pin, sizeof(config.maintenance_pin), "%s",
                 CAB_DEFAULT_MAINTENANCE_PIN);
        if (config.maintenance_config_version == 0) {
            config.maintenance_config_version = 1;
        }
        cab_storage_save_config(&config);
    }
    return true;
}

bool cab_storage_load_config(cab_device_config_t *config) {
    if (!s_initialized || config == NULL) return false;
    size_t length = 0;
    if (nvs_get_blob(s_nvs, "config", NULL, &length) != ESP_OK) return false;
    if (length == sizeof(*config)) {
        return nvs_get_blob(s_nvs, "config", config, &length) == ESP_OK;
    }
    if (length != sizeof(legacy_device_config_t)) return false;

    legacy_device_config_t legacy;
    if (nvs_get_blob(s_nvs, "config", &legacy, &length) != ESP_OK) {
        return false;
    }
    memset(config, 0, sizeof(*config));
    memcpy(config->device_id, legacy.device_id, sizeof(legacy.device_id));
    memcpy(config->device_name, legacy.device_name, sizeof(legacy.device_name));
    config->work_mode = legacy.work_mode;
    config->mesh_channel = legacy.mesh_channel;
    config->fingerprint_count = legacy.fingerprint_count;
    config->permission_version = legacy.permission_version;
    snprintf(config->maintenance_pin, sizeof(config->maintenance_pin), "%s",
             CAB_DEFAULT_MAINTENANCE_PIN);
    config->maintenance_config_version = 1;
    return cab_storage_save_config(config);
}

bool cab_storage_save_config(const cab_device_config_t *config) {
    if (!s_initialized || config == NULL) return false;
    return nvs_set_blob(s_nvs, "config", config, sizeof(*config)) == ESP_OK &&
           nvs_commit(s_nvs) == ESP_OK;
}

size_t cab_storage_permission_count(void) { return s_permission_count; }
uint32_t cab_storage_permission_version(void) { return s_permission_version; }
bool cab_storage_permissions_lost(void) { return s_permission_lost; }

const cab_permission_t *cab_storage_permission_at(size_t index) {
    return index < s_permission_count ? &s_permissions[index] : NULL;
}

bool cab_storage_find_by_local_fingerprint(int fingerprint_id,
                                           cab_permission_t *permission) {
    for (size_t index = 0; index < s_permission_count; ++index) {
        if (s_permissions[index].local_fingerprint_id == fingerprint_id) {
            if (permission != NULL) *permission = s_permissions[index];
            return true;
        }
    }
    return false;
}

bool cab_storage_find_primary_by_user(const char *user_id,
                                      cab_permission_t *permission) {
    if (user_id == NULL) return false;
    uint32_t number = cab_storage_user_id_to_number(user_id);
    for (size_t index = 0; index < s_permission_count; ++index) {
        if (!s_permissions[index].is_backup &&
            s_permissions[index].user_id_number == number) {
            if (permission != NULL) *permission = s_permissions[index];
            return true;
        }
    }
    return false;
}

bool cab_storage_replace_permissions(const cab_permission_t *permissions,
                                     size_t count, uint32_t version) {
    if (count > CAB_STORAGE_PERMISSION_MAX ||
        (count > 0 && permissions == NULL)) return false;
    if (count > 0) memcpy(s_permissions, permissions,
                          count * sizeof(*permissions));
    s_permission_count = count;
    s_permission_version = version;
    return persist_permissions();
}

bool cab_storage_save_permission(const cab_permission_t *permission,
                                 uint32_t version) {
    if (permission == NULL) return false;
    size_t target = s_permission_count;
    for (size_t index = 0; index < s_permission_count; ++index) {
        if (s_permissions[index].local_fingerprint_id ==
            permission->local_fingerprint_id) {
            target = index;
            break;
        }
    }
    if (target == s_permission_count) {
        if (s_permission_count >= CAB_STORAGE_PERMISSION_MAX) return false;
        ++s_permission_count;
    }
    s_permissions[target] = *permission;
    if (version != 0) s_permission_version = version;
    return persist_permissions();
}

bool cab_storage_delete_permission(int fingerprint_id) {
    for (size_t index = 0; index < s_permission_count; ++index) {
        if (s_permissions[index].fingerprint_id == fingerprint_id ||
            s_permissions[index].local_fingerprint_id == fingerprint_id) {
            memmove(&s_permissions[index], &s_permissions[index + 1],
                    (s_permission_count - index - 1) * sizeof(s_permissions[0]));
            --s_permission_count;
            return persist_permissions();
        }
    }
    return true;
}

bool cab_storage_delete_user(const char *user_id, uint32_t version) {
    if (user_id == NULL || user_id[0] == '\0') return false;
    uint32_t number = cab_storage_user_id_to_number(user_id);
    size_t output = 0;
    for (size_t index = 0; index < s_permission_count; ++index) {
        if (s_permissions[index].user_id_number != number) {
            if (output != index) s_permissions[output] = s_permissions[index];
            ++output;
        }
    }
    s_permission_count = output;
    if (version != 0) s_permission_version = version;
    return persist_permissions();
}

bool cab_storage_clear_permissions(void) {
    s_permission_count = 0;
    s_permission_version = 0;
    return persist_permissions();
}

int cab_storage_allocate_fingerprint_id(void) {
    for (int id = 1; id < CAB_STORAGE_PERMISSION_MAX; ++id) {
        if (!cab_storage_find_by_local_fingerprint(id, NULL)) return id;
    }
    return -1;
}

static bool leap_year(int year) {
    return year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
}

uint32_t cab_storage_date_to_days(const char *date) {
    int year = 0, month = 0, day = 0;
    if (date == NULL || sscanf(date, "%d-%d-%d", &year, &month, &day) != 3 ||
        year < 2000 || month < 1 || month > 12 || day < 1 || day > 31) {
        return UINT32_MAX;
    }
    static const uint8_t days_by_month[12] =
        {31,28,31,30,31,30,31,31,30,31,30,31};
    uint32_t days = 0;
    for (int value = 2000; value < year; ++value) {
        days += leap_year(value) ? 366 : 365;
    }
    for (int value = 1; value < month; ++value) {
        days += days_by_month[value - 1];
        if (value == 2 && leap_year(year)) ++days;
    }
    return days + (uint32_t)day - 1;
}

void cab_storage_set_unix_time(uint32_t timestamp) {
    s_time_base = timestamp;
    s_time_base_us = esp_timer_get_time();
    s_time_synced = timestamp != 0;
}

uint32_t cab_storage_unix_time(void) {
    if (!s_time_synced) return (uint32_t)time(NULL);
    return s_time_base + (uint32_t)((esp_timer_get_time() - s_time_base_us) /
                                    1000000);
}

bool cab_storage_time_is_synced(void) { return s_time_synced; }
