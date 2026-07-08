/**
 * wifi_manager.h - WiFi 管理模块
 * 支持 STA 模式（连接路由器）和 AP 模式（开启热点）
 */
#ifndef WIFI_MANAGER_H
#define WIFI_MANAGER_H

#include <Arduino.h>
#include <WiFi.h>
#include "config.h"

class WifiManager {
public:
    // 以 STA 模式启动，连接路由器
    // 返回 true 表示连接成功
    static bool startSTA(const String &ssid, const String &password,
                         unsigned long timeoutMs = 15000);

    // 以 AP 模式启动，开启热点
    // SSID = ESP32_<MAC后4位>，密码 = AP_DEFAULT_PASSWORD，IP = 192.168.4.1
    static bool startAP();

    // 切换工作模式并保存到 Flash（切换后需重启）
    static bool switchMode(WorkMode newMode);

    // 断开当前 WiFi 连接
    static void disconnect();

    // 获取当前工作模式
    static WorkMode getCurrentMode();

    // 获取本机 IP 地址字符串
    static String getLocalIP();

    // 获取 AP 模式 SSID（ESP32_<MAC后4位>）
    static String getAPSSID();

    // 获取 MAC 地址字符串
    static String getMACAddress();

    // STA 模式是否已连接
    static bool isSTAConnected();

    // 主循环调用，维护连接状态
    static void update();

    // 设置状态变化回调（可选）
    typedef void (*StatusCallback)(bool connected);
    static void setStatusCallback(StatusCallback cb);

private:
    static WorkMode currentMode;
    static bool staConnected;
    static StatusCallback statusCb;
};

#endif // WIFI_MANAGER_H
