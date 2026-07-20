/**
 * app_protocol.cpp - Binary application envelope + payload helpers
 *
 * Wire layout MUST match PC BinaryMessageCodec (little-endian):
 *   [0]  magic0=0xB1
 *   [1]  magic1=0x0F
 *   [2]  proto_ver
 *   [3]  flags
 *   [4]  cmd_id u16
 *   [6]  msg_id u16
 *   [8]  corr_id u16
 *   [10] device_id_len u8
 *   [11] source_id_len u8
 *   [12] payload_len u16
 *   [14] timestamp_unix u32
 *   [18] [hmac_ts u32 + nonce 8 + sig 32] if HAS_HMAC
 *        device_id[N]
 *        source_id[M]
 *        payload[P]
 */
#include "app_protocol.h"

#include <Arduino.h>
#include <string.h>
#include <time.h>

static const int APP_HMAC_BLOCK = 4 + 8 + 32; // ts + nonce + sig
static uint16_t s_nextMsgId = 1;

void wrU16(uint8_t* p, uint16_t v) {
    p[0] = (uint8_t)(v & 0xFF);
    p[1] = (uint8_t)((v >> 8) & 0xFF);
}

void wrU32(uint8_t* p, uint32_t v) {
    p[0] = (uint8_t)(v & 0xFF);
    p[1] = (uint8_t)((v >> 8) & 0xFF);
    p[2] = (uint8_t)((v >> 16) & 0xFF);
    p[3] = (uint8_t)((v >> 24) & 0xFF);
}

uint16_t rdU16(const uint8_t* p) {
    return (uint16_t)p[0] | ((uint16_t)p[1] << 8);
}

uint32_t rdU32(const uint8_t* p) {
    return (uint32_t)p[0]
         | ((uint32_t)p[1] << 8)
         | ((uint32_t)p[2] << 16)
         | ((uint32_t)p[3] << 24);
}

static size_t cstrLenCap(const char* s, size_t cap) {
    if (s == nullptr) return 0;
    size_t n = 0;
    while (n < cap && s[n] != '\0') n++;
    return n;
}

uint16_t appNextMsgId() {
    uint16_t id = s_nextMsgId++;
    if (s_nextMsgId == 0) s_nextMsgId = 1;
    return id;
}

int appEncode(uint8_t* out, int outSize,
              uint16_t cmdId, uint16_t msgId, uint16_t corrId, uint8_t flags,
              const char* deviceId, const char* sourceId,
              const uint8_t* payload, uint16_t payloadLen,
              uint32_t timestampUnix) {
    if (out == nullptr || outSize < APP_ENVELOPE_MIN) return -1;
    if (payloadLen > APP_MAX_PAYLOAD) return -1;
    if (payloadLen > 0 && payload == nullptr) return -1;

    // Encoder does not attach HMAC body yet; clear the flag so wire stays consistent.
    flags = (uint8_t)(flags & ~APP_FLAG_HAS_HMAC);

    size_t devLen = cstrLenCap(deviceId, APP_DEVICE_ID_MAX);
    size_t srcLen = cstrLenCap(sourceId, APP_SOURCE_ID_MAX);
    int need = APP_ENVELOPE_MIN + (int)devLen + (int)srcLen + (int)payloadLen;
    if (need > outSize) return -1;

    if (timestampUnix == 0) {
        time_t now = time(nullptr);
        if (now > 0) timestampUnix = (uint32_t)now;
    }

    out[0] = APP_MAGIC_0;
    out[1] = APP_MAGIC_1;
    out[2] = APP_PROTO_VER;
    out[3] = flags;
    wrU16(out + 4, cmdId);
    wrU16(out + 6, msgId);
    wrU16(out + 8, corrId);
    out[10] = (uint8_t)devLen;
    out[11] = (uint8_t)srcLen;
    wrU16(out + 12, payloadLen);
    wrU32(out + 14, timestampUnix);

    int pos = APP_ENVELOPE_MIN;
    if (devLen > 0) {
        memcpy(out + pos, deviceId, devLen);
        pos += (int)devLen;
    }
    if (srcLen > 0) {
        memcpy(out + pos, sourceId, srcLen);
        pos += (int)srcLen;
    }
    if (payloadLen > 0) {
        memcpy(out + pos, payload, payloadLen);
        pos += payloadLen;
    }
    return pos;
}

bool appDecode(const uint8_t* data, int len, AppMessageView& view) {
    memset(&view, 0, sizeof(view));
    if (data == nullptr || len < APP_ENVELOPE_MIN) return false;
    if (data[0] != APP_MAGIC_0 || data[1] != APP_MAGIC_1) return false;
    if (data[2] != APP_PROTO_VER) return false;

    view.flags = data[3];
    view.cmd_id = rdU16(data + 4);
    view.msg_id = rdU16(data + 6);
    view.corr_id = rdU16(data + 8);
    view.device_id_len = data[10];
    view.source_id_len = data[11];
    view.payload_len = rdU16(data + 12);
    view.timestamp_unix = rdU32(data + 14);

    if (view.device_id_len > APP_DEVICE_ID_MAX) return false;
    if (view.source_id_len > APP_SOURCE_ID_MAX) return false;
    if (view.payload_len > APP_MAX_PAYLOAD) return false;

    bool hasHmac = (view.flags & APP_FLAG_HAS_HMAC) != 0;
    int hmacSize = hasHmac ? APP_HMAC_BLOCK : 0;
    int needed = APP_ENVELOPE_MIN + hmacSize + view.device_id_len +
                 view.source_id_len + view.payload_len;
    if (len < needed) return false;

    int pos = APP_ENVELOPE_MIN;
    if (hasHmac) {
        view.hmac = data + pos;
        view.hmac_len = 32; // signature is last 32 of the 44-byte block
        pos += hmacSize;
    }

    view.device_id = (view.device_id_len > 0) ? (const char*)(data + pos) : nullptr;
    pos += view.device_id_len;
    view.source_id = (view.source_id_len > 0) ? (const char*)(data + pos) : nullptr;
    pos += view.source_id_len;
    view.payload = (view.payload_len > 0) ? (data + pos) : nullptr;
    return true;
}

// ---- Payload packers ----
// Heartbeat: freeHeap u32, freePsram u32, minFreeHeap u16, meshLayer u8,
//            flags u8, sendFail u16, queueFull u16, recoveries u16  => 18B
int packHeartbeat(uint8_t* out, int outSize, uint32_t freeHeap, uint32_t freePsram,
                  uint16_t minFreeHeap, uint8_t meshLayer, uint8_t flags,
                  uint16_t sendFail, uint16_t queueFull, uint16_t recoveries) {
    if (out == nullptr || outSize < 18) return -1;
    wrU32(out + 0, freeHeap);
    wrU32(out + 4, freePsram);
    wrU16(out + 8, minFreeHeap);
    out[10] = meshLayer;
    out[11] = flags;
    wrU16(out + 12, sendFail);
    wrU16(out + 14, queueFull);
    wrU16(out + 16, recoveries);
    return 18;
}

bool unpackHeartbeat(const uint8_t* p, uint16_t len,
                     uint32_t& freeHeap, uint32_t& freePsram,
                     uint16_t& minFreeHeap, uint8_t& meshLayer, uint8_t& flags,
                     uint16_t& sendFail, uint16_t& queueFull, uint16_t& recoveries) {
    if (p == nullptr || len < 18) return false;
    freeHeap = rdU32(p + 0);
    freePsram = rdU32(p + 4);
    minFreeHeap = rdU16(p + 8);
    meshLayer = p[10];
    flags = p[11];
    sendFail = rdU16(p + 12);
    queueFull = rdU16(p + 14);
    recoveries = rdU16(p + 16);
    return true;
}

// ACK: refMsgId u16, resultCode u16, tag_len u8, tag[N]
int packAck(uint8_t* out, int outSize, uint16_t refMsgId, uint16_t resultCode, const char* tag) {
    size_t tagLen = cstrLenCap(tag, 64);
    int need = 5 + (int)tagLen;
    if (out == nullptr || outSize < need) return -1;
    wrU16(out + 0, refMsgId);
    wrU16(out + 2, resultCode);
    out[4] = (uint8_t)tagLen;
    if (tagLen > 0) memcpy(out + 5, tag, tagLen);
    return need;
}

bool unpackAck(const uint8_t* p, uint16_t len,
               uint16_t& refMsgId, uint16_t& resultCode, char* tag, int tagMax) {
    if (p == nullptr || len < 5) return false;
    refMsgId = rdU16(p + 0);
    resultCode = rdU16(p + 2);
    uint8_t tagLen = p[4];
    if (5 + tagLen > len) return false;
    if (tag != nullptr && tagMax > 0) {
        int copy = tagLen;
        if (copy >= tagMax) copy = tagMax - 1;
        if (copy > 0) memcpy(tag, p + 5, copy);
        tag[copy] = '\0';
    }
    return true;
}

// ERROR: refMsgId u16, errorCode u16, msg_len u8, msg[N]
int packError(uint8_t* out, int outSize, uint16_t refMsgId, uint16_t errorCode, const char* msg) {
    size_t msgLen = cstrLenCap(msg, 128);
    int need = 5 + (int)msgLen;
    if (out == nullptr || outSize < need) return -1;
    wrU16(out + 0, refMsgId);
    wrU16(out + 2, errorCode);
    out[4] = (uint8_t)msgLen;
    if (msgLen > 0) memcpy(out + 5, msg, msgLen);
    return need;
}

bool unpackError(const uint8_t* p, uint16_t len,
                 uint16_t& refMsgId, uint16_t& errorCode, char* msg, int msgMax) {
    if (p == nullptr || len < 5) return false;
    refMsgId = rdU16(p + 0);
    errorCode = rdU16(p + 2);
    uint8_t msgLen = p[4];
    if (5 + msgLen > len) return false;
    if (msg != nullptr && msgMax > 0) {
        int copy = msgLen;
        if (copy >= msgMax) copy = msgMax - 1;
        if (copy > 0) memcpy(msg, p + 5, copy);
        msg[copy] = '\0';
    }
    return true;
}

// CONTROL_LOCK: lockId u8, action u8
int packControlLock(uint8_t* out, int outSize, uint8_t lockId, uint8_t action) {
    if (out == nullptr || outSize < 2) return -1;
    out[0] = lockId;
    out[1] = action;
    return 2;
}

bool unpackControlLock(const uint8_t* p, uint16_t len, uint8_t& lockId, uint8_t& action) {
    if (p == nullptr || len < 2) return false;
    lockId = p[0];
    action = p[1];
    return true;
}

// ---- Name <-> id mapping (transitional JSON path) ----
struct CmdNameEntry {
    uint16_t id;
    const char* name;
};

static const CmdNameEntry kCmdTable[] = {
    {CMD_REGISTER, "REGISTER"},
    {CMD_HEARTBEAT, "HEARTBEAT"},
    {CMD_HEARTBEAT_ACK, "HEARTBEAT_ACK"},
    {CMD_ACK, "ACK"},
    {CMD_ERROR, "ERROR"},
    {CMD_CONTROL_LOCK, "CONTROL_LOCK"},
    {CMD_ADD_FINGERPRINT, "ADD_FINGERPRINT"},
    {CMD_ADD_FINGERPRINT_RESULT, "ADD_FINGERPRINT_RESULT"},
    {CMD_ENROLL_PROGRESS, "ENROLL_PROGRESS"},
    {CMD_DELETE_FINGERPRINT, "DELETE_FINGERPRINT"},
    {CMD_RESTORE_FINGERPRINT, "RESTORE_FINGERPRINT"},
    {CMD_RESTORE_FINGERPRINT_RESULT, "RESTORE_FINGERPRINT_RESULT"},
    {CMD_DELETE_ALL_FINGERPRINTS, "DELETE_ALL_FINGERPRINTS"},
    {CMD_ADD_BACKUP_FINGERPRINT, "ADD_BACKUP_FINGERPRINT"},
    {CMD_BACKUP_FP_LIST, "BACKUP_FP_LIST"},
    {CMD_BACKUP_FP_LIST_REQUEST, "BACKUP_FP_LIST_REQUEST"},
    {CMD_DELETE_BACKUP_FINGERPRINT, "DELETE_BACKUP_FINGERPRINT"},
    {CMD_VERIFY_WINDOW_EVENT, "VERIFY_WINDOW_EVENT"},
    {CMD_BEGIN_PERMISSION_SYNC, "BEGIN_PERMISSION_SYNC"},
    {CMD_SYNC_PERMISSION, "SYNC_PERMISSION"},
    {CMD_COMMIT_PERMISSION_SYNC, "COMMIT_PERMISSION_SYNC"},
    {CMD_CLEAR_PERMISSIONS, "CLEAR_PERMISSIONS"},
    {CMD_SYNC_ACK, "SYNC_ACK"},
    {CMD_SYNC_PERMISSIONS, "SYNC_PERMISSIONS"},
    {CMD_READ_PERMISSIONS, "READ_PERMISSIONS"},
    {CMD_READ_CONFIG, "READ_CONFIG"},
    {CMD_WRITE_CONFIG, "WRITE_CONFIG"},
    {CMD_CONFIG_RESPONSE, "CONFIG_RESPONSE"},
    {CMD_CONFIG_SAVED, "CONFIG_SAVED"},
    {CMD_READ_STATUS, "READ_STATUS"},
    {CMD_STATUS_RESPONSE, "STATUS_RESPONSE"},
    {CMD_STATUS_REPORT, "STATUS_REPORT"},
    {CMD_TIME_SYNC, "TIME_SYNC"},
    {CMD_REBOOT, "REBOOT"},
    {CMD_REBOOT_ACK, "REBOOT_ACK"},
    {CMD_CLEAR_LOGS, "CLEAR_LOGS"},
    {CMD_SD_QUERY, "SD_QUERY"},
    {CMD_SD_QUERY_RESPONSE, "SD_QUERY_RESPONSE"},
    {CMD_SD_QUERY_PART, "SD_QUERY_PART"},
    {CMD_SD_QUERY_PART_ACK, "SD_QUERY_PART_ACK"},
    {CMD_SD_SAVE, "SD_SAVE"},
    {CMD_SD_SAVE_RESPONSE, "SD_SAVE_RESPONSE"},
    {CMD_SD_QUERY_VERSION, "SD_QUERY_VERSION"},
    {CMD_SD_VERSION_RESPONSE, "SD_VERSION_RESPONSE"},
    {CMD_UPLOAD_FP_TEMPLATE, "UPLOAD_FP_TEMPLATE"},
    {CMD_FP_TEMPLATE_UPLOAD_RESPONSE, "FP_TEMPLATE_UPLOAD_RESPONSE"},
    {CMD_DOWNLOAD_FP_TEMPLATE, "DOWNLOAD_FP_TEMPLATE"},
    {CMD_FP_TEMPLATE_DOWNLOAD_RESPONSE, "FP_TEMPLATE_DOWNLOAD_RESPONSE"},
    {CMD_DELETE_FP_TEMPLATE, "DELETE_FP_TEMPLATE"},
    {CMD_FP_TEMPLATE_DELETE_RESPONSE, "FP_TEMPLATE_DELETE_RESPONSE"},
    {CMD_LOG_REPORT, "LOG_REPORT"},
    {CMD_LOG_REPORT_ACK, "LOG_REPORT_ACK"},
    {CMD_PERM_LOST, "PERM_LOST"},
    {CMD_PERM_LOST_ACK, "PERM_LOST_ACK"},
};

uint16_t appCmdIdFromName(const char* cmd) {
    if (cmd == nullptr) return 0;
    for (size_t i = 0; i < sizeof(kCmdTable) / sizeof(kCmdTable[0]); i++) {
        if (strcmp(cmd, kCmdTable[i].name) == 0) return kCmdTable[i].id;
    }
    return 0;
}

const char* appCmdName(uint16_t id) {
    for (size_t i = 0; i < sizeof(kCmdTable) / sizeof(kCmdTable[0]); i++) {
        if (kCmdTable[i].id == id) return kCmdTable[i].name;
    }
    return nullptr;
}
