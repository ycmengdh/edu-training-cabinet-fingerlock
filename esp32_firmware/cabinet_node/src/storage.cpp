/**
 * storage.cpp - Flash 存储管理实现（V2.0 分区方案）
 * 权限数据：NVS Blob，紧凑二进制格式，A/B双区 + 魔数 + CRC32
 * 离线日志：Flash 环形缓冲（ESP.flashWrite/Read/Erase），写指针+起始指针
 */
#include "storage.h"
#include "debug.h"
#include <sys/time.h>   // settimeofday()
#include <time.h>       // time()

// 静态成员初始化
Preferences Storage::prefs;
Preferences Storage::logPrefs;
bool Storage::initialized = false;

UserPermission *Storage::permCache = nullptr;
int Storage::permCacheCount = 0;
uint32_t Storage::permCacheVersion = 0;
bool Storage::permLost = false;

int Storage::logWritePtr = 0;
int Storage::logStartPtr = 0;
int Storage::logCount = 0;
uint32_t Storage::logSeqCounter = 0;
bool Storage::logPtrLoaded = false;

// 日志扇区擦除状态缓存（避免重复擦除）
static bool logSectorErased[LOG_SECTOR_COUNT] = {false};

// Store the four ring-buffer pointers in one NVS blob/commit.  The previous
// implementation issued four independent NVS commits for every unlock log,
// unnecessarily holding the main loop and increasing flash wear.
static const uint32_t LOG_POINTER_MAGIC = 0x4C505432; // "LPT2"
struct LogPointerState {
    uint32_t magic;
    int32_t writePtr;
    int32_t startPtr;
    int32_t count;
    uint32_t sequence;
};

static_assert(LOG_SECTOR_COUNT * FLASH_SECTOR_SIZE == LOG_STORE_SIZE,
              "logstore geometry must match the partition table");

void Storage::begin() {
    if (!initialized) {
        prefs.begin("esp32_cfg", false);      // 设备配置 + 权限元数据
        initialized = true;

        // 首次启动时所有字符串键都不存在，ESP32 Arduino Preferences::getString()
        // 即使有默认值 fallback 也会通过 log_e() 输出 "nvs_get_str len fail: ... NOT_FOUND"
        // 错误日志。这里通过哨兵键检测首次启动，一次性把所有字段写入默认值，
        // 后续启动 getString 即可命中 NVS，不再报 NOT_FOUND 警告。
        if (!prefs.isKey("cfg_init_done")) {
            DeviceConfig def;
            def.device_id        = DEVICE_ID_DEFAULT;
            def.device_name      = "Cabinet Node";
            def.work_mode        = MODE_MESH;
            def.is_root          = false;
            def.uplink_mode      = UPLINK_USB;
            def.mesh_channel     = MESH_CHANNEL;
            def.mesh_password    = MESH_PASSWORD;
            def.wifi_ssid        = "";
            def.wifi_password    = "";
            def.server_ip        = UPLINK_SERVER_IP_DEFAULT;
            def.server_port      = UPLINK_TCP_PORT;
            def.fingerprint_count = 0;
            def.perm_version     = 0;
            def.hmac_enabled     = false;
            def.hmac_key         = "";
            saveDeviceConfig(def);
            prefs.putBool("cfg_init_done", true);
            Debug::println(F("[STORAGE] first boot: defaults written to NVS"));
        }

        Debug::println(F("[STORAGE] Storage init done"));

        // 加载权限缓存
        loadPermCacheFromFlash();

    }
}

// ====== CRC32 计算 ======
uint32_t Storage::calculateCRC32(const uint8_t *data, size_t len) {
    uint32_t crc = 0xFFFFFFFF;
    for (size_t i = 0; i < len; i++) {
        crc ^= data[i];
        for (int j = 0; j < 8; j++) {
            if (crc & 1) {
                crc = (crc >> 1) ^ 0xEDB88320;
            } else {
                crc >>= 1;
            }
        }
    }
    return crc ^ 0xFFFFFFFF;
}

// ====== 权限数据序列化/反序列化 ======
// V2.7 16B 格式：
//   fp_id(2B) + user_id_num(4B) + lock_perm(1B) + role(1B) + expire_days(4B)
//   + local_fp_id(2B) + flags(1B: bit0=is_backup) + reserved(1B)
// 旧 12B 记录读取时由 deserializePermissionLegacy 迁移。
void Storage::serializePermission(const UserPermission &perm, uint8_t *buf) {
    uint16_t fpId = (uint16_t)perm.fingerprint_id;
    uint32_t uid = perm.user_id_num;
    uint8_t lockPerm = 0;
    for (int i = 0; i < LOCK_COUNT; i++) {
        if (perm.lock_perm[i]) lockPerm |= (1 << i);
    }
    uint8_t role = (uint8_t)perm.role;
    uint16_t localId = (uint16_t)perm.local_fp_id;
    uint8_t flags = perm.is_backup ? 0x01 : 0x00;

    buf[0] = (fpId >> 8) & 0xFF;
    buf[1] = fpId & 0xFF;
    buf[2] = (uid >> 24) & 0xFF;
    buf[3] = (uid >> 16) & 0xFF;
    buf[4] = (uid >> 8) & 0xFF;
    buf[5] = uid & 0xFF;
    buf[6] = lockPerm;
    buf[7] = role;
    uint32_t ed = perm.expire_days;
    buf[8]  = (ed >> 24) & 0xFF;
    buf[9]  = (ed >> 16) & 0xFF;
    buf[10] = (ed >> 8) & 0xFF;
    buf[11] = ed & 0xFF;
    buf[12] = (localId >> 8) & 0xFF;
    buf[13] = localId & 0xFF;
    buf[14] = flags;
    buf[15] = 0;  // reserved
}

void Storage::deserializePermission(const uint8_t *buf, UserPermission &perm) {
    perm.fingerprint_id = (buf[0] << 8) | buf[1];
    perm.user_id_num = ((uint32_t)buf[2] << 24) | ((uint32_t)buf[3] << 16) |
                       ((uint32_t)buf[4] << 8) | buf[5];
    uint8_t lockPerm = buf[6];
    for (int i = 0; i < LOCK_COUNT; i++) {
        perm.lock_perm[i] = (lockPerm & (1 << i)) != 0;
    }
    perm.role = (UserRole)buf[7];
    perm.expire_days = ((uint32_t)buf[8] << 24) | ((uint32_t)buf[9] << 16) |
                       ((uint32_t)buf[10] << 8) | buf[11];
    perm.local_fp_id = ((int)buf[12] << 8) | buf[13];
    perm.is_backup = (buf[14] & 0x01) != 0;
    perm.user_id = userIdNumToString(perm.user_id_num);
    perm.name = "";
    perm.valid = true;
}

// 旧 12B 记录兼容反序列化（V2.6 及更早）
static void deserializePermissionLegacy12(const uint8_t *buf, UserPermission &perm) {
    perm.fingerprint_id = (buf[0] << 8) | buf[1];
    perm.user_id_num = ((uint32_t)buf[2] << 24) | ((uint32_t)buf[3] << 16) |
                       ((uint32_t)buf[4] << 8) | buf[5];
    uint8_t lockPerm = buf[6];
    for (int i = 0; i < LOCK_COUNT; i++) {
        perm.lock_perm[i] = (lockPerm & (1 << i)) != 0;
    }
    perm.role = (UserRole)buf[7];
    perm.expire_days = ((uint32_t)buf[8] << 24) | ((uint32_t)buf[9] << 16) |
                       ((uint32_t)buf[10] << 8) | buf[11];
    // 迁移：主指纹的 local_fp_id 即 fingerprint_id；is_backup=false
    perm.local_fp_id = perm.fingerprint_id;
    perm.is_backup = false;
    perm.user_id = Storage::userIdNumToString(perm.user_id_num);
    perm.name = "";
    perm.valid = true;
}

// user_id 字符串 <-> 数字转换
// "U001" -> 1, "U123" -> 123, 非"U"开头返回哈希
uint32_t Storage::userIdToNum(const String &userId) {
    if (userId.length() == 0) return 0;
    if (userId[0] == 'U' || userId[0] == 'u') {
        String numPart = userId.substring(1);
        return (uint32_t)numPart.toInt();
    }
    // 非标准格式，取字符串哈希
    uint32_t hash = 0;
    for (unsigned int i = 0; i < userId.length(); i++) {
        hash = hash * 31 + (uint8_t)userId[i];
    }
    return hash | 0x80000000; // 标记为哈希值
}

String Storage::userIdNumToString(uint32_t num) {
    if (num == 0) return "";
    if (num & 0x80000000) {
        // 哈希值，无法还原原始字符串
        return "UID_" + String(num & 0x7FFFFFFF);
    }
    return "U" + String(num);
}

// 日期字符串 -> 距2000-01-01的天数
uint32_t Storage::dateToDays(const String &dateStr) {
    if (dateStr.length() == 0) return 0xFFFFFFFF; // 永久
    // 格式 "YYYY-MM-DD HH:MM:SS" 或 "YYYY-MM-DD"
    int year = dateStr.substring(0, 4).toInt();
    int month = dateStr.substring(5, 7).toInt();
    int day = dateStr.substring(8, 10).toInt();
    if (year < 2000) return 0xFFFFFFFF;

    // 简化计算：每年365天 + 闰年修正
    uint32_t days = 0;
    for (int y = 2000; y < year; y++) {
        days += ((y % 4 == 0 && y % 100 != 0) || (y % 400 == 0)) ? 366 : 365;
    }
    int monthDays[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
    if ((year % 4 == 0 && year % 100 != 0) || (year % 400 == 0)) monthDays[1] = 29;
    for (int m = 1; m < month && m <= 12; m++) {
        days += monthDays[m - 1];
    }
    days += (day - 1);
    return days;
}

String Storage::daysToDate(uint32_t days) {
    if (days == 0xFFFFFFFF) return "";
    int year = 2000;
    int remaining = days;
    while (true) {
        int yearDays = ((year % 4 == 0 && year % 100 != 0) || (year % 400 == 0)) ? 366 : 365;
        if (remaining < yearDays) break;
        remaining -= yearDays;
        year++;
    }
    int monthDays[] = {31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31};
    if ((year % 4 == 0 && year % 100 != 0) || (year % 400 == 0)) monthDays[1] = 29;
    int month = 1;
    while (remaining >= monthDays[month - 1]) {
        remaining -= monthDays[month - 1];
        month++;
    }
    int day = remaining + 1;
    char buf[20];
    snprintf(buf, sizeof(buf), "%04d-%02d-%02d 00:00:00", year, month, day);
    return String(buf);
}

// ====== 权限表加载（A/B双区 + CRC校验） ======
void Storage::loadPermCacheFromFlash() {
    permLost = false;
    permCacheCount = 0;
    permCacheVersion = 0;

    int maxBlobSize = PERM_HEADER_SIZE + PERM_RECORD_SIZE * PERM_MAX_USERS;
    uint8_t *buf = (uint8_t*)malloc(maxBlobSize);
    if (buf == nullptr) {
        Debug::println(F("[STORAGE] Permission buffer allocation failed"));
        permLost = true;
        return;
    }

    // 尝试加载 A 区
    int count = 0;
    uint32_t version = 0;
    bool okA = loadPermissionTable(buf, count, version);

    int actualCount = count;
    uint32_t actualVersion = version;

    if (!okA) {
        // A 区失败，尝试 B 区
        // 临时切换 blob key（通过内部标志区分）
        // 这里通过读取另一个 blob 实现
        Debug::println(F("[STORAGE] Partition A permission CRC failed, trying partition B..."));
        // B区使用另一个 NVS key
        size_t bLen = prefs.getBytesLength("perm_b");
        if (bLen > 0 && bLen <= (size_t)maxBlobSize) {
            prefs.getBytes("perm_b", buf, bLen);
            // 解析 B 区
            uint32_t magic = ((uint32_t)buf[12] << 24) | ((uint32_t)buf[13] << 16) |
                             ((uint32_t)buf[14] << 8) | buf[15];
            if (magic == PERM_MAGIC) {
                actualCount = (buf[4] << 8) | buf[5];
                actualVersion = ((uint32_t)buf[0] << 24) | ((uint32_t)buf[1] << 16) |
                                ((uint32_t)buf[2] << 8) | buf[3];
                uint32_t storedCRC = ((uint32_t)buf[8] << 24) | ((uint32_t)buf[9] << 16) |
                                     ((uint32_t)buf[10] << 8) | buf[11];
                int dataLen = PERM_HEADER_SIZE + actualCount * PERM_RECORD_SIZE;
                uint32_t calcCRC = calculateCRC32(buf + 12, 4); // 仅校验count
                // 校验完整数据CRC（header的version+count+reserved 部分 + records）
                uint8_t crcInput[PERM_HEADER_SIZE - 8 + actualCount * PERM_RECORD_SIZE];
                memcpy(crcInput, buf, 8); // version(4)+count(2)+reserved(2)
                memcpy(crcInput + 8, buf + PERM_HEADER_SIZE, actualCount * PERM_RECORD_SIZE);
                calcCRC = calculateCRC32(crcInput, 8 + actualCount * PERM_RECORD_SIZE);

                if (storedCRC == calcCRC && actualCount <= PERM_MAX_USERS) {
                    Debug::printf("[STORAGE] Partition B permission loaded: %d records, version=%u\n",
                                  actualCount, actualVersion);
                } else {
                    Debug::println(F("[STORAGE] Partition B CRC also failed, permissions empty"));
                    actualCount = 0;
                    permLost = true;
                }
            } else {
                Debug::println(F("[STORAGE] Partition B magic mismatch, permissions empty"));
                actualCount = 0;
                permLost = true;
            }
        } else {
            Debug::println(F("[STORAGE] Partition B no data, permissions empty"));
            actualCount = 0;
            permLost = true;
        }
    }

    // 加载到内存缓存
    if (permCache != nullptr) {
        free(permCache);
        permCache = nullptr;
    }
    permCacheCount = actualCount;
    permCacheVersion = actualVersion;

    if (actualCount > 0) {
        permCache = new UserPermission[actualCount];
        // V2.7 兼容：根据 blob 实际长度判断记录是 12B（旧）还是 16B（新）。
        // 旧记录迁移为 local_fp_id=fingerprint_id, is_backup=false，并触发一次回写以升级格式。
        size_t aLen = prefs.getBytesLength("perm_a");
        bool legacyFormat = false;
        if (aLen > 0) {
            size_t expectedV2 = (size_t)PERM_HEADER_SIZE + (size_t)actualCount * PERM_RECORD_SIZE;
            size_t expectedV1 = (size_t)PERM_HEADER_SIZE + (size_t)actualCount * PERM_RECORD_SIZE_V1;
            if (aLen == expectedV1 && aLen != expectedV2) {
                legacyFormat = true;
            }
        }
        // B 区长度同样判断（A 区失败时 buf 来自 B 区）
        if (!legacyFormat) {
            size_t bLen = prefs.getBytesLength("perm_b");
            if (bLen > 0) {
                size_t expectedV2 = (size_t)PERM_HEADER_SIZE + (size_t)actualCount * PERM_RECORD_SIZE;
                size_t expectedV1 = (size_t)PERM_HEADER_SIZE + (size_t)actualCount * PERM_RECORD_SIZE_V1;
                if (bLen == expectedV1 && bLen != expectedV2) {
                    legacyFormat = true;
                }
            }
        }
        for (int i = 0; i < actualCount; i++) {
            const uint8_t *rec = buf + PERM_HEADER_SIZE + i * (legacyFormat ? PERM_RECORD_SIZE_V1 : PERM_RECORD_SIZE);
            if (legacyFormat) {
                deserializePermissionLegacy12(rec, permCache[i]);
            } else {
                deserializePermission(rec, permCache[i]);
            }
        }
        // 旧格式迁移后立即回写为新 16B 格式
        if (legacyFormat) {
            Debug::println(F("[STORAGE] Migrating permission records from V1(12B) to V2(16B)"));
            persistCache();
        }
    }

    free(buf);
    if (permLost) {
        Debug::println(F("[STORAGE] Permission data lost, need to report PERM_LOST to Root"));
    } else {
        Debug::printf("[STORAGE] Permission cache loaded: %d records, version=%u\n",
                      actualCount, actualVersion);
    }
}

// 从A区加载权限表到buf，返回是否CRC校验通过
bool Storage::loadPermissionTable(uint8_t *buf, int &outCount, uint32_t &outVersion) {
    size_t aLen = prefs.getBytesLength("perm_a");
    if (aLen == 0 || aLen > (size_t)(PERM_HEADER_SIZE + PERM_RECORD_SIZE * PERM_MAX_USERS)) {
        outCount = 0;
        outVersion = 0;
        return false;
    }
    prefs.getBytes("perm_a", buf, aLen);

    // 解析 header
    uint32_t magic = ((uint32_t)buf[12] << 24) | ((uint32_t)buf[13] << 16) |
                     ((uint32_t)buf[14] << 8) | buf[15];
    if (magic != PERM_MAGIC) {
        Debug::println(F("[STORAGE] Partition A magic mismatch"));
        return false;
    }

    outVersion = ((uint32_t)buf[0] << 24) | ((uint32_t)buf[1] << 16) |
                 ((uint32_t)buf[2] << 8) | buf[3];
    outCount = (buf[4] << 8) | buf[5];

    if (outCount > PERM_MAX_USERS) {
        Debug::printf("[STORAGE] Partition A permission count abnormal: %d\n", outCount);
        return false;
    }

    // CRC校验：version(4) + count(2) + reserved(2) + records
    uint32_t storedCRC = ((uint32_t)buf[8] << 24) | ((uint32_t)buf[9] << 16) |
                         ((uint32_t)buf[10] << 8) | buf[11];
    int dataLen = 8 + outCount * PERM_RECORD_SIZE;
    uint8_t *crcInput = (uint8_t *)malloc(dataLen);
    if (crcInput == nullptr) return false;
    memcpy(crcInput, buf, 8);
    if (outCount > 0) {
        memcpy(crcInput + 8, buf + PERM_HEADER_SIZE,
               outCount * PERM_RECORD_SIZE);
    }
    uint32_t calcCRC = calculateCRC32(crcInput, dataLen);
    free(crcInput);

    if (storedCRC != calcCRC) {
        Debug::printf("[STORAGE] Partition A CRC check failed: stored=0x%08X calculated=0x%08X\n",
                      storedCRC, calcCRC);
        return false;
    }

    Debug::printf("[STORAGE] Partition A permission loaded: %d records, version=%u\n", outCount, outVersion);
    return true;
}

// 保存权限表到Flash（A/B双区写入）
bool Storage::savePermissionTable(const uint8_t *buf, int count, uint32_t version) {
    if (count < 0 || count > PERM_MAX_USERS) {
        Debug::println(F("[STORAGE] Permission count exceeds limit"));
        return false;
    }

    int totalSize = PERM_HEADER_SIZE + count * PERM_RECORD_SIZE;
    uint8_t *data = (uint8_t*)malloc(totalSize);
    if (data == nullptr) return false;

    // 构造 header: version(4B) + count(2B) + reserved(2B) + CRC32(4B) + magic(4B)
    data[0] = (version >> 24) & 0xFF;
    data[1] = (version >> 16) & 0xFF;
    data[2] = (version >> 8) & 0xFF;
    data[3] = version & 0xFF;
    data[4] = (count >> 8) & 0xFF;
    data[5] = count & 0xFF;
    data[6] = 0; // reserved
    data[7] = 0;

    // 拷贝权限记录
    if (count > 0 && buf != nullptr) {
        memcpy(data + PERM_HEADER_SIZE, buf, count * PERM_RECORD_SIZE);
    }

    // CRC32: header 前 8 字节与 records 连续计算，不包含 CRC/magic。
    int crcLen = 8 + count * PERM_RECORD_SIZE;
    uint8_t *crcInput = (uint8_t *)malloc(crcLen);
    if (crcInput == nullptr) {
        free(data);
        return false;
    }
    memcpy(crcInput, data, 8);
    if (count > 0) {
        memcpy(crcInput + 8, data + PERM_HEADER_SIZE,
               count * PERM_RECORD_SIZE);
    }
    uint32_t crc = calculateCRC32(crcInput, crcLen);
    free(crcInput);
    data[8]  = (crc >> 24) & 0xFF;
    data[9]  = (crc >> 16) & 0xFF;
    data[10] = (crc >> 8) & 0xFF;
    data[11] = crc & 0xFF;

    // 魔数
    data[12] = (PERM_MAGIC >> 24) & 0xFF;
    data[13] = (PERM_MAGIC >> 16) & 0xFF;
    data[14] = (PERM_MAGIC >> 8) & 0xFF;
    data[15] = PERM_MAGIC & 0xFF;

    // A/B双区写入：先写B区（备份），再写A区（主区）
    // 这样断电时至少有一区完整
    size_t writtenB = prefs.putBytes("perm_b", data, totalSize);
    if (writtenB != (size_t)totalSize) {
        free(data);
        Debug::println(F("[STORAGE] Permission backup partition write failed"));
        return false;
    }
    size_t writtenA = prefs.putBytes("perm_a", data, totalSize);

    free(data);
    if (writtenA != (size_t)totalSize) {
        Debug::println(F("[STORAGE] Permission primary partition write failed"));
        return false;
    }
    Debug::printf("[STORAGE] Permission table saved: %d records, version=%u (A/B dual partition)\n", count, version);
    return true;
}

// ====== 设备配置 ======
bool Storage::loadDeviceConfig(DeviceConfig &cfg) {
    if (!initialized) begin();

    cfg.device_id        = prefs.getString("device_id", DEVICE_ID_DEFAULT);
    cfg.device_name      = prefs.getString("device_name", "Cabinet Node");
    cfg.work_mode        = (WorkMode)prefs.getUChar("work_mode", (uint8_t)MODE_MESH);
    // 柜子固件默认永远不是 Root
    cfg.is_root          = prefs.getBool("is_root", false);
    if (cfg.is_root) {
        cfg.is_root = false;
    }
    cfg.uplink_mode      = (UplinkMode)prefs.getUChar("uplink_mode", (uint8_t)UPLINK_USB);
    cfg.mesh_channel     = prefs.getUChar("mesh_channel", MESH_CHANNEL);
    cfg.mesh_password    = prefs.getString("mesh_password", MESH_PASSWORD);
    // ESP-MESH requires real 2.4 GHz router credentials on every node.
    // Empty defaults fail fast instead of silently scanning a placeholder AP.
    cfg.wifi_ssid        = prefs.getString("wifi_ssid", "");
    cfg.wifi_password    = prefs.getString("wifi_password", "");
    cfg.server_ip        = prefs.getString("server_ip", UPLINK_SERVER_IP_DEFAULT);
    cfg.server_port      = prefs.getUShort("server_port", UPLINK_TCP_PORT);
    cfg.fingerprint_count = prefs.getUChar("fp_count", 0);
    cfg.perm_version     = prefs.getUInt("perm_ver", 0);
    cfg.hmac_enabled     = prefs.getBool("hmac_enabled", false);
    cfg.hmac_key         = prefs.getString("hmac_key", "");

    bool hasRecord = prefs.isKey("device_id");
    if (!hasRecord) {
        Debug::println(F("[STORAGE] No config record found, using defaults"));
    } else {
        Debug::printf("[STORAGE] Config: id=%s, root=%s, mode=%s, uplink=%d\n",
                      cfg.device_id.c_str(),
                      cfg.is_root ? "yes" : "no",
                      cfg.work_mode == MODE_MESH ? "Mesh" : "Debug",
                      cfg.uplink_mode);
    }
    return hasRecord;
}

bool Storage::saveDeviceConfig(const DeviceConfig &cfg) {
    if (!initialized) begin();
    prefs.putString("device_id", cfg.device_id);
    prefs.putString("device_name", cfg.device_name);
    prefs.putUChar("work_mode", (uint8_t)cfg.work_mode);
    prefs.putBool("is_root", cfg.is_root);
    prefs.putUChar("uplink_mode", (uint8_t)cfg.uplink_mode);
    prefs.putUChar("mesh_channel", cfg.mesh_channel);
    prefs.putString("mesh_password", cfg.mesh_password);
    prefs.putString("wifi_ssid", cfg.wifi_ssid);
    prefs.putString("wifi_password", cfg.wifi_password);
    prefs.putString("server_ip", cfg.server_ip);
    prefs.putUShort("server_port", cfg.server_port);
    prefs.putUChar("fp_count", cfg.fingerprint_count);
    prefs.putUInt("perm_ver", cfg.perm_version);
    prefs.putBool("hmac_enabled", cfg.hmac_enabled);
    prefs.putString("hmac_key", cfg.hmac_key);
    Debug::println(F("[STORAGE] Device config saved"));
    return true;
}

// ====== 工作模式 ======
WorkMode Storage::loadWorkMode() {
    if (!initialized) begin();
    return (WorkMode)prefs.getUChar("work_mode", (uint8_t)MODE_MESH);
}

bool Storage::saveWorkMode(WorkMode mode) {
    if (!initialized) begin();
    prefs.putUChar("work_mode", (uint8_t)mode);
    Debug::printf("[STORAGE] Work mode saved: %s\n",
                  mode == MODE_MESH ? "Mesh" : "Debug");
    return true;
}

// ====== 用户权限（基于内存缓存） ======
// V2.7：按 fingerprint_id 查找。同时匹配 local_fp_id 以兼容副指纹场景。
// 验证流程应优先使用 findPermissionByAs608Id(local_fp_id)。
bool Storage::loadPermission(int fingerprint_id, UserPermission &perm) {
    if (!initialized) begin();
    if (permCache == nullptr || permCacheCount == 0) {
        perm.valid = false;
        return false;
    }
    for (int i = 0; i < permCacheCount; i++) {
        if (permCache[i].fingerprint_id == fingerprint_id ||
            permCache[i].local_fp_id == fingerprint_id) {
            perm = permCache[i];
            perm.valid = true;
            return true;
        }
    }
    perm.valid = false;
    return false;
}

bool Storage::savePermission(const UserPermission &perm, uint32_t version) {
    if (!initialized) begin();
    if (version > 0) permCacheVersion = version;
    // 查找是否已存在（按 local_fp_id 匹配，因为 AS608 槽位唯一）
    int existIdx = -1;
    for (int i = 0; i < permCacheCount; i++) {
        if (permCache[i].local_fp_id == perm.local_fp_id) {
            existIdx = i;
            break;
        }
    }

    if (existIdx >= 0) {
        permCache[existIdx] = perm;
        permCache[existIdx].valid = true;
    } else {
        // 新增
        if (permCacheCount >= PERM_MAX_USERS) {
            Debug::println(F("[STORAGE] Permission table full"));
            return false;
        }
        UserPermission *newCache = new UserPermission[permCacheCount + 1];
        for (int i = 0; i < permCacheCount; i++) {
            newCache[i] = permCache[i];
        }
        newCache[permCacheCount] = perm;
        newCache[permCacheCount].valid = true;
        if (permCache) delete[] permCache;
        permCache = newCache;
        permCacheCount++;
    }

    // 序列化整个权限表并写入Flash
    uint8_t *buf = (uint8_t*)malloc(permCacheCount * PERM_RECORD_SIZE);
    if (buf == nullptr) return false;
    for (int i = 0; i < permCacheCount; i++) {
        serializePermission(permCache[i], buf + i * PERM_RECORD_SIZE);
    }
    bool ok = savePermissionTable(buf, permCacheCount, permCacheVersion);
    free(buf);
    return ok;
}

bool Storage::deletePermission(int fingerprint_id) {
    if (!initialized) begin();
    int delIdx = -1;
    for (int i = 0; i < permCacheCount; i++) {
        // V2.7：按 fingerprint_id 或 local_fp_id 匹配（AS608 删除时传入的是物理槽位）
        if (permCache[i].fingerprint_id == fingerprint_id ||
            permCache[i].local_fp_id == fingerprint_id) {
            delIdx = i;
            break;
        }
    }
    if (delIdx < 0) return false;

    UserPermission *newCache = nullptr;
    if (permCacheCount > 1) {
        newCache = new UserPermission[permCacheCount - 1];
        int j = 0;
        for (int i = 0; i < permCacheCount; i++) {
            if (i != delIdx) {
                newCache[j++] = permCache[i];
            }
        }
    }
    delete[] permCache;
    permCache = newCache;
    permCacheCount--;

    // 写入Flash
    uint8_t *buf = nullptr;
    if (permCacheCount > 0) {
        buf = (uint8_t*)malloc(permCacheCount * PERM_RECORD_SIZE);
        for (int i = 0; i < permCacheCount; i++) {
            serializePermission(permCache[i], buf + i * PERM_RECORD_SIZE);
        }
    }
    bool ok = savePermissionTable(buf, permCacheCount, permCacheVersion);
    if (buf) free(buf);
    return ok;
}

bool Storage::clearAllPermissions() {
    if (!initialized) begin();
    if (permCache) {
        delete[] permCache;
        permCache = nullptr;
    }
    permCacheCount = 0;
    permCacheVersion = 0;
    permLost = false;
    bool ok = savePermissionTable(nullptr, 0, 0);
    Debug::println(F("[STORAGE] All permission cache cleared"));
    return ok;
}

int Storage::getPermissionCount() {
    if (!initialized) begin();
    return permCacheCount;
}

uint32_t Storage::getPermissionVersion() {
    return permCacheVersion;
}

bool Storage::isPermissionLost() {
    return permLost;
}

bool Storage::replaceAllPermissions(const UserPermission *users, int count, uint32_t version) {
    if (!initialized) begin();
    if (count < 0 || count > PERM_MAX_USERS || (count > 0 && users == nullptr)) return false;

    // V2.7：全量替换只清除主指纹(is_backup=false)记录，保留本机副指纹(is_backup=true)记录。
    // 副指纹是设备专属本地数据，不应被全局权限同步覆盖。
    int backupCount = 0;
    for (int i = 0; i < permCacheCount; i++) {
        if (permCache[i].is_backup) backupCount++;
    }
    int totalCount = count + backupCount;

    UserPermission *newCache = nullptr;
    if (totalCount > 0) {
        newCache = new UserPermission[totalCount];
        if (newCache == nullptr) return false;
        int idx = 0;
        // 先放入主指纹（来自上位机下发）
        for (int i = 0; i < count; i++) {
            newCache[idx] = users[i];
            newCache[idx].is_backup = false;
            // 主指纹的 local_fp_id 默认等于 fingerprint_id（除非已被本机占用，则重新分配）
            if (newCache[idx].local_fp_id <= 0) {
                newCache[idx].local_fp_id = newCache[idx].fingerprint_id;
            }
            newCache[idx].valid = true;
            idx++;
        }
        // 再放入保留的副指纹
        for (int i = 0; i < permCacheCount; i++) {
            if (permCache[i].is_backup) {
                newCache[idx++] = permCache[i];
            }
        }
    }

    // Persist the complete candidate before changing the authoritative RAM
    // cache. A failed transaction therefore keeps the old permissions active.
    uint8_t *buf = nullptr;
    if (totalCount > 0) {
        buf = (uint8_t*)malloc(totalCount * PERM_RECORD_SIZE);
        if (buf == nullptr) {
            delete[] newCache;
            return false;
        }
        for (int i = 0; i < totalCount; i++) {
            serializePermission(newCache[i], buf + i * PERM_RECORD_SIZE);
        }
    }
    bool ok = savePermissionTable(buf, totalCount, version);
    if (buf) free(buf);
    if (!ok) {
        delete[] newCache;
        return false;
    }

    if (permCache) delete[] permCache;
    permCache = newCache;
    permCacheCount = totalCount;
    permCacheVersion = version;
    permLost = false;

    // 更新配置中的版本号
    prefs.putUInt("perm_ver", version);

    Debug::printf("[STORAGE] Full permission replace: %d primary + %d backup = %d records, version=%u\n",
                  count, backupCount, totalCount, version);
    return ok;
}

// ====== 内存缓存持久化（V2.7 副指纹增删复用） ======
bool Storage::persistCache() {
    uint8_t *buf = nullptr;
    if (permCacheCount > 0) {
        buf = (uint8_t*)malloc(permCacheCount * PERM_RECORD_SIZE);
        if (buf == nullptr) return false;
        for (int i = 0; i < permCacheCount; i++) {
            serializePermission(permCache[i], buf + i * PERM_RECORD_SIZE);
        }
    }
    bool ok = savePermissionTable(buf, permCacheCount, permCacheVersion);
    if (buf) free(buf);
    return ok;
}

// ====== 设备专属副指纹（V2.7） ======
// 分配一个未占用的 AS608 物理槽位（0..FINGER_MAX_USERS-1）
// 策略：扫描权限表中已用的 local_fp_id，找最小未占用值。
int Storage::allocLocalFpId() {
    if (!initialized) begin();
    bool used[FINGER_MAX_USERS] = {false};
    for (int i = 0; i < permCacheCount; i++) {
        int id = permCache[i].local_fp_id;
        if (id >= 0 && id < FINGER_MAX_USERS) {
            used[id] = true;
        }
    }
    for (int id = 0; id < FINGER_MAX_USERS; id++) {
        if (!used[id]) return id;
    }
    return -1;  // 槽位已满
}

// 按 AS608 物理槽位查找权限记录（验证入口，主/副共用）
bool Storage::findPermissionByAs608Id(int local_fp_id, UserPermission &perm) {
    if (!initialized) begin();
    if (permCache == nullptr || permCacheCount == 0) {
        perm.valid = false;
        return false;
    }
    for (int i = 0; i < permCacheCount; i++) {
        if (permCache[i].local_fp_id == local_fp_id) {
            perm = permCache[i];
            perm.valid = true;
            return true;
        }
    }
    perm.valid = false;
    return false;
}

// 查找指定用户的主指纹权限记录（用于副指纹权限继承）
bool Storage::findPrimaryPermission(const String &userId, UserPermission &perm) {
    if (!initialized) begin();
    uint32_t uidNum = userIdToNum(userId);
    if (permCache == nullptr || permCacheCount == 0) {
        perm.valid = false;
        return false;
    }
    for (int i = 0; i < permCacheCount; i++) {
        if (permCache[i].user_id_num == uidNum && !permCache[i].is_backup) {
            perm = permCache[i];
            perm.valid = true;
            return true;
        }
    }
    perm.valid = false;
    return false;
}

// 添加一条副指纹记录到本地权限表（is_backup=true）
bool Storage::addBackupFingerprint(const UserPermission &perm) {
    if (!initialized) begin();
    if (permCacheCount >= PERM_MAX_USERS) {
        Debug::println(F("[STORAGE] Permission table full, cannot add backup"));
        return false;
    }
    // 同一用户已有副指纹则拒绝（每人本机最多 1 条副指纹）
    uint32_t uidNum = perm.user_id_num;
    for (int i = 0; i < permCacheCount; i++) {
        if (permCache[i].user_id_num == uidNum && permCache[i].is_backup) {
            Debug::printf("[STORAGE] Backup fingerprint already exists for user %s\n",
                          perm.user_id.c_str());
            return false;
        }
    }
    UserPermission *newCache = new UserPermission[permCacheCount + 1];
    for (int i = 0; i < permCacheCount; i++) {
        newCache[i] = permCache[i];
    }
    newCache[permCacheCount] = perm;
    newCache[permCacheCount].is_backup = true;
    newCache[permCacheCount].valid = true;
    if (permCache) delete[] permCache;
    permCache = newCache;
    permCacheCount++;

    bool ok = persistCache();
    Debug::printf("[STORAGE] Backup fingerprint added: user=%s local_fp_id=%d ok=%d\n",
                  perm.user_id.c_str(), perm.local_fp_id, ok ? 1 : 0);
    return ok;
}

// 列出所有副指纹记录（用于 BACKUP_FP_LIST 上报）
int Storage::listBackupFingerprints(UserPermission *out, int maxCount) {
    if (!initialized) begin();
    int n = 0;
    for (int i = 0; i < permCacheCount && n < maxCount; i++) {
        if (permCache[i].is_backup) {
            out[n++] = permCache[i];
        }
    }
    return n;
}

// 删除指定用户的本机副指纹记录
// 注意：仅删除权限缓存条目，AS608 模板删除由 message_handler 调用 Fingerprint::deleteFingerprint 完成
bool Storage::deleteBackupFingerprint(const String &userId) {
    if (!initialized) begin();
    uint32_t uidNum = userIdToNum(userId);
    int delIdx = -1;
    int localId = -1;
    for (int i = 0; i < permCacheCount; i++) {
        if (permCache[i].user_id_num == uidNum && permCache[i].is_backup) {
            delIdx = i;
            localId = permCache[i].local_fp_id;
            break;
        }
    }
    if (delIdx < 0) return false;

    UserPermission *newCache = nullptr;
    if (permCacheCount > 1) {
        newCache = new UserPermission[permCacheCount - 1];
        int j = 0;
        for (int i = 0; i < permCacheCount; i++) {
            if (i != delIdx) {
                newCache[j++] = permCache[i];
            }
        }
    }
    delete[] permCache;
    permCache = newCache;
    permCacheCount--;

    bool ok = persistCache();
    Debug::printf("[STORAGE] Backup fingerprint deleted: user=%s local_fp_id=%d ok=%d\n",
                  userId.c_str(), localId, ok ? 1 : 0);
    return ok;
}

// ====== 离线日志（Flash 环形缓冲） ======
void Storage::loadLogPointers() {
    bool loadedV2 = false;
    LogPointerState state = {};
    if (logPrefs.getBytesLength("ptrs_v2") == sizeof(state) &&
        logPrefs.getBytes("ptrs_v2", &state, sizeof(state)) == sizeof(state) &&
        state.magic == LOG_POINTER_MAGIC) {
        logWritePtr = state.writePtr;
        logStartPtr = state.startPtr;
        logCount = state.count;
        logSeqCounter = state.sequence;
        loadedV2 = true;
    } else {
        // One-time compatibility migration from the four legacy keys.
        logWritePtr = logPrefs.getInt("wr_ptr", 0);
        logStartPtr = logPrefs.getInt("st_ptr", 0);
        logCount = logPrefs.getInt("count", 0);
        logSeqCounter = logPrefs.getUInt("seq", 0);
    }
    logPtrLoaded = true;

    // 范围校验
    if (logWritePtr < 0 || logWritePtr >= LOG_MAX_ENTRIES) logWritePtr = 0;
    if (logStartPtr < 0 || logStartPtr >= LOG_MAX_ENTRIES) logStartPtr = 0;
    if (logCount < 0) logCount = 0;
    if (logCount > LOG_MAX_ENTRIES) logCount = LOG_MAX_ENTRIES;

    if (!loadedV2) {
        saveLogPointers();
    }

    Debug::printf("[STORAGE] Log pointer loaded: write=%d start=%d count=%d seq=%u\n",
                  logWritePtr, logStartPtr, logCount, logSeqCounter);

    // 初始化扇区擦除状态（假设所有扇区需要擦除）
    for (int i = 0; i < LOG_SECTOR_COUNT; i++) {
        logSectorErased[i] = false;
    }
}

void Storage::saveLogPointers() {
    LogPointerState state = {
        LOG_POINTER_MAGIC,
        logWritePtr,
        logStartPtr,
        logCount,
        logSeqCounter
    };
    if (logPrefs.putBytes("ptrs_v2", &state, sizeof(state)) != sizeof(state)) {
        Debug::println(F("[STORAGE] Failed to persist log pointers"));
    }
}

void Storage::eraseLogSector(int sectorIndex) {
    if (sectorIndex < 0 || sectorIndex >= LOG_SECTOR_COUNT) return;
    uint32_t sectorAddr = LOG_STORE_OFFSET / FLASH_SECTOR_SIZE + sectorIndex;
    if (!logSectorErased[sectorIndex]) {
        ESP.flashEraseSector(sectorAddr);
        logSectorErased[sectorIndex] = true;
    }
}

// 单条日志32B格式：
// log_seq(4B) + fp_id(2B) + lock_id(1B) + result_flags(1B) + timestamp(4B)
// + user_id_num(4B) + user_id_str(15B) + reason_code(1B) = 32B
void Storage::serializeLog(const LogEntry &log, uint8_t *buf) {
    uint32_t seq = log.log_seq;
    uint16_t fpId = (uint16_t)log.fingerprint_id;
    uint8_t lockId = (uint8_t)log.lock_id;
    // result_flags: bit0=success(1)/fail(0), bit1=open(1)/close(0)
    uint8_t flags = 0;
    if (log.result == "success") flags |= 0x01;
    if (log.action == "open") flags |= 0x02;
    uint32_t ts = log.timestamp;
    uint32_t uid = userIdToNum(log.user_id);

    buf[0] = (seq >> 24) & 0xFF;
    buf[1] = (seq >> 16) & 0xFF;
    buf[2] = (seq >> 8) & 0xFF;
    buf[3] = seq & 0xFF;
    buf[4] = (fpId >> 8) & 0xFF;
    buf[5] = fpId & 0xFF;
    buf[6] = lockId;
    buf[7] = flags;
    buf[8] = (ts >> 24) & 0xFF;
    buf[9] = (ts >> 16) & 0xFF;
    buf[10] = (ts >> 8) & 0xFF;
    buf[11] = ts & 0xFF;
    buf[12] = (uid >> 24) & 0xFF;
    buf[13] = (uid >> 16) & 0xFF;
    buf[14] = (uid >> 8) & 0xFF;
    buf[15] = uid & 0xFF;
    // user_id_str: 15B (14 chars + null)
    memset(buf + 16, 0, 15);
    strncpy((char*)(buf + 16), log.user_id.c_str(), 14);
    // reason_code: 1B
    uint8_t reasonCode = 0;
    if (log.reason == "local_cache") reasonCode = 1;
    else if (log.reason == "local_no_permission") reasonCode = 2;
    else if (log.reason == "auth_timeout") reasonCode = 3;
    else if (log.reason == "no_permission") reasonCode = 4;
    else if (log.reason == "remote_control") reasonCode = 5;
    else if (log.reason == "verify_fail_too_many") reasonCode = 6;
    else if (log.reason.length() > 0) reasonCode = 255;
    buf[31] = reasonCode;
}

void Storage::deserializeLog(const uint8_t *buf, LogEntry &log) {
    log.log_seq = ((uint32_t)buf[0] << 24) | ((uint32_t)buf[1] << 16) |
                  ((uint32_t)buf[2] << 8) | buf[3];
    log.fingerprint_id = (buf[4] << 8) | buf[5];
    log.lock_id = buf[6];
    uint8_t flags = buf[7];
    log.result = (flags & 0x01) ? "success" : "fail";
    log.action = (flags & 0x02) ? "open" : "close";
    log.timestamp = ((uint32_t)buf[8] << 24) | ((uint32_t)buf[9] << 16) |
                    ((uint32_t)buf[10] << 8) | buf[11];
    uint32_t uid = ((uint32_t)buf[12] << 24) | ((uint32_t)buf[13] << 16) |
                   ((uint32_t)buf[14] << 8) | buf[15];
    // 优先用字符串中的 user_id
    char uidStr[15];
    memcpy(uidStr, buf + 16, 14);
    uidStr[14] = '\0';
    if (strlen(uidStr) > 0) {
        log.user_id = String(uidStr);
    } else {
        log.user_id = userIdNumToString(uid);
    }
    uint8_t reasonCode = buf[31];
    switch (reasonCode) {
        case 0:  log.reason = ""; break;
        case 1:  log.reason = "local_cache"; break;
        case 2:  log.reason = "local_no_permission"; break;
        case 3:  log.reason = "auth_timeout"; break;
        case 4:  log.reason = "no_permission"; break;
        case 5:  log.reason = "remote_control"; break;
        case 6:  log.reason = "verify_fail_too_many"; break;
        default: log.reason = "other"; break;
    }
}

bool Storage::appendLog(const LogEntry &log) {
    if (!initialized) begin();
    if (!logPtrLoaded) loadLogPointers();

    // 计算写入位置在Flash中的偏移
    int entryOffset = LOG_STORE_OFFSET + logWritePtr * LOG_RECORD_SIZE;
    int sectorIndex = logWritePtr / LOG_ENTRIES_PER_SECTOR;
    int entryInSector = logWritePtr % LOG_ENTRIES_PER_SECTOR;

    // 如果是该扇区的第一条，需要擦除扇区
    if (entryInSector == 0) {
        eraseLogSector(sectorIndex);
    } else {
        logSectorErased[sectorIndex] = false; // 扇区已部分写入，下次需重新擦除
    }

    // 序列化日志
    uint8_t buf[LOG_RECORD_SIZE];
    memset(buf, 0xFF, LOG_RECORD_SIZE); // 空白为0xFF
    LogEntry entry = log;
    if (entry.log_seq == 0) {
        entry.log_seq = ++logSeqCounter;
    } else {
        logSeqCounter = entry.log_seq;
    }
    serializeLog(entry, buf);

    // 写入Flash（ESP.flashWrite 需要 uint32_t* 对齐参数）
    if (!ESP.flashWrite((uint32_t)entryOffset, (uint32_t*)(void*)buf, LOG_RECORD_SIZE)) {
        Debug::println(F("[STORAGE] Log write to Flash failed"));
        return false;
    }

    // 更新写指针
    logWritePtr = (logWritePtr + 1) % LOG_MAX_ENTRIES;

    // 如果写指针追上起始指针，起始指针前移（覆盖最旧）
    if (logCount >= LOG_MAX_ENTRIES) {
        logStartPtr = (logStartPtr + 1) % LOG_MAX_ENTRIES;
    } else {
        logCount++;
    }

    // 保存指针到NVS
    saveLogPointers();

    Debug::printf("[STORAGE] Log written: seq=%u fp=%d lock=%d, write=%d count=%d\n",
                  entry.log_seq, entry.fingerprint_id, entry.lock_id,
                  logWritePtr, logCount);
    return true;
}

bool Storage::readLog(int index, LogEntry &log) {
    if (!initialized) begin();
    if (!logPtrLoaded) loadLogPointers();
    if (index < 0 || index >= logCount) return false;

    // 计算实际位置（从 startPtr 开始偏移 index）
    int actualIdx = (logStartPtr + index) % LOG_MAX_ENTRIES;
    int entryOffset = LOG_STORE_OFFSET + actualIdx * LOG_RECORD_SIZE;

    uint8_t buf[LOG_RECORD_SIZE];
    if (!ESP.flashRead((uint32_t)entryOffset, (uint32_t*)(void*)buf, LOG_RECORD_SIZE)) {
        Debug::println(F("[STORAGE] Log read from Flash failed"));
        return false;
    }

    // 检查是否为空（全0xFF）
    bool allFF = true;
    for (int i = 0; i < LOG_RECORD_SIZE; i++) {
        if (buf[i] != 0xFF) { allFF = false; break; }
    }
    if (allFF) return false;

    deserializeLog(buf, log);
    return true;
}

int Storage::getLogCount() {
    if (!initialized) begin();
    if (!logPtrLoaded) loadLogPointers();
    return logCount;
}

int Storage::getLogCapacity() {
    return LOG_MAX_ENTRIES;
}

bool Storage::markLogsReported(int count) {
    if (!initialized) begin();
    if (!logPtrLoaded) loadLogPointers();
    if (count <= 0) return true;
    if (count > logCount) count = logCount;

    // 移动起始指针
    logStartPtr = (logStartPtr + count) % LOG_MAX_ENTRIES;
    logCount -= count;

    // 保存指针到NVS（修复原版bug：部分上报后Flash未同步）
    saveLogPointers();

    Debug::printf("[STORAGE] Marked %d logs reported, start=%d count=%d\n",
                  count, logStartPtr, logCount);
    return true;
}

bool Storage::clearLogs() {
    if (!initialized) begin();
    logWritePtr = 0;
    logStartPtr = 0;
    logCount = 0;
    logSeqCounter = 0;
    saveLogPointers();

    // 擦除所有日志扇区
    for (int i = 0; i < LOG_SECTOR_COUNT; i++) {
        eraseLogSector(i);
    }
    Debug::println(F("[STORAGE] All logs cleared"));
    return true;
}

uint32_t Storage::getNextLogSeq() {
    if (!initialized) begin();
    if (!logPtrLoaded) loadLogPointers();
    return logSeqCounter + 1;
}

// ====== 时间同步 ======
void Storage::setUnixTime(uint32_t unixTime) {
    struct timeval tv;
    tv.tv_sec = unixTime;
    tv.tv_usec = 0;
    settimeofday(&tv, NULL);
    prefs.putBool("time_synced", true);
    Debug::printf("[STORAGE] System time synced: %u\n", unixTime);
}

uint32_t Storage::getUnixTime() {
    return (uint32_t)time(NULL);
}

bool Storage::isTimeSynced() {
    return prefs.getBool("time_synced", false);
}

// ====== 工具方法 ======
bool Storage::factoryReset() {
    if (!initialized) begin();
    prefs.clear();
    if (permCache) {
        delete[] permCache;
        permCache = nullptr;
    }
    permCacheCount = 0;
    permCacheVersion = 0;
    permLost = false;
    logWritePtr = 0;
    logStartPtr = 0;
    logCount = 0;
    logSeqCounter = 0;
    for (int i = 0; i < LOG_SECTOR_COUNT; i++) {
        eraseLogSector(i);
    }
    Debug::println(F("[STORAGE] Factory reset done"));
    return true;
}
