/**
 * logger.cpp - 运行期调试日志
 * 不持久化开锁日志，不生成 LOG_REPORT Mesh 流量。
 */
#include "logger.h"
#include "debug.h"

// 静态成员初始化
bool Logger::networkReady = false;
unsigned long Logger::lastReportTime = 0;
bool Logger::awaitingAck = false;
String Logger::reportMsgId = "";
int Logger::reportCount = 0;
unsigned long Logger::reportSentTime = 0;

void Logger::init() {
    networkReady = false;
    lastReportTime = 0;
    awaitingAck = false;
    reportMsgId = "";
    reportCount = 0;
    reportSentTime = 0;
    Debug::println(F("[LOG] Runtime-only logging enabled (no NVS/Flash/SD)"));
}

void Logger::log(const LogEntry &entry) {
    LogEntry e = entry;
    Debug::printf("[LOG] Record: seq=%u user=%s fp=%d lock=%d action=%s result=%s\n",
                  e.log_seq, e.user_id.c_str(), e.fingerprint_id, e.lock_id,
                  e.action.c_str(), e.result.c_str());
}

void Logger::log(const String &userId, int fingerprintId, int lockId,
                 const String &action, const String &result,
                 const String &reason, uint32_t timestamp) {
    LogEntry e;
    e.user_id        = userId;
    e.fingerprint_id = fingerprintId;
    e.lock_id        = lockId;
    e.action         = action;
    e.result         = result;
    e.reason         = reason;
    e.timestamp      = timestamp;
    e.log_seq        = 0;
    log(e);
}

int Logger::getPendingCount() {
    return 0;
}

bool Logger::getLog(int index, LogEntry &entry) {
    (void)index;
    (void)entry;
    return false;
}

void Logger::markReported(int count) {
    (void)count;
}

void Logger::clearAll() {
    Debug::println(F("[LOG] No persistent logs to clear"));
}

void Logger::setNetworkReady(bool ready) {
    networkReady = ready;
}

void Logger::reportBatch() {
}

void Logger::handleReportAck(const String &msgId, const String &result) {
    (void)msgId;
    (void)result;
    awaitingAck = false;
    reportMsgId = "";
    reportCount = 0;
}

void Logger::update() {
}
