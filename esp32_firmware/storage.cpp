/**
 * storage.cpp - Flash 存储管理实现（基于 Preferences 库）
 */
#include "storage.h"

Preferences Storage::prefs;
bool Storage::initialized = false;

void Storage::begin() {
    if (!initialized) {
        prefs.begin("esp32_lock", false); // 读写模式打开命名空间
        initialized = true;
        Serial.println(F("[STORAGE] 存储初始化完成"));
    }
}

// ====== 设备配置 ======
bool Storage::loadDeviceConfig(DeviceConfig &cfg) {
    if (!initialized) begin();

    // 默认值
    cfg.device_id        = prefs.getString("device_id", DEVICE_ID_DEFAULT);
    cfg.device_name      = prefs.getString("device_name", "ESP32_Fingerprint_Lock");
    cfg.wifi_ssid        = prefs.getString("wifi_ssid", DEFAULT_WIFI_SSID);
    cfg.wifi_password    = prefs.getString("wifi_password", DEFAULT_WIFI_PASSWORD);
    cfg.server_ip        = prefs.getString("server_ip", "192.168.1.100");
    cfg.server_port      = prefs.getUShort("server_port", TCP_PORT);
    cfg.work_mode        = (WorkMode)prefs.getUChar("work_mode", (uint8_t)MODE_STA);
    cfg.fingerprint_count = prefs.getUChar("fp_count", 0);

    // 判断是否首次启动（无 device_id 记录）
    bool hasRecord = prefs.isKey("device_id");
    if (!hasRecord) {
        Serial.println(F("[STORAGE] 未发现配置记录，使用默认值"));
    } else {
        Serial.printf("[STORAGE] 加载配置: device_id=%s, ssid=%s, server=%s:%u, mode=%s\n",
                      cfg.device_id.c_str(), cfg.wifi_ssid.c_str(),
                      cfg.server_ip.c_str(), cfg.server_port,
                      cfg.work_mode == MODE_AP ? "AP" : "STA");
    }
    return hasRecord;
}

bool Storage::saveDeviceConfig(const DeviceConfig &cfg) {
    if (!initialized) begin();

    prefs.putString("device_id", cfg.device_id);
    prefs.putString("device_name", cfg.device_name);
    prefs.putString("wifi_ssid", cfg.wifi_ssid);
    prefs.putString("wifi_password", cfg.wifi_password);
    prefs.putString("server_ip", cfg.server_ip);
    prefs.putUShort("server_port", cfg.server_port);
    prefs.putUChar("work_mode", (uint8_t)cfg.work_mode);
    prefs.putUChar("fp_count", cfg.fingerprint_count);

    Serial.println(F("[STORAGE] 设备配置已保存"));
    return true;
}

// ====== 工作模式 ======
WorkMode Storage::loadWorkMode() {
    if (!initialized) begin();
    return (WorkMode)prefs.getUChar("work_mode", (uint8_t)MODE_STA);
}

bool Storage::saveWorkMode(WorkMode mode) {
    if (!initialized) begin();
    prefs.putUChar("work_mode", (uint8_t)mode);
    Serial.printf("[STORAGE] 工作模式已保存: %s\n", mode == MODE_AP ? "AP" : "STA");
    return true;
}

// ====== 用户权限缓存 ======
String Storage::permKey(int fingerprint_id) {
    return "perm_" + String(fingerprint_id);
}

bool Storage::loadPermission(int fingerprint_id, UserPermission &perm) {
    if (!initialized) begin();
    String key = permKey(fingerprint_id);
    if (!prefs.isKey(key.c_str())) {
        perm.valid = false;
        return false;
    }
    // 将权限序列化为紧凑字符串：uid|name|role|l0|l1|l2|l3
    String blob = prefs.getString(key.c_str(), "");
    if (blob.length() == 0) {
        perm.valid = false;
        return false;
    }

    // 按 '|' 分隔成 7 个字段：uid|name|role|l0|l1|l2|l3
    String fields[7];
    int fieldIdx = 0;
    int start = 0;
    for (size_t i = 0; i <= blob.length() && fieldIdx < 7; i++) {
        if (i == blob.length() || blob[i] == '|') {
            fields[fieldIdx++] = blob.substring(start, i);
            start = i + 1;
        }
    }
    if (fieldIdx < 7) { perm.valid = false; return false; }

    perm.fingerprint_id  = fingerprint_id;
    perm.user_id         = fields[0];
    perm.name            = fields[1];
    perm.role            = (UserRole)fields[2].toInt();
    perm.lock_perm[0]    = fields[3].toInt() != 0;
    perm.lock_perm[1]    = fields[4].toInt() != 0;
    perm.lock_perm[2]    = fields[5].toInt() != 0;
    perm.lock_perm[3]    = fields[6].toInt() != 0;
    perm.valid           = true;
    return true;
}

bool Storage::savePermission(const UserPermission &perm) {
    if (!initialized) begin();
    String key = permKey(perm.fingerprint_id);
    // 紧凑序列化：uid|name|role|l0|l1|l2|l3
    String blob = perm.user_id + "|" + perm.name + "|" + String((int)perm.role) + "|" +
                  String(perm.lock_perm[0] ? 1 : 0) + "|" +
                  String(perm.lock_perm[1] ? 1 : 0) + "|" +
                  String(perm.lock_perm[2] ? 1 : 0) + "|" +
                  String(perm.lock_perm[3] ? 1 : 0);
    prefs.putString(key.c_str(), blob);
    return true;
}

bool Storage::deletePermission(int fingerprint_id) {
    if (!initialized) begin();
    String key = permKey(fingerprint_id);
    if (prefs.isKey(key.c_str())) {
        prefs.remove(key.c_str());
        return true;
    }
    return false;
}

bool Storage::clearAllPermissions() {
    if (!initialized) begin();
    // 删除所有 perm_ 开头的键
    for (int i = 0; i < FINGER_MAX_USERS; i++) {
        String key = permKey(i);
        if (prefs.isKey(key.c_str())) {
            prefs.remove(key.c_str());
        }
    }
    Serial.println(F("[STORAGE] 已清空所有权限缓存"));
    return true;
}

int Storage::getPermissionCount() {
    if (!initialized) begin();
    int count = 0;
    for (int i = 0; i < FINGER_MAX_USERS; i++) {
        if (prefs.isKey(permKey(i).c_str())) count++;
    }
    return count;
}

// ====== 离线日志 ======
String Storage::logKey(int index) {
    return "log_" + String(index);
}

bool Storage::appendLog(const LogEntry &log) {
    if (!initialized) begin();
    int count = getLogCount();
    if (count >= LOG_BUFFER_MAX) {
        // 环形覆盖：从最旧的一条开始覆盖
        count = LOG_BUFFER_MAX - 1;
        // 整体前移一位，丢弃最旧
        for (int i = 0; i < LOG_BUFFER_MAX - 1; i++) {
            String srcKey = logKey(i + 1);
            String dstKey = logKey(i);
            if (prefs.isKey(srcKey.c_str())) {
                String blob = prefs.getString(srcKey.c_str(), "");
                prefs.putString(dstKey.c_str(), blob);
            }
        }
    }
    // 在 count 位置写入新日志
    String key = logKey(count);
    // 序列化：uid|fp_id|lock_id|action|result|reason|timestamp
    String blob = log.user_id + "|" + String(log.fingerprint_id) + "|" +
                  String(log.lock_id) + "|" + log.action + "|" + log.result +
                  "|" + log.reason + "|" + log.timestamp;
    prefs.putString(key.c_str(), blob);
    prefs.putInt("log_count", count + 1);
    return true;
}

bool Storage::readLog(int index, LogEntry &log) {
    if (!initialized) begin();
    String key = logKey(index);
    if (!prefs.isKey(key.c_str())) return false;
    String blob = prefs.getString(key.c_str(), "");
    if (blob.length() == 0) return false;

    // 按 '|' 分隔成 7 个字段：uid|fp_id|lock_id|action|result|reason|timestamp
    // 注意 timestamp 中可能不含 '|'，作为最后一个字段直接取到末尾
    String fields[7];
    int fieldIdx = 0;
    int start = 0;
    for (size_t i = 0; i <= blob.length() && fieldIdx < 7; i++) {
        if (i == blob.length() || blob[i] == '|') {
            fields[fieldIdx++] = blob.substring(start, i);
            start = i + 1;
        }
    }
    if (fieldIdx < 7) return false;

    log.user_id        = fields[0];
    log.fingerprint_id = fields[1].toInt();
    log.lock_id        = fields[2].toInt();
    log.action         = fields[3];
    log.result         = fields[4];
    log.reason         = fields[5];
    log.timestamp      = fields[6];
    return true;
}

int Storage::getLogCount() {
    if (!initialized) begin();
    return prefs.getInt("log_count", 0);
}

bool Storage::clearLogs() {
    if (!initialized) begin();
    int count = getLogCount();
    for (int i = 0; i < count; i++) {
        String key = logKey(i);
        if (prefs.isKey(key.c_str())) {
            prefs.remove(key.c_str());
        }
    }
    prefs.putInt("log_count", 0);
    Serial.println(F("[STORAGE] 已清空所有日志"));
    return true;
}

// ====== 工具方法 ======
bool Storage::factoryReset() {
    if (!initialized) begin();
    prefs.clear();
    Serial.println(F("[STORAGE] 已恢复出厂设置"));
    return true;
}
