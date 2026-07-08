/**
 * message_handler.cpp - 消息处理实现
 * 处理上位机下发的各类命令，以及本机按键/指纹事件状态机
 */
#include "message_handler.h"
#include <ArduinoJson.h>
#include "storage.h"
#include "fingerprint.h"
#include "lock_control.h"
#include "tcp_comm.h"
#include "logger.h"

// 状态机超时时间
#define WAIT_FINGER_TIMEOUT_MS  30000   // 等待指纹超时 30 秒
#define WAIT_AUTH_TIMEOUT_MS    10000   // 等待上位机鉴权超时 10 秒
#define VERIFY_FAIL_MAX         5       // 连续验证失败告警阈值

int MessageHandler::pendingLockId        = -1;
int MessageHandler::pendingFingerprintId = -1;
MessageHandler::VerifyState MessageHandler::state = STATE_IDLE;
unsigned long MessageHandler::stateEnterTime = 0;
int  MessageHandler::enrollFingerprintId = -1;
String MessageHandler::enrollUserId      = "";
int  MessageHandler::verifyFailCount     = 0;

void MessageHandler::init() {
    state = STATE_IDLE;
    pendingLockId = -1;
    pendingFingerprintId = -1;
    stateEnterTime = millis();
    enrollFingerprintId = -1;
    verifyFailCount = 0;
    Serial.println(F("[MSG] 消息处理器初始化完成"));
}

MessageHandler::VerifyState MessageHandler::getState() {
    return state;
}

void MessageHandler::setState(VerifyState s) {
    Serial.printf("[MSG] 状态切换: %d -> %d\n", state, s);
    state = s;
    stateEnterTime = millis();
}

void MessageHandler::onKeyPressed(int lockId) {
    if (state != STATE_IDLE) {
        Serial.printf("[MSG] 忽略按键 %d（当前状态 %d 忙）\n", lockId, state);
        return;
    }
    pendingLockId = lockId;
    setState(STATE_WAIT_FINGER);
    Serial.printf("[MSG] 按键 %d 触发，等待指纹...\n", lockId);
}

void MessageHandler::sendFingerVerify(int fingerprintId) {
    String data = "{\"fingerprint_id\":" + String(fingerprintId) + "}";
    TcpComm::sendMessage("FINGER_VERIFY", data);
}

bool MessageHandler::tryLocalPermission(int fingerprintId, int lockId) {
    // 离线模式：先查本地缓存权限
    UserPermission perm;
    if (Storage::loadPermission(fingerprintId, perm) && perm.valid) {
        if (lockId >= 0 && lockId < LOCK_COUNT && perm.lock_perm[lockId]) {
            // 本地有权限，开锁
            LockControl::openLock(lockId);
            Logger::log(perm.user_id, fingerprintId, lockId,
                        "open", "success", "local_cache");
            Serial.printf("[MSG] 离线开锁成功: user=%s lock=%d\n",
                          perm.user_id.c_str(), lockId);
            return true;
        } else {
            // 本地权限不足
            Logger::log(perm.user_id, fingerprintId, lockId,
                        "open", "fail", "local_no_permission");
            Serial.printf("[MSG] 离线权限不足: user=%s lock=%d\n",
                          perm.user_id.c_str(), lockId);
            return true;  // 已处理（虽失败）
        }
    }
    return false;  // 本地无缓存，需要上位机鉴权
}

void MessageHandler::checkTimeout() {
    unsigned long now = millis();
    if (state == STATE_WAIT_FINGER && (now - stateEnterTime > WAIT_FINGER_TIMEOUT_MS)) {
        Serial.println(F("[MSG] 等待指纹超时，返回空闲"));
        state = STATE_IDLE;
        pendingLockId = -1;
    } else if (state == STATE_WAIT_AUTH && (now - stateEnterTime > WAIT_AUTH_TIMEOUT_MS)) {
        Serial.println(F("[MSG] 等待上位机鉴权超时"));
        // 超时回退到本地缓存权限
        if (pendingFingerprintId >= 0 && pendingLockId >= 0) {
            if (!tryLocalPermission(pendingFingerprintId, pendingLockId)) {
                Logger::log("", pendingFingerprintId, pendingLockId,
                            "open", "fail", "auth_timeout");
            }
        }
        state = STATE_IDLE;
        pendingLockId = -1;
        pendingFingerprintId = -1;
    }
}

void MessageHandler::startEnroll(int fingerprintId, const String &userId) {
    enrollFingerprintId = fingerprintId;
    enrollUserId = userId;
    setState(STATE_ENROLLING);
    Serial.printf("[MSG] 开始录入指纹: id=%d user=%s\n", fingerprintId, userId.c_str());
}

void MessageHandler::update() {
    unsigned long now = millis();

    switch (state) {
        case STATE_WAIT_FINGER: {
            // 轮询指纹模块
            int fpId = Fingerprint::verifyFingerprint();
            if (fpId >= 0) {
                // 匹配成功
                pendingFingerprintId = fpId;
                verifyFailCount = 0;

                // 优先尝试本地缓存权限（离线快速开锁）
                if (pendingLockId >= 0 &&
                    tryLocalPermission(fpId, pendingLockId)) {
                    // 本地已处理（成功或失败），但仍向上位机上报验证记录
                    sendFingerVerify(fpId);
                    state = STATE_IDLE;
                    pendingLockId = -1;
                    pendingFingerprintId = -1;
                } else {
                    // 本地无缓存，发送验证请求给上位机
                    sendFingerVerify(fpId);
                    setState(STATE_WAIT_AUTH);
                }
            } else if (fpId == -2) {
                // 读取错误（非未匹配），不立即退出，继续等待
            }
            // fpId == -1 表示无手指，继续等待
            break;
        }

        case STATE_WAIT_AUTH:
            // 等待上位机 AUTH_OK / AUTH_FAIL，由 handleIncoming 处理
            checkTimeout();
            break;

        case STATE_ENROLLING: {
            // 执行指纹录入（阻塞式，由命令触发）
            bool ok = Fingerprint::enrollFingerprint(enrollFingerprintId);
            String data = "{\"fingerprint_id\":" + String(enrollFingerprintId) +
                          ",\"user_id\":\"" + enrollUserId + "\",\"result\":\"" +
                          (ok ? "success" : "fail") + "\"}";
            TcpComm::sendMessage("ADD_FINGERPRINT_RESULT", data);

            if (ok) {
                // 更新指纹计数
                DeviceConfig cfg;
                Storage::loadDeviceConfig(cfg);
                cfg.fingerprint_count = Fingerprint::getFingerprintCount();
                Storage::saveDeviceConfig(cfg);
            }
            state = STATE_IDLE;
            enrollFingerprintId = -1;
            enrollUserId = "";
            break;
        }

        case STATE_IDLE:
        default:
            // 空闲，检查是否需要重置失败计数
            break;
    }
}

// ====== 命令处理 ======
void MessageHandler::handleIncoming(const String &message) {
    // 使用 ArduinoJson 解析（堆分配，避免 ESP32 栈溢出）
    DynamicJsonDocument doc(2048);
    DeserializationError err = deserializeJson(doc, message);
    if (err) {
        Serial.printf("[MSG] JSON 解析失败: %s\n", err.c_str());
        return;
    }

    const char *cmd = doc["cmd"] | "";
    if (strlen(cmd) == 0) {
        Serial.println(F("[MSG] 消息缺少 cmd 字段"));
        return;
    }

    String dataStr = "";
    if (doc.containsKey("data")) {
        // 将 data 重新序列化为字符串
        JsonObject dataObj = doc["data"].as<JsonObject>();
        serializeJson(dataObj, dataStr);
    }

    Serial.printf("[MSG] 处理命令: %s\n", cmd);

    if (strcmp(cmd, "AUTH_OK") == 0) {
        cmdAuthOk(dataStr);
    } else if (strcmp(cmd, "AUTH_FAIL") == 0) {
        cmdAuthFail(dataStr);
    } else if (strcmp(cmd, "SYNC_PERMISSIONS") == 0) {
        cmdSyncPermissions(dataStr);
    } else if (strcmp(cmd, "ADD_FINGERPRINT") == 0) {
        cmdAddFingerprint(dataStr);
    } else if (strcmp(cmd, "DELETE_FINGERPRINT") == 0) {
        cmdDeleteFingerprint(dataStr);
    } else if (strcmp(cmd, "CONTROL_LOCK") == 0) {
        cmdControlLock(dataStr);
    } else if (strcmp(cmd, "READ_CONFIG") == 0) {
        cmdReadConfig();
    } else if (strcmp(cmd, "WRITE_CONFIG") == 0) {
        cmdWriteConfig(dataStr);
    } else if (strcmp(cmd, "READ_STATUS") == 0) {
        cmdReadStatus();
    } else if (strcmp(cmd, "CLEAR_LOGS") == 0) {
        cmdClearLogs();
    } else if (strcmp(cmd, "REBOOT") == 0) {
        cmdReboot(dataStr);
    } else if (strcmp(cmd, "HEARTBEAT_ACK") == 0) {
        // 心跳回应，无需处理
    } else {
        Serial.printf("[MSG] 未知命令: %s\n", cmd);
    }
}

void MessageHandler::cmdAuthOk(const String &data) {
    // 解析权限数据
    StaticJsonDocument<512> doc;
    if (deserializeJson(doc, data)) {
        Serial.println(F("[MSG] AUTH_OK data 解析失败"));
        return;
    }

    // 缓存权限到 Flash
    UserPermission perm;
    perm.fingerprint_id = pendingFingerprintId;
    perm.user_id    = doc["user_id"] | "";
    perm.name       = doc["name"] | "";
    perm.role       = (UserRole)(doc["role"] | (int)ROLE_STUDENT);

    JsonObject perms = doc["permissions"].as<JsonObject>();
    perm.lock_perm[0] = perms["lock_0"] | false;
    perm.lock_perm[1] = perms["lock_1"] | false;
    perm.lock_perm[2] = perms["lock_2"] | false;
    perm.lock_perm[3] = perms["lock_3"] | false;
    perm.valid = true;

    Storage::savePermission(perm);
    Serial.printf("[MSG] 权限已缓存: user=%s role=%d perm=[%d,%d,%d,%d]\n",
                  perm.user_id.c_str(), perm.role,
                  perm.lock_perm[0], perm.lock_perm[1],
                  perm.lock_perm[2], perm.lock_perm[3]);

    // 根据按键请求开锁
    if (pendingLockId >= 0 && pendingLockId < LOCK_COUNT) {
        if (perm.lock_perm[pendingLockId]) {
            LockControl::openLock(pendingLockId);
            Logger::log(perm.user_id, pendingFingerprintId, pendingLockId,
                        "open", "success", "");
            Serial.printf("[MSG] 鉴权通过，开锁 %d\n", pendingLockId);
        } else {
            Logger::log(perm.user_id, pendingFingerprintId, pendingLockId,
                        "open", "fail", "no_permission");
            Serial.printf("[MSG] 权限不足，无法开锁 %d\n", pendingLockId);
        }
    }

    state = STATE_IDLE;
    pendingLockId = -1;
    pendingFingerprintId = -1;
    verifyFailCount = 0;
}

void MessageHandler::cmdAuthFail(const String &data) {
    StaticJsonDocument<256> doc;
    String reason = "用户不存在或权限不足";
    if (!deserializeJson(doc, data)) {
        // ArduinoJson 的 | 运算符支持 const char* 默认值，这里显式处理 String
        const char* r = doc["reason"] | "";
        if (strlen(r) > 0) {
            reason = String(r);
        }
    }
    Serial.printf("[MSG] 鉴权失败: %s\n", reason.c_str());
    Logger::log("", pendingFingerprintId, pendingLockId,
                "open", "fail", reason);

    verifyFailCount++;
    if (verifyFailCount >= VERIFY_FAIL_MAX) {
        // 多次失败告警
        TcpComm::sendMessage("ALARM", "{\"type\":\"verify_fail_too_many\",\"count\":" +
                             String(verifyFailCount) + "}");
        verifyFailCount = 0;
    }

    state = STATE_IDLE;
    pendingLockId = -1;
    pendingFingerprintId = -1;
}

void MessageHandler::cmdSyncPermissions(const String &data) {
    // data: {"users":[{...},...]}，权限列表可能较大，使用堆分配
    DynamicJsonDocument doc(4096);
    if (deserializeJson(doc, data)) {
        Serial.println(F("[MSG] SYNC_PERMISSIONS 解析失败"));
        return;
    }
    JsonArray users = doc["users"].as<JsonArray>();
    int count = 0;
    for (JsonObject user : users) {
        UserPermission perm;
        perm.fingerprint_id = user["fingerprint_id"] | -1;
        if (perm.fingerprint_id < 0) continue;
        perm.user_id = user["user_id"] | "";
        perm.name    = user["name"] | "";
        perm.role    = (UserRole)(user["role"] | (int)ROLE_STUDENT);

        JsonObject lp = user["lock_permissions"].as<JsonObject>();
        perm.lock_perm[0] = lp["lock_0"] | false;
        perm.lock_perm[1] = lp["lock_1"] | false;
        perm.lock_perm[2] = lp["lock_2"] | false;
        perm.lock_perm[3] = lp["lock_3"] | false;
        perm.valid = true;
        Storage::savePermission(perm);
        count++;
    }
    Serial.printf("[MSG] 已同步 %d 条权限\n", count);
    TcpComm::sendMessage("SYNC_ACK", "{\"count\":" + String(count) + "}");
}

void MessageHandler::cmdAddFingerprint(const String &data) {
    StaticJsonDocument<256> doc;
    if (deserializeJson(doc, data)) {
        Serial.println(F("[MSG] ADD_FINGERPRINT 解析失败"));
        return;
    }
    int fpId = doc["fingerprint_id"] | -1;
    String userId = doc["user_id"] | "";
    if (fpId < 0) {
        TcpComm::sendMessage("ADD_FINGERPRINT_RESULT",
                             "{\"result\":\"fail\",\"reason\":\"invalid_id\"}");
        return;
    }
    startEnroll(fpId, userId);
}

void MessageHandler::cmdDeleteFingerprint(const String &data) {
    StaticJsonDocument<256> doc;
    if (deserializeJson(doc, data)) {
        Serial.println(F("[MSG] DELETE_FINGERPRINT 解析失败"));
        return;
    }
    int fpId = doc["fingerprint_id"] | -1;
    bool ok = false;
    if (fpId >= 0) {
        ok = Fingerprint::deleteFingerprint(fpId);
        Storage::deletePermission(fpId);
    }
    String result = ok ? "success" : "fail";
    TcpComm::sendMessage("DELETE_FINGERPRINT_RESULT",
                         "{\"fingerprint_id\":" + String(fpId) +
                         ",\"result\":\"" + result + "\"}");

    // 更新指纹计数
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    cfg.fingerprint_count = Fingerprint::getFingerprintCount();
    Storage::saveDeviceConfig(cfg);
}

void MessageHandler::cmdControlLock(const String &data) {
    StaticJsonDocument<256> doc;
    if (deserializeJson(doc, data)) {
        Serial.println(F("[MSG] CONTROL_LOCK 解析失败"));
        return;
    }
    int lockId = doc["lock_id"] | -1;
    String action = doc["action"] | "open";
    if (lockId < 0 || lockId >= LOCK_COUNT) {
        Serial.printf("[MSG] CONTROL_LOCK 锁编号无效: %d\n", lockId);
        return;
    }
    if (action == "open") {
        LockControl::openLock(lockId);
        Logger::log("remote", -1, lockId, "open", "success", "remote_control");
    } else {
        LockControl::closeLock(lockId);
        Logger::log("remote", -1, lockId, "close", "success", "remote_control");
    }
    Serial.printf("[MSG] 远程控制锁 %d %s\n", lockId, action.c_str());
}

void MessageHandler::cmdReadConfig() {
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    String data = "{";
    data += "\"device_id\":\"" + cfg.device_id + "\",";
    data += "\"device_name\":\"" + cfg.device_name + "\",";
    data += "\"wifi_ssid\":\"" + cfg.wifi_ssid + "\",";
    data += "\"wifi_password\":\"" + cfg.wifi_password + "\",";
    data += "\"server_ip\":\"" + cfg.server_ip + "\",";
    data += "\"server_port\":" + String(cfg.server_port) + ",";
    data += "\"work_mode\":\"" + String(cfg.work_mode == MODE_AP ? "ap" : "sta") + "\",";
    data += "\"fingerprint_count\":" + String(cfg.fingerprint_count);
    data += "}";
    TcpComm::sendMessage("CONFIG_RESPONSE", data);
}

void MessageHandler::cmdWriteConfig(const String &data) {
    StaticJsonDocument<512> doc;
    if (deserializeJson(doc, data)) {
        Serial.println(F("[MSG] WRITE_CONFIG 解析失败"));
        return;
    }
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);

    if (doc.containsKey("device_id"))   cfg.device_id = doc["device_id"].as<String>();
    if (doc.containsKey("device_name")) cfg.device_name = doc["device_name"].as<String>();
    if (doc.containsKey("wifi_ssid"))   cfg.wifi_ssid = doc["wifi_ssid"].as<String>();
    if (doc.containsKey("wifi_password")) cfg.wifi_password = doc["wifi_password"].as<String>();
    if (doc.containsKey("server_ip"))   cfg.server_ip = doc["server_ip"].as<String>();
    if (doc.containsKey("server_port")) cfg.server_port = doc["server_port"].as<unsigned int>();
    if (doc.containsKey("work_mode")) {
        String m = doc["work_mode"].as<String>();
        cfg.work_mode = (m == "ap") ? MODE_AP : MODE_STA;
    }

    Storage::saveDeviceConfig(cfg);
    TcpComm::sendMessage("CONFIG_SAVED", "{\"result\":\"success\"}");
    Serial.println(F("[MSG] 配置已更新"));
}

void MessageHandler::cmdReadStatus() {
    bool lockStatus[LOCK_COUNT];
    LockControl::getLockStatus(lockStatus);

    String data = "{";
    data += "\"uptime\":" + String(millis() / 1000) + ",";
    data += "\"lock_status\":[" + String(lockStatus[0] ? 1 : 0) + "," +
            String(lockStatus[1] ? 1 : 0) + "," +
            String(lockStatus[2] ? 1 : 0) + "," +
            String(lockStatus[3] ? 1 : 0) + "],";
    data += "\"log_count\":" + String(Logger::getPendingCount()) + ",";
    data += "\"fingerprint_count\":" + String(Fingerprint::getFingerprintCount()) + ",";
    data += "\"work_mode\":\"" + String(Storage::loadWorkMode() == MODE_AP ? "ap" : "sta") + "\"";
    data += "}";
    TcpComm::sendMessage("STATUS_RESPONSE", data);
}

void MessageHandler::cmdClearLogs() {
    Logger::clearAll();
    TcpComm::sendMessage("LOGS_CLEARED", "{\"result\":\"success\"}");
}

void MessageHandler::cmdReboot(const String &data) {
    StaticJsonDocument<128> doc;
    String mode = "";
    if (!deserializeJson(doc, data)) {
        mode = doc["mode"] | "";
    }
    Serial.printf("[MSG] 准备重启，目标模式: %s\n", mode.c_str());
    TcpComm::sendMessage("REBOOT_ACK", "{\"result\":\"rebooting\"}");
    delay(500);
    if (mode == "ap") {
        Storage::saveWorkMode(MODE_AP);
    } else if (mode == "sta") {
        Storage::saveWorkMode(MODE_STA);
    }
    ESP.restart();
}
