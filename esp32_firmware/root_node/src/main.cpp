/**
 * main.cpp - Root Node main program
 * Mesh root node: bridges cabinet nodes to host PC via Serial/WiFi uplink.
 * Acts as SD card data center for centralized user/fingerprint/permission storage.
 * Displays status on 0.96" TFT (ST7735 80x160).
 *
 * Hardware: ESP32-S3, TFT display, SD_MMC card, Serial uplink.
 * NO fingerprint, NO keys, NO locks, NO status LED.
 */

#include <Arduino.h>
#include <WiFi.h>
#include <time.h>
#include "config.h"
#include "debug.h"
#include "storage.h"
#include "mesh_comm.h"
#include "mesh_bridge.h"
#include "message_handler.h"
#include "display.h"
#include "protocol_frame.h"
#ifdef ENABLE_SD_CARD
#include "sd_storage.h"
#endif

// ====== Global variables ======
DeviceConfig deviceConfig;
unsigned long bootTime = 0;
unsigned long lastStatusReport = 0;

// NTP config (Root STA uplink mode only)
const char *NTP_SERVER_1 = "ntp.aliyun.com";
const char *NTP_SERVER_2 = "pool.ntp.org";
const long  GMT_OFFSET_SEC = 8 * 3600;
const int   DAYLIGHT_OFFSET_SEC = 0;

// ====== Message callbacks ======
// Root: messages targeted at root itself (routed by MeshBridge)
void onMessageReceived(const String &message) {
    MessageHandler::handleIncoming(message);
}

// Root: messages from child cabinet nodes (forwarded by MeshBridge, this is extra logging)
void onMeshMessage(const uint8_t *fromMac, const String &json) {
    Debug::printf("[MAIN] Child node %s: %s\n",
                  MeshComm::macToString(fromMac).c_str(), json.c_str());
    // Persist child state/logs first, then bridge the original message to the
    // PC. Root is the only authority allowed to commit business data.
    MessageHandler::handleMeshMessage(fromMac, json);
    MeshBridge::onMeshMessage(fromMac, json);
}

// ====== NTP time sync (Root STA uplink mode only) ======
void initNTP() {
    if (deviceConfig.uplink_mode != UPLINK_STA) return;
    if (WiFi.status() != WL_CONNECTED) return;

    Debug::println(F("[MAIN] Start NTP time sync..."));
    configTime(GMT_OFFSET_SEC, DAYLIGHT_OFFSET_SEC, NTP_SERVER_1, NTP_SERVER_2);

    unsigned long start = millis();
    while (millis() - start < 5000) {
        if (time(NULL) > 1700000000) {
            struct tm timeinfo;
            if (getLocalTime(&timeinfo, 1000)) {
                Storage::setUnixTime((uint32_t)time(NULL));
                Debug::printf("[MAIN] NTP sync success: %s", asctime(&timeinfo));
                return;
            }
        }
        delay(500);
    }
    Debug::println(F("[MAIN] NTP sync timeout, waiting for PC TIME_SYNC"));
}

// ====== Status report ======
void reportStatus() {
    String data = "{";
    data += "\"online\":true,";
    data += "\"uptime\":" + String((millis() - bootTime) / 1000) + ",";
    data += "\"mesh_layer\":" + String(MeshComm::getMeshLayer()) + ",";
    data += "\"child_count\":" + String(MeshComm::getChildCount()) + ",";
    data += "\"route_count\":" + String(MeshBridge::getRouteCount()) + ",";
    data += "\"uplink_connected\":" + String(MeshBridge::isUplinkConnected() ? "true" : "false") + ",";
#ifdef ENABLE_SD_CARD
    data += "\"sd_ready\":" + String(SdStorage::isReady() ? "true" : "false") + ",";
    data += "\"sd_total\":" + String((unsigned long)SdStorage::getTotalBytes()) + ",";
    data += "\"sd_used\":" + String((unsigned long)SdStorage::getUsedBytes()) + ",";
#endif
    data += "\"time_synced\":" + String(Storage::isTimeSynced() ? "true" : "false");
    data += "}";

    String json = ProtocolFrame::buildMessage("STATUS_REPORT",
                                              deviceConfig.device_id, data);
    MeshBridge::sendToUplink(json);
}

// ====== Initialization ======
void setup() {
    // Serial at 921600 for host communication
    Serial.begin(UPLINK_USB_BAUD);
    delay(300);
    Debug::println();
    Debug::println(F("========================================"));
    Debug::println(F("  ESP32 Root Node Firmware v2.5"));
    Debug::println(F("  Mesh Root + SD Data Center + TFT Display"));
    Debug::println(F("========================================"));

    // 1. Storage init and load device config from NVS
    Storage::begin();
    Storage::loadDeviceConfig(deviceConfig);
    Debug::setDeviceId(deviceConfig.device_id);
    Debug::setFraming(deviceConfig.uplink_mode == UPLINK_USB);

    // 2. Debug init (sets framing mode based on config)
    Debug::init();
    Debug::printf("[MAIN] Device ID: %s, Name: %s\n",
                  deviceConfig.device_id.c_str(),
                  deviceConfig.device_name.c_str());
    Debug::printf("[MAIN] Role: Root, Uplink: %s\n",
                  deviceConfig.uplink_mode == UPLINK_USB ? "USB" :
                  deviceConfig.uplink_mode == UPLINK_AP  ? "WiFi AP" : "WiFi STA");

    // 3. Initialize SD card (SD_MMC 1-bit mode)
#ifdef ENABLE_SD_CARD
    SdStorage::init();
#endif

    // 4. Initialize display
    Display::init();

    // 5. Message handler init
    MessageHandler::init();

    // 6. Set message callbacks (must be before MeshComm::init)
    MeshComm::setMessageCallback(onMessageReceived);
    MeshComm::setMeshMessageCallback(onMeshMessage);

    // 7. Initialize Mesh radio and root uplink bridge
    MeshComm::init();
    // MeshComm owns the mesh radio; MeshBridge owns the root uplink.
    MeshBridge::init();

    // 8. NTP time sync (only for STA uplink mode)
    initNTP();

    bootTime = millis();
    lastStatusReport = millis();

    Debug::println(F("[MAIN] Init done, entering main loop"));
    Debug::println(F("----------------------------------------"));
}

// ====== Main loop ======
void loop() {
    // 1. Mesh communication (includes MeshBridge update for root)
    MeshComm::update();
    MeshBridge::update();

    // 2. Message handler update
    MessageHandler::update();

    // 3. Display refresh
    Display::update();

    // 4. Periodic status report
    unsigned long now = millis();
    if (MeshComm::isConnected() &&
        (now - lastStatusReport >= STATUS_REPORT_INTERVAL || lastStatusReport == 0)) {
        lastStatusReport = now;
        reportStatus();
    }

    delay(5);
}
