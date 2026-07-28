#ifndef CONFIG_H
#define CONFIG_H
#include "config_common.h"

// ===================== 调试 / 上位机直连：UART0 =====================
// 与根节点相同波特率与协议帧（0xA5 0x5A + CRC16），物理口为 UART0 非 USB CDC。
// ESP32-S3 默认：U0TXD=GPIO43, U0RXD=GPIO44（外接 USB-TTL / 上位机串口）
#define DEBUG_UART_TX_PIN       43
#define DEBUG_UART_RX_PIN       44
#define DEBUG_UART_BAUD         UPLINK_USB_BAUD

// ===================== AS608 指纹模块 =====================
// UART2: ESP32 TX=GPIO17 -> AS608 RX, ESP32 RX=GPIO18 <- AS608 TX
#define FINGER_TX_PIN           17
#define FINGER_RX_PIN           18
#define FINGER_UART_BAUD        57600

// 指纹模块电源控制与状态反馈
// PWR: 输出控制上电；STATUS: 输入读供电状态（极性可与控制脚不同）
#define FINGER_PWR_PIN          21
#define FINGER_PWR_STATUS_PIN   42
// 控制脚：低电平有效上电（GPIO21=LOW 上电，HIGH 断电）
// 若某板为高有效，将 FINGER_PWR_ON_LEVEL 改为 HIGH 即可
#define FINGER_PWR_ON_LEVEL     LOW
#define FINGER_PWR_OFF_LEVEL    ((FINGER_PWR_ON_LEVEL == HIGH) ? LOW : HIGH)
// 状态反馈脚：与控制脚独立，高电平表示已上电
#define FINGER_PWR_STATUS_ON_LEVEL  HIGH
// 上电后等待模块稳定再握手
#define FINGER_PWR_STABLE_MS    300

// ---- DM900 手册要求的上电/握手时序参数 ----
// 上电前必须先把 UART TX/RX 拉低（手册·前言 A），保持时间
#define FINGER_PWR_PRELOW_MS        10
// 上电初始化完成后模块主动发出的就绪握手字节（手册 4.8）
#define FINGER_HANDSHAKE_BYTE       0x55
// 读 0x55 的超时窗口；读不到则降级到固定延时继续，不中止
#define FINGER_HANDSHAKE_TIMEOUT_MS 500
// init() 内 checkPassword() 失败重试次数与间隔
#define FINGER_INIT_RETRY           3
#define FINGER_RETRY_DELAY_MS       200

// ===================== 74HC595 移位寄存器 =====================
// Q0-Q3: 继电器(高电平开锁, LOW=关锁), Q4-Q7: 锁状态 LED(高电平亮, LOW=灭)
// 待机默认 595 输出 0x00（锁全关、LED 全灭）
#define SHIFT_DS_PIN            4
#define SHIFT_STCP_PIN          15
#define SHIFT_SHCP_PIN          16

// ===================== 按键: K1-K4 开锁, K5 取消 =====================
// K1=47, K2=48, K3=45, K4=39, K5=40
#define KEY0_PIN                47
#define KEY1_PIN                48
#define KEY2_PIN                45
#define KEY3_PIN                39
#define KEY4_PIN                40
#define KEY_COUNT               5
#define KEY_CANCEL_INDEX        4

// LED 状态指示（Mesh/调试，非锁 LED）
#define LED_PIN                 2

// ===================== 指纹状态 LED（V2.7） =====================
// 双色 LED 指示指纹验证状态：
//   识别中：绿灯慢闪（500ms 周期）
//   成功：  绿灯常亮（持续至 10s 操作窗口结束）
//   失败：  红灯闪烁 3 次
// 若硬件为单 GPIO 双色（共阳/共阴），可通过 FP_LED_COMMON_ANODE 切换极性。
// 若硬件无独立双色 LED，可复用 74HC595 的保留位（需在 lock_control 中扩展）。
#define FP_LED_GREEN_PIN        41
#define FP_LED_RED_PIN          38
// 共阳极 LED：HIGH=灭, LOW=亮。共阴极 LED：HIGH=亮, LOW=灭。
// 默认共阴极（多数开发板板载 LED 为共阴）。
#define FP_LED_COMMON_ANODE     0

// 锁控制参数
#define LOCK_OPEN_DURATION_MS   3000

// 按键参数
#define KEY_DEBOUNCE_MS         20
#define KEY_LONGPRESS_MS        10000

// WiFi 调试模式配置（保留 AP 可选；主调试链路为 UART0）
#define AP_DEFAULT_PASSWORD     "12345678"
#define AP_IP_ADDR              "192.168.4.1"
#define AP_GATEWAY              "192.168.4.1"
#define AP_SUBNET               "255.255.255.0"
#define DEBUG_TCP_PORT          8888
#define DEBUG_TCP_RX_BUF_SIZE   1024

// LED 闪烁参数
#define LED_BLINK_FAST_MS       200
#define LED_BLINK_SLOW_MS       1000
#define LED_BLINK_MEDIUM_MS     500

#endif
