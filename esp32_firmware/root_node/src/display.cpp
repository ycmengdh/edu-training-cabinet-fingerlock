/**
 * display.cpp - TFT status display for Root Node
 * Simple text rendering on 0.96" ST7735 80x160 display via TFT_eSPI.
 * Updates every 1.5 seconds to show root node status.
 *
 * Display failure must never reboot the root node. SD-less bring-up and
 * boards without a panel still need Mesh/USB uplink.
 */
#include "display.h"
#include "config.h"
#include "debug.h"
#include "storage.h"
#include "mesh_comm.h"
#include "mesh_bridge.h"
#ifdef ENABLE_SD_CARD
#include "sd_storage.h"
#endif

// Set to 1 to skip TFT entirely (useful when panel is not wired).
#ifndef ROOT_DISABLE_TFT
#define ROOT_DISABLE_TFT 0
#endif

#if !ROOT_DISABLE_TFT
#include <TFT_eSPI.h>
#include <SPI.h>
#endif

bool Display::initialized = false;
unsigned long Display::lastUpdate = 0;

#if !ROOT_DISABLE_TFT
static TFT_eSPI tft = TFT_eSPI();
#endif

#define DISPLAY_UPDATE_INTERVAL_MS  1500
#define COLOR_BG      TFT_BLACK
#define COLOR_TITLE   TFT_CYAN
#define COLOR_LABEL   TFT_YELLOW
#define COLOR_OK      TFT_GREEN
#define COLOR_FAIL    TFT_RED
#define COLOR_TEXT    TFT_WHITE

void Display::init() {
#if ROOT_DISABLE_TFT
    initialized = false;
    Debug::println(F("[DISP] TFT disabled at compile time (ROOT_DISABLE_TFT=1)"));
    return;
#else
    // Explicit SPI pin setup before TFT_eSPI register macros touch the bus.
    // On ESP32-S3 a bad SPI host mapping can StoreProhibited (addr 0x10).
    SPI.begin(TFT_SCLK_PIN, -1, TFT_MOSI_PIN, TFT_CS_PIN);

    // Defensive pin state before library init.
    pinMode(TFT_CS_PIN, OUTPUT);
    digitalWrite(TFT_CS_PIN, HIGH);
    pinMode(TFT_DC_PIN, OUTPUT);
    digitalWrite(TFT_DC_PIN, HIGH);
    pinMode(TFT_RST_PIN, OUTPUT);
    digitalWrite(TFT_RST_PIN, HIGH);
    delay(10);

    tft.init();
    tft.setRotation(0);  // Portrait 80x160
    tft.fillScreen(COLOR_BG);
    tft.setTextColor(COLOR_TITLE);
    tft.setTextSize(1);
    tft.setCursor(2, 2);
    tft.println("ROOT NODE");
    tft.setTextColor(COLOR_TEXT);
    tft.setCursor(2, 14);
    tft.println("Starting...");

    initialized = true;
    lastUpdate = millis();
    Debug::println(F("[DISP] TFT display initialized"));
#endif
}

void Display::update() {
#if ROOT_DISABLE_TFT
    return;
#else
    if (!initialized) return;

    unsigned long now = millis();
    if (now - lastUpdate < DISPLAY_UPDATE_INTERVAL_MS) return;
    lastUpdate = now;

    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);

    tft.fillScreen(COLOR_BG);

    int y = 2;

    tft.setTextColor(COLOR_TITLE);
    tft.setTextSize(1);
    tft.setCursor(2, y);
    tft.println("ROOT NODE");
    y += 12;

    tft.setTextColor(COLOR_LABEL);
    tft.setCursor(2, y);
    tft.print("ID:");
    tft.setTextColor(COLOR_TEXT);
    tft.println(cfg.device_id);
    y += 10;

    tft.setTextColor(COLOR_LABEL);
    tft.setCursor(2, y);
    tft.print("Mesh:");
    bool meshOk = MeshComm::isConnected();
    tft.setTextColor(meshOk ? COLOR_OK : COLOR_FAIL);
    tft.println(meshOk ? "UP" : "DOWN");
    y += 10;

    tft.setTextColor(COLOR_LABEL);
    tft.setCursor(2, y);
    tft.print("Kids:");
    tft.setTextColor(COLOR_TEXT);
    tft.println(MeshComm::getChildCount());
    y += 10;

    tft.setTextColor(COLOR_LABEL);
    tft.setCursor(2, y);
    tft.print("Up:");
    bool uplink = MeshBridge::isUplinkConnected();
    tft.setTextColor(uplink ? COLOR_OK : COLOR_FAIL);
    tft.println(uplink ? "OK" : "--");
    y += 10;

#ifdef ENABLE_SD_CARD
    tft.setTextColor(COLOR_LABEL);
    tft.setCursor(2, y);
    tft.print("SD:");
    bool sd = SdStorage::isReady();
    tft.setTextColor(sd ? COLOR_OK : COLOR_FAIL);
    tft.println(sd ? "OK" : "NO");
    y += 10;
#endif
#endif
}
