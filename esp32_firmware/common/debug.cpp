/**
 * debug.cpp - 调试输出封装实现
 * USB 上行模式：LOG 协议帧封装，上位机收到的是合法帧（cmd=LOG）
 * 其他模式：透传 Serial 裸文本
 */
#include "debug.h"
#include "config_common.h"
#include "protocol_frame.h"
#include <stdarg.h>

bool   Debug::framing    = false;
String Debug::lineBuffer;
String Debug::deviceId   = "";

// ====== 初始化 ======
void Debug::init() {
    // framing 由 main.cpp 通过 setFraming() 设置
    // deviceId 由 main.cpp 通过 setDeviceId() 设置
}

void Debug::setDeviceId(const String &id) {
    deviceId = id;
}

void Debug::setFraming(bool enable) {
    framing = enable;
}

bool Debug::isFraming() {
    return framing;
}

// ====== JSON 转义 ======
String Debug::escapeJson(const String &s) {
    String out;
    out.reserve(s.length() + 8);
    for (size_t i = 0; i < s.length(); i++) {
        char c = s[i];
        switch (c) {
            case '"':  out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n";  break;
            case '\r': out += "\\r";  break;
            case '\t': out += "\\t";  break;
            default:
                if ((uint8_t)c < 0x20) {
                    char hex[8];
                    snprintf(hex, sizeof(hex), "\\u%04x", (uint8_t)c);
                    out += hex;
                } else {
                    out += c;
                }
                break;
        }
    }
    return out;
}

// ====== 封帧发送 ======
void Debug::sendFramed(const String &msg) {
    // 构造 LOG JSON
    String json = "{\"cmd\":\"LOG\",\"device_id\":\"" + deviceId +
                  "\",\"data\":{\"level\":\"INFO\",\"msg\":\"" + escapeJson(msg) + "\"}}";

    // 编码为协议帧并通过 Serial0 发送。日志通常是单帧，长日志也支持协议分片。
    int frameCapacity = ProtocolFrame::getEncodedCapacity(json);
    if (frameCapacity < 0) return;
    uint8_t *frameBuf = (uint8_t *)malloc(frameCapacity);
    if (frameBuf == nullptr) return;
    int frameLen = ProtocolFrame::encode(json, frameBuf, frameCapacity);
    if (frameLen > 0) {
        Serial.write(frameBuf, frameLen);
        Serial.flush();
    }
    free(frameBuf);
}

// ====== 核心输出 ======
void Debug::output(const String &msg, bool newline) {
    if (framing) {
        // 封帧模式：累积到换行才发送一帧
        lineBuffer += msg;
        if (newline) {
            sendFramed(lineBuffer);
            lineBuffer = "";
        }
    } else {
        // 裸文本模式：直接 Serial 输出
        if (newline) {
            Serial.println(msg);
        } else {
            Serial.print(msg);
        }
    }
}

// ====== println() 无参数重载 ======
void Debug::println() {
    output("", true);
}

// ====== printf ======
void Debug::printf(const char *fmt, ...) {
    char buf[512];
    va_list args;
    va_start(args, fmt);
    vsnprintf(buf, sizeof(buf), fmt, args);
    va_end(args);

    String formatted(buf);
    // 格式串末尾若有 \n，作为一行输出（去掉 \n，由 output 的 newline 参数负责）
    if (formatted.length() > 0 && formatted[formatted.length() - 1] == '\n') {
        formatted.remove(formatted.length() - 1);
        output(formatted, true);
    } else {
        output(formatted, false);
    }
}
