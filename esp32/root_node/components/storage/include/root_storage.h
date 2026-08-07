#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct {
    uint32_t global;
    uint32_t users;
    uint32_t classes;
    uint32_t permissions;
    uint32_t devices;
    uint32_t fingerprints;
    uint32_t logs;
} root_sd_versions_t;

typedef enum {
    ROOT_SD_CHUNK_FAILED,
    ROOT_SD_CHUNK_ACCEPTED,
    ROOT_SD_CHUNK_DUPLICATE,
    ROOT_SD_CHUNK_COMPLETE,
    ROOT_SD_CHUNK_OUT_OF_ORDER,
    ROOT_SD_CHUNK_INVALID,
} root_sd_chunk_result_t;

#define ROOT_SNAPSHOT_HEADER_SIZE 108

typedef enum {
    ROOT_SNAPSHOT_OK = 0,
    ROOT_SNAPSHOT_NOT_FOUND = 1,
    ROOT_SNAPSHOT_INVALID = 2,
    ROOT_SNAPSHOT_IO_ERROR = 3,
    ROOT_SNAPSHOT_HASH_MISMATCH = 4,
    ROOT_SNAPSHOT_OUT_OF_ORDER = 5,
} root_snapshot_result_t;

bool root_storage_init(void);
bool root_storage_ready(void);
const char *root_storage_last_error(void);
uint64_t root_storage_total_bytes(void);
uint64_t root_storage_used_bytes(void);

bool root_storage_table_allowed(const char *table);
bool root_storage_read_table(const char *table, char **json, size_t *length);
bool root_storage_write_table(const char *table, const uint8_t *json,
                              size_t length);
uint32_t root_storage_table_version(const char *table);
bool root_storage_read_versions(root_sd_versions_t *versions);
bool root_storage_increment_version(const char *table);

root_snapshot_result_t root_storage_snapshot_manifest(
    uint8_t header[ROOT_SNAPSHOT_HEADER_SIZE]);
root_snapshot_result_t root_storage_snapshot_begin(
    const uint8_t *header, size_t header_length, uint32_t *next_offset);
root_snapshot_result_t root_storage_snapshot_write(
    const uint8_t upload_id[16], uint32_t offset, const uint8_t *data,
    size_t length, bool flush, uint32_t *next_offset);
root_snapshot_result_t root_storage_snapshot_commit(
    const uint8_t upload_id[16], uint32_t *size);
root_snapshot_result_t root_storage_snapshot_read(
    uint32_t offset, uint8_t *output, size_t capacity, size_t *length,
    uint32_t *total_size);

bool root_storage_chunk_known(const char *table, const char *upload_id,
                              uint32_t part_index, uint32_t part_total);
root_sd_chunk_result_t root_storage_write_chunk(
    const char *table, const char *upload_id, uint32_t part_index,
    uint32_t part_total, uint32_t total_bytes, const uint8_t *data,
    size_t length, uint32_t *expected_part);

bool root_storage_write_template(const char *user_id, int finger_index,
                                 const uint8_t *data, size_t length);
bool root_storage_read_template(const char *user_id, int finger_index,
                                uint8_t *output, size_t output_size,
                                size_t *output_length);
bool root_storage_delete_template(const char *user_id, int finger_index);

#ifdef __cplusplus
}
#endif
