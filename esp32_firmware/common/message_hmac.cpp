#include "message_hmac.h"
#include "debug.h"
#include <mbedtls/md.h>
#include <time.h>
#include <stdlib.h>
#include <string.h>

namespace MessageHmac {

static bool equalsIgnoreCase(const char *a, const char *b) {
    if (!a || !b) return false;
    while (*a && *b) {
        char ca = (*a >= 'a' && *a <= 'z') ? (*a - 32) : *a;
        char cb = (*b >= 'a' && *b <= 'z') ? (*b - 32) : *b;
        if (ca != cb) return false;
        a++;
        b++;
    }
    return *a == 0 && *b == 0;
}

bool isSensitive(const char *cmd) {
    if (!cmd || !*cmd) return false;
    return equalsIgnoreCase(cmd, "CONTROL_LOCK") ||
           equalsIgnoreCase(cmd, "ADD_FINGERPRINT") ||
           equalsIgnoreCase(cmd, "RESTORE_FINGERPRINT") ||
           equalsIgnoreCase(cmd, "DELETE_FINGERPRINT") ||
           equalsIgnoreCase(cmd, "SD_SAVE") ||
           equalsIgnoreCase(cmd, "WRITE_CONFIG") ||
           equalsIgnoreCase(cmd, "BEGIN_PERMISSION_SYNC") ||
           equalsIgnoreCase(cmd, "SYNC_PERMISSION") ||
           equalsIgnoreCase(cmd, "COMMIT_PERMISSION_SYNC") ||
           equalsIgnoreCase(cmd, "CLEAR_PERMISSIONS") ||
           equalsIgnoreCase(cmd, "SYNC_PERMISSIONS");
}

static String compactData(const JsonVariantConst &data) {
    if (data.isNull()) return "{}";
    String out;
    serializeJson(data, out);
    if (out.length() == 0) return "{}";
    return out;
}

static String toLowerHex(const unsigned char *buf, size_t len) {
    static const char *hex = "0123456789abcdef";
    String out;
    out.reserve(len * 2);
    for (size_t i = 0; i < len; i++) {
        out += hex[(buf[i] >> 4) & 0x0F];
        out += hex[buf[i] & 0x0F];
    }
    return out;
}

static bool constantTimeEqual(const String &a, const String &b) {
    if (a.length() != b.length()) return false;
    uint8_t diff = 0;
    for (unsigned i = 0; i < a.length(); i++) {
        diff |= (uint8_t)(a[i] ^ b[i]);
    }
    return diff == 0;
}

bool verify(const JsonDocument &doc, bool hmacEnabled, const String &hmacKey) {
    if (!hmacEnabled) return true;

    const char *cmd = doc["cmd"] | "";
    if (!isSensitive(cmd)) return true;
    if (hmacKey.length() == 0) {
        Debug::println(F("[HMAC] enabled but key empty"));
        return false;
    }

    long ts = doc["hmac_ts"] | 0;
    const char *nonce = doc["hmac_nonce"] | "";
    const char *sig = doc["hmac_sig"] | "";
    if (ts <= 0 || strlen(nonce) == 0 || strlen(sig) == 0) {
        Debug::println(F("[HMAC] missing signature fields"));
        return false;
    }

    time_t now = time(nullptr);
    if (now > 100000 && llabs((long long)now - (long long)ts) > 120) {
        Debug::printf("[HMAC] timestamp skew: now=%ld ts=%ld\n", (long)now, ts);
        return false;
    }

    const char *deviceId = doc["device_id"] | "";
    const char *msgId = doc["msg_id"] | "";
    String dataCompact = compactData(doc["data"]);
    String canonical = String(cmd) + "|" + deviceId + "|" + msgId + "|" +
                       String(ts) + "|" + nonce + "|" + dataCompact;

    unsigned char hash[32];
    const mbedtls_md_info_t *info = mbedtls_md_info_from_type(MBEDTLS_MD_SHA256);
    if (!info) return false;
    int rc = mbedtls_md_hmac(info,
                             (const unsigned char *)hmacKey.c_str(), hmacKey.length(),
                             (const unsigned char *)canonical.c_str(), canonical.length(),
                             hash);
    if (rc != 0) {
        Debug::printf("[HMAC] mbedtls_md_hmac failed: %d\n", rc);
        return false;
    }

    String expected = toLowerHex(hash, sizeof(hash));
    String actual = String(sig);
    actual.toLowerCase();
    if (!constantTimeEqual(expected, actual)) {
        Debug::println(F("[HMAC] signature mismatch"));
        return false;
    }
    return true;
}

} // namespace MessageHmac
