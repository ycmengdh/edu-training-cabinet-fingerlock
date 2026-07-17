/**
 * wifi_manager.cpp - WiFi 管理实现（V2.0 简化版）
 * 仅 AP 调试模式，STA 上行模式由 MeshBridge 管理
 */
#include "wifi_manager.h"
#include "debug.h"

bool WifiManager::apStarted = false;

String WifiManager::getMACAddress() {
    uint8_t mac[6];
    WiFi.macAddress(mac);
    char buf[18];
    snprintf(buf, sizeof(buf), "%02X%02X%02X%02X%02X%02X",
             mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
    return String(buf);
}

String WifiManager::getAPSSID() {
    // 取 MAC 最后 2 字节（4 个十六进制字符）作为后缀
    String mac = getMACAddress();
    String suffix = mac.substring(12);
    return "ESP32_" + suffix;
}

String WifiManager::getLocalIP() {
    return WiFi.softAPIP().toString();
}

bool WifiManager::startAP() {
    String apSSID = getAPSSID();
    Debug::printf("[WIFI] AP debug mode started, SSID=%s, password=%s\n",
                  apSSID.c_str(), AP_DEFAULT_PASSWORD);

    WiFi.mode(WIFI_AP);
    // 配置 AP 静态 IP
    IPAddress local_IP, gateway, subnet;
    local_IP.fromString(AP_IP_ADDR);
    gateway.fromString(AP_GATEWAY);
    subnet.fromString(AP_SUBNET);
    WiFi.softAPConfig(local_IP, gateway, subnet);

    bool ok = WiFi.softAP(apSSID.c_str(), AP_DEFAULT_PASSWORD);
    if (!ok) {
        Debug::println(F("[WIFI] AP start failed"));
        apStarted = false;
        return false;
    }
    apStarted = true;
    Debug::printf("[WIFI] AP started, IP=%s\n",
                  WiFi.softAPIP().toString().c_str());
    return true;
}

void WifiManager::disconnect() {
    WiFi.softAPdisconnect(true);
    apStarted = false;
    Debug::println(F("[WIFI] AP disconnected"));
}

void WifiManager::update() {
    // AP 模式下仅维持状态，无需重连逻辑
    // WiFi.softAPdisconnect 检测异常情况
    if (apStarted && WiFi.softAPgetStationNum() >= 0) {
        // AP 正常运行，stationNum>=0 恒成立，仅作存活检测
    }
}
