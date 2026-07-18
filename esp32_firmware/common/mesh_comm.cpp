/**
 * mesh_comm.cpp - ESP-MESH 自组网通信层实现
 * 替换原 tcp_comm，支持 Root/子节点两种角色 + 调试模式（AP+TCP）
 * Root 节点：MESH_ROOT，桥接上行链路由 main.cpp 处理
 * 子节点：MESH_NODE，通过 esp_mesh_send 向 Root 发送消息
 * 调试模式：AP+TCP 直连（单台维护），使用协议帧封装
 */
#include "mesh_comm.h"
#include "debug.h"
#include "storage.h"
#include "protocol_frame.h"
// #include "mesh_bridge.h"  // Moved to main.cpp
#include "wifi_manager.h"
#include <WiFi.h>
#include <esp_wifi.h>
#include <esp_mesh.h>
#include <esp_event.h>

// ====== 静态成员初始化 ======
bool        MeshComm::meshStarted       = false;
bool        MeshComm::meshConnected     = false;
bool        MeshComm::isRootNode        = false;
int         MeshComm::meshLayer         = 0;
uint8_t     MeshComm::meshParentMac[6]  = {0, 0, 0, 0, 0, 0};
uint8_t     MeshComm::meshSelfMac[6]    = {0, 0, 0, 0, 0, 0};
int         MeshComm::childCount        = 0;
uint8_t     MeshComm::rootMac[6]        = {0, 0, 0, 0, 0, 0};
bool        MeshComm::rootMacKnown      = false;
bool        MeshComm::registeredWithRoot = false;
unsigned long MeshComm::lastHeartbeatTime   = 0;
unsigned long MeshComm::lastReconnectTime   = 0;
int         MeshComm::reconnectAttempt  = 0;
int         MeshComm::reconnectDelays[5] = {5000, 10000, 20000, 40000, 60000};
MeshComm::MessageCallback     MeshComm::msgCb     = nullptr;
MeshComm::MeshMessageCallback MeshComm::meshMsgCb = nullptr;
void       *MeshComm::msgQueue = nullptr;

// 调试模式（AP+TCP）内部状态
static WiFiServer *debugServer = nullptr;
static WiFiClient  debugClient;
static bool        debugAPStarted = false;
static bool        debugClientConnected = false;

// ====== 初始化 ======
void MeshComm::init() {
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    isRootNode = cfg.is_root;

    // 协议帧解析器初始化（调试模式与上行链路共用）
    ProtocolFrame::init();

    if (cfg.work_mode == MODE_DEBUG) {
        Debug::println(F("[MESH] === debug mode (AP+TCP direct) ==="));
        initDebugMode();
    } else {
        Debug::println(F("[MESH] === Mesh ad-hoc network mode ==="));
        initMesh();
    }

    lastHeartbeatTime = millis();
    lastReconnectTime = 0;
    reconnectAttempt = 0;
}

// ====== Mesh 初始化 ======
bool MeshComm::initMesh() {
    // WiFi 初始化（Mesh 需要 AP+STA 模式）
    WiFi.mode(WIFI_AP_STA);
    WiFi.disconnect();
    delay(100);

    // ESP-MESH 初始化
    esp_err_t err = esp_mesh_init();
    if (err != ESP_OK) {
        Debug::printf("[MESH] esp_mesh_init failed: %s\n", esp_err_to_name(err));
        return false;
    }

    // 注册 Mesh 事件处理器
    esp_event_handler_register(MESH_EVENT, ESP_EVENT_ANY_ID, &meshEventHandler, NULL);

    // Mesh 配置
    mesh_cfg_t cfg = MESH_INIT_CONFIG_DEFAULT();
    cfg.channel = MESH_CHANNEL;
    cfg.allow_channel_switch = false;

    // Mesh ID
    uint8_t meshId[6] = MESH_ID;
    memcpy(cfg.mesh_id.addr, meshId, 6);

    // 路由器配置：从设备配置读取（STA 上行模式时 Root 连接路由器获取 IP）
    // USB 上行模式时可为空（Root 不需要外部网络）
    memset(cfg.router.ssid, 0, sizeof(cfg.router.ssid));
    memset(cfg.router.password, 0, sizeof(cfg.router.password));
    cfg.router.allow_router_switch = false;
    DeviceConfig devCfg;
    Storage::loadDeviceConfig(devCfg);
    if (devCfg.uplink_mode == UPLINK_STA && devCfg.wifi_ssid.length() > 0) {
        strncpy((char*)cfg.router.ssid, devCfg.wifi_ssid.c_str(), sizeof(cfg.router.ssid) - 1);
        strncpy((char*)cfg.router.password, devCfg.wifi_password.c_str(),
                sizeof(cfg.router.password) - 1);
        Debug::printf("[MESH] Root STA uplink: router SSID=%s\n", devCfg.wifi_ssid.c_str());
    }

    // Mesh AP 配置：优先使用 NVS 中的 mesh_password，空则回退编译期默认值
    cfg.mesh_ap.max_connection = MESH_AP_MAX_CONNECTION;
    memset(cfg.mesh_ap.password, 0, sizeof(cfg.mesh_ap.password));
    const char *meshPass = (devCfg.mesh_password.length() > 0)
        ? devCfg.mesh_password.c_str()
        : MESH_PASSWORD;
    strncpy((char*)cfg.mesh_ap.password, meshPass, sizeof(cfg.mesh_ap.password) - 1);
    Debug::printf("[MESH] mesh AP password source=%s\n",
                  devCfg.mesh_password.length() > 0 ? "nvs" : "default");

    esp_mesh_set_config(&cfg);
    // 设置 Mesh AP 认证模式（本版本 mesh_ap_cfg_t 无 authmode 字段，需单独设置）
    esp_mesh_set_ap_authmode(WIFI_AUTH_WPA2_PSK);

    // 设置节点类型
    if (isRootNode) {
        esp_mesh_set_type(MESH_ROOT);
        Debug::println(F("[MESH] node type: Root"));
    } else {
        Debug::println(F("[MESH] node type: Node"));
    }

    // 启动 Mesh
    err = esp_mesh_start();
    if (err != ESP_OK) {
        Debug::printf("[MESH] esp_mesh_start failed: %s\n", esp_err_to_name(err));
        return false;
    }

    meshStarted = true;

    // 创建接收队列
    if (msgQueue == nullptr) {
        msgQueue = xQueueCreate(4, sizeof(MeshMessage));
    }

    // 创建 Mesh 接收任务
    xTaskCreatePinnedToCore(meshReceiveTask, "mesh_rx", 8192, NULL, 5, NULL, 0);

    // 获取本机 MAC
    esp_wifi_get_mac(WIFI_IF_STA, meshSelfMac);

    Debug::printf("[MESH] Mesh started, channel=%d, MAC=%s\n",
                  MESH_CHANNEL, macToString(meshSelfMac).c_str());

    // Root 节点桥接初始化由 main.cpp 负责
    if (isRootNode) {
        // MeshBridge::init();  // Moved to main.cpp
    }
    return true;
}

// ====== Mesh 事件处理器 ======
void MeshComm::meshEventHandler(void *arg, esp_event_base_t event_base,
                                int32_t event_id, void *event_data) {
    switch (event_id) {
        case MESH_EVENT_STARTED:
            Debug::println(F("[MESH] event: Mesh started"));
            // Root 启动即视为可服务
            if (isRootNode) {
                meshConnected = true;
            }
            break;

        case MESH_EVENT_STOPPED:
            Debug::println(F("[MESH] event: Mesh stopped"));
            meshConnected = false;
            break;

        case MESH_EVENT_PARENT_CONNECTED: {
            Debug::println(F("[MESH] event: connected to parent node"));
            meshConnected = true;
            reconnectAttempt = 0;
            // 获取父节点 MAC 和层级
            mesh_addr_t parent;
            if (esp_mesh_get_parent_bssid(&parent) == ESP_OK) {
                memcpy(meshParentMac, parent.addr, 6);
            }
            meshLayer = esp_mesh_get_layer();
            Debug::printf("[MESH] layer=%d, parent=%s\n",
                          meshLayer, macToString(meshParentMac).c_str());
            break;
        }

        case MESH_EVENT_PARENT_DISCONNECTED: {
            Debug::println(F("[MESH] event: disconnected from parent node"));
            meshConnected = false;
            registeredWithRoot = false;
            if (!isRootNode) {
                triggerReconnect();
            }
            break;
        }

        case MESH_EVENT_CHILD_CONNECTED: {
            childCount++;
            Debug::printf("[MESH] event: child node connected (total %d)\n", childCount);
            break;
        }

        case MESH_EVENT_CHILD_DISCONNECTED: {
            if (childCount > 0) childCount--;
            Debug::printf("[MESH] event: child node disconnected (remaining %d)\n", childCount);
            break;
        }

        case MESH_EVENT_ROOT_ADDRESS: {
            mesh_addr_t *rootAddr = (mesh_addr_t*)event_data;
            if (rootAddr != nullptr) {
                memcpy(rootMac, rootAddr->addr, 6);
                rootMacKnown = true;
                Debug::printf("[MESH] event: Root address=%s\n", macToString(rootMac).c_str());
            }
            break;
        }

        case MESH_EVENT_CHANNEL_SWITCH:
            Debug::printf("[MESH] event: channel switch\n");
            break;

        default:
            Debug::printf("[MESH] event: id=%d\n", (int)event_id);
            break;
    }
}

// ====== Mesh 接收任务 ======
void MeshComm::meshReceiveTask(void *arg) {
    mesh_addr_t from;
    mesh_data_t data;
    static uint8_t rxBuffer[MESH_RX_BUFFER_SIZE];
    int flag = 0;

    Debug::println(F("[MESH] receive task started"));

    while (true) {
        data.data = rxBuffer;
        data.size = MESH_RX_BUFFER_SIZE;
        flag = 0;

        esp_err_t err = esp_mesh_recv(&from, &data, portMAX_DELAY, &flag, NULL, 0);
        if (err == ESP_OK && data.size > 0 && data.size < MESH_RX_BUFFER_SIZE) {
            // 转发到主循环队列
            MeshMessage msg;
            memcpy(msg.fromMac, from.addr, 6);
            int copyLen = data.size;
            if (copyLen >= MESH_RX_BUFFER_SIZE) copyLen = MESH_RX_BUFFER_SIZE - 1;
            memcpy(msg.json, data.data, copyLen);
            msg.json[copyLen] = '\0';
            msg.length = copyLen;

            if (msgQueue != nullptr) {
                xQueueSend((QueueHandle_t)msgQueue, &msg, 0);
            }
        }
    }
}

// ====== 主循环更新 ======
void MeshComm::update() {
    unsigned long now = millis();

    // 处理接收队列中的消息
    if (msgQueue != nullptr) {
        MeshMessage msg;
        while (xQueueReceive((QueueHandle_t)msgQueue, &msg, 0) == pdTRUE) {
            String json(msg.json);
            processReceivedMessage(msg.fromMac, json);
        }
    }

    if (Storage::loadWorkMode() == MODE_DEBUG) {
        updateDebugMode();
        return;
    }

    // ====== Mesh 模式 ======
    // 重连处理（子节点断线后指数退避重连）
    if (!isRootNode && !meshConnected) {
        int delayIdx = reconnectAttempt;
        if (delayIdx >= 5) delayIdx = 4;
        if (now - lastReconnectTime >= (unsigned long)reconnectDelays[delayIdx]) {
            lastReconnectTime = now;
            reconnectAttempt++;
            Debug::printf("[MESH] retry reconnect (%d), interval %d ms\n",
                          reconnectAttempt, reconnectDelays[delayIdx]);
            // ESP-MESH 自动重连，这里仅打印日志并重置状态
        }
    }

    // 心跳：子节点每 60 秒向 Root 发送 HEARTBEAT
    if (!isRootNode && meshConnected) {
        if (rootMacKnown && !registeredWithRoot) {
            DeviceConfig cfg;
            Storage::loadDeviceConfig(cfg);
            String data = "{\"device_name\":\"" + cfg.device_name +
                          "\",\"firmware_version\":\"" FIRMWARE_VERSION "\"}";
            registeredWithRoot = sendMessage("REGISTER", data);
            if (registeredWithRoot) {
                Debug::println(F("[MESH] cabinet registered with Root"));
            }
        }
        if (now - lastHeartbeatTime >= MESH_HEARTBEAT_INTERVAL) {
            lastHeartbeatTime = now;
            sendRaw("{\"cmd\":\"HEARTBEAT\",\"data\":{}}");
            Debug::println(F("[MESH] send heartbeat"));
        }
    }

    // Root 桥接更新（处理上行链路收发）- 由 main.cpp 调用
    if (isRootNode) {
        // MeshBridge::update();  // Moved to main.cpp
    }
}

// ====== 发送消息 ======
bool MeshComm::sendMessage(const String &cmd, const String &dataJson,
                           const String &msgId) {
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);

    String json = ProtocolFrame::buildMessage(cmd, cfg.device_id, dataJson, msgId);
    return sendRaw(json);
}

bool MeshComm::sendRaw(const String &json) {
    if (Storage::loadWorkMode() == MODE_DEBUG) {
        return debugSendRaw(json);
    }

    if (!meshStarted) {
        Debug::println(F("[MESH] send failed: Mesh not started"));
        return false;
    }

    if (isRootNode) {
        // Root：转发到上行链路（USB/AP/STA）- 由 main.cpp 处理
        // MeshBridge::sendToUplink(json);  // Moved to main.cpp
        return false;
    }

    // 子节点：通过 Mesh 发往 Root
    if (!meshConnected || !rootMacKnown) {
        Debug::println(F("[MESH] send failed: not connected or Root address unknown"));
        return false;
    }

    mesh_addr_t dest;
    memcpy(dest.addr, rootMac, 6);
    mesh_data_t data;
    if (json.length() >= MESH_RX_BUFFER_SIZE) {
        Debug::printf("[MESH] payload too large for Mesh MTU: %u\n",
                      (unsigned)json.length());
        return false;
    }
    data.data = (uint8_t*)json.c_str();
    data.size = json.length();
    data.proto = MESH_PROTO_JSON;
    data.tos = MESH_TOS_P2P;

    esp_err_t err = esp_mesh_send(&dest, &data, MESH_DATA_P2P, NULL, 0);
    if (err != ESP_OK) {
        Debug::printf("[MESH] esp_mesh_send failed: %s\n", esp_err_to_name(err));
        return false;
    }
    return true;
}

// Root 专用：向指定子节点发送消息
bool MeshComm::sendToNode(const uint8_t *mac, const String &json) {
    if (!meshStarted || !isRootNode) {
        Debug::println(F("[MESH] sendToNode failed: not Root or Mesh not started"));
        return false;
    }

    mesh_addr_t dest;
    memcpy(dest.addr, mac, 6);
    mesh_data_t data;
    if (json.length() >= MESH_RX_BUFFER_SIZE) {
        Debug::printf("[MESH] root payload too large for Mesh MTU: %u\n",
                      (unsigned)json.length());
        return false;
    }
    data.data = (uint8_t*)json.c_str();
    data.size = json.length();
    data.proto = MESH_PROTO_JSON;
    data.tos = MESH_TOS_P2P;

    esp_err_t err = esp_mesh_send(&dest, &data, MESH_DATA_P2P, NULL, 0);
    if (err != ESP_OK) {
        Debug::printf("[MESH] sendToNode failed: %s\n", esp_err_to_name(err));
        return false;
    }
    return true;
}

// ====== 消息回调设置 ======
void MeshComm::setMessageCallback(MessageCallback cb) {
    msgCb = cb;
}

void MeshComm::setMeshMessageCallback(MeshMessageCallback cb) {
    meshMsgCb = cb;
}

// ====== 状态查询 ======
bool MeshComm::isConnected() {
    if (Storage::loadWorkMode() == MODE_DEBUG) {
        return debugClientConnected;
    }
    return meshConnected;
}

WorkMode MeshComm::getMode() {
    return Storage::loadWorkMode();
}

bool MeshComm::isRoot() {
    return isRootNode;
}

int MeshComm::getMeshLayer() {
    return meshLayer;
}

String MeshComm::getMeshParentMac() {
    return macToString(meshParentMac);
}

String MeshComm::getMeshMac() {
    return macToString(meshSelfMac);
}

int MeshComm::getChildCount() {
    return childCount;
}

void MeshComm::triggerReconnect() {
    if (!isRootNode) {
        Debug::println(F("[MESH] trigger reconnect"));
        lastReconnectTime = 0;
        reconnectAttempt = 0;
    }
}

int MeshComm::getCrcErrorCount() {
    return ProtocolFrame::getCrcErrorCount();
}

// ====== 调试模式（AP+TCP） ======
bool MeshComm::initDebugMode() {
    // 启动 AP 热点
    if (!WifiManager::startAP()) {
        Debug::println(F("[MESH] debug AP start failed"));
        return false;
    }
    debugAPStarted = true;

    // 启动 TCP 服务端
    if (debugServer == nullptr) {
        debugServer = new WiFiServer(DEBUG_TCP_PORT);
    }
    debugServer->begin(DEBUG_TCP_PORT);
    debugClientConnected = false;

    Debug::printf("[MESH] debug mode started, TCP port=%d\n", DEBUG_TCP_PORT);

    // 发送注册消息（待客户端连入后发送）
    return true;
}

void MeshComm::updateDebugMode() {
    if (!debugAPStarted || debugServer == nullptr) return;

    unsigned long now = millis();

    // 检查客户端连接
    if (debugClientConnected && debugClient.connected()) {
        // 处理接收数据
        debugProcessIncoming();
    } else {
        debugClientConnected = false;
        // 接受新连接
        debugClient = debugServer->accept();
        if (debugClient) {
            debugClientConnected = true;
            Debug::printf("[MESH] debug client connected, IP=%s\n",
                          debugClient.remoteIP().toString().c_str());
            ProtocolFrame::resetDecoder();
            // 发送注册消息
            DeviceConfig cfg;
            Storage::loadDeviceConfig(cfg);
            sendMessage("REGISTER", "{\"device_name\":\"" + cfg.device_name + "\"}");
        }
    }

    // 心跳
    if (debugClientConnected) {
        if (now - lastHeartbeatTime >= MESH_HEARTBEAT_INTERVAL) {
            lastHeartbeatTime = now;
            sendRaw("{\"cmd\":\"HEARTBEAT\",\"data\":{}}");
        }
    }
}

bool MeshComm::debugSendRaw(const String &raw) {
    if (!debugClientConnected || !debugClient.connected()) {
        return false;
    }

    // 使用协议帧封装后发送。长消息按实际分片总长度分配缓冲区。
    int frameCapacity = ProtocolFrame::getEncodedCapacity(raw);
    if (frameCapacity < 0) {
        Debug::println(F("[MESH] debug message exceeds frame reassembly limit"));
        return false;
    }
    uint8_t *frameBuf = (uint8_t *)malloc(frameCapacity);
    if (frameBuf == nullptr) {
        Debug::println(F("[MESH] debug frame buffer allocation failed"));
        return false;
    }
    int frameLen = ProtocolFrame::encode(raw, frameBuf, frameCapacity);
    if (frameLen < 0) {
        Debug::println(F("[MESH] debug send: frame encode failed"));
        free(frameBuf);
        return false;
    }

    size_t sent = debugClient.write(frameBuf, frameLen);
    debugClient.flush();
    free(frameBuf);
    return (sent == (size_t)frameLen);
}

void MeshComm::debugProcessIncoming() {
    while (debugClient.available()) {
        uint8_t byte = debugClient.read();
        String json;
        if (ProtocolFrame::decode(byte, json)) {
            // 收到完整帧，作为本机消息处理
            Debug::printf("[MESH] debug receive: %s\n", json.c_str());
            if (msgCb) {
                msgCb(json);
            }
        }
    }
}

// ====== 处理收到的 Mesh 消息 ======
void MeshComm::processReceivedMessage(const uint8_t *fromMac, const String &json) {
    Debug::printf("[MESH] received message from %s: %s\n",
                  macToString(fromMac).c_str(), json.c_str());

    if (isRootNode) {
        // Root 收到子节点消息：转发到上行链路由 main.cpp 处理
        // meshMsgCb 可选用于额外处理（如日志记录），但不参与转发
        if (meshMsgCb) {
            meshMsgCb(fromMac, json);
        }
        // MeshBridge::onMeshMessage(fromMac, json);  // Moved to main.cpp
    } else {
        // 子节点收到 Root 下发的命令：交给消息处理器
        if (msgCb) {
            msgCb(json);
        }
    }
}

// ====== 辅助方法 ======
String MeshComm::macToString(const uint8_t *mac) {
    char buf[18];
    snprintf(buf, sizeof(buf), "%02X:%02X:%02X:%02X:%02X:%02X",
             mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
    return String(buf);
}

void MeshComm::parseMacString(const String &str, uint8_t *mac) {
    // 支持 "XX:XX:XX:XX:XX:XX" 或 "XXXXXXXXXXXX" 格式
    if (str.length() == 17 && str[2] == ':') {
        // 带冒号格式
        for (int i = 0; i < 6; i++) {
            mac[i] = (uint8_t)strtol(str.substring(i * 3, i * 3 + 2).c_str(), NULL, 16);
        }
    } else if (str.length() == 12) {
        // 无分隔符格式
        for (int i = 0; i < 6; i++) {
            mac[i] = (uint8_t)strtol(str.substring(i * 2, i * 2 + 2).c_str(), NULL, 16);
        }
    } else {
        memset(mac, 0, 6);
    }
}
