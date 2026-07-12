/**
 * storage.cpp - Flash 存储管理实现（V2.0 分区方案）
 * 权限数据：NVS Blob，紧凑二进制格式，A/B双区 + 魔数 + CRC32
 * 离线日志：Flash 环形缓冲（ESP.flashWrite/Read/Erase），写指针+起始指针
 */
#include "storage.h"
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

void Storage::begin() {
    if (!initialized) {
        prefs.begin("esp32_cfg", false);      // 设备配置 + 权限元数据
        logPrefs.begin("esp32_log", false);    // 日志指针
        initialized = true;
        Serial.println(F("[STORAGE] 存储初始化完成"));

        // 加载权限缓存
        loadPermCacheFromFlash();

        // 加载日志指针
        loadLogPointers();
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
// 12B 格式：fp_id(2B) + user_id(4B) + lock_perm(1B) + role(1B) + expire_days(4B)
void Storage::serializePermission(const UserPermission &perm, uint8_t *buf) {
    uint16_t fpId = (uint16_t)perm.fingerprint_id;
    uint32_t uid = perm.user_id_num;
    uint8_t lockPerm = 0;
    for (int i = 0; i < LOCK_COUNT; i++) {
        if (perm.lock_perm[i]) lockPerm |= (1 << i);
    }
    uint8_t role = (uint8_t)perm.role;

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
    perm.user_id = userIdNumToString(perm.user_id_num);
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
        Serial.println(F("[STORAGE] 权限缓冲分配失败"));
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
        Serial.println(F("[STORAGE] A区权限CRC失败，尝试B区..."));
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
                    Serial.printf("[STORAGE] B区权限加载成功: %d条, 版本=%u\n",
                                  actualCount, actualVersion);
                } else {
                    Serial.println(F("[STORAGE] B区CRC也失败，权限为空"));
                    actualCount = 0;
                    permLost = true;
                }
            } else {
                Serial.println(F("[STORAGE] B区魔数不匹配，权限为空"));
                actualCount = 0;
                permLost = true;
            }
        } else {
            Serial.println(F("[STORAGE] B区无数据，权限为空"));
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
        for (int i = 0; i < actualCount; i++) {
            deserializePermission(buf + PERM_HEADER_SIZE + i * PERM_RECORD_SIZE, permCache[i]);
        }
    }

    free(buf);
    if (permLost) {
        Serial.println(F("[STORAGE] 权限数据丢失，需向Root上报PERM_LOST"));
    } else {
        Serial.printf("[STORAGE] 权限缓存加载完成: %d条, 版本=%u\n",
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
        Serial.println(F("[STORAGE] A区魔数不匹配"));
        return false;
    }

    outVersion = ((uint32_t)buf[0] << 24) | ((uint32_t)buf[1] << 16) |
                 ((uint32_t)buf[2] << 8) | buf[3];
    outCount = (buf[4] << 8) | buf[5];

    if (outCount > PERM_MAX_USERS) {
        Serial.printf("[STORAGE] A区权限数异常: %d\n", outCount);
        return false;
    }

    // CRC校验：version(4) + count(2) + reserved(2) + records
    uint32_t storedCRC = ((uint32_t)buf[8] << 24) | ((uint32_t)buf[9] << 16) |
                         ((uint32_t)buf[10] << 8) | buf[11];
    int dataLen = 8 + outCount * PERM_RECORD_SIZE;
    uint32_t calcCRC = calculateCRC32(buf, dataLen);

    if (storedCRC != calcCRC) {
        Serial.printf("[STORAGE] A区CRC校验失败: 存储=0x%08X 计算=0x%08X\n",
                      storedCRC, calcCRC);
        return false;
    }

    Serial.printf("[STORAGE] A区权限加载成功: %d条, 版本=%u\n", outCount, outVersion);
    return true;
}

// 保存权限表到Flash（A/B双区写入）
bool Storage::savePermissionTable(const uint8_t *buf, int count, uint32_t version) {
    if (count < 0 || count > PERM_MAX_USERS) {
        Serial.println(F("[STORAGE] 权限数超限"));
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

    // CRC32：对 version(4) + count(2) + reserved(2) + records 计算
    int crcLen = 8 + count * PERM_RECORD_SIZE;
    uint32_t crc = calculateCRC32(data, crcLen);
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
    prefs.putBytes("perm_b", data, totalSize);
    prefs.putBytes("perm_a", data, totalSize);

    free(data);
    Serial.printf("[STORAGE] 权限表已保存: %d条, 版本=%u (A/B双区)\n", count, version);
    return true;
}

// ====== 设备配置 ======
bool Storage::loadDeviceConfig(DeviceConfig &cfg) {
    if (!initialized) begin();

    cfg.device_id        = prefs.getString("device_id", DEVICE_ID_DEFAULT);
    cfg.device_name      = prefs.getString("device_name", "ESP32_Fingerprint_Lock");
    cfg.work_mode        = (WorkMode)prefs.getUChar("work_mode", (uint8_t)MODE_MESH);
    cfg.is_root          = prefs.getBool("is_root", false);
    cfg.uplink_mode      = (UplinkMode)prefs.getUChar("uplink_mode", (uint8_t)UPLINK_USB);
    cfg.mesh_channel     = prefs.getUChar("mesh_channel", MESH_CHANNEL);
    cfg.mesh_password    = prefs.getString("mesh_password", MESH_PASSWORD);
    cfg.wifi_ssid        = prefs.getString("wifi_ssid", "TrainingRoom_WiFi");
    cfg.wifi_password    = prefs.getString("wifi_password", "12345678");
    cfg.server_ip        = prefs.getString("server_ip", UPLINK_SERVER_IP_DEFAULT);
    cfg.server_port      = prefs.getUShort("server_port", UPLINK_TCP_PORT);
    cfg.fingerprint_count = prefs.getUChar("fp_count", 0);
    cfg.perm_version     = prefs.getUInt("perm_ver", 0);

    bool hasRecord = prefs.isKey("device_id");
    if (!hasRecord) {
        Serial.println(F("[STORAGE] 未发现配置记录，使用默认值"));
    } else {
        Serial.printf("[STORAGE] 配置: id=%s, root=%s, mode=%s, uplink=%d\n",
                      cfg.device_id.c_str(),
                      cfg.is_root ? "是" : "否",
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
    Serial.println(F("[STORAGE] 设备配置已保存"));
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
    Serial.printf("[STORAGE] 工作模式已保存: %s\n",
                  mode == MODE_MESH ? "Mesh" : "Debug");
    return true;
}

// ====== 用户权限（基于内存缓存） ======
bool Storage::loadPermission(int fingerprint_id, UserPermission &perm) {
    if (!initialized) begin();
    if (permCache == nullptr || permCacheCount == 0) {
        perm.valid = false;
        return false;
    }
    for (int i = 0; i < permCacheCount; i++) {
        if (permCache[i].fingerprint_id == fingerprint_id) {
            perm = permCache[i];
            perm.valid = true;
            return true;
        }
    }
    perm.valid = false;
    return false;
}

bool Storage::savePermission(const UserPermission &perm) {
    if (!initialized) begin();
    // 查找是否已存在
    int existIdx = -1;
    for (int i = 0; i < permCacheCount; i++) {
        if (permCache[i].fingerprint_id == perm.fingerprint_id) {
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
            Serial.println(F("[STORAGE] 权限表已满"));
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
        if (permCache[i].fingerprint_id == fingerprint_id) {
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
    Serial.println(F("[STORAGE] 已清空所有权限缓存"));
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
    if (count < 0 || count > PERM_MAX_USERS) return false;

    if (permCache) {
        delete[] permCache;
        permCache = nullptr;
    }
    permCacheCount = count;
    permCacheVersion = version;
    permLost = false;

    if (count > 0) {
        permCache = new UserPermission[count];
        for (int i = 0; i < count; i++) {
            permCache[i] = users[i];
            permCache[i].valid = true;
        }
    }

    // 序列化并写入Flash
    uint8_t *buf = nullptr;
    if (count > 0) {
        buf = (uint8_t*)malloc(count * PERM_RECORD_SIZE);
        for (int i = 0; i < count; i++) {
            serializePermission(permCache[i], buf + i * PERM_RECORD_SIZE);
        }
    }
    bool ok = savePermissionTable(buf, count, version);
    if (buf) free(buf);

    // 更新配置中的版本号
    DeviceConfig cfg;
    loadDeviceConfig(cfg);
    cfg.perm_version = version;
    prefs.putUInt("perm_ver", version);

    Serial.printf("[STORAGE] 全量替换权限: %d条, 版本=%u\n", count, version);
    return ok;
}

// ====== 离线日志（Flash 环形缓冲） ======
void Storage::loadLogPointers() {
    logWritePtr = logPrefs.getInt("wr_ptr", 0);
    logStartPtr = logPrefs.getInt("st_ptr", 0);
    logCount = logPrefs.getInt("count", 0);
    logSeqCounter = logPrefs.getUInt("seq", 0);
    logPtrLoaded = true;

    // 范围校验
    if (logWritePtr < 0 || logWritePtr >= LOG_MAX_ENTRIES) logWritePtr = 0;
    if (logStartPtr < 0 || logStartPtr >= LOG_MAX_ENTRIES) logStartPtr = 0;
    if (logCount < 0) logCount = 0;
    if (logCount > LOG_MAX_ENTRIES) logCount = LOG_MAX_ENTRIES;

    Serial.printf("[STORAGE] 日志指针加载: write=%d start=%d count=%d seq=%u\n",
                  logWritePtr, logStartPtr, logCount, logSeqCounter);

    // 初始化扇区擦除状态（假设所有扇区需要擦除）
    for (int i = 0; i < LOG_SECTOR_COUNT; i++) {
        logSectorErased[i] = false;
    }
}

void Storage::saveLogPointers() {
    logPrefs.putInt("wr_ptr", logWritePtr);
    logPrefs.putInt("st_ptr", logStartPtr);
    logPrefs.putInt("count", logCount);
    logPrefs.putUInt("seq", logSeqCounter);
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

    // 写入Flash
    if (!ESP.flashWrite(entryOffset, buf, LOG_RECORD_SIZE)) {
        Serial.println(F("[STORAGE] 日志写入Flash失败"));
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

    Serial.printf("[STORAGE] 日志写入: seq=%u fp=%d lock=%d, write=%d count=%d\n",
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
    if (!ESP.flashRead(entryOffset, buf, LOG_RECORD_SIZE)) {
        Serial.println(F("[STORAGE] 日志读取Flash失败"));
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

    Serial.printf("[STORAGE] 标记 %d 条日志已上报, start=%d count=%d\n",
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
    Serial.println(F("[STORAGE] 已清空所有日志"));
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
    Serial.printf("[STORAGE] 系统时间已同步: %u\n", unixTime);
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
    logPrefs.clear();
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
    Serial.println(F("[STORAGE] 已恢复出厂设置"));
    return true;
}
