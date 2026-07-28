/**
 * display.h - TFT status display for Root Node
 * Compact non-blocking status console using TFT_eSPI (not LVGL).
 * The header shows CAB/SD/Mesh/runtime; the lower area shows recent events.
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

    // Initialize TFT display
    static void init();

    // Refresh display with current status (called from main loop)
    static void update();

    // Queue a short dynamic event. Safe before init; rendered after TFT starts.
    static void postEvent(const String &text, EventLevel level = EVENT_INFO);
    static void notifyDevice(const String &deviceId, bool online);
    static void notifyDeviceTimeout(const String &deviceId);
    static void notifyCommand(const char *command, const String &deviceId);

    // True when TFT init completed (false when headless / disabled)
    static bool isActive() { return initialized; }

private:
    static bool initialized;
    static unsigned long lastUpdate;
    static bool eventsDirty;
    static uint8_t nextEventSequence;
    static String eventLines[4];
    static EventLevel eventLevels[4];
};

#endif // DISPLAY_H
