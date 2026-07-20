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
String SdStorage::lastError = "";

bool SdStorage::init() {
    if (mounted) return true;

    Debug::println(F("[SD] Init SD card (SD_MMC 1-bit mode)..."));

    // V2.7: 详细诊断 SD 失败原因（推送至 host 便于排查）
    auto reportFail = [](const char *stage, const char *detail) {
        String msg = String("[SD FAIL] ") + stage + ": " + detail;
        Debug::println(msg.c_str());
        // 同步推送到 host（USB-CDC 串口）
        Serial.printf("\r\n[ROOT_SD_FAIL] stage=%s detail=%s\r\n", stage, detail);
        Serial.flush();
        // 保存错误状态供显示
        SdStorage::lastError = msg;
    };

    // Configure SD_MMC pins for 1-bit mode (CLK, CMD, D0)
    if (!SD_MMC.setPins(SD_SCLK_PIN, SD_MOSI_PIN, SD_MISO_PIN)) {
        reportFail("setPins",
            "GPIO16/17/18 may be in use or invalid for SD_MMC 1-bit");
        mounted = false;
        return false;
    }
    Debug::printf("[SD] setPins ok: CLK=GPIO%d CMD=GPIO%d D0=GPIO%d\n",
                  SD_SCLK_PIN, SD_MOSI_PIN, SD_MISO_PIN);

    // V2.7: 显式指定时钟频率以兼容更多 SD 卡（与参考示例 ImageDemo 一致）
    // 一些低速/老旧/劣质 SD 卡在默认 20MHz 下时序违例，需要降至 4MHz 甚至 1MHz。
    // 首次失败时自动重试更慢的频率（覆盖率高、绝不格式化用户数据）。
    const uint32_t kSdClockCandidates[] = { 20000000, 4000000, 1000000 };
    bool beginOk = false;
    uint32_t usedClock = 0;
    const char *failStage = "mount";

    for (uint32_t clockHz : kSdClockCandidates) {
        Debug::printf("[SD] trying mount 1bit=true clock=%luHz\n", (unsigned long)clockHz);
        // mode1bit=true, format_if_mount_failed=false（绝不格式化，保护用户数据）
        if (SD_MMC.begin(SD_MOUNT_POINT, true, false, clockHz)) {
            beginOk = true;
            usedClock = clockHz;
            break;
        }
        Debug::printf("[SD] mount failed at %luHz, retrying slower...\n", (unsigned long)clockHz);
    }

    if (!beginOk) {
        reportFail(failStage,
            "no card / wiring / wrong pin assignment / card incompatible even at 1MHz");
        mounted = false;
        return false;
    }
    Debug::printf("[SD] begin(1bit=true) returned ok at %luHz\n", (unsigned long)usedClock);

    // Detect card type
    uint8_t cardType = SD_MMC.cardType();
    if (cardType == CARD_NONE) {
        reportFail("cardType", "cardType == CARD_NONE after begin()");
        mounted = false;
        return false;
    }

    Debug::printf("[SD] Mount success type=%s capacity=%lluMB used=%lluMB\n",
                  cardType == CARD_MMC ? "MMC" : (cardType == CARD_SD ? "SD" : "SDHC"),
                  SD_MMC.cardSize() / (1024 * 1024),
                  SD_MMC.usedBytes() / (1024 * 1024));

    // Mark the volume ready before reading/writing the initial tables.
    mounted = true;
    lastError = "";

    // Create directory structure
    ensureDir(SD_DATA_DIR);
    ensureDir(SD_FP_DIR);

    // V2.7: SD 上"已知用户表"中是否有 admin 账号的检查 + 强补全
    // 原因：旧版路径重复 bug (/sdcard/sdcard/data/...) 导致首次 mount 时
    // users.json 写入失败，SD 卡上"看似空表"。新固件修复路径后要主动
    // 补全 admin 账号，避免用户无法登录。
    const char kAdminSalt[] = "000102030405060708090a0b0c0d0e0f";
    const char kAdminHash[] = "eb427d2e310382de4e4bf02b93005681040294011a20356bb0348fc49ad70a8f";
    auto ensureAdminExists = [&]() -> bool {
        String usersJson;
        if (!readTable("users", usersJson) || usersJson.length() == 0) {
            // 文件不存在或读失败：直接创建含 admin 的新表
            String initJson = String("[{\"user_id\":\"admin\",\"name\":\"系统管理员\",\"role\":\"admin\","
                "\"fingerprint_id\":null,\"password_salt\":\"") + kAdminSalt +
                "\",\"password_hash\":\"" + kAdminHash +
                "\",\"enabled\":true}]";
            String path = tablePath("users");
            if (atomicWrite(path, (const uint8_t *)initJson.c_str(), initJson.length())) {
                Debug::println(F("[SD] admin account bootstrapped (no users.json)"));
                return true;
            }
            return false;
        }
        // 文件存在但可能不含 admin：检查并补全
        if (usersJson.indexOf("\"user_id\":\"admin\"") < 0) {
            // 简单字符串判断（避免引入 ArduinoJson 解析开销）。如果没有 admin
            // 就在数组开头插入。注意：原子写 = 写 .tmp + rename，不会损坏。
            String insert = String("{\"user_id\":\"admin\",\"name\":\"系统管理员\",\"role\":\"admin\","
                "\"fingerprint_id\":null,\"password_salt\":\"") + kAdminSalt +
                "\",\"password_hash\":\"" + kAdminHash +
                "\",\"enabled\":true},";
            // usersJson 形如 [{...}, {...}]
            int pos = usersJson.indexOf('[');
            if (pos >= 0) {
                String newJson = usersJson.substring(0, pos + 1) + insert + usersJson.substring(pos + 1);
                String path = tablePath("users");
                if (atomicWrite(path, (const uint8_t *)newJson.c_str(), newJson.length())) {
                    Debug::println(F("[SD] admin account injected into existing users.json"));
                    return true;
                }
            }
            return false;
        }
        Debug::println(F("[SD] admin account already present"));
        return true;
    };

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

    // V2.7: 补全 admin 账号（兼容旧 SD 卡或新分区）
    if (!ensureAdminExists()) {
        Debug::println(F("[SD] WARNING: failed to bootstrap admin account"));
        // 不视为致命错误：上层仍可走内置 admin 兜底
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
    String bakPath = path + ".bak";

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

    // 2. Keep previous good file as .bak
    if (SD_MMC.exists(path)) {
        if (SD_MMC.exists(bakPath)) {
            SD_MMC.remove(bakPath);
        }
        if (!SD_MMC.rename(path, bakPath)) {
            Debug::printf("[SD] Backup rename failed: %s -> %s\n", path.c_str(), bakPath.c_str());
            SD_MMC.remove(tmpPath);
            return false;
        }
    }

    // 3. Promote temp to target
    if (!SD_MMC.rename(tmpPath, path)) {
        Debug::printf("[SD] Rename failed: %s -> %s\n", tmpPath.c_str(), path.c_str());
        // Attempt rollback from .bak
        if (SD_MMC.exists(bakPath)) {
            SD_MMC.rename(bakPath, path);
        }
        return false;
    }

    return true;
}

// ====== JSON table read/write ======

bool SdStorage::readTable(const String &tableName, String &outJson) {
    if (!mounted) return false;

    String path = tablePath(tableName);
    String bakPath = path + ".bak";
    File f = SD_MMC.open(path, FILE_READ);
    if (!f) {
        // Fall back to last good backup after a crash mid-write.
        f = SD_MMC.open(bakPath, FILE_READ);
        if (!f) return false;
        Debug::printf("[SD] reading backup table: %s\n", tableName.c_str());
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
