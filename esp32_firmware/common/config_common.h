/**
 * config_common.h - 共享配置定义（柜子节点和根节点共用）
 * 包含: Mesh网络配置、协议帧常量、Flash分区、权限/日志格式、
 *       系统参数、枚举、结构体
 * 不包含: GPIO引脚定义（各项目在自己的 config.h 中定义）
 */
#ifndef CONFIG_COMMON_H
#define CONFIG_COMMON_H

#include <Arduino.h>

// ===================== 系统级常量 =====================
#define LOCK_COUNT               4    // 每柜子锁数量（系统级，权限结构体依赖）
#define DEVICE_ID_DEFAULT        "CABINET_001"
#define FINGER_MAX_USERS         200
#define STATUS_REPORT_INTERVAL   60000
#define FIRMWARE_VERSION         "2.5.0"

// ===================== ESP-MESH 网络配置 =====================
#define MESH_CHANNEL            6
#define MESH_PASSWORD           "Mesh@2026"
#define MESH_MAX_NODE           40
#define MESH_MAX_LAYER          6
#define MESH_AP_MAX_CONNECTION  10
#define MESH_ID                 {0x4D, 0x45, 0x53, 0x48, 0x30, 0x31}

#define MESH_HEARTBEAT_INTERVAL 60000
#define MESH_ROUTE_TIMEOUT_MS   (MESH_HEARTBEAT_INTERVAL * 3UL)
#define MESH_ROUTE_SWEEP_MS     5000
#define MESH_RECONNECT_BASE_MS  5000
#define MESH_RECONNECT_MAX_MS   60000
#define MESH_RX_BUFFER_SIZE     1500

// ===================== 上行链路（Root节点专用） =====================
#define UPLINK_USB_BAUD         921600
#define UPLINK_AP_SSID          "ESP32_Root"
#define UPLINK_AP_PASSWORD      "12345678"
#define UPLINK_TCP_PORT         8888
#define UPLINK_SERVER_IP_DEFAULT "192.168.1.100"
#define UPLINK_TCP_RX_BUF_SIZE  2048
#define UPLINK_TCP_RECONNECT_MS 5000

// ===================== Flash 分区偏移 =====================
#define PERM_STORE_OFFSET       0x314000
#define LOG_STORE_OFFSET        0x334000
#define CONFIG_OFFSET           0x354000
#define FLASH_SECTOR_SIZE       0x1000

// ===================== 协议帧格式常量 =====================
#define FRAME_HEAD1             0xA5
#define FRAME_HEAD2             0x5A
#define FRAME_VERSION_NORMAL    0x01
#define FRAME_VERSION_FRAGMENT  0x02
#define FRAME_MAX_PAYLOAD       1400
#define FRAME_HEADER_SIZE       5
#define FRAME_CRC_SIZE          2
#define FRAGMENT_HEADER_SIZE    4
#define FRAGMENT_MAX_TOTAL      255
#define FRAGMENT_REASSEMBLY_BUF 65536
#define FRAGMENT_TIMEOUT_MS     5000

// ===================== 权限数据格式 =====================
#define PERM_MAGIC              0xA5A55A5A
#define PERM_RECORD_SIZE        12
#define PERM_HEADER_SIZE        16
#define PERM_MAX_USERS          200

// ===================== 离线日志格式 =====================
#define LOG_RECORD_SIZE         32
#define LOG_SECTOR_COUNT        32
#define LOG_ENTRIES_PER_SECTOR  (FLASH_SECTOR_SIZE / LOG_RECORD_SIZE)
#define LOG_MAX_ENTRIES         (LOG_SECTOR_COUNT * LOG_ENTRIES_PER_SECTOR)

#define LOG_REPORT_INTERVAL_MS  10000
#define LOG_REPORT_BATCH_MAX    6     // Keep one Mesh payload below the 1500-byte MTU.
#define LOG_MEM_BUFFER_MAX      64
#define SD_LOG_MAX_ENTRIES      500

// ===================== 指纹模板参数 =====================
#define FP_TEMPLATE_SIZE          512
#define FP_MAX_TEMPLATES_PER_USER 2
#define FP_TEMPLATE_BUF_SIZE      (FP_TEMPLATE_SIZE + 64)

// ===================== SD 卡路径常量（根节点使用） =====================
#define SD_MOUNT_POINT      "/sdcard"
#define SD_DATA_DIR         "/sdcard/data"
#define SD_FP_DIR           "/sdcard/data/fingerprints"

// ===================== 工作模式枚举 =====================
enum WorkMode {
    MODE_MESH  = 0,
    MODE_DEBUG = 1
};

// ===================== 上行链路模式枚举 =====================
enum UplinkMode {
    UPLINK_USB = 0,
    UPLINK_AP  = 1,
    UPLINK_STA = 2
};

// ===================== 权限等级枚举 =====================
enum UserRole {
    ROLE_ADMIN   = 0,
    ROLE_TEACHER = 1,
    ROLE_STUDENT = 2
};

// ===================== 错误码定义 =====================
enum ErrorCode {
    ERR_NONE                = 0,
    ERR_DEVICE_NOT_REGISTER = 1001,
    ERR_DEVICE_ID_MISMATCH  = 1002,
    ERR_USER_NOT_FOUND      = 2001,
    ERR_NO_PERMISSION       = 2002,
    ERR_USER_DISABLED       = 2003,
    ERR_FP_TEMPLATE_FORMAT  = 3001,
    ERR_FP_ID_EXISTS        = 3002,
    ERR_FP_COMM_FAILED      = 3003,
    ERR_LOCK_ID_RANGE       = 4001,
    ERR_LOCK_HARDWARE       = 4002,
    ERR_FLASH_WRITE         = 5001,
    ERR_FLASH_CRC           = 5002,
    ERR_UNKNOWN_CMD         = 9001,
    ERR_JSON_PARSE          = 9002,
    ERR_CRC_CHECK           = 9003,
    ERR_BAD_REQUEST         = 9101,
    ERR_NOT_FOUND           = 9102,
    ERR_INTERNAL            = 9103,
    ERR_PERMISSION_DENIED   = 9104,
    ERR_VERSION_CONFLICT    = 9105
};

// ===================== 设备配置结构体 =====================
struct DeviceConfig {
    String device_id;
    String device_name;
    WorkMode work_mode;
    bool is_root;
    UplinkMode uplink_mode;
    uint8_t mesh_channel;
    String mesh_password;
    String wifi_ssid;
    String wifi_password;
    String server_ip;
    uint16_t server_port;
    uint8_t fingerprint_count;
    uint32_t perm_version;
};

// ===================== 用户权限结构体 =====================
struct UserPermission {
    int fingerprint_id;
    uint32_t user_id_num;
    String user_id;
    String name;
    UserRole role;
    bool lock_perm[LOCK_COUNT];
    uint32_t expire_days;
    bool valid;
};

// ===================== 日志结构体 =====================
struct LogEntry {
    uint32_t log_seq;
    String user_id;
    int fingerprint_id;
    int lock_id;
    String action;
    String result;
    String reason;
    uint32_t timestamp;
};

#endif // CONFIG_COMMON_H
