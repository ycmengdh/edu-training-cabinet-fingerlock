/**
 * storage.h - Flash 存储管理（V2.0 分区方案）
 * 设备配置：NVS 键值存储
 * 权限数据：NVS Blob，A/B 双区写入 + 魔数 0xA5A55A5A + CRC32 校验
 * 离线日志：Flash 环形缓冲（32扇区×4KB），写指针+起始指针
 */
#ifndef STORAGE_H
#define STORAGE_H

#include <Arduino.h>
#include <Preferences.h>
#include "config.h"

class Storage {
public:
    // 初始化存储
    static void begin();

    // ====== 设备配置 ======
    static bool loadDeviceConfig(DeviceConfig &cfg);
    static bool saveDeviceConfig(const DeviceConfig &cfg);

    // ====== 工作模式 ======
    static WorkMode loadWorkMode();
    static bool saveWorkMode(WorkMode mode);

    // ====== 用户权限（紧凑二进制 + A/B双区） ======
    // 加载指定指纹 ID 的权限缓存
    static bool loadPermission(int fingerprint_id, UserPermission &perm);
    // 保存单个用户权限缓存（会触发整个权限表写入）
    static bool savePermission(const UserPermission &perm);
    // 删除单个用户权限缓存
    static bool deletePermission(int fingerprint_id);
    // 清空所有权限缓存
    static bool clearAllPermissions();
    // 获取已缓存的权限数量
    static int getPermissionCount();
    // 获取权限版本号
    static uint32_t getPermissionVersion();
    // 权限数据是否丢失（启动时CRC都失败）
    static bool isPermissionLost();
    // 全量替换权限数据（从 SYNC_PERMISSIONS 调用）
    // users: 权限数组，count: 数量
    static bool replaceAllPermissions(const UserPermission *users, int count, uint32_t version);

    // ====== 离线日志（Flash 环形缓冲） ======
    // 追加一条日志（自动管理环形写指针）
    static bool appendLog(const LogEntry &log);
    // 读取索引处的日志（0=最旧，配合 start 指针）
    static bool readLog(int index, LogEntry &log);
    // 获取已存储的日志条数（start 到 write 之间）
    static int getLogCount();
    // 获取日志环形缓冲总容量
    static int getLogCapacity();
    // 标记前 N 条日志为已上报（移动 start 指针）
    static bool markLogsReported(int count);
    // 清空所有日志
    static bool clearLogs();
    // 获取下一个日志序号
    static uint32_t getNextLogSeq();

    // ====== 时间同步 ======
    static void setUnixTime(uint32_t unixTime);
    static uint32_t getUnixTime();
    static bool isTimeSynced();

    // ====== 工具方法 ======
    static bool factoryReset();
    // user_id 字符串 <-> 数字形式转换（保存权限前需调用以填充 user_id_num）
    // "U001" -> 1, "U123" -> 123, 非"U"开头返回哈希
    static uint32_t userIdToNum(const String &userId);
    static String userIdNumToString(uint32_t num);
    // 日期字符串 -> 距2000-01-01的天数
    static uint32_t dateToDays(const String &dateStr);
    static String daysToDate(uint32_t days);

private:
    static Preferences prefs;        // NVS 命名空间（设备配置 + 权限元数据）
    static Preferences logPrefs;     // NVS 命名空间（日志指针）
    static bool initialized;

    // ====== 权限数据内部方法 ======
    // 权限数据结构：header(16B) + records(12B×N)
    // header: version(4B) + count(2B) + reserved(2B) + CRC32(4B) + magic(4B)
    static bool loadPermissionTable(uint8_t *buf, int &outCount, uint32_t &outVersion);
    static bool savePermissionTable(const uint8_t *buf, int count, uint32_t version);
    static uint32_t calculateCRC32(const uint8_t *data, size_t len);
    static void serializePermission(const UserPermission &perm, uint8_t *buf);
    static void deserializePermission(const uint8_t *buf, UserPermission &perm);

    // 权限表内存缓存（启动时加载）
    static UserPermission *permCache;
    static int permCacheCount;
    static uint32_t permCacheVersion;
    static bool permLost;

    // 从缓存加载权限表
    static void loadPermCacheFromFlash();

    // ====== 日志环形缓冲内部方法 ======
    // 单条日志32B二进制格式：
    // log_seq(4B) + fp_id(2B) + lock_id(1B) + result_flags(1B) + timestamp(4B)
    // + user_id_num(4B) + user_id_str(15B) + reason_code(1B)
    static void serializeLog(const LogEntry &log, uint8_t *buf);
    static void deserializeLog(const uint8_t *buf, LogEntry &log);
    static int  logWritePtr;    // 写指针（0..LOG_MAX_ENTRIES-1）
    static int  logStartPtr;    // 起始指针（0..LOG_MAX_ENTRIES-1）
    static int  logCount;       // 当前条数
    static uint32_t logSeqCounter;
    static bool logPtrLoaded;

    static void loadLogPointers();
    static void saveLogPointers();
    static void eraseLogSector(int sectorIndex);
};

#endif // STORAGE_H
