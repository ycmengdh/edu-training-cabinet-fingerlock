#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#include "esp_err.h"

#ifdef __cplusplus
extern "C" {
#endif

#define ROOT_OTA_UPLOAD_ID_MAX 40
#define ROOT_OTA_VERSION_MAX 31
#define ROOT_OTA_HARDWARE_VERSION_MAX 31
#define ROOT_OTA_SHA256_HEX_LENGTH 64
#define ROOT_OTA_NODE_ERROR_MAX 63

typedef struct {
    char phase[20];
    char upload_id[ROOT_OTA_UPLOAD_ID_MAX + 1];
    char version[ROOT_OTA_VERSION_MAX + 1];
    char hardware_version[ROOT_OTA_HARDWARE_VERSION_MAX + 1];
    char sha256[ROOT_OTA_SHA256_HEX_LENGTH + 1];
    char error[128];
    uint32_t image_size;
    uint32_t received_bytes;
    uint32_t expected_nodes;
    uint32_t completed_nodes;
    uint32_t known_nodes;
    uint32_t compatible_nodes;
    uint32_t pending_nodes;
    uint32_t incompatible_nodes;
    uint32_t unknown_hardware_nodes;
    uint32_t started_at_seconds;
    uint32_t elapsed_seconds;
    uint64_t published_at;
    uint8_t mesh_progress;
    int finish_reason;
    bool active;
} root_ota_status_t;

typedef struct {
    char device_id[25];
    char parent_device_id[25];
    char version[ROOT_OTA_VERSION_MAX + 1];
    char phase[20];
    char error[ROOT_OTA_NODE_ERROR_MAX + 1];
    uint32_t updated_ago_seconds;
    uint8_t mesh_layer;
    uint8_t progress;
    uint8_t retry_count;
    bool online;
    bool compatible;
} root_ota_node_status_t;

bool root_ota_init(void);

esp_err_t root_ota_upload_begin(const char *upload_id, const char *version,
                                const char *hardware_version,
                                const char *sha256, uint32_t image_size,
                                uint64_t published_at,
                                char *error, size_t error_size);
esp_err_t root_ota_upload_chunk(const char *upload_id, uint32_t offset,
                                const uint8_t *data, size_t length,
                                uint32_t *next_offset, bool *duplicate,
                                char *error, size_t error_size);
esp_err_t root_ota_upload_commit(const char *upload_id,
                                 char *actual_version,
                                 size_t actual_version_size,
                                 char *error, size_t error_size);
esp_err_t root_ota_start(char *error, size_t error_size);
void root_ota_note_registration(const char *device_id,
                                const uint8_t *payload, size_t payload_len);
void root_ota_note_progress(const char *device_id,
                            const uint8_t *payload, size_t payload_len);
void root_ota_maintain(void);
void root_ota_get_status(root_ota_status_t *status);
size_t root_ota_get_nodes(size_t offset, size_t limit,
                          root_ota_node_status_t *nodes, size_t *total);

#ifdef __cplusplus
}
#endif
