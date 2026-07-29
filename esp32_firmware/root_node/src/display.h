/**
 * display.h - Root 节点 TFT 状态台（0.96" ST7735 160x80 横屏）
 *
 * 设计目标：一眼看健康（Host / Mesh / SD / 柜数）+ 关键告警不被命令刷掉。
 * 使用 TFT_eSPI 直绘，不依赖 LVGL。显示失败不得影响 Mesh/USB 主路径。
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

    // 初始化 TFT；失败或 ROOT_DISABLE_TFT 时 isActive()=false，业务继续。
    static void init();

    // 主循环刷新：状态差分 + 多页轮播 + 事件区。
    static void update();

    // 事件队列（init 前也可调用，屏就绪后画出）。
    static void postEvent(const String &text, EventLevel level = EVENT_INFO);
    static void notifyDevice(const String &deviceId, bool online);
    static void notifyDeviceTimeout(const String &deviceId);
    static void notifyCommand(const char *command, const String &deviceId);

    static bool isActive() { return initialized; }

private:
    static bool initialized;
    static unsigned long lastUpdate;
    static unsigned long pageSinceMs;
    static uint8_t pageIndex;
    static bool forceOverview;
    static bool frameDirty;
    static bool eventsDirty;
    static bool summaryDirty;
    static uint8_t nextEventSequence;

    static constexpr int kEventSlots = 4;
    static String eventLines[kEventSlots];
    static EventLevel eventLevels[kEventSlots];

    // 命令限频
    static char lastCmdKey[40];
    static unsigned long lastCmdMs;

    static void drawChrome();
    static void paintOverview();
    static void paintResources();
    static void paintIdentity();
    static void paintEvents();
    static void paintHeaderRuntime();
    static void paintHostBadge();
    static bool hasActiveWarning();
    static const char *shortCommand(const char *command);
    static bool shouldShowCommand(const char *command);
};

#endif // DISPLAY_H
