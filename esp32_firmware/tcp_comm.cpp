/**
 * tcp_comm.cpp - TCP 通信实现
 */
#include "tcp_comm.h"
#include "wifi_manager.h"
#include "storage.h"

WiFiClient  TcpComm::client;
WiFiServer *TcpComm::server   = nullptr;
WiFiClient  TcpComm::apClient;

WorkMode    TcpComm::mode         = MODE_STA;
String      TcpComm::serverIp     = "";
uint16_t    TcpComm::serverPort   = TCP_PORT;
bool        TcpComm::connected    = false;

unsigned long TcpComm::lastReconnectTime = 0;
unsigned long TcpComm::lastHeartbeatTime = 0;
TcpComm::MessageCallback TcpComm::msgCb  = nullptr;

char TcpComm::rxBuffer[TCP_RX_BUFFER_SIZE];
int  TcpComm::rxLen = 0;

void TcpComm::init() {
    mode = WifiManager::getCurrentMode();
    rxLen = 0;
    connected = false;
    lastReconnectTime = 0;
    lastHeartbeatTime = millis();

    if (mode == MODE_AP) {
        startServer(TCP_PORT);
    }
    // STA 模式下，连接由主程序在 WiFi 连上后调用 connectToServer
    Serial.printf("[TCP] 初始化完成, 模式=%s\n", mode == MODE_AP ? "AP服务端" : "STA客户端");
}

void TcpComm::setMessageCallback(MessageCallback cb) {
    msgCb = cb;
}

String TcpComm::getTimestamp() {
    // 使用 ESP32 内部 RTC（开机后秒数）生成时间戳
    // 注意：未配置 NTP，时间为相对值；格式与协议保持一致
    struct tm timeinfo;
    if (getLocalTime(&timeinfo, 0)) {
        char buf[32];
        strftime(buf, sizeof(buf), "%Y-%m-%d %H:%M:%S", &timeinfo);
        return String(buf);
    }
    // 回退：使用开机毫秒数
    unsigned long ms = millis();
    unsigned int sec = ms / 1000;
    unsigned int h = sec / 3600;
    unsigned int m = (sec % 3600) / 60;
    unsigned int s = sec % 60;
    char buf[32];
    snprintf(buf, sizeof(buf), "2024-01-01 %02u:%02u:%02u", h, m, s);
    return String(buf);
}

bool TcpComm::connectToServer(const String &serverIp, uint16_t port) {
    TcpComm::serverIp = serverIp;
    TcpComm::serverPort = port;
    mode = MODE_STA;

    Serial.printf("[TCP] 连接上位机 %s:%u ...\n", serverIp.c_str(), port);
    client.stop();
    delay(100);
    if (client.connect(serverIp.c_str(), port, 5000)) {
        connected = true;
        lastHeartbeatTime = millis();
        Serial.printf("[TCP] 已连接上位机 %s:%u\n", serverIp.c_str(), port);
        // 连接成功后发送注册消息（device_name 由 sendMessage 内部从 Flash 读取）
        DeviceConfig cfg;
        Storage::loadDeviceConfig(cfg);
        sendMessage("REGISTER", "{\"device_name\":\"" + cfg.device_name + "\"}");
        return true;
    } else {
        connected = false;
        Serial.println(F("[TCP] 连接上位机失败"));
        return false;
    }
}

bool TcpComm::startServer(uint16_t port) {
    if (server == nullptr) {
        server = new WiFiServer(port);
    }
    server->begin(port);
    mode = MODE_AP;
    connected = false;
    Serial.printf("[TCP] 服务端已启动, 监听端口 %u\n", port);
    return true;
}

bool TcpComm::isConnected() {
    return connected;
}

void TcpComm::disconnect() {
    if (mode == MODE_STA) {
        client.stop();
    } else {
        apClient.stop();
    }
    connected = false;
    Serial.println(F("[TCP] 已断开连接"));
}

void TcpComm::triggerReconnect() {
    if (mode == MODE_STA) {
        connected = false;
        client.stop();
        lastReconnectTime = 0;  // 立即触发重连
        Serial.println(F("[TCP] 触发重连"));
    }
}

bool TcpComm::sendRaw(const String &raw) {
    if (!connected) {
        return false;
    }
    WiFiClient *c = (mode == MODE_AP) ? &apClient : &client;
    if (!c || !c->connected()) {
        connected = false;
        return false;
    }
    size_t sent = c->print(raw);
    c->flush();
    return (sent == raw.length());
}

bool TcpComm::sendMessage(const String &cmd, const String &dataJson) {
    if (!connected) {
        Serial.printf("[TCP] 发送失败（未连接）: %s\n", cmd.c_str());
        return false;
    }

    // 读取 device_id
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);

    // 构造 JSON：{"cmd":"...","device_id":"...","data":{...},"timestamp":"..."}
    String ts = getTimestamp();
    String msg = "{\"cmd\":\"" + cmd + "\",";
    msg += "\"device_id\":\"" + cfg.device_id + "\",";
    if (dataJson.length() > 0) {
        msg += "\"data\":" + dataJson + ",";
    } else {
        msg += "\"data\":{},";
    }
    msg += "\"timestamp\":\"" + ts + "\"}";

    // 每条消息以 \n 结尾，便于接收端按行解析
    msg += "\n";

    bool ok = sendRaw(msg);
    if (ok) {
        Serial.printf("[TCP] >> %s", msg.c_str());
    }
    return ok;
}

void TcpComm::processIncoming() {
    WiFiClient *c = (mode == MODE_AP) ? &apClient : &client;
    if (!c || !c->connected()) return;

    while (c->available()) {
        char ch = c->read();
        if (ch == '\n' || ch == '\r') {
            if (ch == '\r') continue;  // 忽略 \r，按 \n 分隔
            // 收到一行完整消息
            if (rxLen > 0) {
                rxBuffer[rxLen] = '\0';
                String msg(rxBuffer);
                Serial.printf("[TCP] << %s\n", msg.c_str());
                if (msgCb) {
                    msgCb(msg);
                }
                rxLen = 0;
            }
        } else {
            if (rxLen < TCP_RX_BUFFER_SIZE - 1) {
                rxBuffer[rxLen++] = ch;
            } else {
                // 缓冲区溢出，丢弃当前消息
                Serial.println(F("[TCP] 接收缓冲区溢出，丢弃"));
                rxLen = 0;
            }
        }
    }
}

void TcpComm::update() {
    unsigned long now = millis();

    if (mode == MODE_STA) {
        // 客户端模式：维护与上位机的连接
        if (!WifiManager::isSTAConnected()) {
            // WiFi 未连接，无法重连 TCP
            if (connected) {
                connected = false;
                client.stop();
                Serial.println(F("[TCP] WiFi 断开，TCP 连接关闭"));
            }
            return;
        }

        if (!connected || !client.connected()) {
            if (connected) {
                connected = false;
                Serial.println(F("[TCP] 与上位机连接断开"));
            }
            // 按间隔重连
            if (now - lastReconnectTime >= TCP_RECONNECT_INTERVAL || lastReconnectTime == 0) {
                lastReconnectTime = now;
                connectToServer(serverIp, serverPort);
            }
        } else {
            // 已连接：处理收发 + 心跳
            processIncoming();
            if (now - lastHeartbeatTime >= TCP_HEARTBEAT_INTERVAL) {
                lastHeartbeatTime = now;
                sendMessage("HEARTBEAT", "{}");
            }
        }
    } else {
        // AP 服务端模式
        if (server == nullptr) return;

        // 检查是否有新客户端连入
        if (apClient && apClient.connected()) {
            connected = true;
            processIncoming();
            if (now - lastHeartbeatTime >= TCP_HEARTBEAT_INTERVAL) {
                lastHeartbeatTime = now;
                sendMessage("HEARTBEAT", "{}");
            }
        } else {
            connected = false;
            // 接受新连接
            apClient = server->accept();
            if (apClient) {
                connected = true;
                lastHeartbeatTime = now;
                Serial.printf("[TCP] 上位机客户端连入, IP=%s\n",
                              apClient.remoteIP().toString().c_str());
                // 发送注册消息
                DeviceConfig cfg;
                Storage::loadDeviceConfig(cfg);
                sendMessage("REGISTER", "{\"device_name\":\"" + cfg.device_name + "\"}");
            }
        }
    }
}
