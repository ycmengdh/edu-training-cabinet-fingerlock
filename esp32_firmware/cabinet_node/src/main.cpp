/**
 * esp32_firmware.ino - ESP32 指纹锁下位机主程序（V2.0 Mesh版本）
 * 学校实训柜指纹权限管理系统
 *
 * 功能概述：
 *   - AS608 指纹采集/识别
 *   - 4 路继电器锁控制（低电平触发）
 *   - 4 路按键（触发指纹验证 / 长按切换调试模式）
 *   - ESP-MESH 自组网：40 台 ESP32 树状组网，1 Root 桥接上位机
 *   - Root 上行链路：USB 串口 / WiFi AP / WiFi STA（TCP）
 *   - 调试模式：AP+TCP 直连（单台维护）
 *   - 协议帧：0xA5 0x5A 帧头 + CRC-16/MODBUS + 分片支持
 *   - 权限缓存（NVS A/B 双区 + CRC32，离线可用）
 *   - 日志缓存（Flash 环形缓冲 32 扇区，断网续传）
 *   - NTP 时间同步（Root STA 上行模式自动同步）
 *
 * 硬件连接见 config.h
 *
 * 依赖库（Arduino IDE 库管理器安装）：
 *   - Adafruit_Fingerprint
 *   - ArduinoJson (v6.x)
 *   - esp32-arduino 库内置 ESP-MESH 支持
 */

#include <Arduino.h>
#include "debug.h"
#include <WiFi.h>
#include <time.h>
#include "config.h"
#include "storage.h"
#include "fingerprint.h"
#include "lock_control.h"
#include "key_handler.h"
#include "wifi_manager.h"
#include "mesh_comm.h"
#include "message_handler.h"
#include "logger.h"

// ====== 全局变量 ======
DeviceConfig deviceConfig;          // 设备配置
unsigned long lastStatusReport = 0; // 上次状态上报时刻
unsigned long lastLedToggle    = 0; // 上次 LED 切换时刻
bool ledState                  = false;
unsigned long bootTime         = 0;

// NTP 配置（Root STA 上行模式时自动同步）
const char *NTP_SERVER_1 = "ntp.aliyun.com";
const char *NTP_SERVER_2 = "pool.ntp.org";
const long  GMT_OFFSET_SEC = 8 * 3600;   // 东八区
const int   DAYLIGHT_OFFSET_SEC = 0;

// ====== 消息接收回调（本机消息） ======
// 子节点：收到 Root 下发的命令
// 调试模式：收到 PC 直连命令
// Root：本机作为消息目标的命令（由 MeshBridge 路由进来）
void onMessageReceived(const String &message) {
    MessageHandler::handleIncoming(message);
}

// ====== Mesh 消息接收回调（Root 收到子节点消息） ======
// 仅用于额外处理（如日志记录），转发由 MeshBridge 负责
void onMeshMessage(const uint8_t *fromMac, const String &json) {
    // 解析 JSON 提取日志关键字段，便于 Root 侧观察子节点行为
    // 这里仅打印日志，不做业务处理
    Debug::printf("[MAIN] Root received child node %s message: %s\n",
                  MeshComm::macToString(fromMac).c_str(), json.c_str());
}

// ====== LED 指示函数 ======
// 快闪=调试模式，中速闪=Mesh连接中，慢闪=Mesh已连接
void updateLED() {
    unsigned long now = millis();
    WorkMode mode = Storage::loadWorkMode();

    if (mode == MODE_DEBUG) {
        // 调试模式快闪
        if (now - lastLedToggle >= LED_BLINK_FAST_MS) {
            lastLedToggle = now;
            ledState = !ledState;
            digitalWrite(LED_PIN, ledState ? HIGH : LOW);
        }
    } else if (MeshComm::isConnected()) {
        // Mesh 已连接慢闪
        if (now - lastLedToggle >= LED_BLINK_SLOW_MS) {
            lastLedToggle = now;
            ledState = !ledState;
            digitalWrite(LED_PIN, ledState ? HIGH : LOW);
        }
    } else {
        // Mesh 连接中中速闪
        if (now - lastLedToggle >= LED_BLINK_MEDIUM_MS) {
            lastLedToggle = now;
            ledState = !ledState;
            digitalWrite(LED_PIN, ledState ? HIGH : LOW);
        }
    }
}

// ====== NTP 时间同步（Root STA 上行模式） ======
void initNTP() {
    // 仅 Root 节点且 STA 上行模式才有外网，可走 NTP
    if (!deviceConfig.is_root || deviceConfig.uplink_mode != UPLINK_STA) {
        return;
    }
    if (WiFi.status() != WL_CONNECTED) {
        return;
    }
    Debug::println(F("[MAIN] Start NTP time sync..."));
    configTime(GMT_OFFSET_SEC, DAYLIGHT_OFFSET_SEC, NTP_SERVER_1, NTP_SERVER_2);

    // 等待最多 5 秒
    unsigned long start = millis();
    while (millis() - start < 5000) {
        if (Storage::isTimeSynced() || time(NULL) > 1700000000) {
            struct tm timeinfo;
            if (getLocalTime(&timeinfo, 1000)) {
                // 通过 setUnixTime 标记已同步
                Storage::setUnixTime((uint32_t)time(NULL));
                Debug::printf("[MAIN] NTP sync success: %s",
                              asctime(&timeinfo));
                return;
            }
        }
        delay(500);
    }
    Debug::println(F("[MAIN] NTP sync timeout, waiting for PC to send TIME_SYNC"));
}

// ====== 初始化 ======
void setup() {
    // 串口初始化（与上位机通信波特率 921600）
    Serial.begin(UPLINK_USB_BAUD);
    delay(300);
    Debug::println();
    Debug::println(F("========================================"));
    Debug::println(F("  ESP32 Fingerprint Lock Firmware v2.0"));
    Debug::println(F("  School Training Cabinet Fingerprint Permission System"));
    Debug::println(F("  ESP-MESH Self-organizing Network + USB/WiFi Bridge"));
    Debug::println(F("========================================"));

    // 1. LED 初始化（先点亮表示启动中）
    pinMode(LED_PIN, OUTPUT);
    digitalWrite(LED_PIN, HIGH);

    // 2. Flash 存储初始化并加载配置
    Storage::begin();
    Storage::loadDeviceConfig(deviceConfig);
    Debug::setDeviceId(deviceConfig.device_id);
    // 根据配置启用 debug 协议帧封装（仅 Root+USB+Mesh 模式）
    Debug::init();
    Debug::printf("[MAIN] Device ID: %s, Name: %s\n",
                  deviceConfig.device_id.c_str(),
                  deviceConfig.device_name.c_str());
    Debug::printf("[MAIN] Role: %s, Work mode: %s\n",
                  deviceConfig.is_root ? "Root" : "Node",
                  deviceConfig.work_mode == MODE_MESH ? "Mesh" : "Debug");
    if (deviceConfig.is_root) {
        Debug::printf("[MAIN] Uplink: %s\n",
                      deviceConfig.uplink_mode == UPLINK_USB ? "USB" :
                      deviceConfig.uplink_mode == UPLINK_AP  ? "WiFi AP" : "WiFi STA");
    }

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

    // 8. 设置消息回调（必须在 MeshComm::init 之前）
    MeshComm::setMessageCallback(onMessageReceived);
    MeshComm::setMeshMessageCallback(onMeshMessage);

    // 9. 通信初始化（根据 work_mode 启动 Mesh 或 调试模式）
    MeshComm::init();

    // 10. NTP 时间同步（仅 Root STA 上行模式有效）
    initNTP();

    // 11. 设置网络就绪标志（用于日志上报）
    Logger::setNetworkReady(MeshComm::isConnected());

    bootTime = millis();
    lastStatusReport = millis();
    lastLedToggle    = millis();

    Debug::println(F("[MAIN] Init done, entering main loop"));
    Debug::println(F("----------------------------------------"));
}

// ====== 长按切换调试模式处理 ======
// 长按 10 秒：在 Mesh 模式和 Debug 模式之间切换，切换后重启
void handleModeSwitch() {
    if (!KeyHandler::isLongPressDetected()) {
        return;
    }
    WorkMode current = Storage::loadWorkMode();
    WorkMode target  = (current == MODE_MESH) ? MODE_DEBUG : MODE_MESH;

    Debug::printf("[MAIN] Long press detected, switch work mode: %s -> %s, restart in 3 sec...\n",
                  current == MODE_MESH ? "Mesh" : "Debug",
                  target  == MODE_MESH ? "Mesh" : "Debug");

    // 保存新模式到 Flash
    Storage::saveWorkMode(target);

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

// ====== 定期状态上报 ======
void reportStatus() {
    bool lockStatus[LOCK_COUNT];
    LockControl::getLockStatus(lockStatus);

    String data = "{";
    data += "\"online\":true,";
    data += "\"uptime\":" + String((millis() - bootTime) / 1000) + ",";
    data += "\"lock_status\":[";
    for (int i = 0; i < LOCK_COUNT; i++) {
        if (i > 0) data += ",";
        data += String(lockStatus[i] ? 1 : 0);
    }
    data += "],";
    data += "\"fingerprint_count\":" + String(Fingerprint::getFingerprintCount()) + ",";
    data += "\"perm_count\":" + String(Storage::getPermissionCount()) + ",";
    data += "\"perm_version\":" + String(Storage::getPermissionVersion()) + ",";
    data += "\"log_pending\":" + String(Logger::getPendingCount()) + ",";
    data += "\"mesh_layer\":" + String(MeshComm::getMeshLayer()) + ",";
    data += "\"child_count\":" + String(MeshComm::getChildCount()) + ",";
    data += "\"time_synced\":" + String(Storage::isTimeSynced() ? "true" : "false");
    data += "}";

    MeshComm::sendMessage("STATUS_REPORT", data);
}

// ====== 主循环 ======
void loop() {
    // 1. 按键状态更新（含消抖和长按检测）
    KeyHandler::update();

    // 2. 检测长按切换 Mesh/Debug 模式
    handleModeSwitch();

    // 3. 检测短按按键，触发指纹验证流程
    int pressedKey = KeyHandler::getKeyPressed();
    if (pressedKey >= 0) {
        if (pressedKey == KEY_CANCEL_INDEX) {
            MessageHandler::onCancel();
        } else {
            MessageHandler::onKeyPressed(pressedKey);
        }
    }

    // 4. Mesh 通信收发维护（含 Root 桥接、调试模式 TCP）
    MeshComm::update();

    // 5. 消息处理状态机推进（本地指纹验证、录入、PERM_LOST 上报）
    MessageHandler::update();

    // 6. 锁控制非阻塞计时（自动关锁）
    LockControl::update();

    // 7. 日志上报维护（网络可用时批量上报）
    Logger::update();

    // 8. 维护网络就绪状态（连接状态变化时更新日志上报标志）
    Logger::setNetworkReady(MeshComm::isConnected());

    // 9. 定期状态上报（每 STATUS_REPORT_INTERVAL 一次）
    unsigned long now = millis();
    if (MeshComm::isConnected() &&
        (now - lastStatusReport >= STATUS_REPORT_INTERVAL || lastStatusReport == 0)) {
        lastStatusReport = now;
        reportStatus();
    }

    // 10. LED 指示灯更新
    updateLED();

    // 短暂让出 CPU
    delay(5);
}
