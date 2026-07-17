/**
 * display.cpp - TFT status display for Root Node
 * Simple text rendering on 0.96" ST7735 80x160 display via TFT_eSPI.
 * Updates every 1.5 seconds to show root node status.
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
#include <TFT_eSPI.h>

bool Display::initialized = false;
unsigned long Display::lastUpdate = 0;

static TFT_eSPI tft = TFT_eSPI();

#define DISPLAY_UPDATE_INTERVAL_MS  1500
#define COLOR_BG      TFT_BLACK
#define COLOR_TITLE   TFT_CYAN
#define COLOR_LABEL   TFT_YELLOW
#define COLOR_OK      TFT_GREEN
#define COLOR_FAIL    TFT_RED
#define COLOR_TEXT    TFT_WHITE

void Display::init() {
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
}

void Display::update() {
    if (!initialized) return;

    unsigned long now = millis();
    if (now - lastUpdate < DISPLAY_UPDATE_INTERVAL_MS) return;
    lastUpdate = now;

    // Load device config for display
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);

    tft.fillScreen(COLOR_BG);

    int y = 2;

    // Title
    tft.setTextColor(COLOR_TITLE);
    tft.setTextSize(1);
    tft.setCursor(2, y);
    tft.println("ROOT NODE");
    y += 12;

    // Device ID
    tft.setTextColor(COLOR_LABEL);
    tft.setCursor(2, y);
    tft.print("ID:");
    tft.setTextColor(COLOR_TEXT);
    tft.println(cfg.device_id);
    y += 10;

    // Mesh status
    tft.setTextColor(COLOR_LABEL);
    tft.setCursor(2, y);
    tft.print("Mesh:");
    bool meshOk = MeshComm::isConnected();
    tft.setTextColor(meshOk ? COLOR_OK : COLOR_FAIL);
    tft.println(meshOk ? "OK" : "WAIT");
    y += 10;

    // Cabinet count (child nodes)
    tft.setTextColor(COLOR_LABEL);
    tft.setCursor(2, y);
    tft.print("Cab:");
    tft.setTextColor(COLOR_TEXT);
    tft.print(MeshComm::getChildCount());
    tft.print("/");
    tft.println(MeshBridge::getRouteCount());
    y += 10;

    // Uplink status
    tft.setTextColor(COLOR_LABEL);
    tft.setCursor(2, y);
    tft.print("Uplk:");
    bool uplinkOk = MeshBridge::isUplinkConnected();
    tft.setTextColor(uplinkOk ? COLOR_OK : COLOR_FAIL);
    const char *uplinkName = "N/A";
    UplinkMode um = MeshBridge::getUplinkMode();
    if (um == UPLINK_USB) uplinkName = "USB";
    else if (um == UPLINK_AP) uplinkName = "AP";
    else if (um == UPLINK_STA) uplinkName = "STA";
    tft.println(uplinkName);
    y += 10;

    // SD card status
    tft.setTextColor(COLOR_LABEL);
    tft.setCursor(2, y);
    tft.print("SD:");
#ifdef ENABLE_SD_CARD
    bool sdOk = SdStorage::isReady();
    tft.setTextColor(sdOk ? COLOR_OK : COLOR_FAIL);
    tft.println(sdOk ? "OK" : "FAIL");
#else
    tft.setTextColor(COLOR_FAIL);
    tft.println("N/A");
#endif
    y += 10;

    // MAC address
    tft.setTextColor(COLOR_LABEL);
    tft.setCursor(2, y);
    tft.print("MAC:");
    tft.setTextColor(COLOR_TEXT);
    String mac = MeshComm::getMeshMac();
    // Show last 8 chars to fit width
    if (mac.length() > 14) mac = mac.substring(mac.length() - 14);
    tft.println(mac);
    y += 10;

    // Uptime
    tft.setTextColor(COLOR_LABEL);
    tft.setCursor(2, y);
    tft.print("UP:");
    tft.setTextColor(COLOR_TEXT);
    unsigned long upSec = millis() / 1000;
    tft.printf("%lum", upSec / 60);
    y += 10;

    // Firmware version
    tft.setTextColor(COLOR_LABEL);
    tft.setCursor(2, y);
    tft.print("FW:");
    tft.setTextColor(COLOR_TEXT);
    tft.println(FIRMWARE_VERSION);
}
