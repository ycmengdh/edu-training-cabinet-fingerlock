/**
 * wifi_manager.h - WiFi 管理模块（V2.0 简化版）
 * 仅保留 AP 调试模式：调试模式下开热点供单台 PC 直连维护
 * Mesh 模式下 WiFi 由 ESP-MESH 协议栈自管理，不使用本模块
 */
#ifndef WIFI_MANAGER_H
#define WIFI_MANAGER_H

#include <Arduino.h>
#include <WiFi.h>
#include "config.h"

class WifiManager {
public:
    // 以 AP 模式启动，开启调试热点
    // SSID = ESP32_<MAC后4位>，密码 = AP_DEFAULT_PASSWORD，IP = 192.168.4.1
    static bool startAP();

    // 断开当前 WiFi 连接
    static void disconnect();

    // 获取本机 IP 地址字符串（AP 模式返回 softAPIP）
    static String getLocalIP();

    // 获取 AP 模式 SSID（ESP32_<MAC后4位>）
    static String getAPSSID();

    // 获取 MAC 地址字符串（12位十六进制，无分隔符）
    static String getMACAddress();

    // 主循环调用，仅维护 AP 状态
    static void update();

private:
    static bool apStarted;
};

#endif // WIFI_MANAGER_H
