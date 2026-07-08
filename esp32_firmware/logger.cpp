/**
 * logger.cpp - 本地日志缓存实现
 */
#include "logger.h"
#include "storage.h"
#include "tcp_comm.h"

LogEntry Logger::buffer[LOG_BUFFER_MAX];
int Logger::writePos    = 0;
int Logger::bufferCount = 0;
bool Logger::networkReady = false;
unsigned long Logger::lastReportTime = 0;

void Logger::init() {
    writePos     = 0;
    bufferCount  = 0;
    networkReady = false;
    lastReportTime = 0;

    // 从 Flash 加载历史日志到内存缓冲区（用于断电恢复）
    int stored = Storage::getLogCount();
    for (int i = 0; i < stored && i < LOG_BUFFER_MAX; i++) {
        if (Storage::readLog(i, buffer[i])) {
            bufferCount++;
        } else {
            break;
        }
    }
    writePos = bufferCount % LOG_BUFFER_MAX;
    Serial.printf("[LOG] 初始化完成，已加载 %d 条历史日志\n", bufferCount);
}

String Logger::nowTimestamp() {
    struct tm timeinfo;
    if (getLocalTime(&timeinfo, 0)) {
        char buf[32];
        strftime(buf, sizeof(buf), "%Y-%m-%d %H:%M:%S", &timeinfo);
        return String(buf);
    }
    // 回退：使用开机时间
    unsigned long ms = millis();
    unsigned long sec = ms / 1000;
    char buf[32];
    snprintf(buf, sizeof(buf), "uptime-%lus", sec);
    return String(buf);
}

void Logger::log(const LogEntry &entry) {
    LogEntry e = entry;
    if (e.timestamp.length() == 0) {
        e.timestamp = nowTimestamp();
    }
    // 写入内存环形缓冲区：writePos 始终指向下一个写入位置
    // 缓冲区满时覆盖最旧的一条（环形）
    buffer[writePos] = e;
    writePos = (writePos + 1) % LOG_BUFFER_MAX;
    if (bufferCount < LOG_BUFFER_MAX) {
        bufferCount++;
    }
    // 同时写入 Flash 持久化
    Storage::appendLog(e);

    Serial.printf("[LOG] 记录: user=%s fp=%d lock=%d action=%s result=%s\n",
                  e.user_id.c_str(), e.fingerprint_id, e.lock_id,
                  e.action.c_str(), e.result.c_str());
}

void Logger::log(const String &userId, int fingerprintId, int lockId,
                 const String &action, const String &result,
                 const String &reason, const String &timestamp) {
    LogEntry e;
    e.user_id        = userId;
    e.fingerprint_id = fingerprintId;
    e.lock_id        = lockId;
    e.action         = action;
    e.result         = result;
    e.reason         = reason;
    e.timestamp      = timestamp;
    log(e);
}

int Logger::getPendingCount() {
    return bufferCount;
}

bool Logger::getLog(int index, LogEntry &entry) {
    if (index < 0 || index >= bufferCount) return false;
    // 动态计算最旧条目的物理索引：writePos 为下一个写入位置，
    // 最旧条目位于 (writePos - bufferCount) 处，加上 index 偏移
    int actualIdx = (writePos - bufferCount + index + LOG_BUFFER_MAX) % LOG_BUFFER_MAX;
    entry = buffer[actualIdx];
    return true;
}

void Logger::markReported(int count) {
    if (count <= 0) return;
    if (count > bufferCount) count = bufferCount;

    // 无需物理移位：直接减少计数即可，getLog 的动态索引会自动跳过已上报条目
    bufferCount -= count;

    // 同步更新 Flash：bufferCount 归零时清空 Flash 日志，避免历史残留
    if (bufferCount == 0) {
        Storage::clearLogs();
    }
    Serial.printf("[LOG] 已上报 %d 条日志，剩余 %d 条\n", count, bufferCount);
}

void Logger::clearAll() {
    bufferCount = 0;
    writePos    = 0;
    Storage::clearLogs();
    Serial.println(F("[LOG] 已清空所有日志"));
}

void Logger::setNetworkReady(bool ready) {
    networkReady = ready;
    if (ready) {
        Serial.println(F("[LOG] 网络就绪，准备上报日志"));
    }
}

void Logger::reportBatch() {
    if (bufferCount == 0 || !networkReady || !TcpComm::isConnected()) {
        return;
    }
    // 每次最多上报 10 条
    int batch = (bufferCount > 10) ? 10 : bufferCount;

    // 构造批量日志上报 JSON
    String data = "{\"logs\":[";
    for (int i = 0; i < batch; i++) {
        LogEntry e;
        if (!getLog(i, e)) break;
        if (i > 0) data += ",";
        data += "{\"user_id\":\"" + e.user_id + "\",";
        data += "\"fingerprint_id\":" + String(e.fingerprint_id) + ",";
        data += "\"lock_id\":" + String(e.lock_id) + ",";
        data += "\"action\":\"" + e.action + "\",";
        data += "\"result\":\"" + e.result + "\",";
        data += "\"reason\":\"" + e.reason + "\",";
        data += "\"time\":\"" + e.timestamp + "\"}";
    }
    data += "]}";

    if (TcpComm::sendMessage("LOG_REPORT", data)) {
        markReported(batch);
    }
}

void Logger::update() {
    if (!networkReady || !TcpComm::isConnected()) return;
    if (bufferCount == 0) return;

    unsigned long now = millis();
    // 每 5 秒上报一批
    if (now - lastReportTime >= 5000 || lastReportTime == 0) {
        lastReportTime = now;
        reportBatch();
    }
}
