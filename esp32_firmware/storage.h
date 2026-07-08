/**
 * storage.h - Flash 存储管理（基于 Preferences 库）
 * 保存/读取设备配置、工作模式、用户权限缓存、离线日志
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
    // 加载设备配置，若 Flash 中无记录则返回默认值
    static bool loadDeviceConfig(DeviceConfig &cfg);
    // 保存设备配置到 Flash
    static bool saveDeviceConfig(const DeviceConfig &cfg);

    // ====== 工作模式 ======
    // 读取上次保存的工作模式（AP/STA）
    static WorkMode loadWorkMode();
    // 保存工作模式到 Flash
    static bool saveWorkMode(WorkMode mode);

    // ====== 用户权限缓存 ======
    // 读取指定指纹 ID 的权限缓存
    static bool loadPermission(int fingerprint_id, UserPermission &perm);
    // 保存单个用户权限缓存
    static bool savePermission(const UserPermission &perm);
    // 删除单个用户权限缓存
    static bool deletePermission(int fingerprint_id);
    // 清空所有权限缓存
    static bool clearAllPermissions();
    // 获取已缓存的权限数量
    static int getPermissionCount();

    // ====== 离线日志 ======
    // 追加一条日志（按索引循环存储）
    static bool appendLog(const LogEntry &log);
    // 读取索引处的日志
    static bool readLog(int index, LogEntry &log);
    // 获取已存储的日志条数
    static int getLogCount();
    // 清空所有日志
    static bool clearLogs();

    // ====== 工具方法 ======
    // 清空所有存储数据（恢复出厂）
    static bool factoryReset();

private:
    static Preferences prefs;
    static bool initialized;

    // 权限缓存的命名空间键前缀："perm_<id>"
    static String permKey(int fingerprint_id);
    // 日志存储键前缀："log_<index>"
    static String logKey(int index);
};

#endif // STORAGE_H
