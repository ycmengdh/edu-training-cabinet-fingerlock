/**
 * display.h - TFT status display for Root Node
 * Simple text-based display using TFT_eSPI (not LVGL).
 * Shows: device ID, Mesh status, connected cabinet count,
 *        uplink status, SD card status.
 */
#ifndef DISPLAY_H
#define DISPLAY_H

#include <Arduino.h>

class Display {
public:
    // Initialize TFT display
    static void init();

    // Refresh display with current status (called from main loop)
    static void update();

private:
    static bool initialized;
    static unsigned long lastUpdate;
};

#endif // DISPLAY_H
