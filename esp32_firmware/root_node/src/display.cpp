/**
 * display.cpp - Root 节点 TFT 状态台
 *
 * 0.96" ST7735 横屏 160x80：
 *   顶栏  运行时长 HH:MM:SS | Host 链路徽章
 *   中区  常驻总览(CAB/SD/Mesh)
 *   底区  4 行事件；WARNING 优先置顶，高频 CMD 过滤+限频
 *
 * 身份/资源信息(FW/CH/ID/Heap/SD%/Layer/RSSI/上行) 仅在开机自检页显示 3 秒。
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
unsigned long Display::bootPageEndMs = 0;
bool Display::frameDirty = true;
bool Display::eventsDirty = true;
bool Display::overviewDirty = true;
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
#define DISPLAY_BOOT_PAGE_MS     3000
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

String shortVersion(const char *version) {
    String value(version ? version : "?");
    int suffix = value.indexOf('-');
    if (suffix > 0) value = value.substring(0, suffix);
    return value;
}

String idSuffix(String value) {
    const int suffixLength = 4;
    if (value.length() <= suffixLength) return value;
    return value.substring(value.length() - suffixLength);
}

String formatRuntime(unsigned long seconds) {
    unsigned long days = seconds / 86400UL;
    unsigned long hours = (seconds / 3600UL) % 24UL;
    unsigned long minutes = (seconds / 60UL) % 60UL;
    unsigned long secs = seconds % 60UL;
    char text[16];
    if (days > 0)
        snprintf(text, sizeof(text), "%lud %02lu:%02lu:%02lu",
                 days, hours, minutes, secs);
    else
        snprintf(text, sizeof(text), "%02lu:%02lu:%02lu",
                 hours, minutes, secs);
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
    tft.drawFastHLine(0, HEADER_H, W, COLOR_DIVIDER);
    tft.drawFastHLine(0, EVENT_TOP - 2, W, COLOR_DIVIDER);
    frameDirty = false;
    overviewDirty = true;
    eventsDirty = true;
}

void Display::paintHeaderRuntime() {
    drawValue(3, 3, formatRuntime(millis() / 1000UL), COLOR_TITLE, COLOR_HEADER, 88);
}

void Display::paintHostBadge() {
    // 上位机链路状态徽章：HOST OK / HOST NG，右对齐紧贴屏幕右边缘。
    // 不再显示 USB/AP/STA 模式前缀（模式信息保留在开机自检页 UL:xxx 行），
    // 现场维护只关心"上位机在不在线"。
    constexpr int16_t kBadgeX = 108;
    constexpr int16_t kBadgeW = 50;
    bool up = MeshBridge::isUplinkConnected();
    const char *label = up ? "HOST OK" : "HOST NG";
    uint16_t fg = up ? COLOR_OK : COLOR_FAIL;
    uint16_t bg = up ? COLOR_BADGE_OK_BG : COLOR_BADGE_ERR_BG;

    tft.fillRect(kBadgeX, 2, kBadgeW, 10, bg);
    tft.setTextColor(fg, bg);
    // 右对齐：从右边沿倒推光标位置（GLCD 字体 6x8，每字符 6px，留 2px 右边距）
    int16_t textW = (int16_t)strlen(label) * 6;
    int16_t cursorX = kBadgeX + kBadgeW - 2 - textW;
    if (cursorX < kBadgeX + 2) cursorX = kBadgeX + 2;
    tft.setCursor(cursorX, 3);
    tft.print(label);
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

    // 阶段1：画 BOOTING 简页。让屏幕在 SD/Mesh 初始化期间已经亮起，
    // 消除上电黑屏期。此页内容精简，仅展示身份信息 + "Initializing..."。
    // 此时 initialized 仍为 false，update() 不会工作。
    tft.fillScreen(COLOR_BG);

    // Line 1: 标题
    tft.setTextColor(COLOR_TITLE, COLOR_BG);
    tft.setCursor(4, 4);
    tft.print("ROOT BOOT");

    // Line 2: 身份 FW + CH + ID
    {
        DeviceConfig cfg;
        if (!Storage::loadDeviceConfig(cfg)) {
            cfg.device_id = "ROOT";
            cfg.mesh_channel = MESH_CHANNEL;
        }
        char line2[40];
        snprintf(line2, sizeof(line2), "V%s CH%02d ID:%s",
                 shortVersion(FIRMWARE_VERSION).c_str(),
                 (int)cfg.mesh_channel,
                 idSuffix(cfg.device_id).c_str());
        tft.setTextColor(COLOR_TEXT, COLOR_BG);
        tft.setCursor(4, 18);
        tft.print(fitText(line2, 152));
    }

    // Line 6: Initializing...（先占位，等 SD/Mesh 就绪后由 showBootPage 重绘）
    tft.setTextColor(COLOR_LABEL, COLOR_BG);
    tft.setCursor(4, 66);
    tft.print("Initializing...");

    Debug::printf("[DISP] TFT early init ok %dx%d rot=%d, BOOTING page shown\n",
                  tft.width(), tft.height(), ROOT_TFT_ROTATION);
#endif
}

void Display::showBootPage() {
#if ROOT_DISABLE_TFT
    return;
#else
    if (initialized) return;  // 已经切到主界面，忽略

    // 阶段2：重画为完整自检页（FW/CH/ID/SD/UL/Heap/SD%/Layer/RSSI/MESH start）
    // 此时 SD/Mesh 均已完成初始化，可读取真实状态。
    tft.fillScreen(COLOR_BG);
    {
        // Line 1: 标题
        tft.setTextColor(COLOR_TITLE, COLOR_BG);
        tft.setCursor(4, 4);
        tft.print("ROOT BOOT");

        // Line 2: 身份 FW + CH + ID
        DeviceConfig cfg;
        if (!Storage::loadDeviceConfig(cfg)) {
            cfg.device_id = "ROOT";
            cfg.mesh_channel = MESH_CHANNEL;
        }
        char line2[40];
        snprintf(line2, sizeof(line2), "V%s CH%02d ID:%s",
                 shortVersion(FIRMWARE_VERSION).c_str(),
                 (int)cfg.mesh_channel,
                 idSuffix(cfg.device_id).c_str());
        tft.setTextColor(COLOR_TEXT, COLOR_BG);
        tft.setCursor(4, 18);
        tft.print(fitText(line2, 152));

        // Line 3: SD + 上行
        {
            char line3[32];
#ifdef ENABLE_SD_CARD
            bool sdOk = SdStorage::isReady();
            snprintf(line3, sizeof(line3), "%s  UL:%s",
                     sdOk ? "SD:OK" : "SD:FAIL",
                     uplinkLabel(MeshBridge::getUplinkMode()));
            tft.setTextColor(sdOk ? COLOR_OK : COLOR_FAIL, COLOR_BG);
#else
            snprintf(line3, sizeof(line3), "SD:N/A  UL:%s",
                     uplinkLabel(MeshBridge::getUplinkMode()));
            tft.setTextColor(COLOR_LABEL, COLOR_BG);
#endif
            tft.setCursor(4, 30);
            tft.print(fitText(line3, 152));
        }

        // Line 4: Heap + SD%
        {
            uint32_t heapKb = MemPool::freeInternalHeap() / 1024UL;
            char line4[32];
            snprintf(line4, sizeof(line4), "H:%luk", (unsigned long)heapKb);
#ifdef ENABLE_SD_CARD
            if (SdStorage::isReady()) {
                uint64_t total = SdStorage::getTotalBytes();
                uint64_t used = SdStorage::getUsedBytes();
                int pct = 0;
                if (total > 0) pct = (int)((used * 100ULL) / total);
                if (pct > 99) pct = 99;
                char sdPct[12];
                snprintf(sdPct, sizeof(sdPct), "  SD:%d%%", pct);
                strncat(line4, sdPct, sizeof(line4) - strlen(line4) - 1);
            }
#endif
            tft.setTextColor(COLOR_TEXT, COLOR_BG);
            tft.setCursor(4, 42);
            tft.print(fitText(line4, 152));
        }

        // Line 5: Layer + RSSI
        {
            int layer = MeshComm::getMeshLayer();
            int rssi = MeshComm::getLinkRssi();
            char line5[24];
            if (rssi < 0)
                snprintf(line5, sizeof(line5), "L:%d  RSSI:%d", layer, rssi);
            else
                snprintf(line5, sizeof(line5), "L:%d", layer);
            tft.setTextColor(COLOR_TEXT, COLOR_BG);
            tft.setCursor(4, 54);
            tft.print(fitText(line5, 152));
        }

        // Line 6: MESH start
        tft.setTextColor(COLOR_LABEL, COLOR_BG);
        tft.setCursor(4, 66);
        tft.print("MESH start...");
    }

    // 记录自检页结束时刻，由 main.cpp 在 setup() 末尾用 mesh update 填满这 3 秒
    bootPageEndMs = millis() + DISPLAY_BOOT_PAGE_MS;
    Debug::printf("[DISP] boot page shown, main screen at %lu ms\n",
                  (unsigned long)bootPageEndMs);
#endif
}

void Display::activateMainScreen() {
#if ROOT_DISABLE_TFT
    return;
#else
    // 阶段3：切换到主界面（顶栏+总览+事件区）
    drawChrome();
    initialized = true;
    lastUpdate = millis() - DISPLAY_UPDATE_MS;
    overviewDirty = true;
    eventsDirty = true;

    postEvent("SYSTEM START", EVENT_INFO);
    postEvent(String("FW ") + FIRMWARE_VERSION, EVENT_OK);

    Debug::println(F("[DISP] main screen activated (single-page console v3)"));
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

    if (frameDirty) drawChrome();

    // 顶栏：运行时长（每秒变化）+ Host 徽章（状态变化时重绘）
    static String lastRuntime;
    String runtime = formatRuntime(now / 1000UL);
    if (runtime != lastRuntime) {
        lastRuntime = runtime;
        paintHeaderRuntime();
    }

    static int lastHostState = -1;
    int hostState = ((int)MeshBridge::getUplinkMode() * 2) +
                    (MeshBridge::isUplinkConnected() ? 1 : 0);
    if (hostState != lastHostState) {
        lastHostState = hostState;
        paintHostBadge();
    }

    // 中区：单页总览，差分刷新
    static int lastOnline = -1;
    static int lastKnown = -1;
    static int lastSd = -1;
    static int lastMesh = -1;
    static int lastChild = -1;

    int online = MeshBridge::getRouteCount();
    int known = MeshBridge::getRouteKnownCount();
#ifdef ENABLE_SD_CARD
    int sdState = SdStorage::isReady() ? 1 : 0;
#else
    int sdState = -1;
#endif
    int meshState = MeshComm::isMeshConnected() ? 1 : 0;
    int child = MeshComm::getChildCount();

    if (online != lastOnline || known != lastKnown || sdState != lastSd ||
        meshState != lastMesh || child != lastChild) {
        lastOnline = online;
        lastKnown = known;
        lastSd = sdState;
        lastMesh = meshState;
        lastChild = child;
        overviewDirty = true;
    }

    if (overviewDirty) {
        paintOverview();
        overviewDirty = false;
    }

    if (eventsDirty) paintEvents();
#endif
}
