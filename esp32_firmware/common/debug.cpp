/**
 * debug.cpp - 调试输出封装实现
 * USB 上行模式：LOG 协议帧封装，上位机收到的是合法帧（cmd=LOG）
 * 其他模式：透传 Serial 裸文本
 */
#include "debug.h"
#include "config_common.h"
#include "protocol_frame.h"
#include <stdarg.h>
#include <freertos/FreeRTOS.h>
#include <freertos/semphr.h>

bool   Debug::framing    = false;
String Debug::lineBuffer;
String Debug::deviceId   = "";

// Mesh callbacks, the receive task and the Arduino loop run concurrently.
// A recursive mutex is required because frame encoding may itself report an
// error through Debug while the outer log operation still owns the lock.
static SemaphoreHandle_t debugOutputMutex = nullptr;

// ====== 初始化 ======
void Debug::init() {
    // framing 由 main.cpp 通过 setFraming() 设置
    // deviceId 由 main.cpp 通过 setDeviceId() 设置
    if (debugOutputMutex == nullptr) {
        debugOutputMutex = xSemaphoreCreateRecursiveMutex();
    }
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
// Uses heap-allocated buffers and explicit C-string construction to minimize
// stack usage. The previous implementation used String concatenation which
// created multiple temporary String objects on the stack, consuming 200-400B
// of stack per call. When called from meshEventHandler (sys_evt task, 2304B
// stack), this combined with the deep call chain (sendFramed → encode →
// Serial.write → Serial.flush → USB CDC) overflowed the stack and caused
// CORRUPT HEAP / stack canary panics.
void Debug::sendFramed(const String &msg) {
    // Escape the message into a heap-allocated buffer
    String escaped = escapeJson(msg);

    // Build the JSON directly into a heap-allocated buffer
    // Format: {"cmd":"LOG","device_id":"<id>","data":{"level":"INFO","msg":"<msg>"}}
    size_t cap = 80 + deviceId.length() + escaped.length();
    char *jsonBuf = (char *)malloc(cap);
    if (jsonBuf == nullptr) {
        return;
    }
    snprintf(jsonBuf, cap,
             "{\"cmd\":\"LOG\",\"device_id\":\"%s\",\"data\":{\"level\":\"INFO\",\"msg\":\"%s\"}}",
             deviceId.c_str(), escaped.c_str());

    // Encode to protocol frame and send
    String jsonStr(jsonBuf);  // shallow copy needed by ProtocolFrame::encode
    int frameCapacity = ProtocolFrame::getEncodedCapacity(jsonStr);
    if (frameCapacity > 0) {
        uint8_t *frameBuf = (uint8_t *)malloc(frameCapacity);
        if (frameBuf != nullptr) {
            int frameLen = ProtocolFrame::encode(jsonStr, frameBuf, frameCapacity);
            if (frameLen > 0) {
                Serial.write(frameBuf, frameLen);
            }
            free(frameBuf);
        }
    }
    free(jsonBuf);
}

// ====== 核心输出 ======
void Debug::output(const String &msg, bool newline) {
    if (debugOutputMutex != nullptr) {
        xSemaphoreTakeRecursive(debugOutputMutex, portMAX_DELAY);
    }

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

    if (debugOutputMutex != nullptr) {
        xSemaphoreGiveRecursive(debugOutputMutex);
    }
}

// ====== println() 无参数重载 ======
void Debug::println() {
    output("", true);
}

// ====== printf ======
// Uses static buffer to avoid 512B stack consumption per call.
// meshEventHandler runs in the sys_evt task whose stack is only 2304B by
// default; a 512B stack buffer plus String/escapeJson temporaries plus
// Serial.flush deep call chain can overflow that stack and corrupt the
// heap (observed: CORRUPT HEAP + heap_caps_free assert + stack canary
// on sys_evt). Static buffer is safe because debugOutputMutex serializes
// access and the Arduino loop + mesh_rx + sys_evt never legitimately
// interleave Debug::printf calls.
void Debug::printf(const char *fmt, ...) {
    static char buf[512];
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
