/**
 * mesh_bridge.h - Root 节点上行链路桥接
 * Root 节点专用，子节点不初始化
 * 支持 USB 串口 / WiFi AP / WiFi STA 三种上行链路
 * 双向桥接：Mesh 消息 <-> 上行链路（协议帧封装）
 * 维护 device_id -> Mesh MAC 路由表，透传不解析业务内容
 */
#ifndef MESH_BRIDGE_H
#define MESH_BRIDGE_H

#include <Arduino.h>
#include "config.h"

class MeshBridge {
public:
    // 初始化桥接（根据 uplink_mode 选择 USB/AP/STA）
    static void init();

    // 主循环调用，处理上行链路收发
    static void update();

    // Root 收到子节点 Mesh 消息时调用：转发到上行链路
    static void onMeshMessage(const uint8_t *fromMac, const String &json);

    // 将 JSON 发送到上行链路（编码为协议帧后发送）
    static bool sendToUplink(const String &json);

    // 上行链路是否已连接
    static bool isUplinkConnected();

    // 主动向上位机报告根节点与当前 SD 状态。
    static void announceRootStatus();

    // 获取当前上行链路模式
    static UplinkMode getUplinkMode();

    // ====== 路由表操作 ======
    // 记录 device_id -> MAC 映射（子节点 REGISTER 时调用）
    static void addRoute(const String &deviceId, const uint8_t *mac);
    // 查找 device_id 对应的 MAC，找到返回 true
    static bool lookupRoute(const String &deviceId, uint8_t *mac);
    // 获取路由表条目数
    static int getRouteCount();

    // 向所有当前有效柜子路由广播业务 JSON
    static int broadcastToCabinets(const String &json);

private:
    // 路由表条目
    struct RouteEntry {
        char     deviceId[32];
        uint8_t  mac[6];
        unsigned long lastSeen;
        bool     valid;
    };
    static RouteEntry routeTable[MESH_MAX_NODE];
    static int routeCount;
    static void expireStaleRoutes();
    static bool isRouteFresh(int index, unsigned long now);

    static UplinkMode uplinkMode;
    static bool uplinkConnected;
    static bool initialized;

    // ====== USB 串口上行 ======
    static void initUSB();
    static void updateUSB();

    // ====== WiFi AP TCP 上行 ======
    static void initAP();
    static void updateAP();

    // ====== WiFi STA TCP 上行 ======
    static void initSTA();
    static void updateSTA();

    // 处理上行链路收到的完整 JSON（路由到本机或子节点）
    static void handleUplinkMessage(const String &json);

    // 写入数据到当前上行链路
    static bool writeUplink(const uint8_t *data, int len);

    // 读取上行链路字节并送入协议帧解码器
    static void readUplink();
};

#endif // MESH_BRIDGE_H
