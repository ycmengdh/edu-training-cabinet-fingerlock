/**
 * wifi_manager.cpp - Minimal WiFi manager for Root Node
 */
#include "wifi_manager.h"
#include "config.h"
#include "debug.h"
#include <WiFi.h>

bool WifiManager::startAP() {
    IPAddress local_IP, gateway, subnet;
    local_IP.fromString(AP_IP_ADDR);
    gateway.fromString(AP_GATEWAY);
    subnet.fromString(AP_SUBNET);
    WiFi.softAPConfig(local_IP, gateway, subnet);
    bool ok = WiFi.softAP(UPLINK_AP_SSID, UPLINK_AP_PASSWORD);
    if (ok) {
        Debug::printf("[WIFI] AP started: SSID=%s IP=%s\n",
                      UPLINK_AP_SSID, WiFi.softAPIP().toString().c_str());
    } else {
        Debug::println(F("[WIFI] AP start failed"));
    }
    return ok;
}

bool WifiManager::stopAP() {
    bool ok = WiFi.softAPdisconnect(true);
    Debug::println(F("[WIFI] AP stopped"));
    return ok;
}
