/**
 * display.h - Root 节点 TFT 状态台（0.96" ST7735 160x80 横屏）
 *
 * 设计目标：一眼看健康（Host / Mesh / SD / 柜数）+ 关键告警不被命令刷掉。
 * 单页常驻：顶栏(运行时长+Host) / 中区(CAB/SD/Mesh 总览) / 底区(4 行事件)。
 * 身份/资源信息(FW/CH/ID/Heap/SD%/Layer/RSSI)仅在开机自检页显示 3 秒。
 * 使用 TFT_eSPI 直绘，不依赖 LVGL。显示失败不得影响 Mesh/USB 主路径。
 *
 * 三阶段启动（消除上电黑屏期，自检页 3s 非阻塞）：
 *   1. init()              — 硬件初始化 + 画 BOOTING 简页，setup() 早期调用
 *   2. showBootPage()      — SD/Mesh 就绪后画完整自检页，记录 bootPageEndMs
 *   3. activateMainScreen()— bootPageEndMs 到期后切主界面，initialized=true
 */
#ifndef DISPLAY_H
#define DISPLAY_H

#include <Arduino.h>

class Display {
public:
    enum EventLevel : uint8_t {
        EVENT_INFO = 0,
        EVENT_OK,
        EVENT_WARNING
    };

    // 阶段1：硬件初始化 + 画 BOOTING 简页。
    // 在 Storage/Debug 之后立即调用，让屏幕在 Mesh/SD 初始化期间已亮起。
    // 失败或 ROOT_DISABLE_TFT 时 isActive()=false，业务继续。
    static void init();

    // 阶段2：画完整开机自检页（FW/CH/ID/SD/UL/Heap/SD%/Layer/RSSI）。
    // 在 SD/Mesh 初始化完成后调用；记录 bootPageEndMs = now + 3s。
    static void showBootPage();

    // 阶段3：切换到主界面（顶栏+总览+事件区）。
    // 在 bootPageEndMs 到期后调用；此后 update() 才会刷新屏幕。
    static void activateMainScreen();

    // 主循环刷新：状态差分 + 事件区。
    static void update();

    // 事件队列（init 前也可调用，屏就绪后画出）。
    static void postEvent(const String &text, EventLevel level = EVENT_INFO);
    static void notifyDevice(const String &deviceId, bool online);
    static void notifyDeviceTimeout(const String &deviceId);
    static void notifyCommand(const char *command, const String &deviceId);

    static bool isActive() { return initialized; }
    static unsigned long getBootPageEndMs() { return bootPageEndMs; }

private:
    static bool initialized;
    static unsigned long lastUpdate;
    static unsigned long bootPageEndMs;
    static bool frameDirty;
    static bool eventsDirty;
    static bool overviewDirty;
    static uint8_t nextEventSequence;

    static constexpr int kEventSlots = 4;
    static String eventLines[kEventSlots];
    static EventLevel eventLevels[kEventSlots];

    // 命令限频
    static char lastCmdKey[40];
    static unsigned long lastCmdMs;

    static void drawChrome();
    static void paintOverview();
    static void paintEvents();
    static void paintHeaderRuntime();
    static void paintHostBadge();
    static bool hasActiveWarning();
    static const char *shortCommand(const char *command);
    static bool shouldShowCommand(const char *command);
};

#endif // DISPLAY_H
