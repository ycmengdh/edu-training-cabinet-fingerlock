/**
 * logger.cpp - 日志系统实现（V2.x 改造版，需求 9）
 * 需求 9：柜子不保存日志，只发出。
 * 改造点：
 *   - 删除 Flash 环形缓冲写入逻辑（不再调用 Storage::appendLog）
 *   - log() 改为构造 LOG_REPORT 消息直接通过 Mesh 发送给根节点/上位机
 *   - 网络不可用时丢弃日志（不强求能真正被记录）
 *   - 保留日志构造逻辑与对外接口以维持兼容
 */
#include "logger.h"
#include "storage.h"
#include "mesh_comm.h"

// 静态成员初始化
bool     Logger::networkReady  = false;
uint32_t Logger::logSeqCounter = 0;

void Logger::init() {
    networkReady  = false;
    logSeqCounter = 0;
    // 需求 9：不再加载 Flash 日志指针，本地不持久化任何日志
    Serial.println(F("[LOG] 初始化完成（仅网络上报模式，不写本地 Flash）"));
}

void Logger::log(const LogEntry &entry) {
    LogEntry e = entry;
    // 填充时间戳（未提供时使用系统 Unix 时间）
    if (e.timestamp == 0) {
        e.timestamp = Storage::getUnixTime();
    }
    // 分配日志序号（仅内存自增，不持久化）
    logSeqCounter++;
    e.log_seq = logSeqCounter;

    Serial.printf("[LOG] 记录: seq=%u user=%s fp=%d lock=%d action=%s result=%s\n",
                  e.log_seq, e.user_id.c_str(), e.fingerprint_id, e.lock_id,
                  e.action.c_str(), e.result.c_str());

    // 需求 9：直接通过网络上报，不写本地 Flash
    reportOne(e);
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
    e.log_seq        = 0;  // 由 log(LogEntry) 自动分配
    log(e);
}

// 即时上报一条日志：构造 LOG_REPORT 消息（保持原批量格式 {"logs":[...]}，单条入数组）
// 网络不可用则丢弃
void Logger::reportOne(const LogEntry &entry) {
    if (!MeshComm::isConnected()) {
        // 需求 9：网络不可用直接丢弃，不强求能真正被记录
        Serial.println(F("[LOG] 网络不可用，日志丢弃"));
        return;
    }

    // 构造单条日志 JSON，沿用原 LOG_REPORT 的批量格式以便上位机兼容解析
    String data = "{\"logs\":[{";
    data += "\"log_seq\":" + String(entry.log_seq) + ",";
    data += "\"user_id\":\"" + entry.user_id + "\",";
    data += "\"fingerprint_id\":" + String(entry.fingerprint_id) + ",";
    data += "\"lock_id\":" + String(entry.lock_id) + ",";
    data += "\"action\":\"" + entry.action + "\",";
    data += "\"result\":\"" + entry.result + "\",";
    data += "\"reason\":\"" + entry.reason + "\",";
    data += "\"time\":" + String(entry.timestamp) + "}]}";

    if (MeshComm::sendMessage("LOG_REPORT", data)) {
        Serial.printf("[LOG] 已上报 seq=%u\n", entry.log_seq);
    } else {
        // 发送失败也丢弃（不写本地 Flash）
        Serial.println(F("[LOG] LOG_REPORT 发送失败，日志丢弃"));
    }
}

int Logger::getPendingCount() {
    // 需求 9 改造后不再缓存，无待上报日志
    return 0;
}

bool Logger::getLog(int index, LogEntry &entry) {
    // 不再持久化，无历史日志可读
    (void)index;
    (void)entry;
    return false;
}

void Logger::markReported(int count) {
    // 不再持久化，空操作（保留接口兼容）
    (void)count;
}

void Logger::clearAll() {
    // 需求 9：不再写本地 Flash，无缓存可清，仅打印日志
    Serial.println(F("[LOG] clearAll（仅网络模式，无本地缓存可清）"));
}

void Logger::setNetworkReady(bool ready) {
    networkReady = ready;
    if (ready) {
        Serial.println(F("[LOG] 网络就绪（即时上报模式）"));
    }
}

void Logger::update() {
    // 需求 9 改造后日志在 log() 中即时发出，无需批量上报调度。
    // 保留空实现以兼容主循环调用。
}
