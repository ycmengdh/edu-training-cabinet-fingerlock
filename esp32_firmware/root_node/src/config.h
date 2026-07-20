/**
 * config.h - Root Node configuration (Mesh root + SD data center + TFT display)
 * Includes shared config from config_common.h and defines root-node-specific
 * GPIO pins for TFT display and SD_MMC card.
 *
 * Root node has NO fingerprint, NO keys, NO locks, NO status LED.
 */
#ifndef CONFIG_H
#define CONFIG_H

#include "config_common.h"

// ===================== TFT Display (ST7735 0.96" 80x160 SPI) =====================
#define TFT_MOSI_PIN         11
#define TFT_SCLK_PIN         10
#define TFT_CS_PIN           12
#define TFT_DC_PIN           13
#define TFT_RST_PIN          14

#define TFT_WIDTH            80
#define TFT_HEIGHT           160
#define TFT_SPI_FREQUENCY    40000000

// TFT_eSPI rotations 1 and 3 are both 160x80 landscape orientations.
// Change to 3 if the assembled panel is physically upside down.
#ifndef ROOT_TFT_ROTATION
#define ROOT_TFT_ROTATION    1
#endif

// ===================== SD Card (SD_MMC 1-bit mode) =====================
#ifdef ENABLE_SD_CARD
#define SD_SCLK_PIN          17
#define SD_MOSI_PIN          18
#define SD_MISO_PIN          16
#define SD_CS_PIN            47   // Not used by SD_MMC API, for reference only
#endif

// ===================== WiFi AP / Debug Mode Config =====================
#define AP_DEFAULT_PASSWORD     "12345678"
#define AP_IP_ADDR              "192.168.4.1"
#define AP_GATEWAY              "192.168.4.1"
#define AP_SUBNET               "255.255.255.0"
#define DEBUG_TCP_PORT          8888
#define DEBUG_TCP_RX_BUF_SIZE   1024

#endif // CONFIG_H
