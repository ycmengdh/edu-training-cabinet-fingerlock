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
#include <esp_system.h>
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
#include "app_protocol.h"
#include "mem_pool.h"
#ifdef ENABLE_SD_CARD
#include "sd_storage.h"
#endif

// Arduino-ESP32 builds the framework core separately, so a project build flag
// alone may not override its weak default. This application-level override is
// the authoritative loopTask stack size.
SET_LOOP_TASK_STACK_SIZE(32 * 1024);

// ====== Global variables ======
DeviceConfig deviceConfig;
unsigned long bootTime = 0;
unsigned long lastStatusReport = 0;

// NTP config (Root STA uplink mode only)
const char *NTP_SERVER_1 = "ntp.aliyun.com";
const char *NTP_SERVER_2 = "pool.ntp.org";
const long  GMT_OFFSET_SEC = 8 * 3600;
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

// ====== Message callbacks ======
// Root: messages targeted at root itself (routed by MeshBridge)
void onMessageReceived(const String &message) {
    MessageHandler::handleIncoming(message);
}

// Root: messages from child cabinet nodes.
// MeshBridge::onMeshMessage owns side-effects (HEARTBEAT_ACK / REGISTER) and
// transparent uplink forward for both binary app envelopes and legacy JSON.
void onMeshMessage(const uint8_t *fromMac, const String &json) {
    MeshBridge::onMeshMessage(fromMac, json);
}

void onPeerConnectionChanged(const uint8_t *mac, bool connected) {
    MeshBridge::handlePeerConnection(mac, connected);
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
    MemPool::noteHeapSample();
    const uint32_t loopStackFree =
        (uint32_t)uxTaskGetStackHighWaterMark(nullptr) * sizeof(StackType_t);
    String data = "{";
    data += "\"online\":true,";
    data += "\"uptime\":" + String((millis() - bootTime) / 1000) + ",";
    data += "\"mesh_layer\":" + String(MeshComm::getMeshLayer()) + ",";
    data += "\"child_count\":" + String(MeshComm::getChildCount()) + ",";
    data += "\"route_count\":" + String(MeshBridge::getRouteCount()) + ",";
    data += "\"uplink_connected\":" + String(MeshBridge::isUplinkConnected() ? "true" : "false") + ",";
    data += "\"free_heap\":" + String(MemPool::freeInternalHeap()) + ",";
    data += "\"free_psram\":" + String(MemPool::freePsram()) + ",";
    data += "\"min_free_heap\":" + String(MemPool::minFreeInternalHeap()) + ",";
    data += "\"largest_free_block\":" + String(MemPool::largestFreeBlock()) + ",";
    data += "\"loop_stack_free\":" + String(loopStackFree) + ",";
    data += "\"mesh_link_rssi\":" + String(MeshComm::getLinkRssi()) + ",";
    data += "\"mesh_assoc_expire\":" + String(MeshComm::getApAssocExpireSeconds()) + ",";
#ifdef ENABLE_SD_CARD
    data += "\"sd_ready\":" + String(SdStorage::isReady() ? "true" : "false") + ",";
    data += "\"sd_total\":" + String((unsigned long)SdStorage::getTotalBytes()) + ",";
    data += "\"sd_used\":" + String((unsigned long)SdStorage::getUsedBytes()) + ",";
#endif
    data += "\"time_synced\":" + String(Storage::isTimeSynced() ? "true" : "false");
    data += "}";

    // Binary STATUS_REPORT (payload = data JSON)
    MessageHandler::sendMessage("STATUS_REPORT", data);
}

// ====== Initialization ======
void setup() {
    // Host link is USB-Serial-JTAG on GPIO19/20 (Serial when CDC_ON_BOOT=1).
    // Arduino HWCDC defaults to a 256-byte RX queue. SD_SAVE app frames are
    // commonly 300-700 bytes, so the default silently drops their tail and the
    // frame parser reports a CRC error. Allocate the queues before begin().
    Serial.setRxBufferSize(8192);
    Serial.setTxBufferSize(8192);
    Serial.begin(UPLINK_USB_BAUD);
    unsigned long serialWait = millis();
    while (!Serial && millis() - serialWait < 3000) {
        delay(10);
    }
    delay(200);
    esp_reset_reason_t resetReason = esp_reset_reason();
    Serial.print("\r\n[ROOT_BOOT] USB-SERIAL-JTAG ALIVE (GPIO19/20)\r\n");
    Serial.printf("[ROOT_BOOT] RESET_REASON=%d(%s)\r\n",
                  (int)resetReason, resetReasonName(resetReason));
    Serial.flush();
    Debug::println();
    Debug::println(F("========================================"));
    Debug::println(F("  ESP32 Root Node Firmware v2.5"));
    Debug::println(F("  Mesh Root + SD Data Center + TFT Display"));
    Debug::println(F("========================================"));

    // 1. Storage init and load device config from NVS
    Storage::begin();
    Storage::loadDeviceConfig(deviceConfig);
    // Root firmware always acts as Mesh root (USB/AP/STA uplink owner).
    bool cfgDirty = false;
    if (!deviceConfig.is_root) {
        deviceConfig.is_root = true;
        cfgDirty = true;
        Debug::println(F("[MAIN] force is_root=true for root firmware"));
    }
    // USB 上行时清掉 NVS 里残留的 wifi_ssid，否则 Mesh/WiFi 层可能反复扫热点
    // 打出 NO_AP_FOUND（业务根本不需要外部 WiFi）。
    if (deviceConfig.uplink_mode == UPLINK_USB &&
        deviceConfig.wifi_ssid.length() > 0) {
        Debug::printf("[MAIN] clear leftover wifi_ssid='%s' (USB uplink, pure mesh)\n",
                      deviceConfig.wifi_ssid.c_str());
        deviceConfig.wifi_ssid = "";
        deviceConfig.wifi_password = "";
        cfgDirty = true;
    }
    if (cfgDirty) {
        Storage::saveDeviceConfig(deviceConfig);
    }
    if (deviceConfig.device_id == DEVICE_ID_DEFAULT ||
        deviceConfig.device_id.startsWith("CABINET_") ||
        deviceConfig.device_id == "ROOT_001") {
        // V2.7：默认 device_id 改为 "ROOT_" + MAC 后 6 字节十六进制（大写、无冒号）
        // 便于在上位机按硬件身份快速定位根节点
        uint8_t mac[6];
        WiFi.macAddress(mac);
        char id[20];
        snprintf(id, sizeof(id), "ROOT_%02X%02X%02X%02X%02X%02X",
                 mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
        deviceConfig.device_id = String(id);
        deviceConfig.device_name = "Root Node";
        Storage::saveDeviceConfig(deviceConfig);
        Debug::printf("[MAIN] set default root device_id=%s\n", deviceConfig.device_id.c_str());
    }
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
    MeshComm::setPeerConnectionCallback(onPeerConnectionChanged);
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
#ifdef ENABLE_SD_CARD
    Display::postEvent(SdStorage::isReady() ? "SD READY" : "SD UNAVAILABLE",
                       SdStorage::isReady() ? Display::EVENT_OK : Display::EVENT_WARNING);
#endif
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
    Debug::printf("[MAIN] loop stack configured=%u bytes, free=%u bytes\n",
                  (unsigned)getArduinoLoopTaskStackSize(),
                  (unsigned)(uxTaskGetStackHighWaterMark(nullptr) * sizeof(StackType_t)));
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
    // Service host input before and after each bounded Mesh batch. This keeps
    // USB responsive during heartbeat or reconnect bursts from 100 cabinets.
    MeshBridge::update();
    MeshComm::update();
    MeshComm::drainEventLog();
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
