/**
 * logger.h - 日志系统（V2.x 改造版，需求 9）
 * 需求 9：柜子不保存日志，只发出。
 * 本模块不再写本地 Flash 环形缓冲，log() 直接构造 LOG_REPORT 消息
 * 通过 Mesh 发送给根节点/上位机；网络不可用时直接丢弃（不强求能真正被记录）。
 * 保留日志构造逻辑与对外接口（getPendingCount/clearAll 等）以维持兼容。
 */
#ifndef LOGGER_H
#define LOGGER_H

#include <Arduino.h>
#include "config.h"

class Logger {
public:
    // 初始化日志模块（不再加载 Flash 指针，仅复位本地状态）
    static void init();

    // 记录一条日志（需求 9：不写 Flash，直接构造 LOG_REPORT 通过 Mesh 发出）
    // 网络不可用时丢弃该日志
    static void log(const String &userId, int fingerprintId, int lockId,
                    const String &action, const String &result,
                    const String &reason, uint32_t timestamp = 0);

    // 记录一条日志（直接传 LogEntry）
    static void log(const LogEntry &entry);

    // 获取待上报日志数量（需求 9 改造后不再缓存，始终返回 0）
    static int getPendingCount();

    // 获取指定索引的日志（不再持久化，始终返回 false）
    static bool getLog(int index, LogEntry &entry);

    // 标记前 N 条日志为已上报（不再持久化，空操作）
    static void markReported(int count);

    // 清空所有日志（不再持久化，空操作；保持接口供 CLEAR_LOGS 命令调用）
    static void clearAll();

    // 主循环调用（需求 9 改造后日志即时发出，无需批量上报，保留空实现以兼容主循环）
    static void update();

    // 设置网络可用性标志（保留接口；log() 会即时检测 MeshComm 连接状态）
    static void setNetworkReady(bool ready);

private:
    static bool networkReady;
    // 自增日志序号（仅用于上报字段，不持久化）
    static uint32_t logSeqCounter;

    // 即时上报一条日志（构造 LOG_REPORT 并通过 MeshComm 发送）
    static void reportOne(const LogEntry &entry);
};

#endif // LOGGER_H
