/**
 * config.h - ESP32指纹锁全局配置定义（V2.0 Mesh版本）
 * 包含GPIO引脚定义、Mesh网络配置、Flash分区偏移、帧格式常量、
 * 上行链路模式枚举、设备配置结构体、权限结构体等
 */
#ifndef CONFIG_H
#define CONFIG_H

#include <Arduino.h>

// ===================== GPIO 引脚定义 =====================

// AS608 指纹模块 UART2
#define FINGER_TX_PIN       17   // ESP32 TX -> AS608 RX
#define FINGER_RX_PIN       16   // ESP32 RX <- AS608 TX
#define FINGER_UART_BAUD    57600

// 继电器控制引脚（低电平触发）
#define LOCK0_PIN           23   // Lock0 系统锁
#define LOCK1_PIN           22   // Lock1 实训柜1
#define LOCK2_PIN           21   // Lock2 实训柜2
#define LOCK3_PIN           19   // Lock3 实训柜3
#define LOCK_COUNT          4

// 按键输入引脚（上拉输入，低电平有效）
#define KEY0_PIN            5
#define KEY1_PIN            18
#define KEY2_PIN            25
#define KEY3_PIN            26
#define KEY_COUNT           4

// LED 指示灯引脚
#define LED_PIN             27

// 权限指示灯引脚（LED_PERM_PIN 系列）
// 验证通过进入开锁窗口后，有权限的锁对应灯亮，引导用户按对应按键开锁
// 注：每把锁独占一个权限指示灯 GPIO
#define LED_PERM0_PIN       32   // Lock0 权限指示灯
#define LED_PERM1_PIN       33   // Lock1 权限指示灯
#define LED_PERM2_PIN       2    // Lock2 权限指示灯
#define LED_PERM3_PIN       4    // Lock3 权限指示灯

// ===================== SD 卡 SPI 引脚（仅根节点使用） =====================
// 使用 SPI 模式，占用 4 个 GPIO（避开已用引脚 5/16/17/18/19/21/22/23/25/26/27）
#define SD_SPI_MOSI_PIN     15
#define SD_SPI_MISO_PIN     14
#define SD_SPI_SCK_PIN      13
#define SD_SPI_CS_PIN       12
#define SD_SPI_FREQ         4000000   // 4 MHz（SD 卡 SPI 模式稳定频率）
#define SD_MOUNT_POINT      "/sdcard"  // FatFS 挂载点
#define SD_DATA_DIR         "/sdcard/data"  // 业务数据目录
#define SD_FP_DIR           "/sdcard/data/fingerprints"  // 指纹模板目录

// ===================== 锁控制参数 =====================
#define LOCK_OPEN_DURATION_MS   3000   // 开锁持续时间 3 秒

// 10 秒开锁窗口（需求 2）：鉴权通过后进入该窗口，期间可多次开有权限的锁
#define UNLOCK_WINDOW_SECONDS   10

// ===================== 按键参数 =====================
#define KEY_DEBOUNCE_MS         20     // 按键消抖时间
#define KEY_LONGPRESS_MS        10000  // 长按 10 秒切换调试模式

// ===================== WiFi 调试模式配置 =====================
#define AP_DEFAULT_PASSWORD     "12345678"   // 调试 AP 模式热点密码
#define AP_IP_ADDR              "192.168.4.1"
#define AP_GATEWAY              "192.168.4.1"
#define AP_SUBNET               "255.255.255.0"
#define DEBUG_TCP_PORT          8888         // 调试模式 TCP 端口
#define DEBUG_TCP_RX_BUF_SIZE   1024         // 调试 TCP 接收缓冲

// ===================== ESP-MESH 网络配置 =====================
#define MESH_CHANNEL            6                    // Mesh 通信信道
#define MESH_PASSWORD           "Mesh@2026"          // Mesh 网络 AES 加密密码
#define MESH_MAX_NODE           40                   // 最大节点数
#define MESH_MAX_LAYER          6                    // 最大网络层数
#define MESH_AP_MAX_CONNECTION  10                   // 单节点最大子节点连接数
#define MESH_ID                 {0x4D, 0x45, 0x53, 0x48, 0x30, 0x31} // "MESH01"

// Mesh 心跳与重连参数
#define MESH_HEARTBEAT_INTERVAL 60000    // 子节点心跳间隔 60 秒
#define MESH_RECONNECT_BASE_MS  5000     // 重连基础间隔 5 秒
#define MESH_RECONNECT_MAX_MS   60000    // 重连最大间隔 60 秒
#define MESH_RX_BUFFER_SIZE     1500     // Mesh 接收缓冲大小

// ===================== 上行链路（Root节点专用） =====================
#define UPLINK_USB_BAUD         921600   // USB 串口波特率
#define UPLINK_AP_SSID          "ESP32_Root"   // AP 模式热点名
#define UPLINK_AP_PASSWORD      "12345678"     // AP 模式密码
#define UPLINK_TCP_PORT         8888           // TCP 服务端口
#define UPLINK_SERVER_IP_DEFAULT "192.168.1.100" // STA 模式默认上位机IP
#define UPLINK_TCP_RX_BUF_SIZE  2048           // TCP 接收缓冲
#define UPLINK_TCP_RECONNECT_MS 5000           // TCP 重连间隔

// ===================== Flash 分区偏移常量（4.2.3分区方案） =====================
// 容量规划说明：虽然当前编译目标为 ESP32-WROOM（platformio.ini board=esp32dev），
// 但本固件的存储容量参数按 ESP32-S3 N16R8（16MB Flash, 8MB PSRAM）规划，
// 分区偏移与最大用户数等参数均预留充足余量，便于后续平滑迁移到 N16R8 硬件。
#define PERM_STORE_OFFSET       0x314000   // 权限数据分区起始偏移 128KB
#define LOG_STORE_OFFSET        0x334000   // 离线日志分区起始偏移 128KB
#define CONFIG_OFFSET           0x354000   // 设备配置分区偏移 16KB
#define FLASH_SECTOR_SIZE       0x1000     // Flash 扇区大小 4KB

// ===================== 协议帧格式常量（5.5节） =====================
#define FRAME_HEAD1             0xA5       // 帧头第一字节
#define FRAME_HEAD2             0x5A       // 帧头第二字节
#define FRAME_VERSION_NORMAL    0x01       // 正常帧版本号
#define FRAME_VERSION_FRAGMENT  0x02       // 分片帧版本号
#define FRAME_MAX_PAYLOAD       1400       // 单帧最大负载（ESP-MESH MTU适配）
#define FRAME_HEADER_SIZE       5          // 帧头(2) + 版本(1) + 长度(2)
#define FRAME_CRC_SIZE          2          // CRC16 校验长度
#define FRAGMENT_HEADER_SIZE    4          // 分片头：消息ID(1) + 序号(1) + 总数(1) + 保留(1)
#define FRAGMENT_MAX_TOTAL      255        // 最大分片数
#define FRAGMENT_REASSEMBLY_BUF 16384      // 分片重组缓冲大小
#define FRAGMENT_TIMEOUT_MS     5000       // 分片重组超时 5 秒

// ===================== 权限数据格式常量（4.2.3节） =====================
#define PERM_MAGIC              0xA5A55A5A  // 权限数据魔数
#define PERM_RECORD_SIZE        12          // 单条用户权限记录 12B
#define PERM_HEADER_SIZE        16          // 权限文件头 16B
#define PERM_MAX_USERS          200         // 最大用户数（需求 10：最多 200 个用户）
// 容量预警阈值（需求 10）：已用用户数达到 190 时预警，提示清理或扩容
#define CAPACITY_WARN_THRESHOLD 190

// ===================== 离线日志格式常量（4.2.3节） =====================
#define LOG_RECORD_SIZE         32          // 单条日志记录 32B
#define LOG_SECTOR_COUNT        32          // 日志环形扇区数
#define LOG_ENTRIES_PER_SECTOR  (FLASH_SECTOR_SIZE / LOG_RECORD_SIZE) // 128条/扇区
#define LOG_MAX_ENTRIES         (LOG_SECTOR_COUNT * LOG_ENTRIES_PER_SECTOR) // 4096条

// ===================== 日志上报参数 =====================
#define LOG_REPORT_INTERVAL_MS  10000      // 日志批量上报间隔 10 秒
#define LOG_REPORT_BATCH_MAX    20         // 每批最多上报条数
#define LOG_MEM_BUFFER_MAX      64         // 内存日志缓冲最大条数

// ===================== 系统参数 =====================
#define DEVICE_ID_DEFAULT       "CABINET_001"
#define FINGER_MAX_USERS        200         // 最大指纹用户数（AS608 模块上限）
#define STATUS_REPORT_INTERVAL  60000       // 状态上报间隔 60 秒
#define FIRMWARE_VERSION        "2.4.0"     // 固件版本号（SD卡集中存储版本）

// ===================== 指纹模板参数 =====================
#define FP_TEMPLATE_SIZE        512         // AS608 单枚模板字节数
#define FP_MAX_TEMPLATES_PER_USER 2         // 每用户最多模板数
#define FP_TEMPLATE_BUF_SIZE    (FP_TEMPLATE_SIZE + 64)  // 模板读写缓冲（含余量）

// ===================== LED 闪烁参数 =====================
#define LED_BLINK_FAST_MS       200         // 调试模式快闪
#define LED_BLINK_SLOW_MS       1000        // Mesh已连接慢闪
#define LED_BLINK_MEDIUM_MS     500         // Mesh连接中中速闪

// ===================== 工作模式枚举 =====================
enum WorkMode {
    MODE_MESH  = 0,   // Mesh 模式：自组网（默认，日常部署）
    MODE_DEBUG = 1    // 调试模式：AP+TCP 直连（单台维护）
};

// ===================== 上行链路模式枚举（Root专用） =====================
enum UplinkMode {
    UPLINK_USB = 0,   // USB 串口桥接
    UPLINK_AP  = 1,   // WiFi AP 模式（Root开热点）
    UPLINK_STA = 2    // WiFi STA 模式（Root连路由器）
};

// ===================== 权限等级枚举 =====================
enum UserRole {
    ROLE_ADMIN   = 0,   // 系统管理员
    ROLE_TEACHER = 1,   // 老师
    ROLE_STUDENT = 2    // 学生
};

// ===================== 错误码定义（5.6.13节） =====================
enum ErrorCode {
    ERR_NONE                = 0,
    ERR_DEVICE_NOT_REGISTER = 1001,  // 设备未注册
    ERR_DEVICE_ID_MISMATCH  = 1002,  // device_id 不匹配
    ERR_USER_NOT_FOUND      = 2001,  // 用户不存在
    ERR_NO_PERMISSION       = 2002,  // 权限不足
    ERR_USER_DISABLED       = 2003,  // 用户已禁用
    ERR_FP_TEMPLATE_FORMAT  = 3001,  // 指纹模板格式错误
    ERR_FP_ID_EXISTS        = 3002,  // 指纹ID已存在
    ERR_FP_COMM_FAILED      = 3003,  // 指纹模块通信失败
    ERR_LOCK_ID_RANGE       = 4001,  // 锁编号超范围
    ERR_LOCK_HARDWARE       = 4002,  // 锁硬件故障
    ERR_FLASH_WRITE         = 5001,  // Flash 写入失败
    ERR_FLASH_CRC           = 5002,  // Flash 校验失败
    ERR_UNKNOWN_CMD         = 9001,  // 未知命令
    ERR_JSON_PARSE          = 9002,  // JSON 解析失败
    ERR_CRC_CHECK           = 9003,  // CRC 校验失败
    // SD 卡集中存储相关错误
    ERR_BAD_REQUEST         = 9101,  // 请求参数缺失/非法
    ERR_NOT_FOUND           = 9102,  // 资源不存在（表/模板未找到）
    ERR_INTERNAL            = 9103,  // 内部错误（SD卡未就绪/内存分配失败）
    ERR_PERMISSION_DENIED   = 9104,  // 权限拒绝（非根节点访问SD存储）
    ERR_VERSION_CONFLICT    = 9105   // 乐观锁版本冲突
};

// ===================== 设备配置结构体 =====================
struct DeviceConfig {
    String device_id;             // 设备唯一标识（如 CABINET_001）
    String device_name;           // 设备名称
    WorkMode work_mode;           // 工作模式 Mesh/Debug
    bool is_root;                 // 是否为 Mesh 根节点
    UplinkMode uplink_mode;       // 上行链路模式（Root专用）
    uint8_t mesh_channel;         // Mesh 通信信道
    String mesh_password;         // Mesh 网络密码
    // STA 上行模式参数（Root连路由器时使用）
    String wifi_ssid;             // 路由器 SSID
    String wifi_password;         // 路由器密码
    String server_ip;             // 上位机 IP（STA 模式 TCP 客户端连接目标）
    uint16_t server_port;         // 上位机端口
    uint8_t fingerprint_count;    // 已注册指纹数量
    uint32_t perm_version;        // 权限数据版本号
};

// ===================== 用户权限结构体 =====================
// 每个指纹 ID 对应对 4 个锁的访问权限
struct UserPermission {
    int fingerprint_id;           // 指纹模块中的 ID
    uint32_t user_id_num;         // 上位机用户 ID（数字形式）
    String user_id;               // 上位机用户 ID（字符串，如 U001）
    String name;                  // 用户姓名（不持久化，从AUTH_OK获取）
    UserRole role;                // 角色
    bool lock_perm[LOCK_COUNT];   // 4 个锁的权限：true=允许开锁
    uint32_t expire_days;         // 过期天数（距2000-01-01），0xFFFFFFFF=永久
    bool valid;                   // 是否有效（已缓存）
};

// ===================== 日志结构体 =====================
struct LogEntry {
    uint32_t log_seq;             // 日志序号（下位机自增）
    String user_id;               // 用户 ID
    int fingerprint_id;           // 指纹 ID
    int lock_id;                  // 锁编号 0-3
    String action;                // 操作：open/close
    String result;                // 结果：success/fail
    String reason;                // 失败原因
    uint32_t timestamp;           // Unix 时间戳
};

#endif // CONFIG_H
