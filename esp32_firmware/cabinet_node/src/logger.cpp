/**
 * logger.cpp - 离线日志系统实现（V2.0 适配 Flash 环形缓冲）
 * 日志持久化于 Flash 环形缓冲，断电不丢失
 * 网络恢复后批量上报：每 10 秒最多 20 条，通过 MeshComm 发送
 */
#include "logger.h"
#include "debug.h"
#include "storage.h"
#include "mesh_comm.h"

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
    // Flash 日志指针已在 Storage::begin() 中加载
    int stored = Storage::getLogCount();
    Debug::printf("[LOG] Init done, %d logs pending report in Flash (capacity %d)\n",
                  stored, Storage::getLogCapacity());
}

void Logger::log(const LogEntry &entry) {
    LogEntry e = entry;
    // 填充时间戳（未提供时使用系统 Unix 时间）
    if (e.timestamp == 0) {
        e.timestamp = Storage::getUnixTime();
    }
    // 写入 Flash 环形缓冲（自动分配 log_seq）
    Storage::appendLog(e);

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
    e.log_seq        = 0;  // 由 Storage::appendLog 自动分配
    log(e);
}

int Logger::getPendingCount() {
    return Storage::getLogCount();
}

bool Logger::getLog(int index, LogEntry &entry) {
    return Storage::readLog(index, entry);
}

void Logger::markReported(int count) {
    // 委托给 Storage 移动 start 指针并立即保存到 NVS（已修复原版 bug）
    Storage::markLogsReported(count);
    Debug::printf("[LOG] Marked %d logs reported, %d remaining\n", count, Storage::getLogCount());
}

void Logger::clearAll() {
    Storage::clearLogs();
    Debug::println(F("[LOG] All logs cleared"));
}

void Logger::setNetworkReady(bool ready) {
    networkReady = ready;
    if (ready) {
        Debug::println(F("[LOG] Network ready, preparing batch log report"));
    }
}

void Logger::reportBatch() {
    if (!networkReady || !MeshComm::isConnected()) {
        return;
    }
    int pending = Storage::getLogCount();
    if (pending == 0) return;

    if (awaitingAck) {
        if (millis() - reportSentTime < 15000) return;
        Debug::println(F("[LOG] report ACK timeout, retrying batch"));
        awaitingAck = false;
        reportMsgId = "";
        reportCount = 0;
    }

    // 每批最多上报 LOG_REPORT_BATCH_MAX 条
    int batch = (pending > LOG_REPORT_BATCH_MAX) ? LOG_REPORT_BATCH_MAX : pending;

    // 构造批量日志上报 JSON
    String data = "{\"logs\":[";
    int actualReported = 0;
    for (int i = 0; i < batch; i++) {
        LogEntry e;
        if (!Storage::readLog(i, e)) break;
        if (i > 0) data += ",";
        data += "{\"log_seq\":" + String(e.log_seq) + ",";
        data += "\"user_id\":\"" + e.user_id + "\",";
        data += "\"fingerprint_id\":" + String(e.fingerprint_id) + ",";
        data += "\"lock_id\":" + String(e.lock_id) + ",";
        data += "\"action\":\"" + e.action + "\",";
        data += "\"result\":\"" + e.result + "\",";
        data += "\"reason\":\"" + e.reason + "\",";
        data += "\"time\":" + String(e.timestamp) + "}";
        actualReported++;
    }
    data += "]}";

    if (actualReported == 0) return;

    Debug::printf("[LOG] Reporting %d logs...\n", actualReported);
    String msgId = "logbatch-" + String(millis(), HEX);
    if (MeshComm::sendMessage("LOG_REPORT", data, msgId)) {
        // Do not delete local logs until Root confirms the SD write.
        awaitingAck = true;
        reportMsgId = msgId;
        reportCount = actualReported;
        reportSentTime = millis();
        Debug::printf("[LOG] Report sent, waiting for Root ACK (%d logs)\n", actualReported);
    } else {
        Debug::println(F("[LOG] Report failed, retry next time"));
    }
}

void Logger::handleReportAck(const String &msgId, const String &result) {
    if (!awaitingAck || msgId != reportMsgId) return;
    bool ok = result == "success" || result == "ok" || result == "OK";
    if (ok) {
        markReported(reportCount);
        Debug::printf("[LOG] Root persisted batch, marked %d logs\n", reportCount);
    } else {
        Debug::println(F("[LOG] Root rejected batch, retaining logs"));
    }
    awaitingAck = false;
    reportMsgId = "";
    reportCount = 0;
}

void Logger::update() {
    if (!networkReady) return;
    if (!MeshComm::isConnected()) return;
    if (Storage::getLogCount() == 0) return;

    unsigned long now = millis();
    // 每 LOG_REPORT_INTERVAL_MS（10秒）上报一批
    if (now - lastReportTime >= LOG_REPORT_INTERVAL_MS || lastReportTime == 0) {
        lastReportTime = now;
        reportBatch();
    }
}
