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
// 实板映射：OUT1-OUT4(Q0-Q3) 为锁状态 LED，OUT5-OUT8(Q4-Q7) 为锁继电器。
// Lock1-4 继电器分别为 OUT5/OUT6/OUT7/OUT8；LED 分别为 OUT4/OUT3/OUT2/OUT1。
// 两组均高电平有效；权限提示只能写 LED 位，禁止改动继电器位。
// 待机默认 595 输出 0x00（锁全关、LED 全灭）
#define SHIFT_DS_PIN            4
#define SHIFT_STCP_PIN          15
#define SHIFT_SHCP_PIN          16

// ===================== 按键: K1-K4 开锁, K5 取消 =====================
// K1=47, K2=48, K3=45, K4=38, K5=39
#define KEY0_PIN                47
#define KEY1_PIN                48
#define KEY2_PIN                45
#define KEY3_PIN                38
#define KEY4_PIN                39
#define KEY_COUNT               5
#define KEY_CANCEL_INDEX        4

// 锁控制参数
#define LOCK_OPEN_DURATION_MS   500
#define LOCK_FORCE_OFF_MS       2000

// 按键参数
#define KEY_DEBOUNCE_MS         20
#define KEY_LONGPRESS_MS        10000

#endif
