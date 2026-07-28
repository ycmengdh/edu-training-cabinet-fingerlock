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
#define FIRMWARE_VERSION         "2.7.2-stable"

// ===================== 柜机本地交互窗口常量 =====================
// 指纹验证成功后的操作窗口：用户需在此期间按下对应锁按键开锁。
// 超时未操作自动清空本次验证权限，灯光熄灭。
#define VERIFY_WINDOW_MS         10000
// 指纹识别失败后红灯闪烁的次数与节奏（单次闪烁周期 250ms：125ms亮 + 125ms灭）
#define FP_LED_FAIL_BLINK_COUNT  3
#define FP_LED_BLINK_HALF_MS     125
#define FP_LED_IDENTIFY_HALF_MS  250   // 识别中：500ms 周期慢闪

// ===================== ESP-MESH 网络配置 =====================
#define MESH_CHANNEL            6
#define MESH_PASSWORD           "Mesh@2026"
#define MESH_MAX_NODE           100
// MESH_MAX_NODE counts cabinet routes; ESP-MESH capacity also includes Root.
#define MESH_NETWORK_CAPACITY   (MESH_MAX_NODE + 1)
#define MESH_MAX_LAYER          6
// ESP-MESH hard limit is CONFIG_MESH_AP_MAX_CONNECTIONS_DEFAULT = 6.
// Nodes beyond this count attach via intermediate hops, not directly to Root.
#define MESH_AP_MAX_CONNECTION  6
#define MESH_ID                 {0x4D, 0x45, 0x53, 0x48, 0x30, 0x31}

// 3s 心跳按 MAC 分散在完整周期内；100 个柜子平均约 33 包/s，避免同步突发。
// 应用层允许连续丢失三个心跳周期；物理断链仍由 Mesh 事件立即判离线。
#define MESH_HEARTBEAT_INTERVAL 3000
#define MESH_ROUTE_TIMEOUT_MS   12000UL
#define MESH_ROUTE_SWEEP_MS     1000
// Parent 仍显示已关联但 Root 应用层长期不回应时，30s 后重建柜机 Mesh 栈。
// UART0 是独立链路，重建 Mesh 期间不会停止。
#define MESH_FORCE_REASSOC_MS   30000UL
#define MESH_RECONNECT_BASE_MS  1000
#define MESH_RECONNECT_MAX_MS   10000
#define MESH_RX_BUFFER_SIZE     1500
// Mesh RX queue depth used by mesh_comm (xQueueCreate)
#define MESH_RX_QUEUE_DEPTH     32

// ===================== 上行链路（Root节点专用） =====================
#define UPLINK_USB_BAUD         921600
#define UPLINK_AP_SSID          "ESP32_Root"
#define UPLINK_AP_PASSWORD      "12345678"
#define UPLINK_TCP_PORT         8888
#define UPLINK_SERVER_IP_DEFAULT "192.168.1.100"
#define UPLINK_TCP_RX_BUF_SIZE  2048
#define UPLINK_TCP_RECONNECT_MS 5000
// Cabinet UART0 host liveness. The PC sends a READ_STATUS probe every second;
// four missed probes mark only the UART host offline and resume REGISTER announces.
#define UART_HOST_TIMEOUT_MS    4000UL

// ===================== Flash 分区偏移 =====================
// 离线日志使用自定义分区 logstore（见 common/partitions_16MB_log.csv）
// 分区表结束于 0x800000，兼容 8MB/16MB 模组；logstore 固定在 0x7E0000。
// 权限与配置已迁移到 NVS，下列 PERM/CONFIG 偏移仅作历史参考，不再使用。
// #define PERM_STORE_OFFSET       0x314000
#define LOG_STORE_OFFSET        0x7E0000
#define LOG_STORE_SIZE          0x10000
// #define CONFIG_OFFSET           0x354000
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
// Dual-slot PSRAM reassembly: 8KB/slot covers typical multi-part app payloads
// without a 64KB internal-heap single slot.
#define FRAGMENT_MAX_TOTAL      16
#define FRAGMENT_REASSEMBLY_BUF 8192
#define FRAGMENT_SLOT_COUNT     2
#define FRAGMENT_TIMEOUT_MS     5000

// ===================== Reliability / pacing =====================
#define MESH_SEND_PACING_MS     15
#define PERM_SYNC_INTER_ROW_MS  40
#define PERM_SYNC_INTER_NODE_MS 100
#define RELIABLE_TX_SLOTS       16
#define RELIABLE_TX_TIMEOUT_MS  800
#define RELIABLE_TX_MAX_RETRY   3
#define SD_PART_WINDOW          3

// ===================== App binary protocol =====================
#define APP_PROTO_VER           0x01
#define APP_MAGIC_0             0xB1
#define APP_MAGIC_1             0x0F
#define APP_DEVICE_ID_MAX       24
#define APP_SOURCE_ID_MAX       24
#define APP_ENVELOPE_MIN        18
#define APP_MAX_PAYLOAD         1400   // mesh single-packet safe
#define APP_MAX_PAYLOAD_FRAME    4000   // USB/TCP uplink (A5 frame can fragment)
#define APP_FLAG_NEEDS_ACK      0x01
#define APP_FLAG_IS_ACK         0x02
#define APP_FLAG_IS_ERROR       0x04
#define APP_FLAG_HAS_HMAC       0x08
#define APP_FLAG_MULTI_PART     0x10
#define APP_FLAG_BROADCAST      0x80

// ===================== TX pool sizes (MemPool) =====================
#define FRAME_TX_POOL_SIZE      4096
#define MESH_TX_SCRATCH_SIZE    1500

// ===================== 权限数据格式 =====================
// V2.7：记录从 12B 扩展到 16B，新增 local_fp_id(2B) + is_backup(1B) + reserved(1B)。
// 旧 12B 记录读取时由 storage.cpp 兼容迁移：local_fp_id = fingerprint_id, is_backup = false。
// 通过 header 中的 record_size 字段（reserved[0] 复用）区分版本。
#define PERM_MAGIC              0xA5A55A5A
#define PERM_RECORD_SIZE        16
#define PERM_RECORD_SIZE_V1     12     // 旧版本兼容读取
#define PERM_HEADER_SIZE        16
#define PERM_MAX_USERS          200

// ===================== 离线日志格式 =====================
#define LOG_RECORD_SIZE         32
#define LOG_SECTOR_COUNT        (LOG_STORE_SIZE / FLASH_SECTOR_SIZE)
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
// 临时录入槽位：录入时先存到 ID=0，检测通过后迁移到 allocLocalFpId() 分配的真实 ID。
// ID=0 永远无开锁权限（loadVerifiedPermission 直接拒绝），仅用于录入+检测。
#define FP_TEMP_SLOT              0

// ===================== SD 卡路径常量（根节点使用） =====================
// SD_MMC.begin("/sdcard", ...) 的参数是 VFS 挂载点（prefix），底层访问时
// 已自动去掉该前缀。因此所有业务路径应以 /sdcard 之后的相对路径书写。
// 例如：/sdcard/data/version.json 在 SD_MMC.open() 中应写作 /data/version.json。
// 若直接以 /sdcard 前缀打开，路径会变成 /sdcard/sdcard/data/...（重复前缀）
// 导致 mkdir / open 全部失败、SD 看似 mount 成功但业务完全不可用。
#define SD_MOUNT_POINT      "/sdcard"   // 仅作为 VFS prefix，不要拼接到业务路径中
#define SD_DATA_DIR         "/data"     // 数据目录（相对挂载点）
#define SD_FP_DIR           "/data/fingerprints"

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
    ERR_FP_BACKUP_EXISTS    = 3004,   // 该用户本机已有副指纹
    ERR_FP_BACKUP_LIMIT     = 3005,   // 本机副指纹槽位已满
    ERR_FP_BACKUP_NOT_FOUND = 3006,   // 指定用户本机无副指纹
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
    ERR_VERSION_CONFLICT    = 9105,
    // SD 卡不可用：根节点 SD 未挂载或初始化失败。上位机收到此错误码时应
    // 自动切换为电脑本地缓存模式：SD_SAVE 数据写本地磁盘，SD_QUERY 读本地
    // 缓存，UPLOAD_FP_TEMPLATE 模板暂存本地，SD 可用后再回传到根节点 SD。
    // 柜子基本操作（指纹/锁/权限下发）不依赖根节点 SD，照常工作。
    ERR_SD_NOT_READY        = 9201
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
    bool hmac_enabled;
    String hmac_key;
};

// ===================== 用户权限结构体 =====================
// V2.7：新增 local_fp_id 与 is_backup，支持设备专属副指纹。
//   - 主指纹：fingerprint_id 由上位机全局分配，下发时 local_fp_id = fingerprint_id
//   - 副指纹：本机录入，AS608 槽位由 allocLocalFpId() 分配，is_backup=true
// 验证时按 AS608 物理槽位(local_fp_id)查找权限记录，主/副共用同一权限表。
struct UserPermission {
    int fingerprint_id;       // 全局逻辑ID（上位机下发，主指纹用；副指纹时为本机分配的 local_fp_id 副本）
    int local_fp_id;          // AS608 物理槽位（柜子本地实际存储位置，主/副共用）
    bool is_backup;           // false=主指纹（全局下发），true=本机副指纹
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
