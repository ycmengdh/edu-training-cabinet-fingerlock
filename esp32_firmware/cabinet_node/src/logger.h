/**
 * logger.h - 运行期调试日志
 * 开锁记录只输出调试信息，不写 NVS/Flash，也不通过 Mesh 上报到 SD。
 */
#ifndef LOGGER_H
#define LOGGER_H

#include <Arduino.h>
#include "config.h"

class Logger {
public:
    // 初始化运行期日志状态
    static void init();

    // 记录一条运行期调试日志（不持久化）
    static void log(const String &userId, int fingerprintId, int lockId,
                    const String &action, const String &result,
                    const String &reason, uint32_t timestamp = 0);

    // 记录一条日志（直接传 LogEntry）
    static void log(const LogEntry &entry);

    // 持久化已禁用，始终返回 0
    static int getPendingCount();

    // 持久化已禁用，始终返回 false
    static bool getLog(int index, LogEntry &entry);

    // 兼容旧命令的空操作
    static void markReported(int count);

    // 兼容旧命令的空操作
    static void clearAll();

    // 无持久化/上报任务
    static void update();

    // 设置网络可用性标志（true 时触发上报）
    static void setNetworkReady(bool ready);

    // 兼容旧固件回包
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
