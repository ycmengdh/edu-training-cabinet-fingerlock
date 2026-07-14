/**
 * protocol_frame.cpp - 协议帧解析器实现
 * 帧格式：帧头0xA5 0x5A + 版本1B + 长度2B(大端) + JSON负载 + CRC16 2B
 * CRC-16/MODBUS（多项式 0xA001），计算范围：版本+长度+负载
 * 分片支持：Payload>1400B时分片（消息ID+序号+总数+保留1B）
 */
#include "protocol_frame.h"
#include <ArduinoJson.h>

// 静态成员初始化
ProtocolFrame::DecodeState ProtocolFrame::decState = STATE_WAIT_HEAD1;
uint8_t   ProtocolFrame::decVersion = 0;
uint16_t  ProtocolFrame::decLen = 0;
uint16_t  ProtocolFrame::decPos = 0;
uint16_t  ProtocolFrame::decCrcRecv = 0;
uint8_t  *ProtocolFrame::decPayload = nullptr;
int       ProtocolFrame::crcErrorCount = 0;
uint8_t   ProtocolFrame::nextMsgId = 1;

ProtocolFrame::FragmentReassembly ProtocolFrame::fragBuf;

void ProtocolFrame::init() {
    if (decPayload == nullptr) {
        decPayload = (uint8_t*)malloc(FRAME_MAX_PAYLOAD + FRAGMENT_HEADER_SIZE + 16);
    }
    if (fragBuf.data == nullptr) {
        fragBuf.data = (uint8_t*)malloc(FRAGMENT_REASSEMBLY_BUF);
    }
    fragBuf.active = false;
    fragBuf.receivedCount = 0;
    decState = STATE_WAIT_HEAD1;
    decPos = 0;
    decLen = 0;
    crcErrorCount = 0;
    nextMsgId = 1;
    Serial.println(F("[FRAME] 协议帧解析器初始化完成"));
}

uint16_t ProtocolFrame::crc16(const uint8_t *data, size_t len) {
    uint16_t crc = 0xFFFF;
    for (size_t i = 0; i < len; i++) {
        crc ^= data[i];
        for (int j = 0; j < 8; j++) {
            if (crc & 1) {
                crc = (crc >> 1) ^ 0xA001;
            } else {
                crc >>= 1;
            }
        }
    }
    return crc;
}

int ProtocolFrame::encodeSingleFrame(uint8_t version, const uint8_t *payload,
                                     uint16_t len, uint8_t *outBuf, int outBufSize) {
    int totalSize = FRAME_HEADER_SIZE + len + FRAME_CRC_SIZE;
    if (totalSize > outBufSize) {
        Serial.println(F("[FRAME] 编码缓冲区不足"));
        return -1;
    }

    int pos = 0;
    // 帧头
    outBuf[pos++] = FRAME_HEAD1;
    outBuf[pos++] = FRAME_HEAD2;
    // 版本
    outBuf[pos++] = version;
    // 长度（大端）
    outBuf[pos++] = (len >> 8) & 0xFF;
    outBuf[pos++] = len & 0xFF;
    // 负载
    memcpy(outBuf + pos, payload, len);
    pos += len;
    // CRC16（计算范围：版本+长度+负载）
    uint8_t crcData[3 + len];
    crcData[0] = version;
    crcData[1] = (len >> 8) & 0xFF;
    crcData[2] = len & 0xFF;
    memcpy(crcData + 3, payload, len);
    uint16_t crc = crc16(crcData, 3 + len);
    outBuf[pos++] = (crc >> 8) & 0xFF;
    outBuf[pos++] = crc & 0xFF;

    return pos;
}

int ProtocolFrame::encode(const String &json, uint8_t *outBuf, int outBufSize, uint8_t msgId) {
    uint16_t jsonLen = json.length();
    const uint8_t *jsonBytes = (const uint8_t *)json.c_str();

    if (msgId == 0) {
        msgId = nextMsgId++;
        if (nextMsgId == 0) nextMsgId = 1;
    }

    if (jsonLen <= FRAME_MAX_PAYLOAD) {
        // 不需要分片，使用正常版本
        return encodeSingleFrame(FRAME_VERSION_NORMAL, jsonBytes, jsonLen, outBuf, outBufSize);
    }

    // 需要分片
    int chunkSize = FRAME_MAX_PAYLOAD - FRAGMENT_HEADER_SIZE;
    int totalFrags = (jsonLen + chunkSize - 1) / chunkSize;
    if (totalFrags > FRAGMENT_MAX_TOTAL) {
        Serial.printf("[FRAME] 消息过大，分片数 %d 超限\n", totalFrags);
        return -1;
    }

    int totalWritten = 0;
    for (int i = 0; i < totalFrags; i++) {
        int offset = i * chunkSize;
        int thisLen = (offset + chunkSize <= jsonLen) ? chunkSize : (jsonLen - offset);

        uint8_t fragPayload[FRAME_MAX_PAYLOAD];
        fragPayload[0] = msgId;           // 消息ID
        fragPayload[1] = (uint8_t)i;      // 分片序号
        fragPayload[2] = (uint8_t)totalFrags; // 总分片数
        fragPayload[3] = 0;               // 保留
        memcpy(fragPayload + FRAGMENT_HEADER_SIZE, jsonBytes + offset, thisLen);

        int written = encodeSingleFrame(FRAME_VERSION_FRAGMENT, fragPayload,
                                        FRAGMENT_HEADER_SIZE + thisLen,
                                        outBuf + totalWritten, outBufSize - totalWritten);
        if (written < 0) {
            Serial.println(F("[FRAME] 分片编码失败"));
            return -1;
        }
        totalWritten += written;
    }

    Serial.printf("[FRAME] 消息分片发送: %d 字节 -> %d 分片, msgId=%d\n",
                  jsonLen, totalFrags, msgId);
    return totalWritten;
}

String ProtocolFrame::handleFragment(const uint8_t *data, uint16_t len) {
    if (len < FRAGMENT_HEADER_SIZE) {
        Serial.println(F("[FRAME] 分片数据过短"));
        return "";
    }

    uint8_t msgId = data[0];
    uint8_t seq   = data[1];
    uint8_t total = data[2];
    // data[3] 保留

    if (total == 0 || total > FRAGMENT_MAX_TOTAL || seq >= total) {
        Serial.printf("[FRAME] 分片参数无效: seq=%d total=%d\n", seq, total);
        return "";
    }

    // 检查是否是新消息
    if (!fragBuf.active || fragBuf.msgId != msgId) {
        // 新分片消息
        if (fragBuf.active && fragBuf.msgId != msgId) {
            Serial.printf("[FRAME] 丢弃未完成分片消息 msgId=%d (收到新 msgId=%d)\n",
                          fragBuf.msgId, msgId);
        }
        memset(fragBuf.receivedMask, 0, sizeof(fragBuf.receivedMask));
        memset(fragBuf.lengths, 0, sizeof(fragBuf.lengths));
        fragBuf.msgId = msgId;
        fragBuf.total = total;
        fragBuf.receivedCount = 0;
        fragBuf.startTime = millis();
        fragBuf.active = true;
    }

    // 检查是否已收到该分片
    int byteIdx = seq / 8;
    int bitIdx = seq % 8;
    if (fragBuf.receivedMask[byteIdx] & (1 << bitIdx)) {
        Serial.printf("[FRAME] 重复分片 msgId=%d seq=%d，忽略\n", msgId, seq);
        return "";
    }

    // 计算该分片在重组缓冲中的偏移
    int dataOffset = seq * (FRAME_MAX_PAYLOAD - FRAGMENT_HEADER_SIZE);
    int dataLen = len - FRAGMENT_HEADER_SIZE;
    if (dataOffset + dataLen > FRAGMENT_REASSEMBLY_BUF) {
        Serial.println(F("[FRAME] 分片重组缓冲溢出"));
        fragBuf.active = false;
        return "";
    }
    memcpy(fragBuf.data + dataOffset, data + FRAGMENT_HEADER_SIZE, dataLen);
    fragBuf.lengths[seq] = dataLen;
    fragBuf.receivedMask[byteIdx] |= (1 << bitIdx);
    fragBuf.receivedCount++;

    Serial.printf("[FRAME] 收到分片 msgId=%d seq=%d/%d (已收 %d/%d)\n",
                  msgId, seq, total, fragBuf.receivedCount, total);

    // 检查是否收齐
    if (fragBuf.receivedCount >= fragBuf.total) {
        // 拼接完整消息
        int totalLen = 0;
        for (int i = 0; i < fragBuf.total; i++) {
            totalLen += fragBuf.lengths[i];
        }
        String result((char*)fragBuf.data, totalLen);
        fragBuf.active = false;
        Serial.printf("[FRAME] 分片重组完成 msgId=%d, 总长度 %d\n", msgId, totalLen);
        return result;
    }

    return ""; // 还未收齐
}

void ProtocolFrame::checkFragmentTimeout() {
    if (fragBuf.active && (millis() - fragBuf.startTime > FRAGMENT_TIMEOUT_MS)) {
        Serial.printf("[FRAME] 分片重组超时 msgId=%d (已收 %d/%d)，丢弃\n",
                      fragBuf.msgId, fragBuf.receivedCount, fragBuf.total);
        fragBuf.active = false;
        fragBuf.receivedCount = 0;
    }
}

String ProtocolFrame::processPayload(uint8_t version, const uint8_t *data, uint16_t len) {
    checkFragmentTimeout();

    if (version == FRAME_VERSION_NORMAL) {
        // 正常帧，直接返回JSON
        return String((const char*)data, len);
    } else if (version == FRAME_VERSION_FRAGMENT) {
        // 分片帧，重组
        return handleFragment(data, len);
    }
    Serial.printf("[FRAME] 未知版本号: 0x%02X\n", version);
    return "";
}

bool ProtocolFrame::decode(uint8_t byte, String &jsonOut) {
    switch (decState) {
        case STATE_WAIT_HEAD1:
            if (byte == FRAME_HEAD1) {
                decState = STATE_WAIT_HEAD2;
            }
            break;

        case STATE_WAIT_HEAD2:
            if (byte == FRAME_HEAD2) {
                decState = STATE_READ_VERSION;
            } else if (byte == FRAME_HEAD1) {
                // 保持等待 HEAD2 状态
            } else {
                decState = STATE_WAIT_HEAD1;
            }
            break;

        case STATE_READ_VERSION:
            decVersion = byte;
            if (byte != FRAME_VERSION_NORMAL && byte != FRAME_VERSION_FRAGMENT) {
                Serial.printf("[FRAME] 无效版本号: 0x%02X\n", byte);
                decState = STATE_WAIT_HEAD1;
                break;
            }
            decState = STATE_READ_LEN_HI;
            break;

        case STATE_READ_LEN_HI:
            decLen = (uint16_t)byte << 8;
            decState = STATE_READ_LEN_LO;
            break;

        case STATE_READ_LEN_LO:
            decLen |= byte;
            if (decLen == 0 || decLen > FRAME_MAX_PAYLOAD + FRAGMENT_HEADER_SIZE) {
                Serial.printf("[FRAME] 负载长度异常: %d\n", decLen);
                decState = STATE_WAIT_HEAD1;
                break;
            }
            decPos = 0;
            decState = STATE_READ_PAYLOAD;
            break;

        case STATE_READ_PAYLOAD:
            if (decPayload == nullptr) {
                decState = STATE_WAIT_HEAD1;
                break;
            }
            decPayload[decPos++] = byte;
            if (decPos >= decLen) {
                decState = STATE_READ_CRC_HI;
            }
            break;

        case STATE_READ_CRC_HI:
            decCrcRecv = (uint16_t)byte << 8;
            decState = STATE_READ_CRC_LO;
            break;

        case STATE_READ_CRC_LO: {
            decCrcRecv |= byte;
            // 计算 CRC：版本 + 长度 + 负载
            uint8_t crcData[3 + decLen];
            crcData[0] = decVersion;
            crcData[1] = (decLen >> 8) & 0xFF;
            crcData[2] = decLen & 0xFF;
            memcpy(crcData + 3, decPayload, decLen);
            uint16_t calcCrc = crc16(crcData, 3 + decLen);

            if (calcCrc != decCrcRecv) {
                crcErrorCount++;
                Serial.printf("[FRAME] CRC校验失败: 收到=0x%04X 计算=0x%04X (错误计数=%d)\n",
                              decCrcRecv, calcCrc, crcErrorCount);
            } else {
                // CRC 通过，处理负载
                String result = processPayload(decVersion, decPayload, decLen);
                if (result.length() > 0) {
                    jsonOut = result;
                    decState = STATE_WAIT_HEAD1;
                    return true;
                }
            }
            decState = STATE_WAIT_HEAD1;
            break;
        }

        default:
            decState = STATE_WAIT_HEAD1;
            break;
    }
    return false;
}

void ProtocolFrame::decode(const uint8_t *data, int len, FrameCallback cb) {
    String json;
    for (int i = 0; i < len; i++) {
        if (decode(data[i], json)) {
            if (cb) {
                cb(json);
            }
        }
    }
    // 检查分片超时
    checkFragmentTimeout();
}

void ProtocolFrame::resetDecoder() {
    decState = STATE_WAIT_HEAD1;
    decPos = 0;
    decLen = 0;
}

int ProtocolFrame::getCrcErrorCount() {
    return crcErrorCount;
}

int ProtocolFrame::getFragmentCount() {
    return fragBuf.active ? fragBuf.receivedCount : 0;
}

String ProtocolFrame::buildMessage(const String &cmd, const String &device_id,
                                   const String &dataJson, const String &msgId) {
    // 构造时间戳
    struct tm timeinfo;
    String ts;
    if (getLocalTime(&timeinfo, 0)) {
        char buf[32];
        strftime(buf, sizeof(buf), "%Y-%m-%d %H:%M:%S", &timeinfo);
        ts = String(buf);
    } else {
        ts = "1970-01-01 00:00:00";
    }

    // 手动拼接 JSON（避免动态分配大缓冲）
    String msg = "{\"cmd\":\"" + cmd + "\",";
    msg += "\"device_id\":\"" + device_id + "\",";
    if (msgId.length() > 0) {
        msg += "\"msg_id\":\"" + msgId + "\",";
    }
    if (dataJson.length() > 0) {
        msg += "\"data\":" + dataJson + ",";
    } else {
        msg += "\"data\":{},";
    }
    msg += "\"timestamp\":\"" + ts + "\"}";
    return msg;
}
