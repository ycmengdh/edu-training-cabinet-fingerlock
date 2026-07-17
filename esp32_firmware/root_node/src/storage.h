/**
 * storage.h - Minimal NVS storage for Root Node
 * Provides device config persistence via Preferences (NVS).
 * The common library (debug.cpp, mesh_comm.cpp, mesh_bridge.cpp) references
 * Storage:: methods, so this class supplies the needed interface.
 */
#ifndef STORAGE_H
#define STORAGE_H

#include <Arduino.h>
#include "config.h"

class Storage {
public:
    static void begin();

    static bool loadDeviceConfig(DeviceConfig &cfg);
    static bool saveDeviceConfig(const DeviceConfig &cfg);

    static WorkMode loadWorkMode();
    static bool saveWorkMode(WorkMode mode);

    static void setUnixTime(uint32_t unixTime);
    static uint32_t getUnixTime();
    static bool isTimeSynced();

private:
    static bool initialized;
};

#endif // STORAGE_H
