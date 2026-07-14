/**
 * logger.cpp - 离线日志系统实现（V2.0 适配 Flash 环形缓冲）
 * 日志持久化于 Flash 环形缓冲，断电不丢失
 * 网络恢复后批量上报：每 10 秒最多 20 条，通过 MeshComm 发送
 */
#include "logger.h"
#include "storage.h"
#include "mesh_comm.h"

// 静态成员初始化
bool Logger::networkReady = false;
unsigned long Logger::lastReportTime = 0;

void Logger::init() {
    networkReady = false;
    lastReportTime = 0;
    // Flash 日志指针已在 Storage::begin() 中加载
    int stored = Storage::getLogCount();
    Serial.printf("[LOG] 初始化完成，Flash 中待上报日志 %d 条（容量 %d）\n",
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

    Serial.printf("[LOG] 记录: seq=%u user=%s fp=%d lock=%d action=%s result=%s\n",
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
    Serial.printf("[LOG] 标记 %d 条已上报，剩余 %d 条\n", count, Storage::getLogCount());
}

void Logger::clearAll() {
    Storage::clearLogs();
    Serial.println(F("[LOG] 已清空所有日志"));
}

void Logger::setNetworkReady(bool ready) {
    networkReady = ready;
    if (ready) {
        Serial.println(F("[LOG] 网络就绪，准备批量上报日志"));
    }
}

void Logger::reportBatch() {
    if (!networkReady || !MeshComm::isConnected()) {
        return;
    }
    int pending = Storage::getLogCount();
    if (pending == 0) return;

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

    Serial.printf("[LOG] 上报 %d 条日志...\n", actualReported);
    if (MeshComm::sendMessage("LOG_REPORT", data)) {
        // 上报成功，标记已上报（移动 Flash start 指针并保存 NVS）
        markReported(actualReported);
        Serial.printf("[LOG] 上报成功，已标记 %d 条\n", actualReported);
    } else {
        Serial.println(F("[LOG] 上报失败，下次重试"));
    }
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
