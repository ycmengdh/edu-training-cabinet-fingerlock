#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#define CAB_FRAME_MAX_PAYLOAD 1400
#define CAB_FRAME_REASSEMBLY_MAX 8192
#define CAB_APP_MAX_PAYLOAD 4000
#define CAB_APP_ID_MAX 24

#define CAB_APP_FLAG_NEEDS_ACK 0x01
#define CAB_APP_FLAG_IS_ACK 0x02
#define CAB_APP_FLAG_IS_ERROR 0x04
#define CAB_APP_FLAG_HAS_HMAC 0x08
#define CAB_APP_FLAG_MULTI_PART 0x10
#define CAB_APP_FLAG_BROADCAST 0x80

typedef enum {
    CAB_CMD_REGISTER = 0x0001,
    CAB_CMD_HEARTBEAT = 0x0002,
    CAB_CMD_HEARTBEAT_ACK = 0x0003,
    CAB_CMD_ACK = 0x0004,
    CAB_CMD_ERROR = 0x0005,
    CAB_CMD_DEBUG_LOG = 0x0006,
    CAB_CMD_CANCEL_ENROLL = 0x0007,
    CAB_CMD_CONTROL_LOCK = 0x0010,
    CAB_CMD_ADD_FINGERPRINT = 0x0011,
    CAB_CMD_ADD_FINGERPRINT_RESULT = 0x0012,
    CAB_CMD_DELETE_FINGERPRINT = 0x0013,
    CAB_CMD_RESTORE_FINGERPRINT = 0x0014,
    CAB_CMD_RESTORE_FINGERPRINT_RESULT = 0x0015,
    CAB_CMD_DELETE_ALL_FINGERPRINTS = 0x0016,
    CAB_CMD_ENROLL_PROGRESS = 0x0017,
    CAB_CMD_ADD_BACKUP_FINGERPRINT = 0x0018,
    CAB_CMD_BACKUP_FP_LIST = 0x0019,
    CAB_CMD_BACKUP_FP_LIST_REQUEST = 0x001A,
    CAB_CMD_DELETE_BACKUP_FINGERPRINT = 0x001B,
    CAB_CMD_VERIFY_WINDOW_EVENT = 0x001C,
    CAB_CMD_START_FINGERPRINT_TEST = 0x001D,
    CAB_CMD_STOP_FINGERPRINT_TEST = 0x001E,
    CAB_CMD_FINGERPRINT_TEST_EVENT = 0x001F,
    CAB_CMD_BEGIN_PERMISSION_SYNC = 0x0020,
    CAB_CMD_SYNC_PERMISSION = 0x0021,
    CAB_CMD_COMMIT_PERMISSION_SYNC = 0x0022,
    CAB_CMD_CLEAR_PERMISSIONS = 0x0023,
    CAB_CMD_SYNC_ACK = 0x0024,
    CAB_CMD_SYNC_PERMISSIONS = 0x0025,
    CAB_CMD_READ_PERMISSIONS = 0x0026,
    CAB_CMD_PERMISSIONS_RESPONSE = 0x0027,
    CAB_CMD_DELETE_USER_PERMISSION = 0x0028,
    CAB_CMD_READ_CONFIG = 0x0030,
    CAB_CMD_WRITE_CONFIG = 0x0031,
    CAB_CMD_CONFIG_RESPONSE = 0x0032,
    CAB_CMD_CONFIG_SAVED = 0x0033,
    CAB_CMD_READ_STATUS = 0x0034,
    CAB_CMD_STATUS_RESPONSE = 0x0035,
    CAB_CMD_STATUS_REPORT = 0x0036,
    CAB_CMD_TIME_SYNC = 0x0037,
    CAB_CMD_REBOOT = 0x0038,
    CAB_CMD_REBOOT_ACK = 0x0039,
    CAB_CMD_CLEAR_LOGS = 0x003A,
    CAB_CMD_SD_QUERY = 0x0040,
    CAB_CMD_SD_QUERY_RESPONSE = 0x0041,
    CAB_CMD_SD_QUERY_PART = 0x0042,
    CAB_CMD_SD_QUERY_PART_ACK = 0x0043,
    CAB_CMD_SD_SAVE = 0x0044,
    CAB_CMD_SD_SAVE_RESPONSE = 0x0045,
    CAB_CMD_SD_QUERY_VERSION = 0x0046,
    CAB_CMD_SD_VERSION_RESPONSE = 0x0047,
    CAB_CMD_SD_SNAPSHOT_MANIFEST = 0x0048,
    CAB_CMD_SD_SNAPSHOT_MANIFEST_RESPONSE = 0x0049,
    CAB_CMD_SD_SNAPSHOT_BEGIN = 0x004A,
    CAB_CMD_SD_SNAPSHOT_CHUNK = 0x004B,
    CAB_CMD_SD_SNAPSHOT_COMMIT = 0x004C,
    CAB_CMD_SD_SNAPSHOT_RESPONSE = 0x004D,
    CAB_CMD_SD_SNAPSHOT_DOWNLOAD = 0x004E,
    CAB_CMD_SD_SNAPSHOT_DOWNLOAD_PART = 0x004F,
    CAB_CMD_UPLOAD_FP_TEMPLATE = 0x0050,
    CAB_CMD_FP_TEMPLATE_UPLOAD_RESPONSE = 0x0051,
    CAB_CMD_DOWNLOAD_FP_TEMPLATE = 0x0052,
    CAB_CMD_FP_TEMPLATE_DOWNLOAD_RESPONSE = 0x0053,
    CAB_CMD_DELETE_FP_TEMPLATE = 0x0054,
    CAB_CMD_FP_TEMPLATE_DELETE_RESPONSE = 0x0055,
    CAB_CMD_CHECK_FINGERPRINT = 0x0056,
    CAB_CMD_FINGERPRINT_CHECK_RESPONSE = 0x0057,
    CAB_CMD_FINGERPRINT_LIST_REQUEST = 0x0058,
    CAB_CMD_FINGERPRINT_LIST_RESPONSE = 0x0059,
    CAB_CMD_LOG_REPORT = 0x0060,
    CAB_CMD_LOG_REPORT_ACK = 0x0061,
    CAB_CMD_PERM_LOST = 0x0062,
    CAB_CMD_PERM_LOST_ACK = 0x0063,
    CAB_CMD_CABINET_OTA_BEGIN = 0x0070,
    CAB_CMD_CABINET_OTA_CHUNK = 0x0071,
    CAB_CMD_CABINET_OTA_COMMIT = 0x0072,
    CAB_CMD_CABINET_OTA_START = 0x0073,
    CAB_CMD_CABINET_OTA_STATUS = 0x0074,
    CAB_CMD_CABINET_OTA_RESPONSE = 0x0075,
    /* Root-to-cabinet internal notification. Receivers then pull via LAN OTA. */
    CAB_CMD_CABINET_OTA_NOTIFY = 0x0076,
    CAB_CMD_CABINET_OTA_PROGRESS = 0x0077,
    CAB_CMD_CABINET_OTA_NODES = 0x0078,
    CAB_CMD_CABINET_OTA_NODES_RESPONSE = 0x0079,
} cab_command_t;

typedef enum {
    CAB_ERR_DEVICE_NOT_REGISTERED = 1001,
    CAB_ERR_DEVICE_ID_MISMATCH = 1002,
    CAB_ERR_USER_NOT_FOUND = 2001,
    CAB_ERR_NO_PERMISSION = 2002,
    CAB_ERR_USER_DISABLED = 2003,
    CAB_ERR_FP_TEMPLATE_FORMAT = 3001,
    CAB_ERR_FP_ID_EXISTS = 3002,
    CAB_ERR_FP_COMM_FAILED = 3003,
    CAB_ERR_FP_BACKUP_EXISTS = 3004,
    CAB_ERR_FP_BACKUP_LIMIT = 3005,
    CAB_ERR_FP_BACKUP_NOT_FOUND = 3006,
    CAB_ERR_LOCK_ID_RANGE = 4001,
    CAB_ERR_LOCK_HARDWARE = 4002,
    CAB_ERR_FLASH_WRITE = 5001,
    CAB_ERR_FLASH_CRC = 5002,
    CAB_ERR_UNKNOWN_COMMAND = 9001,
    CAB_ERR_JSON_PARSE = 9002,
    CAB_ERR_CRC = 9003,
    CAB_ERR_BAD_REQUEST = 9101,
    CAB_ERR_NOT_FOUND = 9102,
    CAB_ERR_INTERNAL = 9103,
    CAB_ERR_PERMISSION_DENIED = 9104,
    CAB_ERR_VERSION_CONFLICT = 9105,
    CAB_ERR_SD_NOT_READY = 9201,
    CAB_ERR_MESH_FORWARD_FAILED = 9301,
    CAB_ERR_OTA_NOT_READY = 9401,
    CAB_ERR_OTA_INVALID_IMAGE = 9402,
    CAB_ERR_OTA_UPLOAD_STATE = 9403,
    CAB_ERR_OTA_START_FAILED = 9404,
} cab_error_t;

typedef struct {
    uint8_t flags;
    uint16_t command;
    uint16_t message_id;
    uint16_t correlation_id;
    uint32_t timestamp_unix;
    const uint8_t *device_id;
    uint8_t device_id_len;
    const uint8_t *source_id;
    uint8_t source_id_len;
    const uint8_t *payload;
    uint16_t payload_len;
} cab_app_view_t;

typedef void (*cab_frame_callback_t)(const uint8_t *payload, size_t length,
                                     void *context);
typedef int (*cab_frame_write_t)(const uint8_t *data, size_t length,
                                 void *context);

typedef struct {
    uint8_t state;
    uint8_t version;
    uint16_t length;
    uint16_t position;
    uint16_t received_crc;
    uint8_t payload[CAB_FRAME_MAX_PAYLOAD + 4];
    uint8_t fragment_id;
    uint8_t fragment_total;
    uint16_t fragment_lengths[16];
    uint16_t fragment_mask;
    uint8_t reassembly[CAB_FRAME_REASSEMBLY_MAX];
    cab_frame_callback_t callback;
    void *callback_context;
    uint32_t crc_errors;
} cab_frame_parser_t;

void cab_frame_parser_init(cab_frame_parser_t *parser,
                           cab_frame_callback_t callback, void *context);
void cab_frame_parser_feed(cab_frame_parser_t *parser, const uint8_t *data,
                           size_t length);
int cab_frame_send(const uint8_t *payload, size_t length,
                   cab_frame_write_t writer, void *context);
uint16_t cab_crc16(const uint8_t *data, size_t length);

bool cab_app_decode(const uint8_t *data, size_t length, cab_app_view_t *view);
int cab_app_encode(uint8_t *output, size_t output_size, uint16_t command,
                   uint16_t message_id, uint16_t correlation_id, uint8_t flags,
                   const char *device_id, const char *source_id,
                   const uint8_t *payload, uint16_t payload_len,
                   uint32_t timestamp_unix);
uint16_t cab_next_message_id(void);
void cab_app_copy_id(char *output, size_t output_size, const uint8_t *id,
                     uint8_t id_len);

int cab_pack_heartbeat(uint8_t *output, size_t output_size,
                       uint32_t free_heap, uint32_t free_psram,
                       uint16_t min_free_heap, uint8_t layer,
                       uint8_t topology, uint16_t send_failures,
                       uint16_t queue_full, uint16_t recoveries);
int cab_pack_status(uint8_t *output, size_t output_size, uint32_t uptime,
                    uint8_t lock_mask, uint8_t layer, uint8_t flags,
                    uint16_t fingerprint_count, uint16_t permission_count,
                    uint32_t permission_version, uint16_t send_failures,
                    uint16_t queue_full, int8_t rssi, uint8_t assoc_expire,
                    uint16_t fingerprint_poll_max_ms);
int cab_pack_ack(uint8_t *output, size_t output_size, uint16_t reference_id,
                 uint16_t result_code, const char *tag);

#ifdef __cplusplus
}
#endif
