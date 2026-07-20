/**
 * display.cpp - TFT status display for Root Node
 * Compact landscape status dashboard on 0.96" ST7735 160x80 display.
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

#ifndef LOAD_GLCD
#error "Root TFT status display requires LOAD_GLCD"
#endif
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
#define COLOR_HEADER  TFT_NAVY
#define COLOR_DIVIDER TFT_DARKGREY

#if !ROOT_DISABLE_TFT
namespace {
constexpr int16_t HEADER_HEIGHT = 14;
constexpr int16_t LEFT_X = 3;
constexpr int16_t RIGHT_X = 83;
constexpr int16_t VALUE_RIGHT_EDGE = 158;
constexpr int16_t ROW_Y[] = {18, 33, 48, 63};

// The built-in GLCD font is 6 pixels wide at text size 1.
String fitText(String text, int16_t pixelWidth, bool keepEnd = false) {
    const int maxChars = pixelWidth / 6;
    if (maxChars <= 0) return String();
    if (text.length() <= static_cast<unsigned int>(maxChars)) return text;
    return keepEnd ? text.substring(text.length() - maxChars)
                   : text.substring(0, maxChars);
}

String compactMac(String mac) {
    mac.replace(":", "");
    mac.replace("-", "");
    return fitText(mac, 8 * 6, true);  // Keep the most distinctive last 4 bytes.
}

void drawLabel(int16_t x, int16_t y, const char *label) {
    tft.setTextColor(COLOR_LABEL, COLOR_BG);
    tft.setCursor(x, y);
    tft.print(label);
}

void drawValue(int16_t x, int16_t y, const String &value,
               uint16_t color = COLOR_TEXT, uint16_t background = COLOR_BG,
               int16_t rightEdge = VALUE_RIGHT_EDGE) {
    const int16_t width = rightEdge - x + 1;
    tft.fillRect(x, y, width, 8, background);
    tft.setTextColor(color, background);
    tft.setCursor(x, y);
    tft.print(fitText(value, width));
}

void drawLandscapeFrame() {
    tft.fillScreen(COLOR_BG);
    tft.fillRect(0, 0, tft.width(), HEADER_HEIGHT, COLOR_HEADER);

    tft.setTextSize(1);
    tft.setTextColor(COLOR_TITLE, COLOR_HEADER);
    tft.setCursor(4, 3);
    tft.print("ROOT NODE");

    // Two balanced columns; labels stay static and only values are refreshed.
    tft.drawFastVLine(79, HEADER_HEIGHT + 2,
                      tft.height() - HEADER_HEIGHT - 4, COLOR_DIVIDER);
    drawLabel(LEFT_X, ROW_Y[0], "MESH:");
    drawLabel(LEFT_X, ROW_Y[1], "CAB:");
    drawLabel(LEFT_X, ROW_Y[2], "UPLK:");
    drawLabel(LEFT_X, ROW_Y[3], "SD:");
    drawLabel(RIGHT_X, ROW_Y[0], "ID:");
    drawLabel(RIGHT_X, ROW_Y[1], "UP:");
    drawLabel(RIGHT_X, ROW_Y[2], "FW:");
    drawLabel(RIGHT_X, ROW_Y[3], "MAC:");
}
}  // namespace
#endif

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

    static_assert(ROOT_TFT_ROTATION == 1 || ROOT_TFT_ROTATION == 3,
                  "ROOT_TFT_ROTATION must be 1 or 3 for landscape mode");

    tft.init();
    tft.setRotation(ROOT_TFT_ROTATION);
    drawLandscapeFrame();
    drawValue(106, 3, "BOOT", COLOR_LABEL, COLOR_HEADER, 156);

    initialized = true;
    // Make the first loop paint live state immediately instead of waiting 1.5 s.
    lastUpdate = millis() - DISPLAY_UPDATE_INTERVAL_MS;
    Debug::printf("[DISP] TFT initialized: %dx%d, rotation=%d\n",
                  tft.width(), tft.height(), ROOT_TFT_ROTATION);
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

    bool meshOk = MeshComm::isConnected();
    bool uplinkOk = MeshBridge::isUplinkConnected();
    const char *uplinkName = "N/A";
    UplinkMode um = MeshBridge::getUplinkMode();
    if (um == UPLINK_USB) uplinkName = "USB";
    else if (um == UPLINK_AP) uplinkName = "AP";
    else if (um == UPLINK_STA) uplinkName = "STA";

#ifdef ENABLE_SD_CARD
    bool sdOk = SdStorage::isReady();
#else
    bool sdOk = false;
#endif

    const char *overall = (meshOk && uplinkOk) ? "ONLINE" : "WAIT";
    drawValue(106, 3, overall,
              (meshOk && uplinkOk) ? COLOR_OK : COLOR_LABEL,
              COLOR_HEADER, 156);

    // Left column: live connectivity and storage state.
    drawValue(36, ROW_Y[0], meshOk ? "OK" : "WAIT",
              meshOk ? COLOR_OK : COLOR_FAIL, COLOR_BG, 77);
    // CAB：N=活跃路由(30s内有心跳/REGISTER)，M=曾连上过的总数（含已过期）
    // 短 flap 时 N 短暂归零但 M 不变，方便快速判断"是否彻底掉线"
    {
        int n = MeshBridge::getRouteCount();
        int m = MeshBridge::getRouteKnownCount();
        String txt = String(n) + "/" + String(m);
        drawValue(27, ROW_Y[1], txt,
                  n > 0 ? COLOR_OK : (m > 0 ? COLOR_FAIL : COLOR_TEXT),
                  COLOR_BG, 77);
    }
    drawValue(36, ROW_Y[2], uplinkName,
              uplinkOk ? COLOR_OK : COLOR_FAIL, COLOR_BG, 77);
#ifdef ENABLE_SD_CARD
    drawValue(21, ROW_Y[3], sdOk ? "OK" : "FAIL",
              sdOk ? COLOR_OK : COLOR_FAIL, COLOR_BG, 77);
#else
    drawValue(21, ROW_Y[3], "N/A", COLOR_FAIL, COLOR_BG, 77);
#endif

    // Right column: identity and diagnostics, clipped to its 80 px cell.
    drawValue(101, ROW_Y[0], cfg.device_id);
    drawValue(101, ROW_Y[1], String(millis() / 60000UL) + "m");
    drawValue(101, ROW_Y[2], FIRMWARE_VERSION);
    drawValue(107, ROW_Y[3], compactMac(MeshComm::getMeshMac()));
#endif
}
