/**
 * logger.h - 本地日志缓存
 * 内存环形缓冲区存储开锁日志，最多 100 条，网络恢复后批量上报
 */
#ifndef LOGGER_H
#define LOGGER_H

#include <Arduino.h>
#include "config.h"

class Logger {
public:
    // 初始化日志模块（从 Flash 加载历史日志到内存）
    static void init();

    // 记录一条日志（同时写入 Flash 持久化）
    static void log(const String &userId, int fingerprintId, int lockId,
                    const String &action, const String &result,
                    const String &reason, const String &timestamp = "");

    // 记录一条日志（直接传 LogEntry）
    static void log(const LogEntry &entry);

    // 获取待上报日志数量
    static int getPendingCount();

    // 获取指定索引的日志（0 = 最旧）
    static bool getLog(int index, LogEntry &entry);

    // 标记前 N 条日志为已上报并从缓冲区移除
    static void markReported(int count);

    // 清空所有日志（Flash + 内存）
    static void clearAll();

    // 主循环调用，尝试上报日志（网络可用时）
    static void update();

    // 设置网络可用性标志（true 时触发上报）
    static void setNetworkReady(bool ready);

private:
    static LogEntry buffer[LOG_BUFFER_MAX];  // 内存环形缓冲区
    static int writePos;     // 下一个写入位置（0..MAX-1）
    static int bufferCount;  // 当前缓冲数量
    static bool networkReady;
    static unsigned long lastReportTime;

    // 生成当前时间戳
    static String nowTimestamp();

    // 上报一批日志
    static void reportBatch();
};

#endif // LOGGER_H
