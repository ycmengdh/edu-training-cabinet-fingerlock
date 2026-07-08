/**
 * esp32_firmware.ino - ESP32 指纹锁下位机主程序
 * 学校实训柜指纹权限管理系统
 *
 * 功能概述：
 *   - AS608 指纹采集/识别
 *   - 4 路继电器锁控制（低电平触发）
 *   - 4 路按键（触发指纹验证）
 *   - WiFi 双模式：STA（连路由器）/ AP（开热点），长按按键 10 秒切换
 *   - TCP 通信（STA 客户端 / AP 服务端）
 *   - 权限缓存（离线可用）、日志缓存（断网续传）
 *
 * 硬件连接见 config.h
 *
 * 依赖库（Arduino IDE 库管理器安装）：
 *   - Adafruit_Fingerprint
 *   - ArduinoJson (v6.x)
 */

#include <Arduino.h>
#include <WiFi.h>
#include "config.h"
#include "storage.h"
#include "fingerprint.h"
#include "lock_control.h"
#include "key_handler.h"
#include "wifi_manager.h"
#include "tcp_comm.h"
#include "message_handler.h"
#include "logger.h"

// ====== 全局变量 ======
DeviceConfig deviceConfig;          // 设备配置
unsigned long lastStatusReport = 0; // 上次状态上报时刻
unsigned long lastLedToggle    = 0; // 上次 LED 切换时刻
bool ledState                  = false;

// ====== LED 指示函数 ======
// 快闪=AP模式，慢闪=STA已连接，常亮=STA连接中
void updateLED() {
    unsigned long now = millis();
    if (WifiManager::getCurrentMode() == MODE_AP) {
        // AP 模式快闪
        if (now - lastLedToggle >= LED_BLINK_FAST_MS) {
            lastLedToggle = now;
            ledState = !ledState;
            digitalWrite(LED_PIN, ledState ? HIGH : LOW);
        }
    } else if (WifiManager::isSTAConnected()) {
        // STA 已连接慢闪
        if (now - lastLedToggle >= LED_BLINK_SLOW_MS) {
            lastLedToggle = now;
            ledState = !ledState;
            digitalWrite(LED_PIN, ledState ? HIGH : LOW);
        }
    } else {
        // STA 连接中常亮
        digitalWrite(LED_PIN, HIGH);
        ledState = true;
    }
}

// ====== WiFi 状态变化回调 ======
void onWifiStatusChanged(bool connected) {
    if (WifiManager::getCurrentMode() == MODE_STA) {
        if (connected) {
            // WiFi 连上后连接上位机
            TcpComm::connectToServer(deviceConfig.server_ip, deviceConfig.server_port);
            Logger::setNetworkReady(TcpComm::isConnected());
        } else {
            Logger::setNetworkReady(false);
        }
    } else {
        // AP 模式
        Logger::setNetworkReady(TcpComm::isConnected());
    }
}

// ====== TCP 消息接收回调 ======
void onTcpMessage(const String &message) {
    MessageHandler::handleIncoming(message);
}

// ====== 初始化 ======
void setup() {
    // 串口初始化（调试输出）
    Serial.begin(115200);
    delay(300);
    Serial.println();
    Serial.println(F("========================================"));
    Serial.println(F("  ESP32 指纹锁下位机固件 v1.0"));
    Serial.println(F("  学校实训柜指纹权限管理系统"));
    Serial.println(F("========================================"));

    // 1. LED 初始化（先点亮表示启动中）
    pinMode(LED_PIN, OUTPUT);
    digitalWrite(LED_PIN, HIGH);

    // 2. Flash 存储初始化并加载配置
    Storage::begin();
    Storage::loadDeviceConfig(deviceConfig);
    Serial.printf("[MAIN] 设备ID: %s\n", deviceConfig.device_id.c_str());

    // 3. 锁控制初始化（默认全部关闭）
    LockControl::init();

    // 4. 按键初始化
    KeyHandler::init();

    // 5. 指纹模块初始化
    Fingerprint::init();

    // 6. 日志模块初始化
    Logger::init();

    // 7. 消息处理器初始化
    MessageHandler::init();

    // 8. WiFi 初始化：根据 Flash 中保存的工作模式启动
    WifiManager::setStatusCallback(onWifiStatusChanged);
    WorkMode mode = deviceConfig.work_mode;
    Serial.printf("[MAIN] 启动模式: %s\n", mode == MODE_AP ? "AP" : "STA");

    if (mode == MODE_AP) {
        WifiManager::startAP();
    } else {
        WifiManager::startSTA(deviceConfig.wifi_ssid, deviceConfig.wifi_password);
    }

    // 9. TCP 通信初始化并设置消息回调
    TcpComm::setMessageCallback(onTcpMessage);
    TcpComm::init();

    // STA 模式下若 WiFi 已连上，立即连接上位机
    if (mode == MODE_STA && WifiManager::isSTAConnected()) {
        TcpComm::connectToServer(deviceConfig.server_ip, deviceConfig.server_port);
    }

    // 设置网络就绪标志（用于日志上报）
    Logger::setNetworkReady(TcpComm::isConnected());

    lastStatusReport = millis();
    lastLedToggle    = millis();

    Serial.println(F("[MAIN] 初始化完成，进入主循环"));
    Serial.println(F("----------------------------------------"));
}

// ====== 长按切换模式处理 ======
void handleModeSwitch() {
    if (KeyHandler::isLongPressDetected()) {
        Serial.println(F("[MAIN] 检测到长按，切换工作模式..."));
        WorkMode current = deviceConfig.work_mode;
        WorkMode target  = (current == MODE_AP) ? MODE_STA : MODE_AP;

        // 保存新模式到 Flash
        deviceConfig.work_mode = target;
        Storage::saveDeviceConfig(deviceConfig);
        WifiManager::switchMode(target);

        Serial.printf("[MAIN] 模式已保存为 %s，3 秒后重启...\n",
                      target == MODE_AP ? "AP" : "STA");
        // LED 快闪提示即将重启
        for (int i = 0; i < 10; i++) {
            digitalWrite(LED_PIN, HIGH);
            delay(150);
            digitalWrite(LED_PIN, LOW);
            delay(150);
        }
        delay(500);
        ESP.restart();
    }
}

// ====== 主循环 ======
void loop() {
    // 1. 按键状态更新（含消抖和长按检测）
    KeyHandler::update();

    // 2. 检测长按切换 AP/STA 模式
    handleModeSwitch();

    // 3. 检测短按按键，触发指纹验证流程
    int pressedKey = KeyHandler::getKeyPressed();
    if (pressedKey >= 0) {
        MessageHandler::onKeyPressed(pressedKey);
    }

    // 4. WiFi 状态维护
    WifiManager::update();

    // 5. TCP 通信收发维护
    TcpComm::update();

    // 6. 消息处理状态机推进（指纹轮询、鉴权等待等）
    MessageHandler::update();

    // 7. 锁控制非阻塞计时（自动关锁）
    LockControl::update();

    // 8. 日志上报维护
    Logger::update();

    // 9. 定期状态上报
    unsigned long now = millis();
    if (TcpComm::isConnected() &&
        (now - lastStatusReport >= STATUS_REPORT_INTERVAL || lastStatusReport == 0)) {
        lastStatusReport = now;
        bool lockStatus[LOCK_COUNT];
        LockControl::getLockStatus(lockStatus);
        String data = "{\"online\":true,\"lock_status\":[";
        for (int i = 0; i < LOCK_COUNT; i++) {
            if (i > 0) data += ",";
            data += String(lockStatus[i] ? 1 : 0);
        }
        data += "],\"uptime\":" + String(now / 1000) + "}";
        TcpComm::sendMessage("STATUS_REPORT", data);
    }

    // 10. LED 指示灯更新
    updateLED();

    // 短暂让出 CPU
    delay(5);
}
