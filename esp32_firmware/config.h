/**
 * config.h - ESP32指纹锁全局配置定义
 * 包含GPIO引脚定义、WiFi默认配置、TCP端口、设备配置结构体、权限结构体等
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

// ===================== 锁控制参数 =====================
#define LOCK_OPEN_DURATION_MS   3000   // 开锁持续时间 3 秒

// ===================== 按键参数 =====================
#define KEY_DEBOUNCE_MS         20     // 按键消抖时间
#define KEY_LONGPRESS_MS        10000  // 长按 10 秒切换 AP/STA 模式

// ===================== WiFi 默认配置 =====================
#define DEFAULT_WIFI_SSID       "TrainingRoom_WiFi"
#define DEFAULT_WIFI_PASSWORD   "12345678"
#define AP_DEFAULT_PASSWORD     "12345678"   // AP 模式热点密码
#define AP_IP_ADDR              "192.168.4.1"
#define AP_GATEWAY              "192.168.4.1"
#define AP_SUBNET               "255.255.255.0"

// ===================== TCP 通信配置 =====================
#define TCP_PORT                8888
#define TCP_RECONNECT_INTERVAL  5000   // STA 模式重连间隔
#define TCP_HEARTBEAT_INTERVAL  30000  // 心跳保活间隔 30 秒
#define TCP_READ_TIMEOUT        1000   // 读取超时
#define TCP_RX_BUFFER_SIZE      1024   // 接收缓冲区大小

// ===================== 系统参数 =====================
#define DEVICE_ID_DEFAULT       "CABINET_001"
#define FINGER_MAX_USERS        200    // 最大指纹用户数
#define LOG_BUFFER_MAX          100    // 日志缓存最大条数
#define STATUS_REPORT_INTERVAL  60000  // 状态上报间隔 60 秒

// ===================== LED 闪烁参数 =====================
#define LED_BLINK_FAST_MS       200    // AP 模式快闪
#define LED_BLINK_SLOW_MS       1000   // STA 已连接慢闪

// ===================== 工作模式枚举 =====================
enum WorkMode {
    MODE_STA = 0,   // STA 模式：连接路由器
    MODE_AP  = 1    // AP 模式：开启热点
};

// ===================== 权限等级枚举 =====================
enum UserRole {
    ROLE_ADMIN   = 1,   // 系统管理员
    ROLE_TEACHER = 2,   // 老师
    ROLE_STUDENT = 3    // 学生
};

// ===================== 设备配置结构体 =====================
struct DeviceConfig {
    String device_id;          // 设备唯一标识
    String device_name;        // 设备名称
    String wifi_ssid;          // WiFi SSID
    String wifi_password;      // WiFi 密码
    String server_ip;          // 上位机服务器 IP
    uint16_t server_port;      // 上位机服务器端口
    WorkMode work_mode;        // 当前工作模式 AP/STA
    uint8_t fingerprint_count; // 已注册指纹数量
};

// ===================== 用户权限结构体 =====================
// 每个指纹 ID 对应对 4 个锁的访问权限
struct UserPermission {
    int fingerprint_id;        // 指纹模块中的 ID
    String user_id;            // 上位机用户 ID
    String name;               // 用户姓名
    UserRole role;             // 角色
    bool lock_perm[LOCK_COUNT];// 4 个锁的权限：true=允许开锁
    bool valid;                // 是否有效（已缓存）
};

// ===================== 日志结构体 =====================
struct LogEntry {
    String user_id;            // 用户 ID
    int fingerprint_id;        // 指纹 ID
    int lock_id;               // 锁编号 0-3
    String action;             // 操作：open/close
    String result;             // 结果：success/fail
    String reason;             // 失败原因
    String timestamp;          // 时间戳
};

#endif // CONFIG_H
