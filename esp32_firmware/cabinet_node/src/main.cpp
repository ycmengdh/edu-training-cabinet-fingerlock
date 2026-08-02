/**
 * main.cpp - 柜子节点主程序（ESP32-S3）
 * 学校实训柜指纹权限管理系统
 *
 * 功能概述：
 *   - AS608 指纹采集/识别（UART2 + 可控上电）
 *   - 4 路锁 + 锁 LED（74HC595）
 *   - 5 路按键（4 开锁 + 1 取消 / 长按切换调试模式）
 *   - ESP-MESH 自组网（日常）：经根节点与上位机通信
 *   - 调试模式：UART0 串口协议帧直连上位机（与根节点 USB 上行同协议）
 *   - 协议帧：0xA5 0x5A 帧头 + CRC-16/MODBUS + 分片支持
 *   - 权限缓存（NVS A/B 双区 + CRC32，离线可用）
 *   - 日志缓存（Flash 环形缓冲，断网续传）
 *
 * 硬件连接见 config.h 与 esp32_firmware/doc/柜子/IO分配.md
 */

#include <Arduino.h>
#include <esp_system.h>
#include "debug.h"
#include <WiFi.h>
#include <time.h>
#include "config.h"
#include "storage.h"
#include "fingerprint.h"
#include "lock_control.h"
#include "key_handler.h"
#include "mesh_comm.h"
#include "message_handler.h"
#include "logger.h"
#include "app_protocol.h"

// ====== 全局变量 ======
DeviceConfig deviceConfig;          // 设备配置
unsigned long lastStatusReport = 0; // 上次状态上报时刻
unsigned long bootTime         = 0;

// NTP 配置（Root STA 上行模式时自动同步）
const char *NTP_SERVER_1 = "ntp.aliyun.com";
const char *NTP_SERVER_2 = "pool.ntp.org";
const long  GMT_OFFSET_SEC = 8 * 3600;   // 东八区
const int   DAYLIGHT_OFFSET_SEC = 0;

static const char *resetReasonName(esp_reset_reason_t reason) {
    switch (reason) {
        case ESP_RST_POWERON: return "POWERON";
        case ESP_RST_EXT: return "EXTERNAL";
        case ESP_RST_SW: return "SOFTWARE";
        case ESP_RST_PANIC: return "PANIC";
        case ESP_RST_INT_WDT: return "INT_WDT";
        case ESP_RST_TASK_WDT: return "TASK_WDT";
        case ESP_RST_WDT: return "WDT";
        case ESP_RST_DEEPSLEEP: return "DEEPSLEEP";
        case ESP_RST_BROWNOUT: return "BROWNOUT";
        case ESP_RST_SDIO: return "SDIO";
        default: return "UNKNOWN";
    }
}

// ====== 消息接收回调（本机消息） ======
// 子节点：收到 Root 下发的命令
// 调试模式：收到 PC 直连命令
// Root：本机作为消息目标的命令（由 MeshBridge 路由进来）
void onMessageReceived(const String &message) {
    // Binary app envelope (from Mesh or UART0 host) or legacy full JSON.
    // Length-aware: binary payloads may contain 0x00.
    if (message.length() >= APP_ENVELOPE_MIN) {
        AppMessageView view;
        if (appDecode((const uint8_t *)message.c_str(), (int)message.length(), view)) {
            MessageHandler::handleIncomingApp(view);
            return;
        }
    }
    // Legacy full JSON only when not a binary envelope
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
    // 与根节点相同：Serial 波特率 921600 + 协议帧。
    // 柜子物理口为 UART0（GPIO43 TX / GPIO44 RX），非 USB CDC。
    Serial.begin(DEBUG_UART_BAUD, SERIAL_8N1, DEBUG_UART_RX_PIN, DEBUG_UART_TX_PIN);
    delay(300);
    esp_reset_reason_t resetReason = esp_reset_reason();
    Serial.print("\r\n[CABINET_BOOT] UART0-SERIAL ALIVE (GPIO43/44)\r\n");
    Serial.printf("[CABINET_BOOT] RESET_REASON=%d(%s)\r\n",
                  (int)resetReason, resetReasonName(resetReason));
    Serial.flush();

    Debug::println();
    Debug::println(F("========================================"));
    Debug::println(F("  ESP32 Cabinet Node Firmware v2.5"));
    Debug::println(F("  Fingerprint Lock + Mesh / UART0 Host"));
    Debug::println(F("========================================"));

    Storage::begin();
    Storage::loadDeviceConfig(deviceConfig);
    // 柜子固件强制非 Root
    bool cfgDirty = false;
    if (deviceConfig.is_root) {
        deviceConfig.is_root = false;
        cfgDirty = true;
        Debug::println(F("[MAIN] force is_root=false for cabinet firmware"));
    }
    // 与根节点一致：空 ID / 默认 CABINET_001 / 旧 CABINET_* 占位名
    // 一律改成 "CAB_" + STA MAC 后 12 位十六进制（大写、无冒号）。
    // 这样多柜子不会撞 CABINET_001，上位机也能按硬件身份定位。
    if (deviceConfig.device_id.length() == 0 ||
        deviceConfig.device_id == DEVICE_ID_DEFAULT ||
        deviceConfig.device_id.startsWith("CABINET_")) {
        uint8_t mac[6];
        WiFi.macAddress(mac);
        char id[20];
        snprintf(id, sizeof(id), "CAB_%02X%02X%02X%02X%02X%02X",
                 mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
        deviceConfig.device_id = String(id);
        if (deviceConfig.device_name.length() == 0 ||
            deviceConfig.device_name == "Cabinet Node") {
            deviceConfig.device_name = "Cabinet Node";
        }
        cfgDirty = true;
        Debug::printf("[MAIN] set default cabinet device_id=%s\n",
                      deviceConfig.device_id.c_str());
    }
    if (cfgDirty) {
        Storage::saveDeviceConfig(deviceConfig);
    }

    Debug::setDeviceId(deviceConfig.device_id);
    // 柜子 UART0 始终按根节点 USB 方式封 LOG 帧，上位机同一解析路径
    Debug::setFraming(true);
    Debug::init();
    Debug::printf("[MAIN] Device ID: %s, Name: %s\n",
                  deviceConfig.device_id.c_str(),
                  deviceConfig.device_name.c_str());
    Debug::printf("[MAIN] Role: Cabinet, Work mode: Mesh+UART0, host: UART0@%d\n",
                  DEBUG_UART_BAUD);

    LockControl::init();
    KeyHandler::init();
    // 指纹模块初始化：失败重试最多 3 轮，避免冷启动一次抖动导致指纹功能永久不可用
    for (int i = 0; i < 3; i++) {
        if (Fingerprint::init()) break;
        Debug::printf("[MAIN] Fingerprint init failed (round %d/3), retry in 1s\n", i + 1);
        delay(1000);
    }
    Logger::init();
    MessageHandler::init();

    MeshComm::setMessageCallback(onMessageReceived);
    MeshComm::setMeshMessageCallback(onMeshMessage);
    MeshComm::init();

    initNTP();
    Logger::setNetworkReady(MeshComm::isConnected());

    bootTime = millis();
    lastStatusReport = millis();

    Serial.printf("\r\n[CABINET_BOOT] PROTOCOL READY; baud=%d; frame=A5 5A; mode=MESH+UART0\r\n",
                  DEBUG_UART_BAUD);
    Serial.flush();

    Debug::println(F("[MAIN] Init done, entering main loop"));
    Debug::println(F("----------------------------------------"));
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
    DeviceConfig latestConfig;
    Storage::loadDeviceConfig(latestConfig);
    data += "\"fingerprint_count\":" + String(latestConfig.fingerprint_count) + ",";
    data += "\"perm_count\":" + String(Storage::getPermissionCount()) + ",";
    data += "\"perm_version\":" + String(Storage::getPermissionVersion()) + ",";
    data += "\"log_pending\":" + String(Logger::getPendingCount()) + ",";
    data += "\"mesh_layer\":" + String(MeshComm::getMeshLayer()) + ",";
    data += "\"child_count\":" + String(MeshComm::getChildCount()) + ",";
    data += "\"free_heap\":" + String(ESP.getFreeHeap()) + ",";
    data += "\"mesh_send_failures\":" + String(MeshComm::getSendFailureCount()) + ",";
    data += "\"mesh_queue_full\":" + String(MeshComm::getQueueFullCount()) + ",";
    data += "\"mesh_recoveries\":" + String(MeshComm::getRecoveryCount()) + ",";
    data += "\"time_synced\":" + String(Storage::isTimeSynced() ? "true" : "false");
    data += "}";

    MeshComm::sendMessage("STATUS_REPORT", data);
}

// ====== 主循环 ======
void loop() {
    // 1. 按键状态更新（含消抖和长按检测）
    KeyHandler::update();

    // 2. 检测短按按键，触发指纹验证流程
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
    // 排空延迟的 mesh 事件日志（sys_evt 任务栈太小不能直接打印）
    MeshComm::drainEventLog();

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

    // 短暂让出 CPU
    delay(5);
}
