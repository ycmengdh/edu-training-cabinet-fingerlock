/**
 * sd_storage.h - SD 卡集中存储（仅根节点使用）
 * 将全校用户/班级/权限/设备/指纹模板集中存于根节点 SD 卡，
 * 作为多上位机共享的单一权威数据源。
 *
 * 文件结构：
 *   /sdcard/data/
 *   ├── version.json          全局版本元数据（乐观锁）
 *   ├── users.json            用户表
 *   ├── classes.json          班级表
 *   ├── permissions.json      权限表
 *   ├── role_permissions.json 角色默认权限
 *   ├── devices.json          设备注册表
 *   ├── logs.json             开锁日志（可选）
 *   └── fingerprints/
 *       ├── index.json        指纹映射表
 *       └── FP_XXXXX[_2].bin  指纹模板二进制文件（每枚 512B）
 *
 * 所有写入采用"写临时文件 + rename"原子操作，防断电损坏。
 */
#ifndef SD_STORAGE_H
#define SD_STORAGE_H

#ifdef ENABLE_SD_CARD

#include <Arduino.h>
#include "config.h"

enum class TableUploadChunkResult : uint8_t {
    Failed,
    Accepted,
    Duplicate,
    Complete,
    OutOfOrder,
    Invalid
};

class SdStorage {
public:
    // 初始化 SD 卡并挂载 FatFS，创建目录结构
    static bool init();

    // SD 卡是否已挂载就绪
    static bool isReady();

    // V2.7: 获取最近一次 SD 失败原因（为空表示正常或未尝试）
    static const String& getLastError() { return lastError; }

    // ====== JSON 表读写 ======
    // 读取整张表为 JSON 字符串（全量读取，适合中小表）
    // tableName: "users" / "classes" / "permissions" / "devices" / "version"
    // 成功返回 true，outJson 填充内容
    static bool readTable(const String &tableName, String &outJson);

    // 原子写入整张表 JSON（先写 .tmp 再 rename）
    static bool writeTable(const String &tableName, const String &json);

    // 流式写入大表。分块先追加到 .upload，最后一块校验后原子替换正式表。
    static TableUploadChunkResult writeTableChunk(
        const String &tableName, const String &uploadId,
        uint32_t partIndex, uint32_t partTotal, uint32_t totalBytes,
        const uint8_t *data, size_t len, uint32_t &expectedPart);

    // 响应丢失后，主机可能重发已经落盘的块；用于跳过重复版本检查。
    static bool isTableUploadChunkKnown(
        const String &tableName, const String &uploadId,
        uint32_t partIndex, uint32_t partTotal);

    // ====== 指纹模板读写 ======
    // 保存指纹模板到 SD 卡（按 user_id + index 命名）
    // userId: 用户ID（数字或字符串，用于文件名）
    // index:  模板序号（1 或 2）
    // data:   512B 模板数据
    // len:    数据长度
    static bool writeTemplate(const String &userId, int index,
                              const uint8_t *data, size_t len);

    // 读取指纹模板
    // 成功返回 true，outBuf 填充数据，outLen 返回长度
    static bool readTemplate(const String &userId, int index,
                             uint8_t *outBuf, size_t bufSize, size_t &outLen);

    // 删除指定用户所有指纹模板
    static bool deleteTemplate(const String &userId);

    // 删除指定用户的一枚指纹模板
    static bool deleteTemplate(const String &userId, int index);

    // 获取指纹模板文件名（内部命名规则）
    static String getTemplateFileName(const String &userId, int index);

    // ====== 版本元数据 ======
    // 读取全局版本号
    static bool readVersion(uint32_t &globalVer, uint32_t &usersVer,
                            uint32_t &classesVer, uint32_t &permsVer,
                            uint32_t &devicesVer, uint32_t &fpVer,
                            uint32_t &logsVer);

    // 自增某表版本号并刷新全局版本号（写入前调用）
    static bool incrementVersion(const String &tableName);

    // 获取当前全局版本号（快速查询用）
    static uint32_t getGlobalVersion();
    static uint32_t getPermissionsVersion();

    // Append a batch of log objects to logs.json and retain the newest entries.
    static bool appendLogs(const String &logsJson);

    // ====== 工具 ======
    // 获取 SD 卡信息（容量等），用于 READ_STATUS 上报
    static uint64_t getTotalBytes();
    static uint64_t getUsedBytes();

private:
    static bool mounted;
    static uint64_t cachedTotalBytes;
    static uint64_t cachedUsedBytes;
    static bool versionCacheValid;
    static uint32_t cachedGlobalVersion;
    static uint32_t cachedUsersVersion;
    static uint32_t cachedClassesVersion;
    static uint32_t cachedPermissionsVersion;
    static uint32_t cachedDevicesVersion;
    static uint32_t cachedFingerprintVersion;
    static uint32_t cachedLogsVersion;
    // V2.7: 详细错误信息（供 host/显示/调试定位 SD 故障）
    static String lastError;

    // 确保目录存在
    static bool ensureDir(const String &path);

    // 原子写入：写 .tmp → rename
    static bool atomicWrite(const String &path, const uint8_t *data, size_t len);
    static bool promoteTempFile(const String &path, const String &tmpPath);

    // 表名转文件路径
    static String tablePath(const String &tableName);
};

#endif // ENABLE_SD_CARD

#endif // SD_STORAGE_H
