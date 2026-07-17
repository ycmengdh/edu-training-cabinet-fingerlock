#ifndef CONFIG_H
#define CONFIG_H
#include "config_common.h"

// AS608 指纹模块 UART2
#define FINGER_TX_PIN       17
#define FINGER_RX_PIN       18
#define FINGER_UART_BAUD    57600

// 74HC595 移位寄存器控制
#define SHIFT_DS_PIN        4
#define SHIFT_STCP_PIN      15
#define SHIFT_SHCP_PIN      16

// 按键: Key1-4 开锁, Key5 取消
#define KEY0_PIN            47
#define KEY1_PIN            48
#define KEY2_PIN            45
#define KEY3_PIN            38
#define KEY4_PIN            39
#define KEY_COUNT           5
#define KEY_CANCEL_INDEX    4

// LED 状态指示
#define LED_PIN             2

// 锁控制参数
#define LOCK_OPEN_DURATION_MS   3000

// 按键参数
#define KEY_DEBOUNCE_MS         20
#define KEY_LONGPRESS_MS        10000

// WiFi 调试模式配置
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
