/**
 * protocol_frame.cpp - 协议帧解析器实现
 * 帧格式：帧头0xA5 0x5A + 版本1B + 长度2B(大端) + 负载 + CRC16 2B
 * CRC-16/MODBUS（多项式 0xA001），计算范围：版本+长度+负载
 * 分片支持：Payload>1400B时分片（消息ID+序号+总数+保留1B）
 * Phase 0: dual-slot PSRAM reassembly + static frag encode buffer + byte API
 */
#include "protocol_frame.h"
#include "debug.h"
#include "mem_pool.h"
#include <limits.h>

// 静态成员初始化
ProtocolFrame::DecodeState ProtocolFrame::decState = STATE_WAIT_HEAD1;
uint8_t   ProtocolFrame::decVersion = 0;
uint16_t  ProtocolFrame::decLen = 0;
uint16_t  ProtocolFrame::decPos = 0;
uint16_t  ProtocolFrame::decCrcRecv = 0;
uint8_t  *ProtocolFrame::decPayload = nullptr;
int       ProtocolFrame::crcErrorCount = 0;
uint8_t   ProtocolFrame::nextMsgId = 1;

ProtocolFrame::FragmentReassembly ProtocolFrame::fragSlots[FRAGMENT_SLOT_COUNT];

// Single-threaded encode path (Arduino loop / Debug mutex holders).
static uint8_t s_fragPayload[FRAME_MAX_PAYLOAD];

void ProtocolFrame::init() {
    MemPool::init();
    MemPool::noteHeapSample();

    if (decPayload == nullptr) {
        // Prefer permanent internal/PSRAM allocation once.
        decPayload = MemPool::allocPsram(FRAME_MAX_PAYLOAD + FRAGMENT_HEADER_SIZE + 16);
        if (decPayload == nullptr) {
            decPayload = (uint8_t*)malloc(FRAME_MAX_PAYLOAD + FRAGMENT_HEADER_SIZE + 16);
        }
    }

    for (int i = 0; i < FRAGMENT_SLOT_COUNT; i++) {
        if (fragSlots[i].data == nullptr) {
            fragSlots[i].data = MemPool::allocPsram(FRAGMENT_REASSEMBLY_BUF);
            if (fragSlots[i].data == nullptr) {
                fragSlots[i].data = (uint8_t*)malloc(FRAGMENT_REASSEMBLY_BUF);
            }
        }
        fragSlots[i].active = false;
        fragSlots[i].receivedCount = 0;
        fragSlots[i].msgId = 0;
        fragSlots[i].total = 0;
        fragSlots[i].startTime = 0;
        memset(fragSlots[i].receivedMask, 0, sizeof(fragSlots[i].receivedMask));
        memset(fragSlots[i].lengths, 0, sizeof(fragSlots[i].lengths));
    }

    decState = STATE_WAIT_HEAD1;
    decPos = 0;
    decLen = 0;
    crcErrorCount = 0;
    nextMsgId = 1;
    Debug::println(F("[FRAME] protocol frame parser init complete"));
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

uint16_t ProtocolFrame::crc16_buf_step(uint16_t crc, const uint8_t *data, size_t len) {
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
        Debug::println(F("[FRAME] encode buffer insufficient"));
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
    // NOTE: avoid VLA (uint8_t crcData[3 + len]) — it allocates on the stack
    // and can overflow small task stacks (e.g. sys_evt 2304B). Compute CRC
    // incrementally over the three regions instead.
    uint16_t crc = 0xFFFF;
    uint8_t versionByte = version;
    uint8_t lenHi = (len >> 8) & 0xFF;
    uint8_t lenLo = len & 0xFF;
    crc = crc16_buf_step(crc, &versionByte, 1);
    crc = crc16_buf_step(crc, &lenHi, 1);
    crc = crc16_buf_step(crc, &lenLo, 1);
    crc = crc16_buf_step(crc, payload, len);
    outBuf[pos++] = (crc >> 8) & 0xFF;
    outBuf[pos++] = crc & 0xFF;

    return pos;
}

int ProtocolFrame::getEncodedCapacityBytes(size_t payloadLen) {
    if (payloadLen > (size_t)FRAGMENT_REASSEMBLY_BUF) return -1;

    if (payloadLen <= FRAME_MAX_PAYLOAD) {
        return FRAME_HEADER_SIZE + (int)payloadLen + FRAME_CRC_SIZE;
        // Caller often wants a generous upper bound; keep single-frame exact size.
    }

    size_t chunkSize = FRAME_MAX_PAYLOAD - FRAGMENT_HEADER_SIZE;
    size_t totalFrags = (payloadLen + chunkSize - 1) / chunkSize;
    if (totalFrags > FRAGMENT_MAX_TOTAL) return -1;
    size_t capacity = totalFrags * (FRAME_HEADER_SIZE + FRAME_MAX_PAYLOAD + FRAME_CRC_SIZE);
    return capacity > 0x7FFFFFFF ? -1 : (int)capacity;
}

int ProtocolFrame::getEncodedCapacity(const String &json) {
    return getEncodedCapacityBytes(json.length());
}

int ProtocolFrame::encodeBytes(const uint8_t *payload, size_t len, uint8_t *outBuf, int outBufSize, uint8_t msgId) {
    if (payload == nullptr && len > 0) return -1;
    if (outBuf == nullptr || outBufSize <= 0) return -1;
    if (len > (size_t)FRAGMENT_REASSEMBLY_BUF) {
        Debug::println(F("[FRAME] message exceeds reassembly limit"));
        return -1;
    }

    if (msgId == 0) {
        msgId = nextMsgId++;
        if (nextMsgId == 0) nextMsgId = 1;
    }

    if (len <= FRAME_MAX_PAYLOAD) {
        return encodeSingleFrame(FRAME_VERSION_NORMAL, payload, (uint16_t)len, outBuf, outBufSize);
    }

    // 需要分片 — use static s_fragPayload (single-threaded encode)
    int chunkSize = FRAME_MAX_PAYLOAD - FRAGMENT_HEADER_SIZE;
    int totalFrags = (int)((len + chunkSize - 1) / chunkSize);
    if (totalFrags > FRAGMENT_MAX_TOTAL) {
        Debug::printf("[FRAME] message too large, fragment count %d exceeds limit\n", totalFrags);
        return -1;
    }

    int totalWritten = 0;
    for (int i = 0; i < totalFrags; i++) {
        int offset = i * chunkSize;
        int thisLen = (offset + chunkSize <= (int)len) ? chunkSize : ((int)len - offset);

        s_fragPayload[0] = msgId;              // 消息ID
        s_fragPayload[1] = (uint8_t)i;         // 分片序号
        s_fragPayload[2] = (uint8_t)totalFrags;// 总分片数
        s_fragPayload[3] = 0;                  // 保留
        memcpy(s_fragPayload + FRAGMENT_HEADER_SIZE, payload + offset, thisLen);

        int written = encodeSingleFrame(FRAME_VERSION_FRAGMENT, s_fragPayload,
                                        FRAGMENT_HEADER_SIZE + thisLen,
                                        outBuf + totalWritten, outBufSize - totalWritten);
        if (written < 0) {
            Debug::println(F("[FRAME] fragment encode failed"));
            return -1;
        }
        totalWritten += written;
    }

    Debug::printf("[FRAME] message fragment send: %d bytes -> %d fragments, msgId=%d\n",
                  (int)len, totalFrags, msgId);
    return totalWritten;
}

int ProtocolFrame::encode(const String &json, uint8_t *outBuf, int outBufSize, uint8_t msgId) {
    return encodeBytes((const uint8_t *)json.c_str(), json.length(), outBuf, outBufSize, msgId);
}

int ProtocolFrame::handleFragmentBytes(const uint8_t *data, uint16_t len,
                                       uint8_t *outBuf, int outBufSize) {
    if (len < FRAGMENT_HEADER_SIZE) {
        Debug::println(F("[FRAME] fragment data too short"));
        return 0;
    }

    uint8_t msgId = data[0];
    uint8_t seq   = data[1];
    uint8_t total = data[2];
    // data[3] 保留

    if (total == 0 || total > FRAGMENT_MAX_TOTAL || seq >= total) {
        Debug::printf("[FRAME] invalid fragment params: seq=%d total=%d\n", seq, total);
        return 0;
    }

    // Find matching active slot or free slot
    FragmentReassembly *slot = nullptr;
    for (int i = 0; i < FRAGMENT_SLOT_COUNT; i++) {
        if (fragSlots[i].active && fragSlots[i].msgId == msgId && fragSlots[i].total == total) {
            slot = &fragSlots[i];
            break;
        }
    }
    if (slot == nullptr) {
        for (int i = 0; i < FRAGMENT_SLOT_COUNT; i++) {
            if (!fragSlots[i].active) {
                slot = &fragSlots[i];
                break;
            }
        }
    }
    if (slot == nullptr) {
        // Evict oldest active slot
        unsigned long oldest = UINT32_MAX;
        int oldestIdx = 0;
        for (int i = 0; i < FRAGMENT_SLOT_COUNT; i++) {
            if (fragSlots[i].active && fragSlots[i].startTime < oldest) {
                oldest = fragSlots[i].startTime;
                oldestIdx = i;
            }
        }
        Debug::printf("[FRAME] fragment slots full; discard msgId=%d for new msgId=%d\n",
                      fragSlots[oldestIdx].msgId, msgId);
        slot = &fragSlots[oldestIdx];
        slot->active = false;
    }

    if (slot->data == nullptr) {
        Debug::println(F("[FRAME] fragment slot buffer missing"));
        return 0;
    }

    // New message into this slot?
    if (!slot->active || slot->msgId != msgId || slot->total != total) {
        if (slot->active && slot->msgId != msgId) {
            Debug::printf("[FRAME] discard incomplete fragment message msgId=%d (received new msgId=%d)\n",
                          slot->msgId, msgId);
        }
        memset(slot->receivedMask, 0, sizeof(slot->receivedMask));
        memset(slot->lengths, 0, sizeof(slot->lengths));
        slot->msgId = msgId;
        slot->total = total;
        slot->receivedCount = 0;
        slot->startTime = millis();
        slot->active = true;
    }

    // 检查是否已收到该分片
    int byteIdx = seq / 8;
    int bitIdx = seq % 8;
    if (slot->receivedMask[byteIdx] & (1 << bitIdx)) {
        Debug::printf("[FRAME] duplicate fragment msgId=%d seq=%d, ignored\n", msgId, seq);
        return 0;
    }

    // 计算该分片在重组缓冲中的偏移
    int dataOffset = seq * (FRAME_MAX_PAYLOAD - FRAGMENT_HEADER_SIZE);
    int dataLen = len - FRAGMENT_HEADER_SIZE;
    if (dataOffset + dataLen > FRAGMENT_REASSEMBLY_BUF) {
        Debug::println(F("[FRAME] fragment reassembly buffer overflow"));
        slot->active = false;
        return 0;
    }
    memcpy(slot->data + dataOffset, data + FRAGMENT_HEADER_SIZE, dataLen);
    slot->lengths[seq] = dataLen;
    slot->receivedMask[byteIdx] |= (1 << bitIdx);
    slot->receivedCount++;

    Debug::printf("[FRAME] received fragment msgId=%d seq=%d/%d (received %d/%d)\n",
                  msgId, seq, total, slot->receivedCount, total);

    // 检查是否收齐
    if (slot->receivedCount >= slot->total) {
        int totalLen = 0;
        for (int i = 0; i < slot->total; i++) {
            totalLen += slot->lengths[i];
        }
        // Compact in order (slots already at fixed offsets when all chunks same size;
        // last may be short — lengths[] tracks actual). Copy sequentially.
        // Data was stored at seq * chunkSize offsets already contiguous when all
        // but last are full-size; totalLen is sum of lengths.
        if (outBuf == nullptr || outBufSize < totalLen) {
            Debug::println(F("[FRAME] reassembly output buffer too small"));
            slot->active = false;
            return 0;
        }
        // Re-pack in order in case of sparse layout (should already be sequential)
        int writePos = 0;
        int chunkSize = FRAME_MAX_PAYLOAD - FRAGMENT_HEADER_SIZE;
        for (int i = 0; i < slot->total; i++) {
            int off = i * chunkSize;
            memcpy(outBuf + writePos, slot->data + off, slot->lengths[i]);
            writePos += slot->lengths[i];
        }
        slot->active = false;
        Debug::printf("[FRAME] fragment reassembly complete msgId=%d, total length %d\n", msgId, totalLen);
        return totalLen;
    }

    return 0; // 还未收齐
}

String ProtocolFrame::handleFragment(const uint8_t *data, uint16_t len) {
    // Temporary buffer on stack is too large; use first free slot's data as staging
    // after completion via handleFragmentBytes into a heap-less path:
    // Use MemPool frameTx only if small enough; for reassembly result use slot data pack
    // into a static reassembly out if needed. Prefer allocating from PSRAM once.
    static uint8_t *s_reasmOut = nullptr;
    if (s_reasmOut == nullptr) {
        s_reasmOut = MemPool::allocPsram(FRAGMENT_REASSEMBLY_BUF);
        if (s_reasmOut == nullptr) {
            s_reasmOut = (uint8_t*)malloc(FRAGMENT_REASSEMBLY_BUF);
        }
    }
    if (s_reasmOut == nullptr) return "";
    int outLen = handleFragmentBytes(data, len, s_reasmOut, FRAGMENT_REASSEMBLY_BUF);
    if (outLen <= 0) return "";
    return String((char*)s_reasmOut, outLen);
}

void ProtocolFrame::checkFragmentTimeout() {
    unsigned long now = millis();
    for (int i = 0; i < FRAGMENT_SLOT_COUNT; i++) {
        FragmentReassembly &slot = fragSlots[i];
        if (slot.active && (now - slot.startTime > FRAGMENT_TIMEOUT_MS)) {
            Debug::printf("[FRAME] fragment reassembly timeout msgId=%d (received %d/%d), discarded\n",
                          slot.msgId, slot.receivedCount, slot.total);
            slot.active = false;
            slot.receivedCount = 0;
        }
    }
}

int ProtocolFrame::processPayloadBytes(uint8_t version, const uint8_t *data, uint16_t len,
                                       uint8_t *outBuf, int outBufSize) {
    checkFragmentTimeout();

    if (version == FRAME_VERSION_NORMAL) {
        if (outBuf == nullptr || outBufSize < (int)len) return 0;
        memcpy(outBuf, data, len);
        return (int)len;
    } else if (version == FRAME_VERSION_FRAGMENT) {
        return handleFragmentBytes(data, len, outBuf, outBufSize);
    }
    Debug::printf("[FRAME] unknown version: 0x%02X\n", version);
    return 0;
}

String ProtocolFrame::processPayload(uint8_t version, const uint8_t *data, uint16_t len) {
    checkFragmentTimeout();

    if (version == FRAME_VERSION_NORMAL) {
        return String((const char*)data, len);
    } else if (version == FRAME_VERSION_FRAGMENT) {
        return handleFragment(data, len);
    }
    Debug::printf("[FRAME] unknown version: 0x%02X\n", version);
    return "";
}

bool ProtocolFrame::decodeBytes(uint8_t byte, uint8_t *outBuf, int outBufSize, int &outLen) {
    outLen = 0;
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
                Debug::printf("[FRAME] invalid version: 0x%02X\n", byte);
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
                Debug::printf("[FRAME] abnormal payload length: %d\n", decLen);
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
            // 计算 CRC：版本 + 长度 + 负载（增量计算，避免 VLA 栈分配）
            uint16_t calcCrc = 0xFFFF;
            uint8_t versionByte = decVersion;
            uint8_t lenHi = (decLen >> 8) & 0xFF;
            uint8_t lenLo = decLen & 0xFF;
            calcCrc = crc16_buf_step(calcCrc, &versionByte, 1);
            calcCrc = crc16_buf_step(calcCrc, &lenHi, 1);
            calcCrc = crc16_buf_step(calcCrc, &lenLo, 1);
            calcCrc = crc16_buf_step(calcCrc, decPayload, decLen);

            if (calcCrc != decCrcRecv) {
                crcErrorCount++;
                Debug::printf("[FRAME] CRC checksum failed: received=0x%04X calculated=0x%04X (error count=%d)\n",
                              decCrcRecv, calcCrc, crcErrorCount);
            } else {
                int n = processPayloadBytes(decVersion, decPayload, decLen, outBuf, outBufSize);
                if (n > 0) {
                    outLen = n;
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

bool ProtocolFrame::decode(uint8_t byte, String &jsonOut) {
    // Reuse decodeBytes with a static/PSRAM staging buffer for complete payload.
    static uint8_t *s_decodeOut = nullptr;
    if (s_decodeOut == nullptr) {
        s_decodeOut = MemPool::allocPsram(FRAGMENT_REASSEMBLY_BUF);
        if (s_decodeOut == nullptr) {
            s_decodeOut = (uint8_t*)malloc(FRAGMENT_REASSEMBLY_BUF);
        }
    }
    if (s_decodeOut == nullptr) {
        // Fall back to original path without byte buffer
        // Minimal: keep state machine via decodeBytes with null out for non-complete
        // and for complete frames we need a buffer — fail soft
        return false;
    }
    int outLen = 0;
    if (decodeBytes(byte, s_decodeOut, FRAGMENT_REASSEMBLY_BUF, outLen)) {
        jsonOut = String((char*)s_decodeOut, outLen);
        return true;
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
    int sum = 0;
    for (int i = 0; i < FRAGMENT_SLOT_COUNT; i++) {
        if (fragSlots[i].active) sum += fragSlots[i].receivedCount;
    }
    return sum;
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
