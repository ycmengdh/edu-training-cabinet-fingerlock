/**
 * logger.h - 离线日志系统（V2.0 适配 Flash 环形缓冲）
 * 日志持久化于 Flash 环形缓冲（32扇区×4KB，4096条），断电不丢失
 * 网络恢复后批量上报：每 10 秒最多 20 条
 * 日志序号自增（log_seq），支持断点续传
 * 通过 MeshComm 上报日志
 */
#ifndef LOGGER_H
#define LOGGER_H

#include <Arduino.h>
#include "config.h"

class Logger {
public:
    // 初始化日志模块（Flash 指针已在 Storage::begin 中加载）
    static void init();

    // 记录一条日志（写入 Flash 环形缓冲，自动分配 log_seq）
    static void log(const String &userId, int fingerprintId, int lockId,
                    const String &action, const String &result,
                    const String &reason, uint32_t timestamp = 0);

    // 记录一条日志（直接传 LogEntry）
    static void log(const LogEntry &entry);

    // 获取待上报日志数量（Flash 环形缓冲中的条数）
    static int getPendingCount();

    // 获取指定索引的日志（0 = 最旧，从 start 指针开始）
    static bool getLog(int index, LogEntry &entry);

    // 标记前 N 条日志为已上报（移动 start 指针，立即保存到 NVS）
    static void markReported(int count);

    // 清空所有日志（Flash + NVS 指针）
    static void clearAll();

    // 主循环调用，按间隔批量上报日志（网络可用时）
    static void update();

    // 设置网络可用性标志（true 时触发上报）
    static void setNetworkReady(bool ready);

    // Root acknowledges only after the batch is persisted on SD.
    static void handleReportAck(const String &msgId, const String &result);

private:
    static bool networkReady;
    static unsigned long lastReportTime;
    static bool awaitingAck;
    static String reportMsgId;
    static int reportCount;
    static unsigned long reportSentTime;

    // 批量上报日志（每批最多 LOG_REPORT_BATCH_MAX 条）
    static void reportBatch();
};

#endif // LOGGER_H
