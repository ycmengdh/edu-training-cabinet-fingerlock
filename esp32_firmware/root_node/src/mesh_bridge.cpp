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
#include "mem_pool.h"
#include "app_protocol.h"
#include "cmd_ids.h"
#ifdef ENABLE_SD_CARD
#include "sd_storage.h"
#endif
#include <ArduinoJson.h>
#include <WiFi.h>
#include <string.h>

// ====== 静态成员初始化 ======
MeshBridge::RouteEntry MeshBridge::routeTable[MESH_MAX_NODE];
int MeshBridge::routeCount = 0;
UplinkMode MeshBridge::uplinkMode = UPLINK_USB;
bool MeshBridge::uplinkConnected = false;
bool MeshBridge::initialized = false;
// V2.7：最近一次收到上行链路有效数据时刻（用于诊断 Mesh 健康度）
unsigned long MeshBridge::lastUplinkRxMs = 0;

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
    String selfMac = MeshComm::getMeshMac();
    String data = "{\"device_name\":\"" + cfg.device_name +
                  "\",\"is_root\":true,\"firmware_version\":\"" FIRMWARE_VERSION "\"";
    data += ",\"role\":\"root\"";
    data += ",\"mesh_mac\":\"" + selfMac + "\"";
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

    // Binary REGISTER：device_id=逻辑名，source_id=MAC（上位机节点唯一键）
    uint8_t out[512];
    int n = appEncode(out, (int)sizeof(out), CMD_REGISTER, appNextMsgId(), 0, 0,
                      cfg.device_id.c_str(), selfMac.c_str(),
                      (const uint8_t *)data.c_str(), (uint16_t)data.length(), 0);
    if (n > 0) {
        MeshBridge::sendToUplinkBytes(out, (uint16_t)n);
    } else {
        MeshBridge::sendToUplink(ProtocolFrame::buildMessage("REGISTER", cfg.device_id, data));
    }
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
    // Binary app envelope: always ensure source_id carries peer Mesh MAC so the
    // host can key devices by MAC even if cabinet firmware omitted source_id.
    if (json.length() >= APP_ENVELOPE_MIN) {
        AppMessageView view;
        const uint8_t *raw = (const uint8_t *)json.c_str();
        uint16_t rawLen = (uint16_t)json.length();
        if (appDecode(raw, (int)rawLen, view)) {
            char didCopy[APP_DEVICE_ID_MAX + 1];
            didCopy[0] = '\0';
            if (view.device_id_len > 0 && view.device_id != nullptr) {
                size_t n = view.device_id_len;
                if (n > APP_DEVICE_ID_MAX) n = APP_DEVICE_ID_MAX;
                memcpy(didCopy, view.device_id, n);
                didCopy[n] = '\0';
                addRoute(String(didCopy), fromMac);
            }

            char srcCopy[APP_SOURCE_ID_MAX + 1];
            srcCopy[0] = '\0';
            if (view.source_id_len > 0 && view.source_id != nullptr) {
                size_t n = view.source_id_len;
                if (n > APP_SOURCE_ID_MAX) n = APP_SOURCE_ID_MAX;
                memcpy(srcCopy, view.source_id, n);
                srcCopy[n] = '\0';
            } else if (fromMac != nullptr) {
                String peerMac = MeshComm::macToString(fromMac);
                strncpy(srcCopy, peerMac.c_str(), sizeof(srcCopy) - 1);
                srcCopy[sizeof(srcCopy) - 1] = '\0';
            }

            // Side-effects (HEARTBEAT_ACK / REGISTER) use original bytes
            MessageHandler::handleMeshMessageApp(fromMac, raw, rawLen);

            // Prefer re-encoded packet with guaranteed source_id=MAC
            uint8_t rebuilt[MESH_RX_BUFFER_SIZE];
            int n = appEncode(rebuilt, (int)sizeof(rebuilt),
                              view.cmd_id, view.msg_id, view.corr_id, view.flags,
                              didCopy[0] ? didCopy : nullptr,
                              srcCopy[0] ? srcCopy : nullptr,
                              view.payload, view.payload_len,
                              view.timestamp_unix);
            if (n > 0) {
                sendToUplinkBytes(rebuilt, (uint16_t)n);
            } else {
                sendToUplinkBytes(raw, rawLen);
            }
            return;
        }
    }

    // Legacy JSON path
    {
        StaticJsonDocument<768> doc;
        DeserializationError err = deserializeJson(doc, json);
        if (err) {
            Debug::printf("[BRIDGE] child msg JSON parse failed: %s\n", err.c_str());
        } else {
            const char *did = doc["device_id"] | "";
            if (strlen(did) > 0) {
                addRoute(String(did), fromMac);
            } else {
                Debug::printf("[BRIDGE] child msg has no device_id, json[0..80]=%.80s\n",
                              json.c_str());
            }
            // 旧 JSON：补 mesh_mac 再上行，主机可按 MAC 建节点
            if (!doc["data"].isNull() && doc["data"].is<JsonObject>()) {
                JsonObject data = doc["data"].as<JsonObject>();
                if (!data.containsKey("mesh_mac") || strlen(data["mesh_mac"] | "") == 0) {
                    data["mesh_mac"] = MeshComm::macToString(fromMac);
                    String patched;
                    serializeJson(doc, patched);
                    MessageHandler::handleMeshMessage(fromMac, patched);
                    sendToUplink(patched);
                    return;
                }
            }
        }
    }
    MessageHandler::handleMeshMessage(fromMac, json);
    sendToUplink(json);
}

bool MeshBridge::sendToUplinkBytes(const uint8_t *appMsg, uint16_t len) {
    if (appMsg == nullptr || len == 0) return false;
    if (!uplinkConnected && uplinkMode != UPLINK_USB) return false;

    int frameCapacity = ProtocolFrame::getEncodedCapacityBytes(len);
    if (frameCapacity < 0) {
        Debug::println(F("[BRIDGE] binary message exceeds frame reassembly limit"));
        return false;
    }
    uint8_t *frameBuf = MemPool::frameTxBuf();
    size_t poolSize = MemPool::frameTxBufSize();
    bool heapOwned = false;
    if (frameBuf == nullptr || (size_t)frameCapacity > poolSize) {
        frameBuf = (uint8_t *)malloc((size_t)frameCapacity);
        if (frameBuf == nullptr) return false;
        heapOwned = true;
        poolSize = (size_t)frameCapacity;
    }
    int frameLen = ProtocolFrame::encodeBytes(appMsg, len, frameBuf, (int)poolSize);
    if (frameLen < 0) {
        if (heapOwned) free(frameBuf);
        return false;
    }
    bool ok = writeUplink(frameBuf, frameLen);
    if (heapOwned) free(frameBuf);
    if (!ok && uplinkMode != UPLINK_USB) {
        uplinkConnected = false;
        hostProtocolSeen = false;
    }
    return ok;
}

// ====== 发送 JSON 到上行链路（协议帧封装） ======
bool MeshBridge::sendToUplink(const String &json) {
    if (!uplinkConnected && uplinkMode != UPLINK_USB) {
        return false;
    }

    // Phase 0: encode into static/PSRAM TX pool (common path, no malloc).
    // Rare multi-fragment messages exceeding FRAME_TX_POOL_SIZE fall back
    // to a one-shot malloc so large SD_QUERY responses still work.
    int frameCapacity = ProtocolFrame::getEncodedCapacity(json);
    if (frameCapacity < 0) {
        Debug::println(F("[BRIDGE] message exceeds frame reassembly limit"));
        return false;
    }
    uint8_t *frameBuf = MemPool::frameTxBuf();
    size_t poolSize = MemPool::frameTxBufSize();
    bool heapOwned = false;
    if (frameBuf == nullptr || (size_t)frameCapacity > poolSize) {
        frameBuf = (uint8_t *)malloc((size_t)frameCapacity);
        if (frameBuf == nullptr) {
            Debug::println(F("[BRIDGE] frame buffer allocation failed"));
            return false;
        }
        heapOwned = true;
        poolSize = (size_t)frameCapacity;
    }
    int frameLen = ProtocolFrame::encode(json, frameBuf, (int)poolSize);
    if (frameLen < 0) {
        Debug::println(F("[BRIDGE] frame encode failed"));
        if (heapOwned) free(frameBuf);
        return false;
    }

    bool ok = writeUplink(frameBuf, frameLen);
    if (heapOwned) free(frameBuf);
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
        return (sent == (size_t)len);
    } else if (uplinkMode == UPLINK_AP) {
        if (!bridgeClient.connected()) return false;
        size_t sent = bridgeClient.write(data, len);
        return (sent == (size_t)len);
    } else if (uplinkMode == UPLINK_STA) {
        if (!bridgeClient.connected()) return false;
        size_t sent = bridgeClient.write(data, len);
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
    static uint8_t payloadBuf[FRAGMENT_REASSEMBLY_BUF];

    if (uplinkMode == UPLINK_USB) {
        while (Serial.available()) {
            uint8_t b = Serial.read();
            // Frame decoder ignores non-A5 bytes while waiting for head, so
            // plain-text probes can coexist with framed protocol traffic.
            handlePlainTextProbe(b);
            int outLen = 0;
            if (ProtocolFrame::decodeBytes(b, payloadBuf, (int)sizeof(payloadBuf), outLen)) {
                hostProtocolSeen = true;
                lastUplinkRxMs = millis();
                handleUplinkPayload(payloadBuf, outLen);
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
            int outLen = 0;
            if (ProtocolFrame::decodeBytes(b, payloadBuf, (int)sizeof(payloadBuf), outLen)) {
                hostProtocolSeen = true;
                lastUplinkRxMs = millis();
                handleUplinkPayload(payloadBuf, outLen);
            }
        }
    }
}

static bool isRootOnlyCmd(uint16_t cmdId) {
    return cmdId == CMD_REGISTER || cmdId == CMD_READ_CONFIG ||
           cmdId == CMD_WRITE_CONFIG || cmdId == CMD_READ_STATUS ||
           cmdId == CMD_REBOOT || cmdId == CMD_TIME_SYNC ||
           cmdId == CMD_SD_QUERY || cmdId == CMD_SD_SAVE ||
           cmdId == CMD_SD_QUERY_VERSION || cmdId == CMD_UPLOAD_FP_TEMPLATE ||
           cmdId == CMD_DOWNLOAD_FP_TEMPLATE || cmdId == CMD_DELETE_FP_TEMPLATE ||
           cmdId == CMD_SD_QUERY_PART_ACK;
}

void MeshBridge::handleUplinkPayload(const uint8_t *data, int len) {
    if (data == nullptr || len <= 0) return;

    // ---- Binary app envelope (no 64KB JSON parse for routing) ----
    AppMessageView view;
    if (appDecode(data, len, view)) {
        DeviceConfig cfg;
        Storage::loadDeviceConfig(cfg);

        char did[APP_DEVICE_ID_MAX + 1];
        did[0] = '\0';
        if (view.device_id_len > 0 && view.device_id != nullptr) {
            size_t n = view.device_id_len;
            if (n > APP_DEVICE_ID_MAX) n = APP_DEVICE_ID_MAX;
            memcpy(did, view.device_id, n);
            did[n] = '\0';
        }

        Debug::printf("[BRIDGE] << uplink app cmd=0x%04X did=%s len=%d\n",
                      view.cmd_id, did, len);

        if (did[0] == '\0') {
            if (isRootOnlyCmd(view.cmd_id) || (view.flags & APP_FLAG_BROADCAST) == 0) {
                // Prefer local for empty-id root management cmds.
                if (isRootOnlyCmd(view.cmd_id)) {
                    MessageHandler::handleIncomingApp(view);
                    return;
                }
            }
            expireStaleRoutes();
            int sent = 0;
            for (int i = 0; i < routeCount; i++) {
                if (routeTable[i].valid &&
                    MeshComm::sendToNodeApp(routeTable[i].mac, data, (uint16_t)len)) {
                    sent++;
                }
            }
            Debug::printf("[BRIDGE] binary broadcast to %d cabinets\n", sent);
            return;
        }

        if (strcmp(did, cfg.device_id.c_str()) == 0) {
            MessageHandler::handleIncomingApp(view);
            return;
        }

        uint8_t targetMac[6];
        if (!lookupRoute(String(did), targetMac)) {
            Debug::printf("[BRIDGE] route for device %s not found, dropped\n", did);
            uint8_t errPl[96];
            int pl = packError(errPl, (int)sizeof(errPl), view.msg_id,
                               (uint16_t)ERR_DEVICE_NOT_REGISTER, "device not registered");
            if (pl > 0) {
                uint8_t out[192];
                int n = appEncode(out, (int)sizeof(out), CMD_ERROR, view.msg_id, 0,
                                  APP_FLAG_IS_ERROR, cfg.device_id.c_str(), nullptr,
                                  errPl, (uint16_t)pl, 0);
                if (n > 0) sendToUplinkBytes(out, (uint16_t)n);
            }
            return;
        }

        bool ok = MeshComm::sendToNodeApp(targetMac, data, (uint16_t)len);
        Debug::printf("[BRIDGE] binary forward to %s: %s\n", did, ok ? "ok" : "fail");
        return;
    }

    // ---- Legacy full JSON message ----
    String json;
    json.reserve(len + 1);
    for (int i = 0; i < len; i++) json += (char)data[i];
    handleUplinkMessage(json);
}

// ====== 处理上行链路收到的 JSON（路由到本机或子节点） ======
void MeshBridge::handleUplinkMessage(const String &json) {
    Debug::printf("[BRIDGE] << uplink receive(legacy JSON): %s\n", json.c_str());

    // Small doc for routing only — never 64KB on the hot path.
    StaticJsonDocument<768> doc;
    DeserializationError err = deserializeJson(doc, json);
    if (err) {
        Debug::printf("[BRIDGE] JSON parse failed: %s\n", err.c_str());
        return;
    }

    const char *did = doc["device_id"] | "";
    const char *cmd = doc["cmd"] | "";

    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);

    if (strlen(did) == 0) {
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
        MessageHandler::handleIncoming(json);
        return;
    }

    uint8_t targetMac[6];
    if (!lookupRoute(String(did), targetMac)) {
        Debug::printf("[BRIDGE] route for device %s not found, dropped\n", did);
        String errJson = "{\"cmd\":\"ERROR\",\"device_id\":\"" + cfg.device_id +
                         "\",\"data\":{\"error_code\":" + String(ERR_DEVICE_NOT_REGISTER) +
                         ",\"message\":\"device not registered: " + String(did) + "\"}}";
        sendToUplink(errJson);
        return;
    }

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

int MeshBridge::getRouteKnownCount() {
    // 总条目数（含已过期但还在数组中的），用于诊断 CAB 短暂掉线场景
    return routeCount;
}

bool MeshBridge::getRouteDeviceId(int index, char *outBuf, size_t bufSize) {
    if (outBuf == nullptr || bufSize == 0 || index < 0 || index >= routeCount) return false;
    if (!routeTable[index].valid) return false;
    unsigned long now = millis();
    if (!isRouteFresh(index, now)) return false;
    strncpy(outBuf, routeTable[index].deviceId, bufSize - 1);
    outBuf[bufSize - 1] = '\0';
    return true;
}

unsigned long MeshBridge::getLastUplinkAgeMs() {
    if (lastUplinkRxMs == 0) return 0xFFFFFFFFUL;
    return millis() - lastUplinkRxMs;
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
