/**
 * app_protocol.h - Binary application envelope encode/decode (Phase 1)
 * Little-endian wire format. Not yet wired into message_handler.
 */
#pragma once

#include <stddef.h>
#include <stdint.h>
#include "config_common.h"
#include "cmd_ids.h"

struct AppMessageView {
    uint8_t flags;
    uint16_t cmd_id;
    uint16_t msg_id;
    uint16_t corr_id;
    uint32_t timestamp_unix;
    const char* device_id;   // points into decode buffer (not NUL-terminated)
    uint8_t device_id_len;
    const char* source_id;
    uint8_t source_id_len;
    const uint8_t* payload;
    uint16_t payload_len;
    const uint8_t* hmac;     // valid when APP_FLAG_HAS_HMAC; points into buffer
    uint8_t hmac_len;        // typically 32 for SHA-256
};

// Little-endian helpers
void wrU16(uint8_t* p, uint16_t v);
void wrU32(uint8_t* p, uint32_t v);
uint16_t rdU16(const uint8_t* p);
uint32_t rdU32(const uint8_t* p);

// Encode into out buffer; returns bytes written or -1 on error.
// timestampUnix=0 uses current epoch seconds when available, else 0.
int appEncode(uint8_t* out, int outSize,
              uint16_t cmdId, uint16_t msgId, uint16_t corrId, uint8_t flags,
              const char* deviceId, const char* sourceId,
              const uint8_t* payload, uint16_t payloadLen,
              uint32_t timestampUnix = 0);

// Decode; returns true if valid. String fields point into `data` (lens provided).
bool appDecode(const uint8_t* data, int len, AppMessageView& view);

// Payload packers (return length written, or -1)
int packHeartbeat(uint8_t* out, int outSize, uint32_t freeHeap, uint32_t freePsram,
                  uint16_t minFreeHeap, uint8_t meshLayer, uint8_t flags,
                  uint16_t sendFail, uint16_t queueFull, uint16_t recoveries);
int packAck(uint8_t* out, int outSize, uint16_t refMsgId, uint16_t resultCode, const char* tag);
int packError(uint8_t* out, int outSize, uint16_t refMsgId, uint16_t errorCode, const char* msg);
int packControlLock(uint8_t* out, int outSize, uint8_t lockId, uint8_t action);

// Unpack counterparts
bool unpackHeartbeat(const uint8_t* p, uint16_t len,
                     uint32_t& freeHeap, uint32_t& freePsram,
                     uint16_t& minFreeHeap, uint8_t& meshLayer, uint8_t& flags,
                     uint16_t& sendFail, uint16_t& queueFull, uint16_t& recoveries);
bool unpackAck(const uint8_t* p, uint16_t len,
               uint16_t& refMsgId, uint16_t& resultCode, char* tag, int tagMax);
bool unpackError(const uint8_t* p, uint16_t len,
                 uint16_t& refMsgId, uint16_t& errorCode, char* msg, int msgMax);
bool unpackControlLock(const uint8_t* p, uint16_t len, uint8_t& lockId, uint8_t& action);

// Map legacy string cmd to id (transitional). Returns 0 if unknown.
uint16_t appCmdIdFromName(const char* cmd);
const char* appCmdName(uint16_t id);

// Rolling msg_id generator (skips 0)
uint16_t appNextMsgId();
