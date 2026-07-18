/**
 * mesh_bridge.cpp - Root 节点上行链路桥接实现
 * 支持 USB 串口 / WiFi AP / WiFi STA 三种上行链路
 * 双向桥接：Mesh 消息 <-> 上行链路（协议帧封装）
 * 维护 device_id -> Mesh MAC 路由表，透传不解析业务内容
 */
#include "mesh_bridge.h"
#include "debug.h"
#include "mesh_comm.h"
#include "message_handler.h"
#include "storage.h"
#include "protocol_frame.h"
#ifdef ENABLE_SD_CARD
#include "sd_storage.h"
#endif
#include <ArduinoJson.h>
#include <WiFi.h>

// ====== 静态成员初始化 ======
MeshBridge::RouteEntry MeshBridge::routeTable[MESH_MAX_NODE];
int MeshBridge::routeCount = 0;
UplinkMode MeshBridge::uplinkMode = UPLINK_USB;
bool MeshBridge::uplinkConnected = false;
bool MeshBridge::initialized = false;

// AP/STA TCP 上行链路资源
static WiFiServer *bridgeServer = nullptr;   // AP 模式 TCP 服务端
static WiFiClient  bridgeClient;             // 当前连接的客户端（AP）/ 上位机连接（STA）
static unsigned long lastSTAReconnect = 0;   // STA 模式上次重连时刻
static unsigned long lastRootAnnouncement = 0;
static bool hostProtocolSeen = false;
static const unsigned long ROOT_ANNOUNCE_INTERVAL_MS = 3000;

static void announceRootToHost(const char *uplink) {
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    String data = "{\"device_name\":\"" + cfg.device_name +
                  "\",\"is_root\":true,\"firmware_version\":\"" FIRMWARE_VERSION "\"";
    data += ",\"uplink\":\"";
    data += uplink;
    data += "\"";
#ifdef ENABLE_SD_CARD
    data += ",\"sd_ready\":";
    data += SdStorage::isReady() ? "true" : "false";
#else
    data += ",\"sd_ready\":false";
#endif
    data += "}";
    MeshBridge::sendToUplink(ProtocolFrame::buildMessage("REGISTER", cfg.device_id, data));
    lastRootAnnouncement = millis();
}

// ====== 初始化 ======
void MeshBridge::init() {
    if (initialized) return;

    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    uplinkMode = cfg.uplink_mode;

    Debug::printf("[BRIDGE] uplink mode: %s\n",
                  uplinkMode == UPLINK_USB ? "USB serial" :
                  uplinkMode == UPLINK_AP  ? "WiFi AP" : "WiFi STA");

    switch (uplinkMode) {
        case UPLINK_USB: initUSB(); break;
        case UPLINK_AP:  initAP();  break;
        case UPLINK_STA: initSTA(); break;
    }

    initialized = true;
}

void MeshBridge::announceRootStatus() {
    const char *uplink = uplinkMode == UPLINK_USB ? "usb" :
                         uplinkMode == UPLINK_AP ? "ap" : "sta";
    announceRootToHost(uplink);
}

// ====== USB 串口上行 ======
void MeshBridge::initUSB() {
    // Host uplink is USB-Serial-JTAG (GPIO19/20) when CDC_ON_BOOT=1.
    // Baud is host-side for CDC; keep Serial ready and announce root.
    Serial.flush();
    uplinkConnected = true;
    Debug::printf("[BRIDGE] USB-Serial-JTAG uplink ready (GPIO19/20)\n");
    announceRootToHost("usb");
}

void MeshBridge::updateUSB() {
    // 读取串口字节并送入协议帧解码器
    readUplink();
    if (!hostProtocolSeen && millis() - lastRootAnnouncement >= ROOT_ANNOUNCE_INTERVAL_MS) {
        announceRootToHost("usb");
    }
}

// ====== WiFi AP TCP 上行 ======
void MeshBridge::initAP() {
    // Root 开 AP 热点供上位机连接（注意：与 Mesh AP 可能冲突，建议用于非 Mesh 场景）
    IPAddress local_IP, gateway, subnet;
    local_IP.fromString(AP_IP_ADDR);
    gateway.fromString(AP_GATEWAY);
    subnet.fromString(AP_SUBNET);
    WiFi.softAPConfig(local_IP, gateway, subnet);
    bool ok = WiFi.softAP(UPLINK_AP_SSID, UPLINK_AP_PASSWORD);
    if (!ok) {
        Debug::println(F("[BRIDGE] AP hotspot start failed"));
        return;
    }
    if (bridgeServer == nullptr) {
        bridgeServer = new WiFiServer(UPLINK_TCP_PORT);
    }
    bridgeServer->begin(UPLINK_TCP_PORT);
    uplinkConnected = false;
    Debug::printf("[BRIDGE] AP uplink started, SSID=%s, TCP=%d, IP=%s\n",
                  UPLINK_AP_SSID, UPLINK_TCP_PORT,
                  WiFi.softAPIP().toString().c_str());
}

void MeshBridge::updateAP() {
    if (bridgeServer == nullptr) return;

    if (uplinkConnected && bridgeClient.connected()) {
        readUplink();
    } else {
        uplinkConnected = false;
        hostProtocolSeen = false;
        // 接受新连接
        bridgeClient = bridgeServer->accept();
        if (bridgeClient) {
            uplinkConnected = true;
            ProtocolFrame::resetDecoder();
            Debug::printf("[BRIDGE] host connected to AP, IP=%s\n",
                          bridgeClient.remoteIP().toString().c_str());
            announceRootToHost("ap");
        }
    }
}

// ====== WiFi STA TCP 上行 ======
void MeshBridge::initSTA() {
    // Root 通过 Mesh DS 连接路由器后，作为 TCP 客户端连接上位机
    // WiFi STA 连接由 Mesh 路由器配置处理，这里仅初始化 TCP 客户端
    uplinkConnected = false;
    Debug::println(F("[BRIDGE] STA uplink: waiting for Mesh DS to connect to router..."));
}

void MeshBridge::updateSTA() {
    unsigned long now = millis();

    // 检查是否有路由器分配的 IP（Mesh DS 已连接）
    if (WiFi.localIP() == IPAddress(0, 0, 0, 0)) {
        if (uplinkConnected) {
            uplinkConnected = false;
            hostProtocolSeen = false;
            bridgeClient.stop();
            Debug::println(F("[BRIDGE] STA failed to obtain IP, disconnect TCP"));
        }
        return;
    }

    // 维护 TCP 客户端连接
    if (uplinkConnected && bridgeClient.connected()) {
        readUplink();
    } else {
        if (uplinkConnected) {
            uplinkConnected = false;
            hostProtocolSeen = false;
            Debug::println(F("[BRIDGE] TCP connection to host disconnected"));
        }
        // 按间隔重连
        if (now - lastSTAReconnect >= UPLINK_TCP_RECONNECT_MS || lastSTAReconnect == 0) {
            lastSTAReconnect = now;
            DeviceConfig cfg;
            Storage::loadDeviceConfig(cfg);
            Debug::printf("[BRIDGE] connecting to host %s:%u ...\n",
                          cfg.server_ip.c_str(), cfg.server_port);
            bridgeClient.stop();
            if (bridgeClient.connect(cfg.server_ip.c_str(), cfg.server_port, 5000)) {
                uplinkConnected = true;
                ProtocolFrame::resetDecoder();
                Debug::printf("[BRIDGE] connected to host %s:%u\n",
                              cfg.server_ip.c_str(), cfg.server_port);
                announceRootToHost("sta");
            } else {
                Debug::println(F("[BRIDGE] connect to host failed"));
            }
        }
    }
}

// ====== 主循环更新 ======
void MeshBridge::update() {
    if (!initialized) return;

    static unsigned long lastRouteSweep = 0;
    unsigned long now = millis();
    if (now - lastRouteSweep >= MESH_ROUTE_SWEEP_MS || lastRouteSweep == 0) {
        lastRouteSweep = now;
        expireStaleRoutes();
    }

    switch (uplinkMode) {
        case UPLINK_USB: updateUSB(); break;
        case UPLINK_AP:  updateAP();  break;
        case UPLINK_STA: updateSTA(); break;
    }
}

// ====== Root 收到子节点 Mesh 消息：转发到上行链路 ======
void MeshBridge::onMeshMessage(const uint8_t *fromMac, const String &json) {
    // 解析 device_id 并更新路由表
    {
        StaticJsonDocument<512> doc;
        if (!deserializeJson(doc, json)) {
            const char *did = doc["device_id"] | "";
            if (strlen(did) > 0) {
                addRoute(String(did), fromMac);
            }
        }
    }
    // 透传转发到上行链路（不解析业务内容）
    sendToUplink(json);
}

// ====== 发送 JSON 到上行链路（协议帧封装） ======
bool MeshBridge::sendToUplink(const String &json) {
    if (!uplinkConnected && uplinkMode != UPLINK_USB) {
        return false;
    }

    // 编码为协议帧。大消息可能包含多个协议分片，按实际容量临时分配。
    int frameCapacity = ProtocolFrame::getEncodedCapacity(json);
    if (frameCapacity < 0) {
        Debug::println(F("[BRIDGE] message exceeds frame reassembly limit"));
        return false;
    }
    uint8_t *frameBuf = (uint8_t *)malloc(frameCapacity);
    if (frameBuf == nullptr) {
        Debug::println(F("[BRIDGE] frame buffer allocation failed"));
        return false;
    }
    int frameLen = ProtocolFrame::encode(json, frameBuf, frameCapacity);
    if (frameLen < 0) {
        Debug::println(F("[BRIDGE] frame encode failed"));
        free(frameBuf);
        return false;
    }

    bool ok = writeUplink(frameBuf, frameLen);
    free(frameBuf);
    if (!ok && uplinkMode != UPLINK_USB) {
        uplinkConnected = false;
        hostProtocolSeen = false;
    }
    return ok;
}

// ====== 写入数据到当前上行链路 ======
bool MeshBridge::writeUplink(const uint8_t *data, int len) {
    if (uplinkMode == UPLINK_USB) {
        size_t sent = Serial.write(data, len);
        Serial.flush();
        return (sent == (size_t)len);
    } else if (uplinkMode == UPLINK_AP) {
        if (!bridgeClient.connected()) return false;
        size_t sent = bridgeClient.write(data, len);
        bridgeClient.flush();
        return (sent == (size_t)len);
    } else if (uplinkMode == UPLINK_STA) {
        if (!bridgeClient.connected()) return false;
        size_t sent = bridgeClient.write(data, len);
        bridgeClient.flush();
        return (sent == (size_t)len);
    }
    return false;
}

// Plain-text probes for serial tools that cannot send binary frames yet.
// Reply with "PONG" so the host can prove the correct COM port is open.
static void handlePlainTextProbe(uint8_t b) {
    static char line[16];
    static uint8_t pos = 0;
    if (b == '\r') return;
    if (b == '\n') {
        line[pos < sizeof(line) ? pos : (sizeof(line) - 1)] = 0;
        if (strcasecmp(line, "PING") == 0 || strcasecmp(line, "AT") == 0) {
            Serial.print("PONG\r\n");
            Serial.flush();
        } else if (strcasecmp(line, "HELP") == 0) {
            Serial.print("OK REGISTER_FRAME=HEX baud=921600\r\n");
            Serial.flush();
        }
        pos = 0;
        return;
    }
    if (pos + 1 < sizeof(line) && b >= 0x20 && b < 0x7F) {
        line[pos++] = (char)b;
    } else {
        pos = 0;
    }
}

// ====== 读取上行链路字节并送入协议帧解码器 ======
void MeshBridge::readUplink() {
    if (uplinkMode == UPLINK_USB) {
        while (Serial.available()) {
            uint8_t b = Serial.read();
            // Frame decoder ignores non-A5 bytes while waiting for head, so
            // plain-text probes can coexist with framed protocol traffic.
            handlePlainTextProbe(b);
            String json;
            if (ProtocolFrame::decode(b, json)) {
                hostProtocolSeen = true;
                handleUplinkMessage(json);
            }
        }
    } else {
        // AP / STA 模式从 TCP 客户端读取
        if (!bridgeClient.connected()) {
            uplinkConnected = false;
            hostProtocolSeen = false;
            return;
        }
        while (bridgeClient.available()) {
            uint8_t b = bridgeClient.read();
            String json;
            if (ProtocolFrame::decode(b, json)) {
                hostProtocolSeen = true;
                handleUplinkMessage(json);
            }
        }
    }
}

// ====== 处理上行链路收到的 JSON（路由到本机或子节点） ======
void MeshBridge::handleUplinkMessage(const String &json) {
    Debug::printf("[BRIDGE] << uplink receive: %s\n", json.c_str());

    // 解析 device_id 决定路由
    DynamicJsonDocument doc(65536);
    DeserializationError err = deserializeJson(doc, json);
    if (err) {
        Debug::printf("[BRIDGE] JSON parse failed: %s\n", err.c_str());
        return;
    }

    const char *did = doc["device_id"] | "";
    const char *cmd = doc["cmd"] | "";

    // 检查是否发往本机（Root）
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);

    // Empty device_id is an intentional broadcast from the PC.
    if (strlen(did) == 0) {
        // Root-management commands may arrive before the host has received
        // the root REGISTER announcement (for example from the standalone AP
        // configuration window). Handle them locally instead of broadcasting
        // them to every cabinet.
        bool rootCommand = strcmp(cmd, "REGISTER") == 0 ||
                           strcmp(cmd, "READ_CONFIG") == 0 ||
                           strcmp(cmd, "WRITE_CONFIG") == 0 ||
                           strcmp(cmd, "READ_STATUS") == 0 ||
                           strcmp(cmd, "REBOOT") == 0 ||
                           strcmp(cmd, "TIME_SYNC") == 0 ||
                           strcmp(cmd, "SD_QUERY") == 0 ||
                           strcmp(cmd, "SD_SAVE") == 0 ||
                           strcmp(cmd, "SD_QUERY_VERSION") == 0 ||
                           strcmp(cmd, "UPLOAD_FP_TEMPLATE") == 0 ||
                           strcmp(cmd, "DOWNLOAD_FP_TEMPLATE") == 0 ||
                           strcmp(cmd, "DELETE_FP_TEMPLATE") == 0;
        if (rootCommand) {
            MessageHandler::handleIncoming(json);
            return;
        }
        expireStaleRoutes();
        int sent = 0;
        for (int i = 0; i < routeCount; i++) {
            if (routeTable[i].valid && MeshComm::sendToNode(routeTable[i].mac, json)) {
                sent++;
            }
        }
        Debug::printf("[BRIDGE] broadcast forwarded to %d cabinet nodes\n", sent);
        return;
    }

    if (strcmp(did, cfg.device_id.c_str()) == 0) {
        // 目标是 Root 本机：交给消息处理器
        MessageHandler::handleIncoming(json);
        return;
    }

    // 目标是子节点：查路由表转发
    uint8_t targetMac[6];
    if (!lookupRoute(String(did), targetMac)) {
        Debug::printf("[BRIDGE] route for device %s not found, dropped\n", did);
        // 回复错误：设备未注册
        String errJson = "{\"cmd\":\"ERROR\",\"device_id\":\"" + cfg.device_id +
                         "\",\"data\":{\"error_code\":" + String(ERR_DEVICE_NOT_REGISTER) +
                         ",\"message\":\"device not registered: " + String(did) + "\"}}";
        sendToUplink(errJson);
        return;
    }

    // 通过 Mesh 发送到子节点（原始 JSON，不加帧）
    bool ok = MeshComm::sendToNode(targetMac, json);
    Debug::printf("[BRIDGE] forward to %s [%s]: %s\n",
                  did, MeshComm::macToString(targetMac).c_str(),
                  ok ? "success" : "failed");
}

// ====== 上行链路状态 ======
bool MeshBridge::isUplinkConnected() {
    if (uplinkMode == UPLINK_USB) return true;
    return uplinkConnected;
}

UplinkMode MeshBridge::getUplinkMode() {
    return uplinkMode;
}

// ====== 路由表操作 ======
void MeshBridge::addRoute(const String &deviceId, const uint8_t *mac) {
    // Update an existing device even if its previous route expired.
    for (int i = 0; i < routeCount; i++) {
        if (strcmp(routeTable[i].deviceId, deviceId.c_str()) == 0) {
            memcpy(routeTable[i].mac, mac, 6);
            routeTable[i].lastSeen = millis();
            routeTable[i].valid = true;
            return;
        }
    }

    // Reuse an expired slot before growing the high-water mark.
    for (int i = 0; i < routeCount; i++) {
        if (!routeTable[i].valid) {
            strncpy(routeTable[i].deviceId, deviceId.c_str(), sizeof(routeTable[i].deviceId) - 1);
            routeTable[i].deviceId[sizeof(routeTable[i].deviceId) - 1] = '\0';
            memcpy(routeTable[i].mac, mac, 6);
            routeTable[i].lastSeen = millis();
            routeTable[i].valid = true;
            Debug::printf("[BRIDGE] route added: %s -> %s (reuse slot %d)\n",
                          deviceId.c_str(), MeshComm::macToString(mac).c_str(), i);
            return;
        }
    }
    if (routeCount >= MESH_MAX_NODE) {
        Debug::println(F("[BRIDGE] routing table full"));
        return;
    }
    strncpy(routeTable[routeCount].deviceId, deviceId.c_str(),
            sizeof(routeTable[routeCount].deviceId) - 1);
    routeTable[routeCount].deviceId[sizeof(routeTable[routeCount].deviceId) - 1] = '\0';
    memcpy(routeTable[routeCount].mac, mac, 6);
    routeTable[routeCount].lastSeen = millis();
    routeTable[routeCount].valid = true;
    routeCount++;
    Debug::printf("[BRIDGE] route added: %s -> %s (total %d)\n",
                  deviceId.c_str(), MeshComm::macToString(mac).c_str(), routeCount);
}

bool MeshBridge::lookupRoute(const String &deviceId, uint8_t *mac) {
    unsigned long now = millis();
    for (int i = 0; i < routeCount; i++) {
        if (routeTable[i].valid && strcmp(routeTable[i].deviceId, deviceId.c_str()) == 0) {
            if (!isRouteFresh(i, now)) {
                routeTable[i].valid = false;
                MessageHandler::handleDeviceOffline(String(routeTable[i].deviceId));
                return false;
            }
            memcpy(mac, routeTable[i].mac, 6);
            return true;
        }
    }
    return false;
}

int MeshBridge::getRouteCount() {
    int active = 0;
    unsigned long now = millis();
    for (int i = 0; i < routeCount; i++) {
        if (isRouteFresh(i, now)) active++;
    }
    return active;
}

int MeshBridge::broadcastToCabinets(const String &json) {
    expireStaleRoutes();
    int sent = 0;
    for (int i = 0; i < routeCount; i++) {
        if (routeTable[i].valid && MeshComm::sendToNode(routeTable[i].mac, json)) {
            sent++;
        }
    }
    return sent;
}

bool MeshBridge::isRouteFresh(int index, unsigned long now) {
    return index >= 0 && index < routeCount && routeTable[index].valid &&
           now - routeTable[index].lastSeen < MESH_ROUTE_TIMEOUT_MS;
}

void MeshBridge::expireStaleRoutes() {
    unsigned long now = millis();
    for (int i = 0; i < routeCount; i++) {
        if (routeTable[i].valid && !isRouteFresh(i, now)) {
            routeTable[i].valid = false;
            String deviceId(routeTable[i].deviceId);
            Debug::printf("[BRIDGE] route expired: %s\n", deviceId.c_str());
            MessageHandler::handleDeviceOffline(deviceId);
        }
    }
}
