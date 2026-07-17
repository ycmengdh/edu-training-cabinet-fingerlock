/**
 * protocol_frame.h - 协议帧解析器
 * 帧格式：帧头0xA5 0x5A + 版本1B + 长度2B(大端) + JSON负载 + CRC16 2B
 * 支持 CRC-16/MODBUS 校验和大消息分片（Payload>1400B）
 */
#ifndef PROTOCOL_FRAME_H
#define PROTOCOL_FRAME_H

#include <Arduino.h>
#include "config_common.h"

class ProtocolFrame {
public:
    // 接收回调函数类型：收到完整JSON时调用
    typedef void (*FrameCallback)(const String &json);

    // 初始化帧解析器
    static void init();

    // ====== 编码 ======
    // 将 JSON 字符串编码为帧（或多帧分片），写入 outBuf
    // msgId: 分片消息ID（0=自动分配）
    // 返回写入的总字节数，-1 表示失败
    static int encode(const String &json, uint8_t *outBuf, int outBufSize, uint8_t msgId = 0);

    // 返回 encode 所需的输出缓冲区容量，-1 表示消息超过重组上限。
    static int getEncodedCapacity(const String &json);

    // ====== 解码（逐字节状态机） ======
    // 喂入一个字节，返回 true 表示收到完整帧（jsonOut 输出JSON）
    static bool decode(uint8_t byte, String &jsonOut);

    // 批量解码：喂入数据块，对每个完整帧调用回调
    static void decode(const uint8_t *data, int len, FrameCallback cb);

    // 重置解码器状态
    static void resetDecoder();

    // ====== CRC16 ======
    // CRC-16/MODBUS 计算（多项式 0xA001）
    static uint16_t crc16(const uint8_t *data, size_t len);

    // ====== 统计 ======
    static int getCrcErrorCount();
    static int getFragmentCount();

    // ====== 辅助：构造完整JSON消息字符串 ======
    // 组装 cmd/device_id/data/timestamp 的 JSON 字符串
    static String buildMessage(const String &cmd, const String &device_id,
                               const String &dataJson, const String &msgId = "");

private:
    // 解码状态机
    enum DecodeState {
        STATE_WAIT_HEAD1,    // 等待帧头 0xA5
        STATE_WAIT_HEAD2,    // 等待帧头 0x5A
        STATE_READ_VERSION,  // 读版本字节
        STATE_READ_LEN_HI,   // 读长度高字节
        STATE_READ_LEN_LO,   // 读长度低字节
        STATE_READ_PAYLOAD,  // 读负载
        STATE_READ_CRC_HI,   // 读CRC高字节
        STATE_READ_CRC_LO    // 读CRC低字节
    };

    static DecodeState decState;
    static uint8_t  decVersion;
    static uint16_t decLen;
    static uint16_t decPos;
    static uint16_t decCrcRecv;
    static uint8_t *decPayload;
    static int      crcErrorCount;

    // 分片重组缓冲
    struct FragmentReassembly {
        uint8_t  msgId;
        uint8_t  total;
        uint8_t  receivedMask[32];   // 位图：最多255分片
        uint8_t *data;
        uint16_t lengths[FRAGMENT_MAX_TOTAL];
        int      receivedCount;
        unsigned long startTime;
        bool     active;
    };
    static FragmentReassembly fragBuf;
    static uint8_t  nextMsgId;

    // 处理解码后的负载（区分正常帧和分片帧）
    static String processPayload(uint8_t version, const uint8_t *data, uint16_t len);

    // 处理分片重组
    static String handleFragment(const uint8_t *data, uint16_t len);

    // 检查分片超时
    static void checkFragmentTimeout();

    // 编码单帧（不带分片）
    static int encodeSingleFrame(uint8_t version, const uint8_t *payload,
                                 uint16_t len, uint8_t *outBuf, int outBufSize);
};

#endif // PROTOCOL_FRAME_H
