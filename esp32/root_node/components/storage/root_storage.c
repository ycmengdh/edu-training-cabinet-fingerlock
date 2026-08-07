#include "root_storage.h"

#include <ctype.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

#include "cJSON.h"
#include "driver/sdmmc_host.h"
#include "esp_vfs_fat.h"
#include "psa/crypto.h"
#include "sdmmc_cmd.h"

#define SD_MOUNT_POINT "/sdcard"
#define SD_DATA_DIR "/sdcard/data"
#define SD_FP_DIR "/sdcard/data/fingerprints"
#define SD_LOG_DIR "/sdcard/data/logs"
#define SD_SNAPSHOT_PATH "/sdcard/data/business.snapshot.gz"
#define SD_SNAPSHOT_UPLOAD_PATH "/sdcard/data/business.snapshot.gz.upload"
#define SD_SNAPSHOT_BACKUP_PATH "/sdcard/data/business.snapshot.gz.bak"
#define SNAPSHOT_MAX_COMPRESSED_SIZE (32U * 1024U * 1024U)
#define SNAPSHOT_MAX_RAW_SIZE (128U * 1024U * 1024U)
#define SNAPSHOT_HASH_BUFFER_SIZE 8192

typedef struct {
    bool active;
    bool committed;
    char table[32];
    char upload_id[41];
    char path[160];
    uint32_t part_total;
    uint32_t next_part;
    uint32_t total_bytes;
    uint32_t written_bytes;
} upload_state_t;

static bool s_ready;
static sdmmc_card_t *s_card;
static char s_error[160];
static root_sd_versions_t s_versions;
static bool s_versions_valid;
static upload_state_t s_upload;

typedef struct {
    bool active;
    FILE *file;
    uint8_t header[ROOT_SNAPSHOT_HEADER_SIZE];
    uint8_t upload_id[16];
    uint32_t compressed_size;
    uint32_t next_offset;
} snapshot_upload_state_t;

static snapshot_upload_state_t s_snapshot_upload;

typedef struct {
    FILE *file;
    uint32_t total_size;
    uint32_t next_offset;
} snapshot_download_state_t;

static snapshot_download_state_t s_snapshot_download;

static void set_error(const char *stage, esp_err_t error) {
    snprintf(s_error, sizeof(s_error), "%s: %s", stage,
             esp_err_to_name(error));
}

static bool path_exists(const char *path) {
    struct stat info;
    return stat(path, &info) == 0;
}

static bool ensure_directory(const char *path) {
    return path_exists(path) || mkdir(path, 0775) == 0 || errno == EEXIST;
}

bool root_storage_table_allowed(const char *table) {
    static const char *allowed[] = {
        "users", "classes", "permissions", "role_permissions",
        "devices", "fingerprints", "logs", "version"
    };
    if (table == NULL) return false;
    for (size_t index = 0; index < sizeof(allowed) / sizeof(allowed[0]);
         ++index) {
        if (strcmp(table, allowed[index]) == 0) return true;
    }
    return false;
}

static bool table_path(const char *table, char *output, size_t output_size) {
    if (!root_storage_table_allowed(table)) return false;
    return snprintf(output, output_size, "%s/%s.json", SD_DATA_DIR, table) > 0;
}

static bool atomic_write(const char *path, const uint8_t *data,
                         size_t length) {
    char temporary[160];
    char backup[160];
    if (snprintf(temporary, sizeof(temporary), "%s.tmp", path) >=
            (int)sizeof(temporary) ||
        snprintf(backup, sizeof(backup), "%s.bak", path) >=
            (int)sizeof(backup)) return false;
    FILE *file = fopen(temporary, "wb");
    if (file == NULL) return false;
    bool ok = fwrite(data, 1, length, file) == length && fflush(file) == 0 &&
              fsync(fileno(file)) == 0;
    fclose(file);
    if (!ok) {
        remove(temporary);
        return false;
    }
    remove(backup);
    bool had_original = path_exists(path);
    if (had_original && rename(path, backup) != 0) {
        remove(temporary);
        return false;
    }
    if (rename(temporary, path) != 0) {
        if (had_original) rename(backup, path);
        remove(temporary);
        return false;
    }
    return true;
}

static uint16_t read_le16(const uint8_t *value) {
    return (uint16_t)value[0] | ((uint16_t)value[1] << 8);
}

static uint32_t read_le32(const uint8_t *value) {
    return (uint32_t)value[0] | ((uint32_t)value[1] << 8) |
           ((uint32_t)value[2] << 16) | ((uint32_t)value[3] << 24);
}

static bool snapshot_header_valid(const uint8_t *header) {
    if (header == NULL || memcmp(header, "BSNP", 4) != 0 ||
        header[4] != 1 || header[5] != 1 ||
        read_le16(header + 6) != ROOT_SNAPSHOT_HEADER_SIZE) return false;
    uint32_t compressed_size = read_le32(header + 12);
    uint32_t raw_size = read_le32(header + 16);
    if (compressed_size == 0 || compressed_size > SNAPSHOT_MAX_COMPRESSED_SIZE ||
        raw_size == 0 || raw_size > SNAPSHOT_MAX_RAW_SIZE) return false;
    uint8_t nonzero = 0;
    for (size_t index = 0; index < 16; ++index) nonzero |= header[24 + index];
    return nonzero != 0;
}

static bool snapshot_header_identity_equal(const uint8_t *left,
                                           const uint8_t *right) {
    return snapshot_header_valid(left) && snapshot_header_valid(right) &&
           read_le32(left + 12) == read_le32(right + 12) &&
           read_le32(left + 16) == read_le32(right + 16) &&
           memcmp(left + 24, right + 24, 80) == 0;
}

static void snapshot_upload_close(void) {
    if (s_snapshot_upload.file != NULL) fclose(s_snapshot_upload.file);
    s_snapshot_upload.file = NULL;
    s_snapshot_upload.active = false;
}

static void snapshot_download_close(void) {
    if (s_snapshot_download.file != NULL) fclose(s_snapshot_download.file);
    memset(&s_snapshot_download, 0, sizeof(s_snapshot_download));
}

static bool read_snapshot_header_at(const char *path, uint8_t *header,
                                    uint32_t *file_size) {
    FILE *file = fopen(path, "rb");
    if (file == NULL || fseek(file, 0, SEEK_END) != 0) {
        if (file != NULL) fclose(file);
        return false;
    }
    long size = ftell(file);
    if (size < ROOT_SNAPSHOT_HEADER_SIZE || size > UINT32_MAX ||
        fseek(file, 0, SEEK_SET) != 0 ||
        fread(header, 1, ROOT_SNAPSHOT_HEADER_SIZE, file) !=
            ROOT_SNAPSHOT_HEADER_SIZE) {
        fclose(file);
        return false;
    }
    fclose(file);
    if (!snapshot_header_valid(header) ||
        (uint32_t)size != ROOT_SNAPSHOT_HEADER_SIZE + read_le32(header + 12))
        return false;
    if (file_size != NULL) *file_size = (uint32_t)size;
    return true;
}

static bool initialize_file(const char *table, const char *json) {
    char path[128];
    return table_path(table, path, sizeof(path)) &&
           (path_exists(path) || atomic_write(path, (const uint8_t *)json,
                                              strlen(json)));
}

bool root_storage_init(void) {
    if (s_ready) return true;
    const int frequencies[] = {20000, 4000, 1000};
    esp_err_t last_error = ESP_FAIL;
    for (size_t attempt = 0;
         attempt < sizeof(frequencies) / sizeof(frequencies[0]); ++attempt) {
        sdmmc_host_t host = SDMMC_HOST_DEFAULT();
        host.max_freq_khz = frequencies[attempt];
        sdmmc_slot_config_t slot = SDMMC_SLOT_CONFIG_DEFAULT();
        slot.width = 1;
        slot.clk = GPIO_NUM_17;
        slot.cmd = GPIO_NUM_18;
        slot.d0 = GPIO_NUM_16;
        slot.d1 = GPIO_NUM_NC;
        slot.d2 = GPIO_NUM_NC;
        slot.d3 = GPIO_NUM_NC;
        slot.flags |= SDMMC_SLOT_FLAG_INTERNAL_PULLUP;
        esp_vfs_fat_sdmmc_mount_config_t mount = {
            .format_if_mount_failed = false,
            .max_files = 8,
            .allocation_unit_size = 16 * 1024,
            .disk_status_check_enable = false,
            .use_one_fat = false,
        };
        last_error = esp_vfs_fat_sdmmc_mount(SD_MOUNT_POINT, &host, &slot,
                                             &mount, &s_card);
        if (last_error == ESP_OK) break;
    }
    if (last_error != ESP_OK) {
        set_error("sd mount failed", last_error);
        return false;
    }
    if (!ensure_directory(SD_DATA_DIR) || !ensure_directory(SD_FP_DIR) ||
        !ensure_directory(SD_LOG_DIR)) {
        snprintf(s_error, sizeof(s_error), "sd directory creation failed");
        return false;
    }
    static const struct { const char *table; const char *json; } defaults[] = {
        {"version", "{\"global_version\":0,\"users_version\":0,"
                    "\"classes_version\":0,\"permissions_version\":0,"
                    "\"devices_version\":0,\"fp_version\":0,"
                    "\"logs_version\":0,\"last_update_time\":\"\","
                    "\"last_update_source\":\"init\"}"},
        {"users", "[{\"user_id\":\"admin\",\"name\":\"System Administrator\","
                  "\"role\":\"admin\",\"fingerprint_id\":null,"
                  "\"password_salt\":\"000102030405060708090a0b0c0d0e0f\","
                  "\"password_hash\":\"eb427d2e310382de4e4bf02b93005681040294011a20356bb0348fc49ad70a8f\","
                  "\"enabled\":true}]"},
        {"classes", "[]"}, {"permissions", "[]"},
        {"role_permissions", "[{\"role\":\"admin\",\"lock_0\":true,"
                             "\"lock_1\":true,\"lock_2\":true,\"lock_3\":true},"
                             "{\"role\":\"teacher\",\"lock_0\":false,"
                             "\"lock_1\":true,\"lock_2\":true,\"lock_3\":true},"
                             "{\"role\":\"student\",\"lock_0\":false,"
                             "\"lock_1\":false,\"lock_2\":false,\"lock_3\":false}]"},
        {"fingerprints", "[]"}, {"devices", "[]"}, {"logs", "[]"},
    };
    for (size_t index = 0; index < sizeof(defaults) / sizeof(defaults[0]);
         ++index) {
        if (!initialize_file(defaults[index].table, defaults[index].json)) {
            snprintf(s_error, sizeof(s_error), "initialize table %s failed",
                     defaults[index].table);
            return false;
        }
    }
    s_ready = true;
    s_error[0] = '\0';
    root_storage_read_versions(&s_versions);
    return true;
}

bool root_storage_ready(void) { return s_ready; }
const char *root_storage_last_error(void) { return s_error; }

uint64_t root_storage_total_bytes(void) {
    uint64_t total_bytes = 0;
    uint64_t free_bytes = 0;
    return s_ready &&
           esp_vfs_fat_info(SD_MOUNT_POINT, &total_bytes, &free_bytes) == ESP_OK
        ? total_bytes : 0;
}

uint64_t root_storage_used_bytes(void) {
    uint64_t total_bytes = 0;
    uint64_t free_bytes = 0;
    return s_ready &&
           esp_vfs_fat_info(SD_MOUNT_POINT, &total_bytes, &free_bytes) == ESP_OK
        ? total_bytes - free_bytes : 0;
}

bool root_storage_read_table(const char *table, char **json, size_t *length) {
    if (!s_ready || json == NULL || length == NULL) return false;
    *json = NULL;
    *length = 0;
    char path[128];
    if (!table_path(table, path, sizeof(path))) return false;
    FILE *file = fopen(path, "rb");
    if (file == NULL) {
        char backup[160];
        snprintf(backup, sizeof(backup), "%s.bak", path);
        file = fopen(backup, "rb");
    }
    if (file == NULL || fseek(file, 0, SEEK_END) != 0) {
        if (file != NULL) fclose(file);
        return false;
    }
    long file_size = ftell(file);
    if (file_size < 0 || fseek(file, 0, SEEK_SET) != 0) {
        fclose(file);
        return false;
    }
    char *output = malloc((size_t)file_size + 1);
    if (output == NULL) { fclose(file); return false; }
    size_t read = fread(output, 1, (size_t)file_size, file);
    fclose(file);
    if (read != (size_t)file_size) { free(output); return false; }
    output[read] = '\0';
    *json = output;
    *length = read;
    return true;
}

bool root_storage_read_versions(root_sd_versions_t *versions) {
    if (versions == NULL) return false;
    if (s_versions_valid) { *versions = s_versions; return true; }
    char *json = NULL;
    size_t length = 0;
    if (!root_storage_read_table("version", &json, &length)) return false;
    cJSON *root = cJSON_ParseWithLength(json, length);
    free(json);
    if (root == NULL) return false;
#define READ_VERSION(field, key) do { \
    cJSON *item = cJSON_GetObjectItemCaseSensitive(root, key); \
    s_versions.field = cJSON_IsNumber(item) ? (uint32_t)item->valuedouble : 0; \
} while (0)
    READ_VERSION(global, "global_version");
    READ_VERSION(users, "users_version");
    READ_VERSION(classes, "classes_version");
    READ_VERSION(permissions, "permissions_version");
    READ_VERSION(devices, "devices_version");
    READ_VERSION(fingerprints, "fp_version");
    READ_VERSION(logs, "logs_version");
#undef READ_VERSION
    cJSON_Delete(root);
    s_versions_valid = true;
    *versions = s_versions;
    return true;
}

uint32_t root_storage_table_version(const char *table) {
    root_sd_versions_t versions;
    if (!root_storage_read_versions(&versions)) return 0;
    if (strcmp(table, "users") == 0) return versions.users;
    if (strcmp(table, "classes") == 0) return versions.classes;
    if (strcmp(table, "permissions") == 0 ||
        strcmp(table, "role_permissions") == 0) return versions.permissions;
    if (strcmp(table, "devices") == 0) return versions.devices;
    if (strcmp(table, "fingerprints") == 0) return versions.fingerprints;
    if (strcmp(table, "logs") == 0) return versions.logs;
    return versions.global;
}

bool root_storage_increment_version(const char *table) {
    root_sd_versions_t versions;
    if (!root_storage_read_versions(&versions)) memset(&versions, 0,
                                                    sizeof(versions));
    if (strcmp(table, "users") == 0) { ++versions.users; ++versions.permissions; }
    else if (strcmp(table, "classes") == 0) ++versions.classes;
    else if (strcmp(table, "permissions") == 0 ||
             strcmp(table, "role_permissions") == 0) ++versions.permissions;
    else if (strcmp(table, "devices") == 0) ++versions.devices;
    else if (strcmp(table, "fingerprints") == 0) ++versions.fingerprints;
    else if (strcmp(table, "logs") == 0) ++versions.logs;
    else return false;
    ++versions.global;
    char json[512];
    int length = snprintf(json, sizeof(json),
        "{\"global_version\":%lu,\"users_version\":%lu,"
        "\"classes_version\":%lu,\"permissions_version\":%lu,"
        "\"devices_version\":%lu,\"fp_version\":%lu,"
        "\"logs_version\":%lu,\"last_update_time\":\"\","
        "\"last_update_source\":\"root_sd\"}",
        (unsigned long)versions.global, (unsigned long)versions.users,
        (unsigned long)versions.classes, (unsigned long)versions.permissions,
        (unsigned long)versions.devices,
        (unsigned long)versions.fingerprints, (unsigned long)versions.logs);
    char path[128];
    if (!table_path("version", path, sizeof(path)) ||
        !atomic_write(path, (const uint8_t *)json, (size_t)length)) return false;
    s_versions = versions;
    s_versions_valid = true;
    return true;
}

bool root_storage_write_table(const char *table, const uint8_t *json,
                              size_t length) {
    if (!s_ready || json == NULL || !root_storage_table_allowed(table))
        return false;
    char path[128];
    if (!table_path(table, path, sizeof(path)) ||
        !atomic_write(path, json, length)) return false;
    if (strcmp(table, "version") == 0) {
        s_versions_valid = false;
        return true;
    }
    return root_storage_increment_version(table);
}

root_snapshot_result_t root_storage_snapshot_manifest(
    uint8_t header[ROOT_SNAPSHOT_HEADER_SIZE]) {
    if (!s_ready || header == NULL) return ROOT_SNAPSHOT_IO_ERROR;
    if (read_snapshot_header_at(SD_SNAPSHOT_PATH, header, NULL))
        return ROOT_SNAPSHOT_OK;
    if (read_snapshot_header_at(SD_SNAPSHOT_BACKUP_PATH, header, NULL))
        return ROOT_SNAPSHOT_OK;
    return ROOT_SNAPSHOT_NOT_FOUND;
}

root_snapshot_result_t root_storage_snapshot_begin(
    const uint8_t *header, size_t header_length, uint32_t *next_offset) {
    if (next_offset != NULL) *next_offset = 0;
    if (!s_ready) return ROOT_SNAPSHOT_IO_ERROR;
    if (header_length != ROOT_SNAPSHOT_HEADER_SIZE ||
        !snapshot_header_valid(header)) return ROOT_SNAPSHOT_INVALID;

    snapshot_download_close();
    snapshot_upload_close();
    uint32_t resume_offset = 0;
    uint8_t staged_header[ROOT_SNAPSHOT_HEADER_SIZE];
    FILE *staged = fopen(SD_SNAPSHOT_UPLOAD_PATH, "rb");
    if (staged != NULL) {
        bool same = fseek(staged, 0, SEEK_END) == 0;
        long staged_size = same ? ftell(staged) : -1;
        same = same && staged_size >= ROOT_SNAPSHOT_HEADER_SIZE &&
               staged_size <= ROOT_SNAPSHOT_HEADER_SIZE +
                   (long)read_le32(header + 12) &&
               fseek(staged, 0, SEEK_SET) == 0 &&
               fread(staged_header, 1, sizeof(staged_header), staged) ==
                   sizeof(staged_header) &&
               snapshot_header_identity_equal(header, staged_header);
        if (same)
            resume_offset = (uint32_t)staged_size - ROOT_SNAPSHOT_HEADER_SIZE;
        fclose(staged);
        if (!same) remove(SD_SNAPSHOT_UPLOAD_PATH);
    }

    FILE *file = fopen(SD_SNAPSHOT_UPLOAD_PATH,
                       resume_offset > 0 ? "r+b" : "w+b");
    if (file == NULL || fseek(file, 0, SEEK_SET) != 0 ||
        fwrite(header, 1, header_length, file) != header_length ||
        fflush(file) != 0 ||
        fseek(file, ROOT_SNAPSHOT_HEADER_SIZE + resume_offset,
              SEEK_SET) != 0) {
        if (file != NULL) fclose(file);
        return ROOT_SNAPSHOT_IO_ERROR;
    }

    memset(&s_snapshot_upload, 0, sizeof(s_snapshot_upload));
    s_snapshot_upload.active = true;
    s_snapshot_upload.file = file;
    memcpy(s_snapshot_upload.header, header, header_length);
    memcpy(s_snapshot_upload.upload_id, header + 24, 16);
    s_snapshot_upload.compressed_size = read_le32(header + 12);
    s_snapshot_upload.next_offset = resume_offset;
    if (next_offset != NULL) *next_offset = resume_offset;
    return ROOT_SNAPSHOT_OK;
}

root_snapshot_result_t root_storage_snapshot_write(
    const uint8_t upload_id[16], uint32_t offset, const uint8_t *data,
    size_t length, bool flush, uint32_t *next_offset) {
    if (next_offset != NULL) *next_offset = s_snapshot_upload.next_offset;
    if (!s_ready || !s_snapshot_upload.active ||
        s_snapshot_upload.file == NULL || upload_id == NULL || data == NULL ||
        length == 0 || memcmp(upload_id, s_snapshot_upload.upload_id, 16) != 0)
        return ROOT_SNAPSHOT_INVALID;
    if (offset < s_snapshot_upload.next_offset &&
        offset + length <= s_snapshot_upload.next_offset)
        return ROOT_SNAPSHOT_OK;
    if (offset != s_snapshot_upload.next_offset)
        return ROOT_SNAPSHOT_OUT_OF_ORDER;
    if (length > s_snapshot_upload.compressed_size - offset)
        return ROOT_SNAPSHOT_INVALID;
    if (fwrite(data, 1, length, s_snapshot_upload.file) != length ||
        (flush && fflush(s_snapshot_upload.file) != 0))
        return ROOT_SNAPSHOT_IO_ERROR;
    s_snapshot_upload.next_offset += (uint32_t)length;
    if (next_offset != NULL) *next_offset = s_snapshot_upload.next_offset;
    return ROOT_SNAPSHOT_OK;
}

static root_snapshot_result_t snapshot_verify_upload(void) {
    FILE *file = fopen(SD_SNAPSHOT_UPLOAD_PATH, "rb");
    if (file == NULL || fseek(file, ROOT_SNAPSHOT_HEADER_SIZE, SEEK_SET) != 0) {
        if (file != NULL) fclose(file);
        return ROOT_SNAPSHOT_IO_ERROR;
    }
    uint8_t *buffer = malloc(SNAPSHOT_HASH_BUFFER_SIZE);
    if (buffer == NULL) {
        fclose(file);
        return ROOT_SNAPSHOT_IO_ERROR;
    }
    psa_status_t status = psa_crypto_init();
    psa_hash_operation_t operation = PSA_HASH_OPERATION_INIT;
    if (status == PSA_SUCCESS)
        status = psa_hash_setup(&operation, PSA_ALG_SHA_256);
    uint32_t remaining = s_snapshot_upload.compressed_size;
    while (status == PSA_SUCCESS && remaining > 0) {
        size_t wanted = remaining > SNAPSHOT_HASH_BUFFER_SIZE
            ? SNAPSHOT_HASH_BUFFER_SIZE : remaining;
        size_t length = fread(buffer, 1, wanted, file);
        if (length != wanted) {
            status = PSA_ERROR_GENERIC_ERROR;
            break;
        }
        status = psa_hash_update(&operation, buffer, length);
        remaining -= (uint32_t)length;
    }
    uint8_t digest[32];
    size_t digest_length = 0;
    if (status == PSA_SUCCESS)
        status = psa_hash_finish(&operation, digest, sizeof(digest),
                                 &digest_length);
    psa_hash_abort(&operation);
    free(buffer);
    fclose(file);
    if (status != PSA_SUCCESS || digest_length != sizeof(digest))
        return ROOT_SNAPSHOT_IO_ERROR;
    return memcmp(digest, s_snapshot_upload.header + 72, sizeof(digest)) == 0
        ? ROOT_SNAPSHOT_OK : ROOT_SNAPSHOT_HASH_MISMATCH;
}

root_snapshot_result_t root_storage_snapshot_commit(
    const uint8_t upload_id[16], uint32_t *size) {
    if (size != NULL) *size = s_snapshot_upload.next_offset;
    if (!s_ready || !s_snapshot_upload.active ||
        s_snapshot_upload.file == NULL || upload_id == NULL ||
        memcmp(upload_id, s_snapshot_upload.upload_id, 16) != 0 ||
        s_snapshot_upload.next_offset != s_snapshot_upload.compressed_size)
        return ROOT_SNAPSHOT_INVALID;
    if (fflush(s_snapshot_upload.file) != 0 ||
        fsync(fileno(s_snapshot_upload.file)) != 0) {
        snapshot_upload_close();
        return ROOT_SNAPSHOT_IO_ERROR;
    }
    fclose(s_snapshot_upload.file);
    s_snapshot_upload.file = NULL;

    root_snapshot_result_t verified = snapshot_verify_upload();
    if (verified != ROOT_SNAPSHOT_OK) {
        remove(SD_SNAPSHOT_UPLOAD_PATH);
        s_snapshot_upload.active = false;
        return verified;
    }

    remove(SD_SNAPSHOT_BACKUP_PATH);
    bool had_original = path_exists(SD_SNAPSHOT_PATH);
    if (had_original && rename(SD_SNAPSHOT_PATH,
                               SD_SNAPSHOT_BACKUP_PATH) != 0) {
        s_snapshot_upload.active = false;
        return ROOT_SNAPSHOT_IO_ERROR;
    }
    if (rename(SD_SNAPSHOT_UPLOAD_PATH, SD_SNAPSHOT_PATH) != 0) {
        if (had_original) rename(SD_SNAPSHOT_BACKUP_PATH, SD_SNAPSHOT_PATH);
        s_snapshot_upload.active = false;
        return ROOT_SNAPSHOT_IO_ERROR;
    }
    s_snapshot_upload.active = false;
    if (size != NULL) *size = s_snapshot_upload.compressed_size;
    return ROOT_SNAPSHOT_OK;
}

root_snapshot_result_t root_storage_snapshot_read(
    uint32_t offset, uint8_t *output, size_t capacity, size_t *length,
    uint32_t *total_size) {
    if (length != NULL) *length = 0;
    if (total_size != NULL) *total_size = 0;
    if (!s_ready || output == NULL || capacity == 0 || length == NULL ||
        total_size == NULL) return ROOT_SNAPSHOT_INVALID;

    if (s_snapshot_download.file == NULL ||
        offset != s_snapshot_download.next_offset) {
        snapshot_download_close();
        uint8_t header[ROOT_SNAPSHOT_HEADER_SIZE];
        uint32_t size = 0;
        const char *path = SD_SNAPSHOT_PATH;
        if (!read_snapshot_header_at(path, header, &size)) {
            path = SD_SNAPSHOT_BACKUP_PATH;
            if (!read_snapshot_header_at(path, header, &size))
                return ROOT_SNAPSHOT_NOT_FOUND;
        }
        if (offset >= size) return ROOT_SNAPSHOT_INVALID;
        FILE *file = fopen(path, "rb");
        if (file == NULL || fseek(file, offset, SEEK_SET) != 0) {
            if (file != NULL) fclose(file);
            return ROOT_SNAPSHOT_IO_ERROR;
        }
        s_snapshot_download.file = file;
        s_snapshot_download.total_size = size;
        s_snapshot_download.next_offset = offset;
    }
    uint32_t size = s_snapshot_download.total_size;
    size_t wanted = size - offset > capacity ? capacity : size - offset;
    size_t read = fread(output, 1, wanted, s_snapshot_download.file);
    if (read != wanted) {
        snapshot_download_close();
        return ROOT_SNAPSHOT_IO_ERROR;
    }
    *length = read;
    *total_size = size;
    s_snapshot_download.next_offset += (uint32_t)read;
    if (s_snapshot_download.next_offset >= size)
        snapshot_download_close();
    return ROOT_SNAPSHOT_OK;
}

bool root_storage_chunk_known(const char *table, const char *upload_id,
                              uint32_t part_index, uint32_t part_total) {
    return s_upload.active && strcmp(table, s_upload.table) == 0 &&
           strcmp(upload_id, s_upload.upload_id) == 0 &&
           part_total == s_upload.part_total &&
           (part_index < s_upload.next_part ||
            (s_upload.committed && part_index + 1 == part_total));
}

root_sd_chunk_result_t root_storage_write_chunk(
    const char *table, const char *upload_id, uint32_t part_index,
    uint32_t part_total, uint32_t total_bytes, const uint8_t *data,
    size_t length, uint32_t *expected_part) {
    if (expected_part != NULL) *expected_part = s_upload.next_part;
    if (!s_ready || !root_storage_table_allowed(table) ||
        upload_id == NULL || upload_id[0] == '\0' || strlen(upload_id) > 40 ||
        part_total == 0 || part_index >= part_total || total_bytes == 0 ||
        data == NULL || length == 0) return ROOT_SD_CHUNK_INVALID;
    if (root_storage_chunk_known(table, upload_id, part_index, part_total)) {
        if (expected_part != NULL) *expected_part = s_upload.next_part;
        return ROOT_SD_CHUNK_DUPLICATE;
    }
    bool same = s_upload.active && strcmp(table, s_upload.table) == 0 &&
                strcmp(upload_id, s_upload.upload_id) == 0;
    if (!same) {
        if (part_index != 0) {
            if (expected_part != NULL) *expected_part = 0;
            return ROOT_SD_CHUNK_OUT_OF_ORDER;
        }
        memset(&s_upload, 0, sizeof(s_upload));
        s_upload.active = true;
        snprintf(s_upload.table, sizeof(s_upload.table), "%s", table);
        snprintf(s_upload.upload_id, sizeof(s_upload.upload_id), "%s",
                 upload_id);
        s_upload.part_total = part_total;
        s_upload.total_bytes = total_bytes;
        char target[128];
        table_path(table, target, sizeof(target));
        snprintf(s_upload.path, sizeof(s_upload.path), "%s.upload", target);
        remove(s_upload.path);
    }
    if (part_total != s_upload.part_total ||
        total_bytes != s_upload.total_bytes ||
        part_index != s_upload.next_part) {
        if (expected_part != NULL) *expected_part = s_upload.next_part;
        return ROOT_SD_CHUNK_OUT_OF_ORDER;
    }
    FILE *file = fopen(s_upload.path, part_index == 0 ? "wb" : "ab");
    if (file == NULL) return ROOT_SD_CHUNK_FAILED;
    bool ok = fwrite(data, 1, length, file) == length && fflush(file) == 0;
    fclose(file);
    if (!ok || s_upload.written_bytes + length > total_bytes) {
        return ROOT_SD_CHUNK_FAILED;
    }
    s_upload.written_bytes += (uint32_t)length;
    ++s_upload.next_part;
    if (expected_part != NULL) *expected_part = s_upload.next_part;
    if (s_upload.next_part < s_upload.part_total) return ROOT_SD_CHUNK_ACCEPTED;
    if (s_upload.written_bytes != s_upload.total_bytes) {
        remove(s_upload.path);
        s_upload.active = false;
        return ROOT_SD_CHUNK_INVALID;
    }
    FILE *completed = fopen(s_upload.path, "rb");
    if (completed == NULL) return ROOT_SD_CHUNK_FAILED;
    uint8_t *content = malloc(s_upload.total_bytes);
    ok = content != NULL && fread(content, 1, s_upload.total_bytes, completed) ==
                            s_upload.total_bytes;
    fclose(completed);
    if (ok) ok = root_storage_write_table(table, content,
                                           s_upload.total_bytes);
    free(content);
    remove(s_upload.path);
    s_upload.committed = ok;
    if (!ok) s_upload.active = false;
    return ok ? ROOT_SD_CHUNK_COMPLETE : ROOT_SD_CHUNK_FAILED;
}

static bool template_path(const char *user_id, int finger_index,
                          char *output, size_t output_size) {
    if (user_id == NULL || user_id[0] == '\0' ||
        finger_index < 1 || finger_index > 2) return false;
    char safe[65];
    size_t length = 0;
    for (const char *p = user_id; *p != '\0' && length + 1 < sizeof(safe); ++p)
        safe[length++] = isalnum((unsigned char)*p) ? *p : '_';
    safe[length] = '\0';
    return snprintf(output, output_size, "%s/FP_%s%s.bin", SD_FP_DIR, safe,
                    finger_index > 1 ? "_2" : "") < (int)output_size;
}

bool root_storage_write_template(const char *user_id, int finger_index,
                                 const uint8_t *data, size_t length) {
    char path[160];
    return s_ready && data != NULL &&
           template_path(user_id, finger_index, path, sizeof(path)) &&
           atomic_write(path, data, length) &&
           root_storage_increment_version("fingerprints");
}

bool root_storage_read_template(const char *user_id, int finger_index,
                                uint8_t *output, size_t output_size,
                                size_t *output_length) {
    if (output_length != NULL) *output_length = 0;
    char path[160];
    if (!s_ready || output == NULL || output_length == NULL ||
        !template_path(user_id, finger_index, path, sizeof(path))) return false;
    FILE *file = fopen(path, "rb");
    if (file == NULL || fseek(file, 0, SEEK_END) != 0) {
        if (file != NULL) fclose(file);
        return false;
    }
    long length = ftell(file);
    if (length < 0 || (size_t)length > output_size ||
        fseek(file, 0, SEEK_SET) != 0) { fclose(file); return false; }
    *output_length = fread(output, 1, (size_t)length, file);
    fclose(file);
    return *output_length == (size_t)length;
}

bool root_storage_delete_template(const char *user_id, int finger_index) {
    bool deleted = false;
    int first = finger_index == 0 ? 1 : finger_index;
    int last = finger_index == 0 ? 2 : finger_index;
    for (int index = first; index <= last; ++index) {
        char path[160];
        if (!template_path(user_id, index, path, sizeof(path))) return false;
        if (!path_exists(path) || remove(path) == 0) deleted = true;
    }
    return deleted && root_storage_increment_version("fingerprints");
}
