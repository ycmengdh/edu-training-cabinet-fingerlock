/**
 * Optional HMAC-SHA256 verification for sensitive management commands.
 * Canonical: cmd|device_id|msg_id|ts|nonce|compact_data
 */
#ifndef MESSAGE_HMAC_H
#define MESSAGE_HMAC_H

#include <Arduino.h>
#include <ArduinoJson.h>

namespace MessageHmac {
    // Returns true when the command requires a signature if HMAC is enabled.
    bool isSensitive(const char *cmd);

    // Verify envelope fields on a parsed JSON document.
    // When cfg.hmac_enabled is false, always returns true.
    bool verify(const JsonDocument &doc, bool hmacEnabled, const String &hmacKey);
}

#endif // MESSAGE_HMAC_H
