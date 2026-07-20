/**
 * reliable_tx.h - Application-layer retransmit for needs_ack control messages
 * Skeleton for Phase 1. Max pending payload 256B; larger sends fire-and-forget.
 */
#pragma once

#include <stdint.h>

namespace ReliableTx {
    typedef bool (*SendFn)(const uint8_t* data, uint16_t len, const uint8_t* macOrNull);

    void init(SendFn sendFn);

    // Queue a copy of app message bytes for retransmit.
    // mac6 null = uplink/upstream (no dest MAC).
    // If len > 256, send once without store; returns immediate send result.
    bool sendReliable(const uint8_t* appMsg, uint16_t len, const uint8_t* mac6, uint16_t msgId);

    void onAck(uint16_t msgId);
    void update(); // call from main loop — retransmit / timeout
    int pendingCount();
}
