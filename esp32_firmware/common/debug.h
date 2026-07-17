/**
 * debug.h - 调试输出封装
 * USB 上行模式：将 debug 信息封装为 LOG 协议帧（cmd=LOG）发送，避免干扰上位机解析
 * 其他模式：透明回退到 Serial 裸文本输出（方便串口监视器调试）
 *
 * LOG 帧 JSON 格式：
 *   {"cmd":"LOG","device_id":"xxx","data":{"level":"INFO","msg":"..."}}
 */
#ifndef DEBUG_H
#define DEBUG_H

#include <Arduino.h>

class Debug {
public:
    // 初始化（Storage::begin 之后调用，根据配置决定是否封帧）
    static void init();

    // 设置设备 ID（缓存，避免 sendFramed 每次重读 Flash）
    static void setDeviceId(const String &id);

    // 手动设置是否启用协议帧封装
    static void setFraming(bool enable);

    // 当前是否处于封帧模式
    static bool isFraming();

    // ====== 输出接口（与 Serial 用法一致） ======
    // 无参数：输出空行
    static void println();

    // 通用模板：任何可 String() 转换的类型（const char*, String, F(), int, float ...）
    template<typename T>
    static void print(T val) { output(String(val), false); }

    template<typename T>
    static void println(T val) { output(String(val), true); }

    // printf 风格（格式串末尾 \n 自动处理为一行）
    static void printf(const char *fmt, ...);

private:
    static bool   framing;     // true=协议帧封装, false=裸文本
    static String lineBuffer;  // print() 不带换行时的累积缓冲
    static String deviceId;    // 缓存的设备 ID（setDeviceId 设置）

    // 核心输出：封帧模式缓冲到换行再发送，裸文本模式直接 Serial
    static void output(const String &msg, bool newline);

    // 将一行消息封装为 LOG 协议帧并通过 Serial 发送
    static void sendFramed(const String &msg);

    // JSON 字符串转义
    static String escapeJson(const String &s);
};

#endif // DEBUG_H
