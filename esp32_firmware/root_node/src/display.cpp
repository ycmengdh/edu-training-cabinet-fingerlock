/**
 * display.cpp - TFT status display for Root Node
 * Compact landscape status console on 0.96" ST7735 160x80 display.
 * Health stays visible in the top 30px; recent events use the lower 50px.
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
bool Display::eventsDirty = true;
uint8_t Display::nextEventSequence = 0;
String Display::eventLines[4];
Display::EventLevel Display::eventLevels[4] = {
    Display::EVENT_INFO, Display::EVENT_INFO,
    Display::EVENT_INFO, Display::EVENT_INFO
};

#if !ROOT_DISABLE_TFT
static TFT_eSPI tft = TFT_eSPI();
#endif

#define DISPLAY_UPDATE_INTERVAL_MS  500
#define COLOR_BG      TFT_BLACK
#define COLOR_TITLE   TFT_CYAN
#define COLOR_LABEL   TFT_LIGHTGREY
#define COLOR_OK      TFT_GREEN
#define COLOR_FAIL    TFT_RED
#define COLOR_TEXT    TFT_WHITE
#define COLOR_HEADER  TFT_NAVY
#define COLOR_DIVIDER TFT_DARKGREY

#if !ROOT_DISABLE_TFT
namespace {
constexpr int16_t HEADER_HEIGHT = 14;
constexpr int16_t SUMMARY_Y = 18;
constexpr int16_t EVENT_TOP = 31;
constexpr int16_t EVENT_Y[] = {34, 46, 58, 70};

// The built-in GLCD font is 6 pixels wide at text size 1.
String fitText(String text, int16_t pixelWidth, bool keepEnd = false) {
    const int maxChars = pixelWidth / 6;
    if (maxChars <= 0) return String();
    if (text.length() <= static_cast<unsigned int>(maxChars)) return text;
    return keepEnd ? text.substring(text.length() - maxChars)
                   : text.substring(0, maxChars);
}

String compactId(String value) {
    value.replace("CABINET_", "CAB_");
    if (value.length() > 12) return value.substring(value.length() - 12);
    return value;
}

String formatRuntime(unsigned long seconds) {
    unsigned long days = seconds / 86400UL;
    unsigned long hours = (seconds / 3600UL) % 24UL;
    unsigned long minutes = (seconds / 60UL) % 60UL;
    char text[16];
    if (days > 0) snprintf(text, sizeof(text), "%lud%02luh", days, hours);
    else snprintf(text, sizeof(text), "%02lu:%02lu", hours, minutes);
    return String(text);
}

void drawValue(int16_t x, int16_t y, const String &value,
               uint16_t color = COLOR_TEXT, uint16_t background = COLOR_BG,
               int16_t rightEdge = 158) {
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
    tft.print("ROOT");
    tft.drawFastHLine(0, EVENT_TOP - 2, tft.width(), COLOR_DIVIDER);
}

uint16_t eventColor(Display::EventLevel level) {
    if (level == Display::EVENT_OK) return COLOR_OK;
    if (level == Display::EVENT_WARNING) return COLOR_FAIL;
    return COLOR_TEXT;
}
}  // namespace
#endif

void Display::postEvent(const String &text, EventLevel level) {
    char prefix[5];
    snprintf(prefix, sizeof(prefix), "%02u ", (unsigned)nextEventSequence);
    nextEventSequence = (uint8_t)((nextEventSequence + 1U) % 100U);
    String compact(prefix);
    compact += text;
    compact.replace("\r", " ");
    compact.replace("\n", " ");
    for (int i = 3; i > 0; --i) {
        eventLines[i] = eventLines[i - 1];
        eventLevels[i] = eventLevels[i - 1];
    }
    eventLines[0] = compact;
    eventLevels[0] = level;
    eventsDirty = true;
}

void Display::notifyDevice(const String &deviceId, bool online) {
    postEvent(String(online ? "+ CAB " : "- CAB ") + compactId(deviceId),
              online ? EVENT_OK : EVENT_WARNING);
}

void Display::notifyDeviceTimeout(const String &deviceId) {
    postEvent("! CAB " + compactId(deviceId), EVENT_WARNING);
}

void Display::notifyCommand(const char *command, const String &deviceId) {
    String line = "> ";
    line += command != nullptr ? command : "CMD";
    if (deviceId.length() > 0) {
        line += " ";
        line += compactId(deviceId);
    }
    postEvent(line, EVENT_INFO);
}

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
    drawValue(112, 3, "BOOT", COLOR_LABEL, COLOR_HEADER, 156);

    initialized = true;
    postEvent("SYSTEM START", EVENT_INFO);
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

    bool meshOk = MeshComm::isConnected();

#ifdef ENABLE_SD_CARD
    bool sdOk = SdStorage::isReady();
#else
    bool sdOk = false;
#endif

    static String lastRuntime;
    static int lastOnline = -1;
    static int lastKnown = -1;
    static int lastSdState = -1;
    static int lastMeshState = -1;

    String runtime = "UP " + formatRuntime(now / 1000UL);
    if (runtime != lastRuntime) {
        lastRuntime = runtime;
        drawValue(91, 3, runtime, COLOR_TITLE, COLOR_HEADER, 156);
    }

    int online = MeshBridge::getRouteCount();
    int known = MeshBridge::getRouteKnownCount();
    if (online != lastOnline || known != lastKnown) {
        lastOnline = online;
        lastKnown = known;
        drawValue(3, SUMMARY_Y, "CAB " + String(online) + "/" + String(known),
                  online > 0 ? COLOR_OK : (known > 0 ? COLOR_FAIL : COLOR_TEXT),
                  COLOR_BG, 65);
    }
#ifdef ENABLE_SD_CARD
    int sdState = sdOk ? 1 : 0;
    if (sdState != lastSdState) {
        lastSdState = sdState;
        drawValue(70, SUMMARY_Y, sdOk ? "SD OK" : "SD ERR",
                  sdOk ? COLOR_OK : COLOR_FAIL, COLOR_BG, 108);
    }
#else
    if (lastSdState != 0) {
        lastSdState = 0;
        drawValue(70, SUMMARY_Y, "SD N/A", COLOR_FAIL, COLOR_BG, 108);
    }
#endif
    int meshState = meshOk ? 1 : 0;
    if (meshState != lastMeshState) {
        lastMeshState = meshState;
        drawValue(113, SUMMARY_Y, meshOk ? "M OK" : "M ERR",
                  meshOk ? COLOR_OK : COLOR_FAIL, COLOR_BG, 158);
    }

    if (eventsDirty) {
        for (int i = 0; i < 4; ++i) {
            drawValue(3, EVENT_Y[i], eventLines[i], eventColor(eventLevels[i]),
                      COLOR_BG, 158);
        }
        eventsDirty = false;
    }
#endif
}
