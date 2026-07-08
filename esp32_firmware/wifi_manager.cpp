/**
 * wifi_manager.cpp - WiFi 管理实现
 */
#include "wifi_manager.h"
#include "storage.h"

WorkMode WifiManager::currentMode = MODE_STA;
bool WifiManager::staConnected = false;
WifiManager::StatusCallback WifiManager::statusCb = nullptr;

String WifiManager::getMACAddress() {
    uint8_t mac[6];
    WiFi.macAddress(mac);
    char buf[18];
    snprintf(buf, sizeof(buf), "%02X%02X%02X%02X%02X%02X",
             mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
    return String(buf);
}

String WifiManager::getAPSSID() {
    // 取 MAC 后 4 位
    String mac = getMACAddress();
    String suffix = mac.substring(8);  // 后 4 字节 = 8 个十六进制字符的后 4 位
    // 取后 4 个十六进制字符（即 MAC 最后 2 字节）
    suffix = mac.substring(12);
    return "ESP32_" + suffix;
}

String WifiManager::getLocalIP() {
    if (currentMode == MODE_AP) {
        return WiFi.softAPIP().toString();
    } else {
        return WiFi.localIP().toString();
    }
}

WorkMode WifiManager::getCurrentMode() {
    return currentMode;
}

bool WifiManager::isSTAConnected() {
    return (currentMode == MODE_STA) && (WiFi.status() == WL_CONNECTED);
}

void WifiManager::setStatusCallback(StatusCallback cb) {
    statusCb = cb;
}

bool WifiManager::startSTA(const String &ssid, const String &password,
                           unsigned long timeoutMs) {
    currentMode = MODE_STA;
    Serial.printf("[WIFI] STA 模式启动，连接 SSID=%s ...\n", ssid.c_str());

    WiFi.mode(WIFI_STA);
    WiFi.disconnect(true);
    delay(100);
    WiFi.begin(ssid.c_str(), password.c_str());

    unsigned long start = millis();
    while (WiFi.status() != WL_CONNECTED) {
        delay(500);
        Serial.print(".");
        if (millis() - start > timeoutMs) {
            Serial.println();
            Serial.println(F("[WIFI] STA 连接超时"));
            staConnected = false;
            if (statusCb) statusCb(false);
            return false;
        }
    }
    Serial.println();
    staConnected = true;
    Serial.printf("[WIFI] STA 连接成功, IP=%s, RSSI=%d dBm\n",
                  WiFi.localIP().toString().c_str(), WiFi.RSSI());
    if (statusCb) statusCb(true);
    return true;
}

bool WifiManager::startAP() {
    currentMode = MODE_AP;
    String apSSID = getAPSSID();
    Serial.printf("[WIFI] AP 模式启动，SSID=%s, 密码=%s\n",
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
        Serial.println(F("[WIFI] AP 启动失败"));
        return false;
    }
    Serial.printf("[WIFI] AP 已启动, IP=%s\n",
                  WiFi.softAPIP().toString().c_str());
    return true;
}

bool WifiManager::switchMode(WorkMode newMode) {
    Serial.printf("[WIFI] 切换工作模式: %s -> %s\n",
                  currentMode == MODE_AP ? "AP" : "STA",
                  newMode == MODE_AP ? "AP" : "STA");
    // 保存新模式到 Flash
    Storage::saveWorkMode(newMode);
    currentMode = newMode;
    return true;
}

void WifiManager::disconnect() {
    WiFi.disconnect(true);
    staConnected = false;
    Serial.println(F("[WIFI] 已断开连接"));
}

void WifiManager::update() {
    if (currentMode == MODE_STA) {
        bool nowConn = (WiFi.status() == WL_CONNECTED);
        if (nowConn != staConnected) {
            staConnected = nowConn;
            if (nowConn) {
                Serial.printf("[WIFI] STA 重新连接, IP=%s\n",
                              WiFi.localIP().toString().c_str());
            } else {
                Serial.println(F("[WIFI] STA 连接断开"));
            }
            if (statusCb) statusCb(nowConn);
        }
    }
}
