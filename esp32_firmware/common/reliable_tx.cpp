/**
 * reliable_tx.cpp - Pending-slot retransmit table (control messages only)
 */
#include "reliable_tx.h"
#include "config_common.h"

#include <Arduino.h>
#include <string.h>

namespace {

constexpr uint16_t kMaxStoredPayload = 256;

struct Slot {
    bool active;
    uint16_t msgId;
    uint16_t len;
    uint8_t retries;
    unsigned long nextSendMs;
    uint8_t mac[6];
    bool hasMac;
    uint8_t data[kMaxStoredPayload];
};

Slot s_slots[RELIABLE_TX_SLOTS];
ReliableTx::SendFn s_sendFn = nullptr;
int s_pending = 0;

int findFreeSlot() {
    for (int i = 0; i < RELIABLE_TX_SLOTS; i++) {
        if (!s_slots[i].active) return i;
    }
    return -1;
}

int findByMsgId(uint16_t msgId) {
    for (int i = 0; i < RELIABLE_TX_SLOTS; i++) {
        if (s_slots[i].active && s_slots[i].msgId == msgId) return i;
    }
    return -1;
}

bool doSend(const Slot& s) {
    if (s_sendFn == nullptr) return false;
    return s_sendFn(s.data, s.len, s.hasMac ? s.mac : nullptr);
}

} // namespace

namespace ReliableTx {

void init(SendFn sendFn) {
    s_sendFn = sendFn;
    memset(s_slots, 0, sizeof(s_slots));
    s_pending = 0;
}

bool sendReliable(const uint8_t* appMsg, uint16_t len, const uint8_t* mac6, uint16_t msgId) {
    if (appMsg == nullptr || len == 0 || s_sendFn == nullptr) return false;

    // Oversized: fire once, no retransmit table.
    if (len > kMaxStoredPayload) {
        return s_sendFn(appMsg, len, mac6);
    }

    // Replace existing same msgId if any.
    int idx = findByMsgId(msgId);
    if (idx < 0) idx = findFreeSlot();
    if (idx < 0) {
        // Table full — still attempt immediate send without reliability.
        return s_sendFn(appMsg, len, mac6);
    }

    Slot& s = s_slots[idx];
    bool wasActive = s.active;
    s.active = true;
    s.msgId = msgId;
    s.len = len;
    s.retries = 0;
    s.nextSendMs = millis() + RELIABLE_TX_TIMEOUT_MS;
    s.hasMac = (mac6 != nullptr);
    if (s.hasMac) memcpy(s.mac, mac6, 6);
    memcpy(s.data, appMsg, len);
    if (!wasActive) s_pending++;

    return s_sendFn(appMsg, len, mac6);
}

void onAck(uint16_t msgId) {
    int idx = findByMsgId(msgId);
    if (idx < 0) return;
    s_slots[idx].active = false;
    s_slots[idx].len = 0;
    if (s_pending > 0) s_pending--;
}

void update() {
    if (s_sendFn == nullptr || s_pending == 0) return;
    unsigned long now = millis();
    for (int i = 0; i < RELIABLE_TX_SLOTS; i++) {
        Slot& s = s_slots[i];
        if (!s.active) continue;
        if ((long)(now - s.nextSendMs) < 0) continue;

        if (s.retries >= RELIABLE_TX_MAX_RETRY) {
            s.active = false;
            s.len = 0;
            if (s_pending > 0) s_pending--;
            continue;
        }

        doSend(s);
        s.retries++;
        s.nextSendMs = now + RELIABLE_TX_TIMEOUT_MS;
    }
}

int pendingCount() {
    return s_pending;
}

} // namespace ReliableTx
