/**
 * display.cpp - Root 节点 TFT 状态台
 *
 * 0.96" ST7735 横屏 160x80：
 *   顶栏  ROOT | 运行时长 | Host 链路
 *   中区  三页轮播：总览(CAB/SD/Mesh) / 资源(heap/SD%/层) / 身份(FW/ID)
 *   底区  4 行事件；WARNING 优先置顶，高频 CMD 过滤+限频
 *
 * 显示故障不得 reboot；无屏板可 ROOT_DISABLE_TFT=1。
 */
#include "display.h"
#include "config.h"
#include "debug.h"
#include "storage.h"
#include "mesh_comm.h"
#include "mesh_bridge.h"
#include "mem_pool.h"
#ifdef ENABLE_SD_CARD
#include "sd_storage.h"
#endif

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
unsigned long Display::pageSinceMs = 0;
uint8_t Display::pageIndex = 0;
bool Display::forceOverview = false;
bool Display::frameDirty = true;
bool Display::eventsDirty = true;
bool Display::summaryDirty = true;
uint8_t Display::nextEventSequence = 0;
String Display::eventLines[Display::kEventSlots];
Display::EventLevel Display::eventLevels[Display::kEventSlots] = {
    Display::EVENT_INFO, Display::EVENT_INFO,
    Display::EVENT_INFO, Display::EVENT_INFO
};
char Display::lastCmdKey[40] = {0};
unsigned long Display::lastCmdMs = 0;

#if !ROOT_DISABLE_TFT
static TFT_eSPI tft = TFT_eSPI();
#endif

#define DISPLAY_UPDATE_MS        400
#define DISPLAY_PAGE_MS          7000
#define DISPLAY_CMD_THROTTLE_MS  900

#define COLOR_BG      0x0000
#define COLOR_TITLE   0x07FF  /* cyan */
#define COLOR_LABEL   0xC618  /* light grey */
#define COLOR_OK      0x07E0  /* green */
#define COLOR_FAIL    0xF800  /* red */
#define COLOR_WARN    0xFD20  /* orange */
#define COLOR_TEXT    0xFFFF  /* white */
#define COLOR_HEADER  0x000F  /* navy */
#define COLOR_DIVIDER 0x7BEF  /* dark grey */
#define COLOR_PAGE    0x03EF  /* dark cyan */
#define COLOR_BADGE_OK_BG   0x0320
#define COLOR_BADGE_ERR_BG  0x8000
#define COLOR_WARN_ROW_BG   0x2000

// ---- 与是否启用 TFT 无关的纯逻辑工具 ----
namespace {

String fitText(String text, int16_t pixelWidth, bool keepEnd = false) {
    const int maxChars = pixelWidth / 6;
    if (maxChars <= 0) return String();
    if ((int)text.length() <= maxChars) return text;
    if (keepEnd) {
        if (maxChars <= 1) return text.substring(text.length() - 1);
        return String(".") + text.substring(text.length() - (maxChars - 1));
    }
    if (maxChars <= 1) return text.substring(0, 1);
    return text.substring(0, maxChars - 1) + ".";
}

String compactId(String value) {
    value.replace("CABINET_", "C");
    value.replace("CAB_", "C");
    value.replace("ROOT_", "R");
    if (value.length() > 10) return value.substring(value.length() - 10);
    return value;
}

String formatRuntime(unsigned long seconds) {
    unsigned long days = seconds / 86400UL;
    unsigned long hours = (seconds / 3600UL) % 24UL;
    unsigned long minutes = (seconds / 60UL) % 60UL;
    char text[16];
    if (days > 0) snprintf(text, sizeof(text), "%lud%02lu", days, hours);
    else snprintf(text, sizeof(text), "%02lu:%02lu", hours, minutes);
    return String(text);
}

const char *uplinkLabel(UplinkMode mode) {
    switch (mode) {
        case UPLINK_USB: return "USB";
        case UPLINK_AP:  return "AP";
        case UPLINK_STA: return "STA";
        default:         return "UL";
    }
}

}  // namespace

#if !ROOT_DISABLE_TFT
namespace {

constexpr int16_t W = 160;
constexpr int16_t HEADER_H = 14;
constexpr int16_t BODY_Y = 16;
constexpr int16_t BODY_H = 16;
constexpr int16_t EVENT_TOP = 34;
constexpr int16_t EVENT_Y[4] = {36, 48, 60, 72};

void drawValue(int16_t x, int16_t y, const String &value,
               uint16_t color = COLOR_TEXT, uint16_t background = COLOR_BG,
               int16_t rightEdge = 158) {
    const int16_t width = rightEdge - x + 1;
    if (width <= 0) return;
    tft.fillRect(x, y, width, 8, background);
    tft.setTextColor(color, background);
    tft.setCursor(x, y);
    tft.print(fitText(value, width));
}

void drawBadge(int16_t x, int16_t y, int16_t w, const char *text,
               uint16_t fg, uint16_t bg) {
    tft.fillRect(x, y, w, 10, bg);
    tft.setTextColor(fg, bg);
    tft.setCursor(x + 2, y + 1);
    tft.print(fitText(String(text), w - 3));
}

}  // namespace
#endif

const char *Display::shortCommand(const char *command) {
    if (command == nullptr || command[0] == '\0') return "CMD";
    if (!strcmp(command, "REGISTER")) return "REG";
    if (!strcmp(command, "HEARTBEAT") || !strcmp(command, "HEARTBEAT_ACK")) return "HB";
    if (!strcmp(command, "CONTROL_LOCK")) return "LOCK";
    if (!strcmp(command, "BEGIN_PERMISSION_SYNC")) return "PSYNC";
    if (!strcmp(command, "SYNC_PERMISSION") || !strcmp(command, "SYNC_PERMISSIONS")) return "PERM";
    if (!strcmp(command, "COMMIT_PERMISSION_SYNC")) return "PCOM";
    if (!strcmp(command, "CLEAR_PERMISSIONS")) return "PCLR";
    if (!strcmp(command, "ADD_FINGERPRINT")) return "FP+";
    if (!strcmp(command, "DELETE_FINGERPRINT")) return "FP-";
    if (!strcmp(command, "RESTORE_FINGERPRINT")) return "FPR";
    if (!strcmp(command, "ADD_BACKUP_FINGERPRINT")) return "BFP+";
    if (!strcmp(command, "DELETE_BACKUP_FINGERPRINT")) return "BFP-";
    if (!strcmp(command, "SD_QUERY") || !strcmp(command, "SD_QUERY_VERSION")) return "SDQ";
    if (!strcmp(command, "SD_SAVE")) return "SDS";
    if (!strcmp(command, "UPLOAD_FP_TEMPLATE")) return "TUP";
    if (!strcmp(command, "DOWNLOAD_FP_TEMPLATE")) return "TDN";
    if (!strcmp(command, "READ_STATUS") || !strcmp(command, "STATUS_REPORT")) return "ST";
    if (!strcmp(command, "TIME_SYNC")) return "TIME";
    if (!strcmp(command, "REBOOT")) return "RBT";
    if (!strcmp(command, "WRITE_CONFIG") || !strcmp(command, "READ_CONFIG")) return "CFG";
    if (!strcmp(command, "VERIFY_WINDOW_EVENT")) return "VWIN";
    static char buf[8];
    size_t n = strlen(command);
    if (n <= 6) return command;
    memcpy(buf, command, 5);
    buf[5] = '.';
    buf[6] = '\0';
    return buf;
}

bool Display::shouldShowCommand(const char *command) {
    if (command == nullptr || command[0] == '\0') return false;
    static const char *kHide[] = {
        "HEARTBEAT", "HEARTBEAT_ACK", "ACK", "ERROR",
        "READ_STATUS", "STATUS_RESPONSE", "STATUS_REPORT",
        "DEBUG_LOG", "LOG_REPORT", "LOG_REPORT_ACK",
        "SD_QUERY_PART", "SD_QUERY_PART_ACK",
        "ENROLL_PROGRESS", "SYNC_ACK",
        "PERM_LOST_ACK", "FP_TEMPLATE_UPLOAD_RESPONSE",
        "FP_TEMPLATE_DOWNLOAD_RESPONSE", "FP_TEMPLATE_DELETE_RESPONSE",
        "SD_QUERY_RESPONSE", "SD_SAVE_RESPONSE", "SD_VERSION_RESPONSE",
        "CONFIG_RESPONSE", "CONFIG_SAVED", "PERMISSIONS_RESPONSE",
        "FINGERPRINT_CHECK_RESPONSE", "REBOOT_ACK",
        nullptr
    };
    for (int i = 0; kHide[i] != nullptr; ++i) {
        if (!strcmp(command, kHide[i])) return false;
    }
    return true;
}

bool Display::hasActiveWarning() {
    for (int i = 0; i < kEventSlots; ++i) {
        if (eventLevels[i] == EVENT_WARNING && eventLines[i].length() > 0)
            return true;
    }
    return false;
}

void Display::postEvent(const String &text, EventLevel level) {
    char prefix[5];
    snprintf(prefix, sizeof(prefix), "%02u ", (unsigned)nextEventSequence);
    nextEventSequence = (uint8_t)((nextEventSequence + 1U) % 100U);

    String compact(prefix);
    compact += text;
    compact.replace("\r", " ");
    compact.replace("\n", " ");
    while (compact.indexOf("  ") >= 0) compact.replace("  ", " ");

    if (level == EVENT_WARNING) {
        for (int i = kEventSlots - 1; i > 0; --i) {
            eventLines[i] = eventLines[i - 1];
            eventLevels[i] = eventLevels[i - 1];
        }
        eventLines[0] = compact;
        eventLevels[0] = level;
        forceOverview = true;
    } else {
        int insertAt = 0;
        while (insertAt < kEventSlots && eventLevels[insertAt] == EVENT_WARNING &&
               eventLines[insertAt].length() > 0) {
            ++insertAt;
        }
        if (insertAt >= kEventSlots) insertAt = kEventSlots - 1;
        for (int i = kEventSlots - 1; i > insertAt; --i) {
            eventLines[i] = eventLines[i - 1];
            eventLevels[i] = eventLevels[i - 1];
        }
        eventLines[insertAt] = compact;
        eventLevels[insertAt] = level;
    }
    eventsDirty = true;
}

void Display::notifyDevice(const String &deviceId, bool online) {
    postEvent(String(online ? "+ " : "- ") + compactId(deviceId),
              online ? EVENT_OK : EVENT_WARNING);
}

void Display::notifyDeviceTimeout(const String &deviceId) {
    postEvent(String("! ") + compactId(deviceId) + " TO", EVENT_WARNING);
}

void Display::notifyCommand(const char *command, const String &deviceId) {
    if (!shouldShowCommand(command)) return;

    const char *shortName = shortCommand(command);
    char key[40];
    snprintf(key, sizeof(key), "%s|%s", shortName,
             deviceId.c_str() ? deviceId.c_str() : "");
    unsigned long now = millis();
    if (lastCmdKey[0] != '\0' && !strcmp(lastCmdKey, key) &&
        (now - lastCmdMs) < DISPLAY_CMD_THROTTLE_MS) {
        return;
    }
    strncpy(lastCmdKey, key, sizeof(lastCmdKey) - 1);
    lastCmdKey[sizeof(lastCmdKey) - 1] = '\0';
    lastCmdMs = now;

    String line = ">";
    line += shortName;
    if (deviceId.length() > 0) {
        line += " ";
        line += compactId(deviceId);
    }
    postEvent(line, EVENT_INFO);
}

#if !ROOT_DISABLE_TFT
void Display::drawChrome() {
    tft.fillScreen(COLOR_BG);
    tft.fillRect(0, 0, W, HEADER_H, COLOR_HEADER);
    tft.setTextSize(1);
    tft.setTextColor(COLOR_TITLE, COLOR_HEADER);
    tft.setCursor(3, 3);
    tft.print("ROOT");
    tft.drawFastHLine(0, HEADER_H, W, COLOR_DIVIDER);
    tft.drawFastHLine(0, EVENT_TOP - 2, W, COLOR_DIVIDER);
    frameDirty = false;
    summaryDirty = true;
    eventsDirty = true;
}

void Display::paintHeaderRuntime() {
    drawValue(36, 3, formatRuntime(millis() / 1000UL), COLOR_TITLE, COLOR_HEADER, 88);
}

void Display::paintHostBadge() {
    UplinkMode mode = MeshBridge::getUplinkMode();
    bool up = MeshBridge::isUplinkConnected();
    char label[12];
    snprintf(label, sizeof(label), "%s%s", uplinkLabel(mode), up ? "" : "!");
    drawBadge(92, 2, 64, label,
              up ? COLOR_OK : COLOR_FAIL,
              up ? COLOR_BADGE_OK_BG : COLOR_BADGE_ERR_BG);
}

void Display::paintOverview() {
    tft.fillRect(0, BODY_Y, W, BODY_H, COLOR_BG);

    int online = MeshBridge::getRouteCount();
    int known = MeshBridge::getRouteKnownCount();
    char cab[20];
    snprintf(cab, sizeof(cab), "CAB %d/%d", online, known);
    uint16_t cabColor = online > 0 ? COLOR_OK
                        : (known > 0 ? COLOR_FAIL : COLOR_TEXT);
    drawValue(2, BODY_Y + 4, cab, cabColor, COLOR_BG, 58);

#ifdef ENABLE_SD_CARD
    bool sdOk = SdStorage::isReady();
    drawBadge(60, BODY_Y + 3, 42, sdOk ? "SD OK" : "SD!",
              sdOk ? COLOR_OK : COLOR_FAIL,
              sdOk ? COLOR_BADGE_OK_BG : COLOR_BADGE_ERR_BG);
#else
    drawBadge(60, BODY_Y + 3, 42, "SD--", COLOR_LABEL, COLOR_BG);
#endif

    bool meshOk = MeshComm::isMeshConnected();
    int children = MeshComm::getChildCount();
    char mesh[12];
    if (meshOk) snprintf(mesh, sizeof(mesh), "M%d", children);
    else snprintf(mesh, sizeof(mesh), "M!");
    drawBadge(106, BODY_Y + 3, 50, mesh,
              meshOk ? COLOR_OK : COLOR_FAIL,
              meshOk ? COLOR_BADGE_OK_BG : COLOR_BADGE_ERR_BG);

    tft.setTextColor(COLOR_PAGE, COLOR_BG);
    tft.setCursor(152, BODY_Y + 4);
    tft.print("1");
}

void Display::paintResources() {
    tft.fillRect(0, BODY_Y, W, BODY_H, COLOR_BG);

    uint32_t heapKb = MemPool::freeInternalHeap() / 1024UL;
    char line[32];
    snprintf(line, sizeof(line), "H%luk", (unsigned long)heapKb);

#ifdef ENABLE_SD_CARD
    if (SdStorage::isReady()) {
        uint64_t total = SdStorage::getTotalBytes();
        uint64_t used = SdStorage::getUsedBytes();
        int pct = 0;
        if (total > 0) pct = (int)((used * 100ULL) / total);
        if (pct > 99) pct = 99;
        char sd[12];
        snprintf(sd, sizeof(sd), " SD%d%%", pct);
        size_t usedLen = strlen(line);
        strncat(line, sd, sizeof(line) - usedLen - 1);
    } else {
        size_t usedLen = strlen(line);
        strncat(line, " SD!", sizeof(line) - usedLen - 1);
    }
#endif

    int layer = MeshComm::getMeshLayer();
    int rssi = MeshComm::getLinkRssi();
    char tail[16];
    if (rssi < 0)
        snprintf(tail, sizeof(tail), " L%d %d", layer, rssi);
    else
        snprintf(tail, sizeof(tail), " L%d", layer);
    {
        size_t usedLen = strlen(line);
        strncat(line, tail, sizeof(line) - usedLen - 1);
    }

    drawValue(2, BODY_Y + 4, line, COLOR_TEXT, COLOR_BG, 150);
    tft.setTextColor(COLOR_PAGE, COLOR_BG);
    tft.setCursor(152, BODY_Y + 4);
    tft.print("2");
}

void Display::paintIdentity() {
    tft.fillRect(0, BODY_Y, W, BODY_H, COLOR_BG);

    DeviceConfig cfg;
    if (!Storage::loadDeviceConfig(cfg)) {
        cfg.device_id = "ROOT";
        cfg.mesh_channel = MESH_CHANNEL;
    }

    String left = String("FW") + FIRMWARE_VERSION;
    drawValue(2, BODY_Y + 4, left, COLOR_TITLE, COLOR_BG, 96);

    bool timeOk = Storage::isTimeSynced();
    char right[20];
    snprintf(right, sizeof(right), "c%d %s%s",
             (int)cfg.mesh_channel,
             compactId(cfg.device_id).c_str(),
             timeOk ? "" : "!");
    drawValue(98, BODY_Y + 4, right, timeOk ? COLOR_OK : COLOR_WARN, COLOR_BG, 150);

    tft.setTextColor(COLOR_PAGE, COLOR_BG);
    tft.setCursor(152, BODY_Y + 4);
    tft.print("3");
}

void Display::paintEvents() {
    for (int i = 0; i < kEventSlots; ++i) {
        uint16_t color = COLOR_TEXT;
        uint16_t bg = COLOR_BG;
        if (eventLevels[i] == EVENT_OK) color = COLOR_OK;
        else if (eventLevels[i] == EVENT_WARNING) {
            color = COLOR_FAIL;
            bg = COLOR_WARN_ROW_BG;
        }
        drawValue(2, EVENT_Y[i],
                  eventLines[i].length() ? eventLines[i] : String(""),
                  color, bg, 158);
    }
    eventsDirty = false;
}
#else
// Headless stubs — symbols still linked when TFT disabled.
void Display::drawChrome() {}
void Display::paintHeaderRuntime() {}
void Display::paintHostBadge() {}
void Display::paintOverview() {}
void Display::paintResources() {}
void Display::paintIdentity() {}
void Display::paintEvents() {}
#endif

void Display::init() {
#if ROOT_DISABLE_TFT
    initialized = false;
    Debug::println(F("[DISP] TFT disabled (ROOT_DISABLE_TFT=1)"));
    return;
#else
    SPI.begin(TFT_SCLK_PIN, -1, TFT_MOSI_PIN, TFT_CS_PIN);

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
    tft.setTextSize(1);

    // 开机自检页
    tft.fillScreen(COLOR_BG);
    tft.setTextColor(COLOR_TITLE, COLOR_BG);
    tft.setCursor(4, 8);
    tft.print("ROOT BOOT");
    tft.setTextColor(COLOR_TEXT, COLOR_BG);
    tft.setCursor(4, 24);
    tft.print(fitText(String("FW ") + FIRMWARE_VERSION, 152));
    tft.setCursor(4, 36);
#ifdef ENABLE_SD_CARD
    tft.setTextColor(SdStorage::isReady() ? COLOR_OK : COLOR_FAIL, COLOR_BG);
    tft.print(SdStorage::isReady() ? "SD  READY" : "SD  FAIL");
#else
    tft.setTextColor(COLOR_LABEL, COLOR_BG);
    tft.print("SD  N/A");
#endif
    tft.setCursor(4, 48);
    tft.setTextColor(COLOR_TEXT, COLOR_BG);
    {
        char ul[24];
        snprintf(ul, sizeof(ul), "UL  %s", uplinkLabel(MeshBridge::getUplinkMode()));
        tft.print(ul);
    }
    tft.setCursor(4, 60);
    tft.print("MESH start...");
    delay(500);

    drawChrome();
    initialized = true;
    pageIndex = 0;
    pageSinceMs = millis();
    lastUpdate = millis() - DISPLAY_UPDATE_MS;
    forceOverview = true;
    summaryDirty = true;
    eventsDirty = true;

    postEvent("SYSTEM START", EVENT_INFO);
    postEvent(String("FW ") + FIRMWARE_VERSION, EVENT_OK);

    Debug::printf("[DISP] TFT ready %dx%d rot=%d (status console v2)\n",
                  tft.width(), tft.height(), ROOT_TFT_ROTATION);
#endif
}

void Display::update() {
#if ROOT_DISABLE_TFT
    return;
#else
    if (!initialized) return;

    unsigned long now = millis();
    if (now - lastUpdate < DISPLAY_UPDATE_MS) return;
    lastUpdate = now;

    // 新告警出现时立刻切回总览一页周期，之后仍可轮播资源/身份页；
    // 告警行本身一直钉在事件区顶部，不会被普通 CMD 挤掉。
    if (forceOverview) {
        if (pageIndex != 0) {
            pageIndex = 0;
            summaryDirty = true;
        }
        forceOverview = false;
        pageSinceMs = now;
    } else if (now - pageSinceMs >= DISPLAY_PAGE_MS) {
        pageSinceMs = now;
        pageIndex = (uint8_t)((pageIndex + 1) % 3);
        summaryDirty = true;
    }

    if (frameDirty) drawChrome();

    static String lastRuntime;
    String runtime = formatRuntime(now / 1000UL);
    if (runtime != lastRuntime) {
        lastRuntime = runtime;
        paintHeaderRuntime();
    }

    static int lastHostState = -1;
    int hostState = (MeshBridge::isUplinkConnected() ? 2 : 0) +
                    (int)MeshBridge::getUplinkMode();
    if (hostState != lastHostState) {
        lastHostState = hostState;
        paintHostBadge();
    }

    static int lastOnline = -1;
    static int lastKnown = -1;
    static int lastSd = -1;
    static int lastMesh = -1;
    static int lastChild = -1;
    static uint32_t lastHeapBucket = 0;

    int online = MeshBridge::getRouteCount();
    int known = MeshBridge::getRouteKnownCount();
#ifdef ENABLE_SD_CARD
    int sdState = SdStorage::isReady() ? 1 : 0;
#else
    int sdState = -1;
#endif
    int meshState = MeshComm::isMeshConnected() ? 1 : 0;
    int child = MeshComm::getChildCount();
    uint32_t heapBucket = MemPool::freeInternalHeap() / 8192UL;

    if (online != lastOnline || known != lastKnown || sdState != lastSd ||
        meshState != lastMesh || child != lastChild ||
        heapBucket != lastHeapBucket) {
        lastOnline = online;
        lastKnown = known;
        lastSd = sdState;
        lastMesh = meshState;
        lastChild = child;
        lastHeapBucket = heapBucket;
        summaryDirty = true;
    }

    if (summaryDirty) {
        if (pageIndex == 0) paintOverview();
        else if (pageIndex == 1) paintResources();
        else paintIdentity();
        summaryDirty = false;
    }

    if (eventsDirty) paintEvents();
#endif
}
