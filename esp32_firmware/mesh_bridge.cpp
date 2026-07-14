/**
 * mesh_bridge.cpp - Root 节点上行链路桥接实现
 * 支持 USB 串口 / WiFi AP / WiFi STA 三种上行链路
 * 双向桥接：Mesh 消息 <-> 上行链路（协议帧封装）
 * 维护 device_id -> Mesh MAC 路由表，透传不解析业务内容
 */
#include "mesh_bridge.h"
#include "mesh_comm.h"
#include "message_handler.h"
#include "storage.h"
#include "protocol_frame.h"
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

// ====== 初始化 ======
void MeshBridge::init() {
    if (initialized) return;

    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    uplinkMode = cfg.uplink_mode;

    Serial.printf("[BRIDGE] 上行链路模式: %s\n",
                  uplinkMode == UPLINK_USB ? "USB串口" :
                  uplinkMode == UPLINK_AP  ? "WiFi AP" : "WiFi STA");

    switch (uplinkMode) {
        case UPLINK_USB: initUSB(); break;
        case UPLINK_AP:  initAP();  break;
        case UPLINK_STA: initSTA(); break;
    }

    initialized = true;
}

// ====== USB 串口上行 ======
void MeshBridge::initUSB() {
    // USB CDC 虚拟串口（或 UART0），高波特率降低传输延迟
    Serial.flush();
    Serial.updateBaudRate(UPLINK_USB_BAUD);
    uplinkConnected = true;
    Serial.printf("[BRIDGE] USB 串口已就绪 @%d bps\n", UPLINK_USB_BAUD);
    // 发送桥接就绪通知
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    sendToUplink("{\"cmd\":\"BRIDGE_READY\",\"device_id\":\"" + cfg.device_id +
                 "\",\"data\":{\"role\":\"root\",\"uplink\":\"usb\"}}");
}

void MeshBridge::updateUSB() {
    // 读取串口字节并送入协议帧解码器
    readUplink();
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
        Serial.println(F("[BRIDGE] AP 热点启动失败"));
        return;
    }
    if (bridgeServer == nullptr) {
        bridgeServer = new WiFiServer(UPLINK_TCP_PORT);
    }
    bridgeServer->begin(UPLINK_TCP_PORT);
    uplinkConnected = false;
    Serial.printf("[BRIDGE] AP 上行已启动, SSID=%s, TCP=%d, IP=%s\n",
                  UPLINK_AP_SSID, UPLINK_TCP_PORT,
                  WiFi.softAPIP().toString().c_str());
}

void MeshBridge::updateAP() {
    if (bridgeServer == nullptr) return;

    if (uplinkConnected && bridgeClient.connected()) {
        readUplink();
    } else {
        uplinkConnected = false;
        // 接受新连接
        bridgeClient = bridgeServer->accept();
        if (bridgeClient) {
            uplinkConnected = true;
            ProtocolFrame::resetDecoder();
            Serial.printf("[BRIDGE] 上位机连入 AP, IP=%s\n",
                          bridgeClient.remoteIP().toString().c_str());
            // 发送桥接就绪通知
            DeviceConfig cfg;
            Storage::loadDeviceConfig(cfg);
            sendToUplink("{\"cmd\":\"BRIDGE_READY\",\"device_id\":\"" + cfg.device_id +
                         "\",\"data\":{\"role\":\"root\",\"uplink\":\"ap\"}}");
        }
    }
}

// ====== WiFi STA TCP 上行 ======
void MeshBridge::initSTA() {
    // Root 通过 Mesh DS 连接路由器后，作为 TCP 客户端连接上位机
    // WiFi STA 连接由 Mesh 路由器配置处理，这里仅初始化 TCP 客户端
    uplinkConnected = false;
    Serial.println(F("[BRIDGE] STA 上行: 等待 Mesh DS 连接路由器..."));
}

void MeshBridge::updateSTA() {
    unsigned long now = millis();

    // 检查是否有路由器分配的 IP（Mesh DS 已连接）
    if (WiFi.localIP() == IPAddress(0, 0, 0, 0)) {
        if (uplinkConnected) {
            uplinkConnected = false;
            bridgeClient.stop();
            Serial.println(F("[BRIDGE] STA 未获取 IP，断开 TCP"));
        }
        return;
    }

    // 维护 TCP 客户端连接
    if (uplinkConnected && bridgeClient.connected()) {
        readUplink();
    } else {
        if (uplinkConnected) {
            uplinkConnected = false;
            Serial.println(F("[BRIDGE] 与上位机 TCP 连接断开"));
        }
        // 按间隔重连
        if (now - lastSTAReconnect >= UPLINK_TCP_RECONNECT_MS || lastSTAReconnect == 0) {
            lastSTAReconnect = now;
            DeviceConfig cfg;
            Storage::loadDeviceConfig(cfg);
            Serial.printf("[BRIDGE] 连接上位机 %s:%u ...\n",
                          cfg.server_ip.c_str(), cfg.server_port);
            bridgeClient.stop();
            if (bridgeClient.connect(cfg.server_ip.c_str(), cfg.server_port, 5000)) {
                uplinkConnected = true;
                ProtocolFrame::resetDecoder();
                Serial.printf("[BRIDGE] 已连接上位机 %s:%u\n",
                              cfg.server_ip.c_str(), cfg.server_port);
                // 发送桥接就绪通知
                sendToUplink("{\"cmd\":\"BRIDGE_READY\",\"device_id\":\"" + cfg.device_id +
                             "\",\"data\":{\"role\":\"root\",\"uplink\":\"sta\"}}");
            } else {
                Serial.println(F("[BRIDGE] 连接上位机失败"));
            }
        }
    }
}

// ====== 主循环更新 ======
void MeshBridge::update() {
    if (!initialized) return;

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

    // 编码为协议帧
    static uint8_t frameBuf[FRAME_MAX_PAYLOAD + FRAME_HEADER_SIZE + FRAME_CRC_SIZE + 32];
    int frameLen = ProtocolFrame::encode(json, frameBuf, sizeof(frameBuf));
    if (frameLen < 0) {
        Serial.println(F("[BRIDGE] 帧编码失败"));
        return false;
    }

    bool ok = writeUplink(frameBuf, frameLen);
    if (!ok && uplinkMode != UPLINK_USB) {
        uplinkConnected = false;
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

// ====== 读取上行链路字节并送入协议帧解码器 ======
void MeshBridge::readUplink() {
    if (uplinkMode == UPLINK_USB) {
        while (Serial.available()) {
            uint8_t b = Serial.read();
            String json;
            if (ProtocolFrame::decode(b, json)) {
                handleUplinkMessage(json);
            }
        }
    } else {
        // AP / STA 模式从 TCP 客户端读取
        if (!bridgeClient.connected()) {
            uplinkConnected = false;
            return;
        }
        while (bridgeClient.available()) {
            uint8_t b = bridgeClient.read();
            String json;
            if (ProtocolFrame::decode(b, json)) {
                handleUplinkMessage(json);
            }
        }
    }
}

// ====== 处理上行链路收到的 JSON（路由到本机或子节点） ======
void MeshBridge::handleUplinkMessage(const String &json) {
    Serial.printf("[BRIDGE] << 上行接收: %s\n", json.c_str());

    // 解析 device_id 决定路由
    StaticJsonDocument<512> doc;
    DeserializationError err = deserializeJson(doc, json);
    if (err) {
        Serial.printf("[BRIDGE] JSON 解析失败: %s\n", err.c_str());
        return;
    }

    const char *did = doc["device_id"] | "";
    if (strlen(did) == 0) {
        Serial.println(F("[BRIDGE] 消息缺少 device_id，丢弃"));
        return;
    }

    // 检查是否发往本机（Root）
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);

    if (strcmp(did, cfg.device_id.c_str()) == 0) {
        // 目标是 Root 本机：交给消息处理器
        MessageHandler::handleIncoming(json);
        return;
    }

    // 目标是子节点：查路由表转发
    uint8_t targetMac[6];
    if (!lookupRoute(String(did), targetMac)) {
        Serial.printf("[BRIDGE] 未找到设备 %s 的路由，丢弃\n", did);
        // 回复错误：设备未注册
        String errJson = "{\"cmd\":\"ERROR\",\"device_id\":\"" + cfg.device_id +
                         "\",\"data\":{\"error_code\":" + String(ERR_DEVICE_NOT_REGISTER) +
                         ",\"message\":\"device not registered: " + String(did) + "\"}}";
        sendToUplink(errJson);
        return;
    }

    // 通过 Mesh 发送到子节点（原始 JSON，不加帧）
    bool ok = MeshComm::sendToNode(targetMac, json);
    Serial.printf("[BRIDGE] 转发到 %s [%s]: %s\n",
                  did, MeshComm::macToString(targetMac).c_str(),
                  ok ? "成功" : "失败");
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
    // 查找是否已存在
    for (int i = 0; i < routeCount; i++) {
        if (routeTable[i].valid && strcmp(routeTable[i].deviceId, deviceId.c_str()) == 0) {
            memcpy(routeTable[i].mac, mac, 6);
            routeTable[i].lastSeen = millis();
            return;
        }
    }
    // 新增条目
    if (routeCount >= MESH_MAX_NODE) {
        // 找一个无效条目复用
        for (int i = 0; i < MESH_MAX_NODE; i++) {
            if (!routeTable[i].valid) {
                strncpy(routeTable[i].deviceId, deviceId.c_str(), sizeof(routeTable[i].deviceId) - 1);
                routeTable[i].deviceId[sizeof(routeTable[i].deviceId) - 1] = '\0';
                memcpy(routeTable[i].mac, mac, 6);
                routeTable[i].lastSeen = millis();
                routeTable[i].valid = true;
                Serial.printf("[BRIDGE] 路由新增: %s -> %s (复用槽 %d)\n",
                              deviceId.c_str(), MeshComm::macToString(mac).c_str(), i);
                return;
            }
        }
        Serial.println(F("[BRIDGE] 路由表已满"));
        return;
    }
    strncpy(routeTable[routeCount].deviceId, deviceId.c_str(),
            sizeof(routeTable[routeCount].deviceId) - 1);
    routeTable[routeCount].deviceId[sizeof(routeTable[routeCount].deviceId) - 1] = '\0';
    memcpy(routeTable[routeCount].mac, mac, 6);
    routeTable[routeCount].lastSeen = millis();
    routeTable[routeCount].valid = true;
    routeCount++;
    Serial.printf("[BRIDGE] 路由新增: %s -> %s (共 %d)\n",
                  deviceId.c_str(), MeshComm::macToString(mac).c_str(), routeCount);
}

bool MeshBridge::lookupRoute(const String &deviceId, uint8_t *mac) {
    for (int i = 0; i < routeCount; i++) {
        if (routeTable[i].valid && strcmp(routeTable[i].deviceId, deviceId.c_str()) == 0) {
            memcpy(mac, routeTable[i].mac, 6);
            return true;
        }
    }
    return false;
}

int MeshBridge::getRouteCount() {
    return routeCount;
}
