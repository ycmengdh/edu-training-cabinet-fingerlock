/**
 * wifi_manager.h - Minimal WiFi manager for Root Node
 * The common library (mesh_comm.cpp) references WifiManager::startAP() for
 * debug mode. The root node always operates in Mesh mode, so this is a
 * minimal implementation that starts an AP hotspot.
 */
#ifndef WIFI_MANAGER_H
#define WIFI_MANAGER_H

#include <Arduino.h>

class WifiManager {
public:
    static bool startAP();
    static bool stopAP();
};

#endif // WIFI_MANAGER_H
