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
    // Host link is USB-Serial-JTAG on GPIO19/20 (Serial when CDC_ON_BOOT=1).
    Serial.begin(UPLINK_USB_BAUD);
    unsigned long serialWait = millis();
    while (!Serial && millis() - serialWait < 3000) {
        delay(10);
    }
    delay(200);
    Serial.print("\r\n[ROOT_BOOT] USB-SERIAL-JTAG ALIVE (GPIO19/20)\r\n");
    Serial.flush();
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

    // 3. Bring up the critical communication path before optional peripherals.
    // SD/TFT faults must never prevent REGISTER or host command handling.
    MessageHandler::init();
    MeshComm::setMessageCallback(onMessageReceived);
    MeshComm::setMeshMessageCallback(onMeshMessage);
    MeshComm::init();
    MeshBridge::init();
    if (deviceConfig.uplink_mode == UPLINK_USB) {
        Serial.printf("\r\n[ROOT_BOOT] PROTOCOL READY; baud=%d; frame=A5 5A\r\n",
                      UPLINK_USB_BAUD);
        Serial.flush();
    }

    // 4. Initialize SD card (optional). Missing card must not reboot the node.
#ifdef ENABLE_SD_CARD
    if (!SdStorage::init()) {
        Debug::println(F("[MAIN] WARNING: SD unavailable — data APIs will fail until a card is mounted"));
    }
#else
    Debug::println(F("[MAIN] SD support not compiled in"));
#endif
    // Keep two short ASCII boot markers even in framed USB mode. They let a
    // plain serial terminal prove that SD failure did not stop the firmware;
    // the host frame decoder treats these bytes as harmless unframed data.
    if (deviceConfig.uplink_mode == UPLINK_USB) {
#ifdef ENABLE_SD_CARD
        Serial.printf("\r\n[ROOT_BOOT] SD=%s; continuing startup\r\n",
                      SdStorage::isReady() ? "READY" : "UNAVAILABLE");
#else
        Serial.print("\r\n[ROOT_BOOT] SD=DISABLED; continuing startup\r\n");
#endif
        Serial.flush();
    }
    // The initial REGISTER is sent before SD initialization. Report once more
    // now so the host receives the final storage state.
    MeshBridge::announceRootStatus();

    // 5. Initialize display last. The preceding marker pinpoints a panel/SPI
    // fault without hiding the fact that the protocol bridge already started.
    if (deviceConfig.uplink_mode == UPLINK_USB) {
        Serial.print("\r\n[ROOT_BOOT] DISPLAY INIT\r\n");
        Serial.flush();
    }
    Display::init();
    if (!Display::isActive()) {
        Debug::println(F("[MAIN] WARNING: display not active — continuing headless"));
    }
    if (deviceConfig.uplink_mode == UPLINK_USB) {
        Serial.printf("\r\n[ROOT_BOOT] DISPLAY=%s; entering main loop\r\n",
                      Display::isActive() ? "READY" : "OFF");
        Serial.flush();
    }

    // 6. NTP time sync (only for STA uplink mode)
    initNTP();

    bootTime = millis();
    lastStatusReport = millis();

    Debug::println(F("[MAIN] Init done, entering main loop"));
    Debug::printf("[MAIN] SD=%s Display=%s MeshInit=ok\n",
#ifdef ENABLE_SD_CARD
                  SdStorage::isReady() ? "ready" : "missing",
#else
                  "disabled",
#endif
                  Display::isActive() ? "ok" : "off");
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
