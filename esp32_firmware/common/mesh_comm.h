/**
 * mesh_comm.h - ESP-MESH 自组网通信层
 * 替换原 tcp_comm，支持 Root 节点和子节点两种角色
 * Root 节点：MESH_ROOT + 桥接上行链路（USB/AP/STA）
 * 子节点：MESH_NODE，通过 esp_mesh_send 向 Root 发送消息
 * 调试模式：AP+TCP 直连（单台维护）
 */
#ifndef MESH_COMM_H
#define MESH_COMM_H

#include <Arduino.h>
#include <esp_event.h>
#include "config_common.h"

class MeshComm {
public:
    // 消息接收回调（JSON字符串）
    typedef void (*MessageCallback)(const String &message);

    // Mesh 消息接收回调（带来源MAC）
    typedef void (*MeshMessageCallback)(const uint8_t *fromMac, const String &json);

    // 初始化通信（根据配置决定 Mesh/Debug 模式）
    static void init();

    // 主循环调用，维护连接、心跳、重连
    static void update();

    // 发送 JSON 消息（自动补充 device_id 和 timestamp）
    // cmd: 命令名，dataJson: data 字段 JSON
    static bool sendMessage(const String &cmd, const String &dataJson = "",
                            const String &msgId = "");

    // 发送原始 JSON 字符串
    // 子节点：通过 Mesh 发往 Root
    // Root：通过 Bridge 发往上位机
    // Debug：通过 TCP 发送
    static bool sendRaw(const String &json);

    // Root 专用：向指定子节点发送消息（按 Mesh MAC）
    static bool sendToNode(const uint8_t *mac, const String &json);

    // 设置消息接收回调（调试模式或子节点本地处理）
    static void setMessageCallback(MessageCallback cb);

    // 设置 Mesh 消息接收回调（Root 收到子节点消息时调用）
    static void setMeshMessageCallback(MeshMessageCallback cb);

    // 当前是否已连接
    // Mesh 模式：Mesh 已组网
    // Debug 模式：TCP 已连接
    static bool isConnected();

    // 获取当前通信模式
    static WorkMode getMode();

    // 是否为 Root 节点
    static bool isRoot();

    // 获取 Mesh 网络信息
    static int  getMeshLayer();
    static String getMeshParentMac();
    static String getMeshMac();
    static int  getChildCount();

    // 触发重连
    static void triggerReconnect();

    // 获取 CRC 错误计数
    static int getCrcErrorCount();

    // MAC 地址转字符串（XX:XX:XX:XX:XX:XX）
    static String macToString(const uint8_t *mac);
    // 字符串转 MAC 地址
    static void parseMacString(const String &str, uint8_t *mac);

private:
    // ====== Mesh 状态 ======
    static bool meshStarted;
    static bool meshConnected;
    static bool isRootNode;
    static int  meshLayer;
    static uint8_t meshParentMac[6];
    static uint8_t meshSelfMac[6];
    static int  childCount;

    // Root MAC（子节点发消息的目标）
    static uint8_t rootMac[6];
    static bool rootMacKnown;
    static bool registeredWithRoot;

    // 心跳与重连
    static unsigned long lastHeartbeatTime;
    static unsigned long lastReconnectTime;
    static int  reconnectAttempt;
    static int  reconnectDelays[5]; // 5/10/20/40/60s 指数退避

    // 消息回调
    static MessageCallback msgCb;
    static MeshMessageCallback meshMsgCb;

    // 接收任务队列
    struct MeshMessage {
        uint8_t fromMac[6];
        char    json[MESH_RX_BUFFER_SIZE];
        uint16_t length;
    };
    static void *msgQueue; // QueueHandle_t

    // ====== Mesh 初始化 ======
    static bool initMesh();
    static void meshEventHandler(void *arg, esp_event_base_t event_base,
                                 int32_t event_id, void *event_data);
    static void meshReceiveTask(void *arg);

    // ====== 调试模式（AP+TCP） ======
    static bool initDebugMode();
    static void updateDebugMode();
    static bool debugSendRaw(const String &raw);
    static void debugProcessIncoming();

    // ====== 辅助 ======
    static void   processReceivedMessage(const uint8_t *fromMac, const String &json);
};

#endif // MESH_COMM_H
