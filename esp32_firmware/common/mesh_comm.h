/**
 * mesh_comm.h - ESP-MESH 自组网通信层
 * Root：MESH_ROOT + 上位机上行（由 MeshBridge 处理 USB/AP/STA）
 * 柜子：MESH_NODE 找父节点；同时常开 UART0 协议口（与根节点 USB 同协议）便于单柜直连上位机
 */
#ifndef MESH_COMM_H
#define MESH_COMM_H

#include <Arduino.h>
#include <esp_event.h>
#include "config_common.h"

class MeshComm {
public:
    typedef void (*MessageCallback)(const String &message);
    typedef void (*MeshMessageCallback)(const uint8_t *fromMac, const String &json);
    typedef void (*PeerConnectionCallback)(const uint8_t *mac, bool connected);

    static void init();
    static void update();

    static bool sendMessage(const String &cmd, const String &dataJson = "",
                            const String &msgId = "");
    static bool sendRaw(const String &json);
    static bool sendToNode(const uint8_t *mac, const String &json);

    // Binary application message path (Phase 2/3): raw app envelope bytes.
    static bool sendAppRaw(const uint8_t *appMsg, uint16_t len);
    static bool sendToNodeApp(const uint8_t *mac, const uint8_t *appMsg, uint16_t len);
    static bool sendApp(uint16_t cmdId, uint16_t msgId, uint8_t flags,
                        const uint8_t *payload, uint16_t payloadLen,
                        const char *deviceIdOverride = nullptr);

    static void setMessageCallback(MessageCallback cb);
    static void setMeshMessageCallback(MeshMessageCallback cb);
    static void setPeerConnectionCallback(PeerConnectionCallback cb);

    // 任一可用管理链路；柜子同时维护 Mesh 和 UART0。
    static bool isConnected();
    static bool isMeshConnected();
    static bool isUartHostReady();
    static bool isUartHostConnected();

    static WorkMode getMode();
    static bool isRoot();

    static int  getMeshLayer();
    static String getMeshParentMac();
    static String getMeshMac();
    static int  getChildCount();

    static void triggerReconnect();
    static int getCrcErrorCount();
    static uint32_t getSendFailureCount();
    static uint32_t getQueueFullCount();
    static uint32_t getRecoveryCount();
    static uint32_t getDuplicateReplayCount();
    static int getLinkRssi();
    static int getApAssocExpireSeconds();

    static String macToString(const uint8_t *mac);
    static void parseMacString(const String &str, uint8_t *mac);

    // Drain deferred mesh event logs from the main loop (sys_evt task stack
    // is too small to run Debug::printf -> ProtocolFrame::encode directly).
    static void drainEventLog();

private:
    static bool meshStarted;
    static bool meshConnected;
    static bool isRootNode;
    static int  meshLayer;
    static uint8_t meshParentMac[6];
    static uint8_t meshSelfMac[6];
    static int  childCount;

    static uint8_t rootMac[6];
    static bool rootMacKnown;
    static bool registeredWithRoot;

    static unsigned long lastHeartbeatTime;
    static unsigned long unansweredHeartbeatSince;
    static bool rootResponseTimedOut;
    static unsigned long lastReconnectTime;
    static unsigned long lastRegisterAttemptTime;  // 防止 REGISTER 风暴
    static int  reconnectAttempt;
    static int  reconnectDelays[5];

    static MessageCallback msgCb;
    static MeshMessageCallback meshMsgCb;
    static PeerConnectionCallback peerConnectionCb;

    struct MeshMessage {
        uint8_t fromMac[6];
        char    json[MESH_RX_BUFFER_SIZE];
        uint16_t length;
    };
    static void *msgQueue;

    // Event log queue: meshEventHandler runs in the sys_evt task (2304B stack,
    // too small for Debug::printf + ProtocolFrame::encode + Serial.flush
    // call chain). Push raw event IDs here, drain from main loop.
    struct EventLogEntry {
        int32_t event_id;
        int32_t child_count;
        int32_t mesh_layer;
        uint8_t mac[6];
        uint8_t reason;   // PARENT_DISCONNECTED reason code (0 if N/A)
    };
    static void *eventLogQueue;
    static void pushEventLog(int32_t eventId, const uint8_t *mac = nullptr,
                             uint8_t reason = 0);

    static bool initMesh();
    static bool restartCabinetMeshStack();
    static void meshEventHandler(void *arg, esp_event_base_t event_base,
                                 int32_t event_id, void *event_data);
    static void meshReceiveTask(void *arg);

    // 柜子 UART0 主机协议口（与根节点 USB 上行同帧格式）
    static bool initUartHost();
    static void updateUartHost();
    static bool uartHostSendRaw(const String &raw);
    static bool sendControlAppToMesh(uint16_t cmdId, uint16_t msgId,
                                     const uint8_t *payload, uint16_t payloadLen);
    static bool sendAppRawToMesh(const uint8_t *appMsg, uint16_t len);
    static bool sendAppRawToUart(const uint8_t *appMsg, uint16_t len);
    static void uartHostProcessIncoming();
    static void uartHostAnnounceRegister();

    static void processReceivedMessage(const uint8_t *fromMac, const String &json);
};

#endif // MESH_COMM_H
