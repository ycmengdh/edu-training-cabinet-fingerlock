/**
 * tcp_comm.h - TCP 通信模块
 * STA 模式作为 TCP 客户端连接上位机；AP 模式作为 TCP 服务端监听 8888
 */
#ifndef TCP_COMM_H
#define TCP_COMM_H

#include <Arduino.h>
#include <WiFi.h>
#include "config.h"

class TcpComm {
public:
    // 消息接收回调函数类型
    typedef void (*MessageCallback)(const String &message);

    // 初始化（根据当前模式决定客户端/服务端）
    static void init();

    // 以客户端模式连接到上位机 server_ip:server_port
    static bool connectToServer(const String &serverIp, uint16_t port);

    // 以服务端模式启动监听
    static bool startServer(uint16_t port = TCP_PORT);

    // 主循环调用，处理连接维护、收发数据
    static void update();

    // 发送 JSON 消息（自动补充 device_id 和 timestamp）
    // cmd: 命令名，dataJson: data 字段的 JSON 字符串（可为空）
    static bool sendMessage(const String &cmd, const String &dataJson = "");

    // 直接发送原始字符串
    static bool sendRaw(const String &raw);

    // 设置消息接收回调
    static void setMessageCallback(MessageCallback cb);

    // 当前是否已连接（客户端：与上位机连接；服务端：有客户端连入）
    static bool isConnected();

    // 断开当前连接
    static void disconnect();

    // 强制触发重连（STA 模式）
    static void triggerReconnect();

private:
    static WiFiClient   client;          // STA 模式客户端
    static WiFiServer   *server;         // AP 模式服务端
    static WiFiClient   apClient;        // AP 模式接入的客户端

    static WorkMode mode;
    static String  serverIp;
    static uint16_t serverPort;
    static bool    connected;

    static unsigned long lastReconnectTime;  // 上次重连时刻
    static unsigned long lastHeartbeatTime;  // 上次心跳时刻
    static MessageCallback msgCb;

    static char rxBuffer[TCP_RX_BUFFER_SIZE];
    static int  rxLen;

    // 生成当前时间戳字符串
    static String getTimestamp();

    // 处理一行完整 JSON 消息（按 \n 分隔）
    static void processIncoming();
};

#endif // TCP_COMM_H
