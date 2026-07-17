/**
 * sd_storage.cpp - SD card centralized storage implementation (Root Node only)
 * Based on Arduino-ESP32 built-in SD_MMC + FS library, 1-bit mode.
 */
#ifdef ENABLE_SD_CARD

#include "sd_storage.h"
#include "debug.h"
#include <SD_MMC.h>
#include <FS.h>
#include <ArduinoJson.h>

bool SdStorage::mounted = false;

bool SdStorage::init() {
    if (mounted) return true;

    Debug::println(F("[SD] Init SD card (SD_MMC 1-bit mode)..."));

    // Configure SD_MMC pins for 1-bit mode (CLK, CMD, D0)
    if (!SD_MMC.setPins(SD_SCLK_PIN, SD_MOSI_PIN, SD_MISO_PIN)) {
        Debug::println(F("[SD] SD_MMC setPins failed! Check pin configuration"));
        mounted = false;
        return false;
    }

    // Mount SD card in 1-bit mode, format if mount failed
    if (!SD_MMC.begin(SD_MOUNT_POINT, true, true)) {
        Debug::println(F("[SD] SD card mount failed! Check: 1. card inserted 2. wiring 3. pins"));
        mounted = false;
        return false;
    }

    // Detect card type
    uint8_t cardType = SD_MMC.cardType();
    if (cardType == CARD_NONE) {
        Debug::println(F("[SD] SD card not detected"));
        mounted = false;
        return false;
    }

    Debug::printf("[SD] Mount success type=%s capacity=%lluMB used=%lluMB\n",
                  cardType == CARD_MMC ? "MMC" : (cardType == CARD_SD ? "SD" : "SDHC"),
                  SD_MMC.cardSize() / (1024 * 1024),
                  SD_MMC.usedBytes() / (1024 * 1024));

    // Mark the volume ready before reading/writing the initial tables.
    mounted = true;

    // Create directory structure
    ensureDir(SD_DATA_DIR);
    ensureDir(SD_FP_DIR);

    // These files are the root node database. The PC only accesses them via
    // SD_QUERY/SD_SAVE and must not create a second business database.
    struct InitialTable { const char *name; const char *json; };
    const InitialTable initialTables[] = {
        {"version", "{\"global_version\":0,\"users_version\":0,\"classes_version\":0,\"permissions_version\":0,\"devices_version\":0,\"fp_version\":0,\"logs_version\":0,\"last_update_time\":\"\",\"last_update_source\":\"init\"}"},
        {"users", "[{\"user_id\":\"admin\",\"name\":\"系统管理员\",\"role\":\"admin\",\"fingerprint_id\":null,\"password_salt\":\"000102030405060708090a0b0c0d0e0f\",\"password_hash\":\"eb427d2e310382de4e4bf02b93005681040294011a20356bb0348fc49ad70a8f\",\"enabled\":true}]"},
        {"classes", "[]"},
        {"permissions", "[]"},
        {"role_permissions", "[{\"role\":\"admin\",\"lock_0\":true,\"lock_1\":true,\"lock_2\":true,\"lock_3\":true},{\"role\":\"teacher\",\"lock_0\":false,\"lock_1\":true,\"lock_2\":true,\"lock_3\":true},{\"role\":\"student\",\"lock_0\":false,\"lock_1\":false,\"lock_2\":false,\"lock_3\":false}]"},
        {"devices", "[]"},
        {"logs", "[]"}
    };
    for (const InitialTable &table : initialTables) {
        String path = tablePath(table.name);
        if (!SD_MMC.exists(path)) {
            if (!atomicWrite(path, (const uint8_t *)table.json, strlen(table.json))) {
                Debug::printf("[SD] initialize table failed: %s\n", table.name);
                mounted = false;
                return false;
            }
            Debug::printf("[SD] initialized table: %s\n", table.name);
        }
    }

    return true;
}

bool SdStorage::isReady() {
    return mounted;
}

// ====== Directory and path ======

String SdStorage::tablePath(const String &tableName) {
    return String(SD_DATA_DIR) + "/" + tableName + ".json";
}

bool SdStorage::ensureDir(const String &path) {
    if (!SD_MMC.exists(path)) {
        if (SD_MMC.mkdir(path)) {
            Debug::printf("[SD] Create dir: %s\n", path.c_str());
            return true;
        } else {
            Debug::printf("[SD] Create dir failed: %s\n", path.c_str());
            return false;
        }
    }
    return true;
}

// ====== Atomic write ======

bool SdStorage::atomicWrite(const String &path, const uint8_t *data, size_t len) {
    String tmpPath = path + ".tmp";

    // 1. Write temp file
    File f = SD_MMC.open(tmpPath, FILE_WRITE);
    if (!f) {
        Debug::printf("[SD] Open temp file failed: %s\n", tmpPath.c_str());
        return false;
    }
    size_t written = f.write(data, len);
    f.flush();
    f.close();

    if (written != len) {
        Debug::printf("[SD] Incomplete write: %u/%u\n", (unsigned)written, (unsigned)len);
        SD_MMC.remove(tmpPath);
        return false;
    }

    // 2. Remove original file if exists
    if (SD_MMC.exists(path)) {
        SD_MMC.remove(path);
    }

    // 3. Rename temp file to target
    if (!SD_MMC.rename(tmpPath, path)) {
        Debug::printf("[SD] Rename failed: %s -> %s\n", tmpPath.c_str(), path.c_str());
        return false;
    }

    return true;
}

// ====== JSON table read/write ======

bool SdStorage::readTable(const String &tableName, String &outJson) {
    if (!mounted) return false;

    String path = tablePath(tableName);
    File f = SD_MMC.open(path, FILE_READ);
    if (!f) {
        return false;
    }

    outJson = "";
    outJson.reserve(f.size() + 16);
    while (f.available()) {
        String chunk = f.readString();
        outJson += chunk;
    }
    f.close();
    return true;
}

bool SdStorage::writeTable(const String &tableName, const String &json) {
    if (!mounted) return false;

    ensureDir(SD_DATA_DIR);
    String path = tablePath(tableName);

    bool ok = atomicWrite(path, (const uint8_t *)json.c_str(), json.length());
    if (ok) {
        if (tableName != "version") {
            incrementVersion(tableName);
        }
        Debug::printf("[SD] Write %s success (%u bytes)\n", tableName.c_str(), (unsigned)json.length());
    }
    return ok;
}

bool SdStorage::appendLogs(const String &logsJson) {
    if (!mounted || logsJson.length() == 0) return false;

    DynamicJsonDocument incomingDoc(16384);
    if (deserializeJson(incomingDoc, logsJson)) return false;
    JsonArray incoming = incomingDoc.as<JsonArray>();
    if (incoming.isNull()) return false;

    String existingJson;
    DynamicJsonDocument document(131072);
    JsonArray stored = document.to<JsonArray>();
    if (readTable("logs", existingJson) && existingJson.length() > 0) {
        document.clear();
        if (deserializeJson(document, existingJson)) {
            document.clear();
            stored = document.to<JsonArray>();
        } else {
            stored = document.as<JsonArray>();
        }
    }

    bool appended = false;
    for (JsonVariant item : incoming) {
        const char *deviceId = item["device_id"] | "";
        uint32_t logSeq = item["log_seq"] | 0;
        bool duplicate = false;

        // The cabinet retries a batch when the ACK is lost. Treat the
        // cabinet-local sequence as an idempotency key after scoping it by
        // device, so a retry cannot create duplicate root records.
        if (deviceId[0] != '\0' && logSeq > 0) {
            for (JsonObject existing : stored) {
                const char *existingDevice = existing["device_id"] | "";
                uint32_t existingSeq = existing["log_seq"] | 0;
                if (existingSeq == logSeq && strcmp(existingDevice, deviceId) == 0) {
                    duplicate = true;
                    break;
                }
            }
        }

        if (!duplicate) {
            stored.add(item);
            appended = true;
        }
    }

    if (!appended) return true;
    while (stored.size() > SD_LOG_MAX_ENTRIES) stored.remove(0);

    String output;
    serializeJson(stored, output);
    if (!atomicWrite(tablePath("logs"), (const uint8_t *)output.c_str(), output.length())) {
        return false;
    }
    incrementVersion("logs");
    return true;
}

// ====== Fingerprint template read/write ======

String SdStorage::getTemplateFileName(const String &userId, int index) {
    String safeId = "";
    safeId.reserve(userId.length());
    for (size_t i = 0; i < userId.length(); i++) {
        char c = userId[i];
        if (isalnum((unsigned char)c)) {
            safeId += c;
        } else {
            safeId += '_';
        }
    }
    String name = "FP_";
    name += safeId;
    if (index > 1) {
        name += "_";
        name += String(index);
    }
    name += ".bin";
    return name;
}

bool SdStorage::writeTemplate(const String &userId, int index,
                              const uint8_t *data, size_t len) {
    if (!mounted) return false;

    ensureDir(SD_FP_DIR);
    String fileName = getTemplateFileName(userId, index);
    String path = String(SD_FP_DIR) + "/" + fileName;

    bool ok = atomicWrite(path, data, len);
    if (ok) {
        Debug::printf("[SD] Fingerprint template written: %s (%u bytes)\n", fileName.c_str(), (unsigned)len);
    }
    return ok;
}

bool SdStorage::readTemplate(const String &userId, int index,
                             uint8_t *outBuf, size_t bufSize, size_t &outLen) {
    if (!mounted) return false;

    String fileName = getTemplateFileName(userId, index);
    String path = String(SD_FP_DIR) + "/" + fileName;

    File f = SD_MMC.open(path, FILE_READ);
    if (!f) {
        Debug::printf("[SD] Fingerprint template not found: %s\n", fileName.c_str());
        return false;
    }

    size_t fileLen = f.size();
    if (fileLen > bufSize) {
        Debug::printf("[SD] Template too large: %u > buffer %u\n", (unsigned)fileLen, (unsigned)bufSize);
        f.close();
        return false;
    }

    outLen = f.read(outBuf, fileLen);
    f.close();
    return outLen == fileLen;
}

bool SdStorage::deleteTemplate(const String &userId) {
    if (!mounted) return false;

    int deleted = 0;
    for (int index = 1; index <= FP_MAX_TEMPLATES_PER_USER; index++) {
        String fileName = getTemplateFileName(userId, index);
        String path = String(SD_FP_DIR) + "/" + fileName;
        if (SD_MMC.exists(path) && SD_MMC.remove(path)) {
            deleted++;
            Debug::printf("[SD] Delete template: %s\n", fileName.c_str());
        }
    }

    Debug::printf("[SD] User %s deleted %d templates\n", userId.c_str(), deleted);
    return deleted > 0;
}

// ====== Version metadata ======

bool SdStorage::readVersion(uint32_t &globalVer, uint32_t &usersVer,
                            uint32_t &classesVer, uint32_t &permsVer,
                            uint32_t &devicesVer, uint32_t &fpVer,
                            uint32_t &logsVer) {
    String json;
    if (!readTable("version", json)) {
        globalVer = usersVer = classesVer = permsVer = devicesVer = fpVer = logsVer = 0;
        return false;
    }

    DynamicJsonDocument doc(512);
    DeserializationError err = deserializeJson(doc, json);
    if (err) {
        globalVer = usersVer = classesVer = permsVer = devicesVer = fpVer = logsVer = 0;
        return false;
    }

    globalVer  = doc["global_version"] | 0;
    usersVer   = doc["users_version"] | 0;
    classesVer = doc["classes_version"] | 0;
    permsVer   = doc["permissions_version"] | 0;
    devicesVer = doc["devices_version"] | 0;
    fpVer      = doc["fp_version"] | 0;
    logsVer    = doc["logs_version"] | 0;
    return true;
}

bool SdStorage::incrementVersion(const String &tableName) {
    uint32_t g, u, c, p, d, fp, logs;
    readVersion(g, u, c, p, d, fp, logs);

    if (tableName == "users") u++;
    else if (tableName == "classes") c++;
    else if (tableName == "permissions") p++;
    else if (tableName == "role_permissions") p++;
    else if (tableName == "devices") d++;
    else if (tableName == "fingerprints" || tableName == "fp") fp++;
    else if (tableName == "logs") logs++;
    else return false;

    g++;

    DynamicJsonDocument doc(512);
    doc["global_version"] = g;
    doc["users_version"] = u;
    doc["classes_version"] = c;
    doc["permissions_version"] = p;
    doc["devices_version"] = d;
    doc["fp_version"] = fp;
    doc["logs_version"] = logs;

    time_t now = time(nullptr);
    String ts = (now > 1700000000) ? String((long)now) : String("");
    doc["last_update_time"] = ts;
    doc["last_update_source"] = "root_sd";

    String json;
    serializeJson(doc, json);

    String path = tablePath("version");
    return atomicWrite(path, (const uint8_t *)json.c_str(), json.length());
}

uint32_t SdStorage::getGlobalVersion() {
    uint32_t g, u, c, p, d, fp, logs;
    readVersion(g, u, c, p, d, fp, logs);
    return g;
}

// ====== SD card capacity info ======

uint64_t SdStorage::getTotalBytes() {
    return mounted ? SD_MMC.cardSize() : 0;
}

uint64_t SdStorage::getUsedBytes() {
    return mounted ? SD_MMC.usedBytes() : 0;
}

#endif // ENABLE_SD_CARD
