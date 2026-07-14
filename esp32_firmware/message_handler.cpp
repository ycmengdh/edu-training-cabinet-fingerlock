/**
 * message_handler.cpp - 消息处理实现（V2.0 Mesh版本）
 * 保留原状态机骨架，传 JsonObject 引用避免两次解析
 * 新增 ACK 确认机制（msg_id 原样回传）和错误码处理
 * 适配新命令：REGISTER/TIME_SYNC/PERM_LOST/LOG_REPORT_ACK/SYNC_PERMISSIONS
 */
#include "message_handler.h"
#include "storage.h"
#include "fingerprint.h"
#include "lock_control.h"
#include "mesh_comm.h"
#include "logger.h"
#include "sd_storage.h"

// 状态机超时时间
#define WAIT_FINGER_TIMEOUT_MS  30000   // 等待指纹超时 30 秒
#define WAIT_AUTH_TIMEOUT_MS    10000   // 等待上位机鉴权超时 10 秒
#define VERIFY_FAIL_MAX         5       // 连续验证失败告警阈值
#define PERM_LOST_REPORT_INTERVAL 60000 // PERM_LOST 上报间隔 60 秒

// 2000-01-01 00:00:00 的 Unix 时间戳
#define UNIX_2000_01_01  946684800UL

// 静态成员初始化
int MessageHandler::pendingLockId        = -1;
int MessageHandler::pendingFingerprintId = -1;
MessageHandler::VerifyState MessageHandler::state = STATE_IDLE;
unsigned long MessageHandler::stateEnterTime = 0;
int  MessageHandler::enrollFingerprintId = -1;
String MessageHandler::enrollUserId      = "";
int  MessageHandler::verifyFailCount     = 0;
bool MessageHandler::permLostPending     = false;
unsigned long MessageHandler::lastPermLostReport = 0;

// 需求 2：10 秒开锁窗口状态初始化
bool MessageHandler::windowAllowedLocks[LOCK_COUNT] = {false, false, false, false};
int  MessageHandler::windowSeconds        = UNLOCK_WINDOW_SECONDS;
unsigned long MessageHandler::windowEnterTime   = 0;
String MessageHandler::windowUserId       = "";
int  MessageHandler::windowFingerprintId  = -1;

void MessageHandler::init() {
    state = STATE_IDLE;
    pendingLockId = -1;
    pendingFingerprintId = -1;
    stateEnterTime = millis();
    enrollFingerprintId = -1;
    verifyFailCount = 0;

    // 检查权限数据是否丢失（启动时 CRC 都失败）
    if (Storage::isPermissionLost()) {
        permLostPending = true;
        Serial.println(F("[MSG] 权限数据丢失，待上报 PERM_LOST"));
    }

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
    // 需求 2：开锁窗口期内按按钮直接开对应锁（可多次开锁），无需再次验证指纹
    if (state == STATE_UNLOCK_WINDOW) {
        if (lockId < 0 || lockId >= LOCK_COUNT) {
            return;
        }
        // 开锁前检查该锁是否有权限（allowed_locks）
        if (windowAllowedLocks[lockId]) {
            LockControl::openLock(lockId);  // 窗口期已启用权限校验，有权限才会真正开锁
            Logger::log(windowUserId, windowFingerprintId, lockId,
                        "open", "success", "unlock_window");
            Serial.printf("[MSG] 窗口期内开锁 %d（用户 %s）\n",
                          lockId, windowUserId.c_str());
        } else {
            Logger::log(windowUserId, windowFingerprintId, lockId,
                        "open", "fail", "no_permission_window");
            Serial.printf("[MSG] 窗口期内开锁 %d 被拒（无权限）\n", lockId);
        }
        return;  // 不切换状态，继续留在开锁窗口期
    }

    if (state != STATE_IDLE) {
        Serial.printf("[MSG] 忽略按键 %d（当前状态 %d 忙）\n", lockId, state);
        return;
    }
    pendingLockId = lockId;
    setState(STATE_WAIT_FINGER);
    Serial.printf("[MSG] 按键 %d 触发，等待指纹...\n", lockId);
}

// 需求 2：进入 10 秒开锁窗口
void MessageHandler::enterUnlockWindow(const bool allowed[LOCK_COUNT], int windowSec,
                                       const String &userId, int fingerprintId) {
    for (int i = 0; i < LOCK_COUNT; i++) {
        windowAllowedLocks[i] = allowed[i];
    }
    windowSeconds       = (windowSec > 0) ? windowSec : UNLOCK_WINDOW_SECONDS;
    windowEnterTime     = millis();
    windowUserId        = userId;
    windowFingerprintId = fingerprintId;

    // 启用 LockControl 窗口期权限校验
    LockControl::setAllowedLocks(windowAllowedLocks);
    // 点亮有权限的锁对应指示灯
    for (int i = 0; i < LOCK_COUNT; i++) {
        LockControl::setPermissionLed(i, windowAllowedLocks[i]);
    }

    setState(STATE_UNLOCK_WINDOW);
    Serial.printf("[MSG] 进入开锁窗口 %d 秒，用户=%s allowed=[%d,%d,%d,%d]\n",
                  windowSeconds, userId.c_str(),
                  windowAllowedLocks[0], windowAllowedLocks[1],
                  windowAllowedLocks[2], windowAllowedLocks[3]);
}

// 需求 2：退出 10 秒开锁窗口
void MessageHandler::exitUnlockWindow() {
    // 关闭全部权限指示灯
    LockControl::clearAllPermissionLeds();
    // 清除窗口期权限校验
    LockControl::clearAllowedLocks();
    for (int i = 0; i < LOCK_COUNT; i++) {
        windowAllowedLocks[i] = false;
    }
    windowUserId       = "";
    windowFingerprintId = -1;
    Serial.println(F("[MSG] 退出开锁窗口"));
    setState(STATE_IDLE);
}

// ====== 消息发送（通过 MeshComm） ======
bool MessageHandler::sendMessage(const String &cmd, const String &dataJson,
                                 const String &msgId) {
    return MeshComm::sendMessage(cmd, dataJson, msgId);
}

void MessageHandler::sendAck(const String &msgId, const String &result) {
    if (msgId.length() == 0) return;
    String data = "{\"result\":\"" + result + "\"}";
    sendMessage("ACK", data, msgId);
}

void MessageHandler::sendError(ErrorCode code, const String &message,
                               const String &msgId) {
    String data = "{\"error_code\":" + String((int)code) + ",";
    data += "\"message\":\"" + message + "\"}";
    sendMessage("ERROR", data, msgId);
    Serial.printf("[MSG] 错误响应: code=%d msg=%s\n", (int)code, message.c_str());
}

void MessageHandler::sendFingerVerify(int fingerprintId) {
    String data = "{\"fingerprint_id\":" + String(fingerprintId) + "}";
    sendMessage("FINGER_VERIFY", data);
}

// ====== 权限过期检查 ======
bool MessageHandler::isPermissionExpired(const UserPermission &perm) {
    if (perm.expire_days == 0xFFFFFFFF) return false;  // 永久
    uint32_t nowUnix = Storage::getUnixTime();
    if (nowUnix < UNIX_2000_01_01) return false;  // 时间未同步，不检查
    uint32_t nowDays = (nowUnix - UNIX_2000_01_01) / 86400;
    return nowDays > perm.expire_days;
}

bool MessageHandler::tryLocalPermission(int fingerprintId, int lockId) {
    // 离线模式：先查本地缓存权限
    UserPermission perm;
    if (Storage::loadPermission(fingerprintId, perm) && perm.valid) {
        // 检查权限是否过期
        if (isPermissionExpired(perm)) {
            Logger::log(perm.user_id, fingerprintId, lockId,
                        "open", "fail", "permission_expired");
            Serial.printf("[MSG] 权限已过期: user=%s\n", perm.user_id.c_str());
            return true;  // 已处理（失败）
        }
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
            return true;  // 已处理（失败）
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
    } else if (state == STATE_UNLOCK_WINDOW &&
               (now - windowEnterTime > (unsigned long)windowSeconds * 1000UL)) {
        // 需求 2：开锁窗口超时，关灯并退出
        Serial.println(F("[MSG] 开锁窗口超时，退出"));
        exitUnlockWindow();
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

    // 权限丢失上报（每 60 秒重试，直到 PC 处理）
    if (permLostPending && MeshComm::isConnected()) {
        if (now - lastPermLostReport >= PERM_LOST_REPORT_INTERVAL || lastPermLostReport == 0) {
            lastPermLostReport = now;
            sendMessage("PERM_LOST", "{\"reason\":\"crc_failed\"}");
            Serial.println(F("[MSG] 上报 PERM_LOST"));
        }
    }

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

        case STATE_UNLOCK_WINDOW:
            // 需求 2：开锁窗口期，等待按键开锁或超时退出
            checkTimeout();
            break;

        case STATE_ENROLLING: {
            // 执行指纹录入（阻塞式，由命令触发）
            bool ok = Fingerprint::enrollFingerprint(enrollFingerprintId);
            String data = "{\"fingerprint_id\":" + String(enrollFingerprintId) +
                          ",\"user_id\":\"" + enrollUserId + "\",\"result\":\"" +
                          (ok ? "success" : "fail") + "\"}";
            sendMessage("ADD_FINGERPRINT_RESULT", data);

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

// ====== 命令分发（解析一次，传 JsonObject 引用） ======
void MessageHandler::handleIncoming(const String &message) {
    // 使用 ArduinoJson 解析（堆分配，避免 ESP32 栈溢出）
    // 缓冲区 8KB：足以容纳单条 SD_SAVE 记录、UPLOAD_FP_TEMPLATE（含 1024B hex）
    // 注：全量大表（如全校 users.json）需通过增量记录方式更新，单帧不超 8KB
    DynamicJsonDocument doc(8192);
    DeserializationError err = deserializeJson(doc, message);
    if (err) {
        Serial.printf("[MSG] JSON 解析失败: %s\n", err.c_str());
        sendError(ERR_JSON_PARSE, "json parse failed");
        return;
    }

    const char *cmd = doc["cmd"] | "";
    if (strlen(cmd) == 0) {
        Serial.println(F("[MSG] 消息缺少 cmd 字段"));
        sendError(ERR_UNKNOWN_CMD, "missing cmd field");
        return;
    }

    const char *msgId = doc["msg_id"] | "";
    const char *did = doc["device_id"] | "";

    // 检查 device_id 是否匹配本机（广播命令除外）
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    if (strlen(did) > 0 && strcmp(did, cfg.device_id.c_str()) != 0) {
        // 不是发往本机的消息，忽略
        return;
    }

    JsonObject data = doc["data"].as<JsonObject>();

    Serial.printf("[MSG] 处理命令: %s (msg_id=%s)\n", cmd, msgId);

    // 命令分发
    if (strcmp(cmd, "AUTH_OK") == 0) {
        cmdAuthOk(data, msgId);
    } else if (strcmp(cmd, "AUTH_FAIL") == 0) {
        cmdAuthFail(data, msgId);
    } else if (strcmp(cmd, "SYNC_PERMISSIONS") == 0) {
        cmdSyncPermissions(data, msgId);
    } else if (strcmp(cmd, "ADD_FINGERPRINT") == 0) {
        cmdAddFingerprint(data, msgId);
    } else if (strcmp(cmd, "DELETE_FINGERPRINT") == 0) {
        cmdDeleteFingerprint(data, msgId);
    } else if (strcmp(cmd, "DELETE_ALL_FINGERPRINTS") == 0) {
        cmdDeleteAllFingerprints(msgId);
    } else if (strcmp(cmd, "CONTROL_LOCK") == 0) {
        cmdControlLock(data, msgId);
    } else if (strcmp(cmd, "READ_CONFIG") == 0) {
        cmdReadConfig(msgId);
    } else if (strcmp(cmd, "WRITE_CONFIG") == 0) {
        cmdWriteConfig(data, msgId);
    } else if (strcmp(cmd, "READ_STATUS") == 0) {
        cmdReadStatus(msgId);
    } else if (strcmp(cmd, "READ_PERMISSIONS") == 0) {
        cmdReadPermissions(msgId);
    } else if (strcmp(cmd, "CLEAR_LOGS") == 0) {
        cmdClearLogs(msgId);
    } else if (strcmp(cmd, "REBOOT") == 0) {
        cmdReboot(data, msgId);
    } else if (strcmp(cmd, "TIME_SYNC") == 0) {
        cmdTimeSync(data, msgId);
    } else if (strcmp(cmd, "REGISTER") == 0) {
        cmdRegister(msgId);
    } else if (strcmp(cmd, "DEPLOY_USER") == 0) {
        // 需求 6/8：下发用户（权限 + 可选指纹模板）
        cmdDeployUser(data, msgId);
    } else if (strcmp(cmd, "REMOVE_USER") == 0) {
        // 需求 10：删除单个用户
        cmdRemoveUser(data, msgId);
    } else if (strcmp(cmd, "DELETE_CLASS_USERS") == 0) {
        // 需求 10：按班级批量删除用户
        cmdDeleteClassUsers(data, msgId);
    } else if (strcmp(cmd, "ENROLL_FP_STAGE") == 0) {
        // 需求 5：分步录入指纹
        cmdEnrollFpStage(data, msgId);
    } else if (strcmp(cmd, "READ_CAPACITY") == 0) {
        // 需求 10：读取容量
        cmdReadCapacity(msgId);
    } else if (strcmp(cmd, "CANCEL_VERIFY") == 0) {
        // 需求 2：取消验证 / 退出开锁窗口
        cmdCancelVerify(msgId);
    } else if (strcmp(cmd, "SD_QUERY") == 0) {
        cmdSdQuery(data, msgId);
    } else if (strcmp(cmd, "SD_SAVE") == 0) {
        cmdSdSave(data, msgId);
    } else if (strcmp(cmd, "SD_QUERY_VERSION") == 0) {
        cmdSdQueryVersion(msgId);
    } else if (strcmp(cmd, "UPLOAD_FP_TEMPLATE") == 0) {
        cmdUploadFpTemplate(data, msgId);
    } else if (strcmp(cmd, "DOWNLOAD_FP_TEMPLATE") == 0) {
        cmdDownloadFpTemplate(data, msgId);
    } else if (strcmp(cmd, "DELETE_FP_TEMPLATE") == 0) {
        cmdDeleteFpTemplate(data, msgId);
    } else if (strcmp(cmd, "HEARTBEAT_ACK") == 0) {
        // 心跳回应，无需处理
    } else if (strcmp(cmd, "LOG_REPORT_ACK") == 0) {
        // 日志上报确认，Logger 自行管理标记
    } else if (strcmp(cmd, "PERM_LOST_ACK") == 0) {
        // 权限丢失已确认，停止重发
        permLostPending = false;
        Serial.println(F("[MSG] PERM_LOST 已被上位机确认"));
    } else {
        Serial.printf("[MSG] 未知命令: %s\n", cmd);
        sendError(ERR_UNKNOWN_CMD, String("unknown command: ") + cmd, msgId);
    }
}

// ====== 命令处理实现 ======
void MessageHandler::cmdAuthOk(const JsonObject &data, const String &msgId) {
    // 缓存权限到 Flash
    UserPermission perm;
    perm.fingerprint_id = pendingFingerprintId;
    perm.user_id    = data["user_id"] | "";
    perm.name       = data["name"] | "";
    perm.role       = (UserRole)(data["role"] | (int)ROLE_STUDENT);
    perm.user_id_num = Storage::userIdToNum(perm.user_id);  // 字符串转数字形式

    JsonObject perms = data["permissions"].as<JsonObject>();
    perm.lock_perm[0] = perms["lock_0"] | false;
    perm.lock_perm[1] = perms["lock_1"] | false;
    perm.lock_perm[2] = perms["lock_2"] | false;
    perm.lock_perm[3] = perms["lock_3"] | false;

    // 过期时间处理
    const char *expireDate = data["expire_date"] | "";
    if (strlen(expireDate) > 0) {
        perm.expire_days = Storage::dateToDays(String(expireDate));
    } else {
        perm.expire_days = 0xFFFFFFFF;  // 永久
    }

    perm.valid = true;
    Storage::savePermission(perm);
    Serial.printf("[MSG] 权限已缓存: user=%s role=%d perm=[%d,%d,%d,%d]\n",
                  perm.user_id.c_str(), perm.role,
                  perm.lock_perm[0], perm.lock_perm[1],
                  perm.lock_perm[2], perm.lock_perm[3]);

    // ====== 需求 2：解析 window_seconds 与 allowed_locks，进入 10 秒开锁窗口 ======
    int windowSec = data["window_seconds"] | UNLOCK_WINDOW_SECONDS;

    bool allowed[LOCK_COUNT];
    // 优先使用 allowed_locks 数组字段；缺省时由 permissions 推导
    JsonArray allowedArr = data["allowed_locks"].as<JsonArray>();
    if (allowedArr.isNull()) {
        for (int i = 0; i < LOCK_COUNT; i++) {
            allowed[i] = perm.lock_perm[i];
        }
    } else {
        for (int i = 0; i < LOCK_COUNT; i++) {
            allowed[i] = (i < (int)allowedArr.size()) ? (bool)allowedArr[i] : false;
        }
    }

    // 检查权限过期：过期则不开窗口，直接记录失败
    if (isPermissionExpired(perm)) {
        Logger::log(perm.user_id, pendingFingerprintId, pendingLockId,
                    "open", "fail", "permission_expired");
        Serial.println(F("[MSG] 权限已过期，不进入开锁窗口"));
        state = STATE_IDLE;
        pendingLockId = -1;
        pendingFingerprintId = -1;
        return;
    }

    // 进入开锁窗口（点亮有权限锁的指示灯，启用窗口期权限校验）
    enterUnlockWindow(allowed, windowSec, perm.user_id, pendingFingerprintId);

    // 立即开用户最初请求的锁（若该锁有权限），提供即时反馈
    if (pendingLockId >= 0 && pendingLockId < LOCK_COUNT) {
        if (allowed[pendingLockId]) {
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

    // 注意：不返回 IDLE，保持在 STATE_UNLOCK_WINDOW，等待窗口期内按键或超时
    pendingLockId = -1;
    pendingFingerprintId = -1;
    verifyFailCount = 0;
}

void MessageHandler::cmdAuthFail(const JsonObject &data, const String &msgId) {
    const char* reason = data["reason"] | "用户不存在或权限不足";
    Serial.printf("[MSG] 鉴权失败: %s\n", reason);
    Logger::log("", pendingFingerprintId, pendingLockId,
                "open", "fail", String(reason));

    verifyFailCount++;
    if (verifyFailCount >= VERIFY_FAIL_MAX) {
        // 多次失败告警
        sendMessage("ALARM", "{\"type\":\"verify_fail_too_many\",\"count\":" +
                    String(verifyFailCount) + "}");
        verifyFailCount = 0;
    }

    state = STATE_IDLE;
    pendingLockId = -1;
    pendingFingerprintId = -1;
}

void MessageHandler::cmdSyncPermissions(const JsonObject &data, const String &msgId) {
    // 全量同步权限：data: {"version":1,"users":[{...},...]}
    uint32_t version = data["version"] | 0;
    JsonArray users = data["users"].as<JsonArray>();
    int count = users.size();

    if (count > PERM_MAX_USERS) {
        sendError(ERR_FLASH_WRITE, "too many users", msgId);
        return;
    }

    UserPermission *permList = nullptr;
    if (count > 0) {
        permList = new UserPermission[count];
        int idx = 0;
        for (JsonObject user : users) {
            UserPermission &perm = permList[idx];
            perm.fingerprint_id = user["fingerprint_id"] | -1;
            if (perm.fingerprint_id < 0) continue;
            perm.user_id = user["user_id"] | "";
            perm.name    = user["name"] | "";
            perm.role    = (UserRole)(user["role"] | (int)ROLE_STUDENT);
            perm.user_id_num = Storage::userIdToNum(perm.user_id);  // 字符串转数字形式

            JsonObject lp = user["lock_permissions"].as<JsonObject>();
            perm.lock_perm[0] = lp["lock_0"] | false;
            perm.lock_perm[1] = lp["lock_1"] | false;
            perm.lock_perm[2] = lp["lock_2"] | false;
            perm.lock_perm[3] = lp["lock_3"] | false;

            const char *expireDate = user["expire_date"] | "";
            if (strlen(expireDate) > 0) {
                perm.expire_days = Storage::dateToDays(String(expireDate));
            } else {
                perm.expire_days = 0xFFFFFFFF;
            }
            perm.valid = true;
            idx++;
        }
        count = idx;
    }

    bool ok = Storage::replaceAllPermissions(permList, count, version);
    if (permList) delete[] permList;

    // 权限同步成功后清除丢失标志
    if (ok) {
        permLostPending = false;
    }

    Serial.printf("[MSG] 已同步 %d 条权限, 版本=%u\n", count, version);
    String respData = "{\"count\":" + String(count) +
                      ",\"version\":" + String(version) +
                      ",\"result\":\"" + (ok ? "success" : "fail") + "\"}";
    sendMessage("SYNC_ACK", respData, msgId);
}

void MessageHandler::cmdAddFingerprint(const JsonObject &data, const String &msgId) {
    int fpId = data["fingerprint_id"] | -1;
    String userId = data["user_id"] | "";
    if (fpId < 0) {
        sendError(ERR_FP_TEMPLATE_FORMAT, "invalid fingerprint id", msgId);
        return;
    }

    // 检查指纹 ID 是否已存在
    UserPermission existing;
    if (Storage::loadPermission(fpId, existing) && existing.valid) {
        sendError(ERR_FP_ID_EXISTS, "fingerprint id already exists", msgId);
        return;
    }

    sendAck(msgId, "enrolling");
    startEnroll(fpId, userId);
}

void MessageHandler::cmdDeleteFingerprint(const JsonObject &data, const String &msgId) {
    int fpId = data["fingerprint_id"] | -1;
    bool ok = false;
    if (fpId >= 0) {
        ok = Fingerprint::deleteFingerprint(fpId);
        Storage::deletePermission(fpId);
    }
    String result = ok ? "success" : "fail";
    if (!ok) {
        sendError(ERR_FP_COMM_FAILED, "delete fingerprint failed", msgId);
        return;
    }
    String respData = "{\"fingerprint_id\":" + String(fpId) +
                      ",\"result\":\"" + result + "\"}";
    sendMessage("DELETE_FINGERPRINT_RESULT", respData, msgId);

    // 更新指纹计数
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    cfg.fingerprint_count = Fingerprint::getFingerprintCount();
    Storage::saveDeviceConfig(cfg);
}

void MessageHandler::cmdDeleteAllFingerprints(const String &msgId) {
    bool ok = Fingerprint::deleteAllFingerprints();
    Storage::clearAllPermissions();
    String result = ok ? "success" : "fail";

    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    cfg.fingerprint_count = 0;
    Storage::saveDeviceConfig(cfg);

    String respData = "{\"result\":\"" + result + "\"}";
    sendMessage("DELETE_ALL_FINGERPRINTS_RESULT", respData, msgId);
    Serial.println(F("[MSG] 已清空所有指纹和权限"));
}

void MessageHandler::cmdControlLock(const JsonObject &data, const String &msgId) {
    int lockId = data["lock_id"] | -1;
    String action = data["action"] | "open";
    if (lockId < 0 || lockId >= LOCK_COUNT) {
        sendError(ERR_LOCK_ID_RANGE, "lock id out of range", msgId);
        return;
    }
    if (action == "open") {
        if (LockControl::openLock(lockId)) {
            Logger::log("remote", -1, lockId, "open", "success", "remote_control");
        } else {
            sendError(ERR_LOCK_HARDWARE, "lock open failed", msgId);
            return;
        }
    } else {
        LockControl::closeLock(lockId);
        Logger::log("remote", -1, lockId, "close", "success", "remote_control");
    }
    Serial.printf("[MSG] 远程控制锁 %d %s\n", lockId, action.c_str());
    sendAck(msgId, action);
}

void MessageHandler::cmdReadConfig(const String &msgId) {
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    String data = "{";
    data += "\"device_id\":\"" + cfg.device_id + "\",";
    data += "\"device_name\":\"" + cfg.device_name + "\",";
    data += "\"is_root\":" + String(cfg.is_root ? "true" : "false") + ",";
    data += "\"work_mode\":\"" + String(cfg.work_mode == MODE_MESH ? "mesh" : "debug") + "\",";
    data += "\"uplink_mode\":" + String((int)cfg.uplink_mode) + ",";
    data += "\"mesh_channel\":" + String(cfg.mesh_channel) + ",";
    data += "\"wifi_ssid\":\"" + cfg.wifi_ssid + "\",";
    data += "\"server_ip\":\"" + cfg.server_ip + "\",";
    data += "\"server_port\":" + String(cfg.server_port) + ",";
    data += "\"fingerprint_count\":" + String(cfg.fingerprint_count) + ",";
    data += "\"perm_version\":" + String(cfg.perm_version) + ",";
    data += "\"firmware_version\":\"" FIRMWARE_VERSION "\"";
    data += "}";
    sendMessage("CONFIG_RESPONSE", data, msgId);
}

void MessageHandler::cmdWriteConfig(const JsonObject &data, const String &msgId) {
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);

    if (data.containsKey("device_id"))   cfg.device_id = data["device_id"].as<String>();
    if (data.containsKey("device_name")) cfg.device_name = data["device_name"].as<String>();
    if (data.containsKey("is_root"))     cfg.is_root = data["is_root"].as<bool>();
    if (data.containsKey("uplink_mode")) cfg.uplink_mode = (UplinkMode)(data["uplink_mode"] | 0);
    if (data.containsKey("mesh_channel")) cfg.mesh_channel = data["mesh_channel"] | MESH_CHANNEL;
    if (data.containsKey("mesh_password")) cfg.mesh_password = data["mesh_password"].as<String>();
    if (data.containsKey("wifi_ssid"))   cfg.wifi_ssid = data["wifi_ssid"].as<String>();
    if (data.containsKey("wifi_password")) cfg.wifi_password = data["wifi_password"].as<String>();
    if (data.containsKey("server_ip"))   cfg.server_ip = data["server_ip"].as<String>();
    if (data.containsKey("server_port")) cfg.server_port = data["server_port"] | UPLINK_TCP_PORT;

    Storage::saveDeviceConfig(cfg);
    sendMessage("CONFIG_SAVED", "{\"result\":\"success\"}", msgId);
    Serial.println(F("[MSG] 配置已更新"));
}

void MessageHandler::cmdReadStatus(const String &msgId) {
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
    data += "\"perm_count\":" + String(Storage::getPermissionCount()) + ",";
    data += "\"perm_version\":" + String(Storage::getPermissionVersion()) + ",";
    data += "\"mesh_layer\":" + String(MeshComm::getMeshLayer()) + ",";
    data += "\"child_count\":" + String(MeshComm::getChildCount()) + ",";
    data += "\"work_mode\":\"" + String(Storage::loadWorkMode() == MODE_MESH ? "mesh" : "debug") + "\",";
    data += "\"time_synced\":" + String(Storage::isTimeSynced() ? "true" : "false") + ",";
    // 需求 3c：STATUS_RESPONSE 增加 perm_max 字段（值=200）
    data += "\"perm_max\":" + String(PERM_MAX_USERS);
    data += "}";
    sendMessage("STATUS_RESPONSE", data, msgId);
}

void MessageHandler::cmdReadPermissions(const String &msgId) {
    int count = Storage::getPermissionCount();
    // 构造权限列表 JSON
    String data = "{\"count\":" + String(count) + ",\"version\":" +
                  String(Storage::getPermissionVersion()) + ",\"users\":[";
    for (int i = 0; i < count; i++) {
        // 通过遍历内存缓存获取权限（loadPermission 按 fp_id 查找，这里需要遍历）
        // 使用 getPermissionCount + 逐个读取的方式不可行，改为上报简化信息
        // 实际实现中可扩展 Storage 提供遍历接口
    }
    data += "]}";
    sendMessage("PERMISSIONS_RESPONSE", data, msgId);
    Serial.printf("[MSG] 上报权限列表: %d 条\n", count);
}

void MessageHandler::cmdClearLogs(const String &msgId) {
    Logger::clearAll();
    sendMessage("LOGS_CLEARED", "{\"result\":\"success\"}", msgId);
}

void MessageHandler::cmdReboot(const JsonObject &data, const String &msgId) {
    String mode = data["mode"] | "";
    Serial.printf("[MSG] 准备重启，目标模式: %s\n", mode.c_str());
    sendMessage("REBOOT_ACK", "{\"result\":\"rebooting\"}", msgId);
    delay(500);
    if (mode == "debug") {
        Storage::saveWorkMode(MODE_DEBUG);
    } else if (mode == "mesh") {
        Storage::saveWorkMode(MODE_MESH);
    }
    ESP.restart();
}

void MessageHandler::cmdTimeSync(const JsonObject &data, const String &msgId) {
    uint32_t timestamp = data["timestamp"] | 0;
    if (timestamp > 0) {
        Storage::setUnixTime(timestamp);
        Serial.printf("[MSG] 时间已同步: %u\n", timestamp);
        sendAck(msgId, "time_synced");
    } else {
        sendError(ERR_UNKNOWN_CMD, "invalid timestamp", msgId);
    }
}

void MessageHandler::cmdRegister(const String &msgId) {
    // 上位机查询设备信息，返回注册响应
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    String data = "{";
    data += "\"device_id\":\"" + cfg.device_id + "\",";
    data += "\"device_name\":\"" + cfg.device_name + "\",";
    data += "\"is_root\":" + String(cfg.is_root ? "true" : "false") + ",";
    data += "\"firmware_version\":\"" FIRMWARE_VERSION "\",";
    data += "\"mesh_mac\":\"" + MeshComm::getMeshMac() + "\",";
    data += "\"mesh_layer\":" + String(MeshComm::getMeshLayer());
    data += "}";
    sendMessage("REGISTER_RESPONSE", data, msgId);
    Serial.println(F("[MSG] 已响应 REGISTER 查询"));
}

// ============================================================
// ====== 需求 2/5/6/8/10 新增命令实现 ======
// ============================================================

// 需求 6/8：下发用户 —— 写入本地权限表，若有 fp_template(base64) 则下发到 AS608
void MessageHandler::cmdDeployUser(const JsonObject &data, const String &msgId) {
    String userId = data["user_id"] | "";
    int fpId = data["fingerprint_id"] | -1;
    if (userId.length() == 0 || fpId < 0 || fpId >= FINGER_MAX_USERS) {
        sendError(ERR_BAD_REQUEST, "invalid user_id or fingerprint_id", msgId);
        return;
    }

    // 解析 permissions[4]
    bool lockPerm[LOCK_COUNT] = {false, false, false, false};
    JsonArray perms = data["permissions"].as<JsonArray>();
    if (!perms.isNull()) {
        for (int i = 0; i < LOCK_COUNT && i < (int)perms.size(); i++) {
            lockPerm[i] = (bool)perms[i];
        }
    } else {
        // 兼容 permissions 对象形式 {lock_0..lock_3}
        JsonObject po = data["permissions"].as<JsonObject>();
        if (!po.isNull()) {
            lockPerm[0] = po["lock_0"] | false;
            lockPerm[1] = po["lock_1"] | false;
            lockPerm[2] = po["lock_2"] | false;
            lockPerm[3] = po["lock_3"] | false;
        }
    }

    // 构造权限记录并写入本地权限表
    UserPermission perm;
    perm.fingerprint_id = fpId;
    perm.user_id    = userId;
    perm.user_id_num = Storage::userIdToNum(userId);
    perm.name       = data["name"] | "";
    perm.role       = (UserRole)(data["role"] | (int)ROLE_STUDENT);
    for (int i = 0; i < LOCK_COUNT; i++) perm.lock_perm[i] = lockPerm[i];
    const char *expireDate = data["expire_date"] | "";
    perm.expire_days = strlen(expireDate) > 0 ? Storage::dateToDays(String(expireDate))
                                              : 0xFFFFFFFF;
    perm.valid = true;
    bool permOk = Storage::savePermission(perm);

    // 若附带 fp_template（base64），解码后写入 AS608
    bool fpOk = true;
    const char *tplB64 = data["fp_template"] | "";
    if (strlen(tplB64) > 0) {
        uint8_t *buf = (uint8_t *)malloc(FP_TEMPLATE_BUF_SIZE);
        if (!buf) {
            sendError(ERR_INTERNAL, "memory alloc failed", msgId);
            return;
        }
        size_t decLen = base64Decode(String(tplB64), buf, FP_TEMPLATE_BUF_SIZE);
        if (decLen == 0) {
            fpOk = false;
            Serial.println(F("[MSG] DEPLOY_USER: fp_template base64 解码失败"));
        } else {
            fpOk = Fingerprint::writeTemplate(fpId, buf, decLen);
            Serial.printf("[MSG] DEPLOY_USER: 写入 AS608 模板 id=%d, %u 字节, %s\n",
                          fpId, (unsigned)decLen, fpOk ? "成功" : "失败");
        }
        free(buf);
    }

    bool success = permOk && fpOk;
    String resp = "{\"user_id\":\"" + userId + "\",\"fingerprint_id\":" + String(fpId) +
                  ",\"success\":" + String(success ? "true" : "false") + "}";
    sendMessage("DEPLOY_USER_RESPONSE", resp, msgId);
    Serial.printf("[MSG] DEPLOY_USER %s(fp=%d): perm=%s fp=%s\n",
                  userId.c_str(), fpId, permOk ? "ok" : "fail", fpOk ? "ok" : "skip/fail");
}

// 需求 10：删除单个用户 —— 删除本地权限 + AS608 指纹
void MessageHandler::cmdRemoveUser(const JsonObject &data, const String &msgId) {
    String userId = data["user_id"] | "";
    int fpId = data["fingerprint_id"] | -1;
    if (userId.length() == 0 && fpId < 0) {
        sendError(ERR_BAD_REQUEST, "missing user_id or fingerprint_id", msgId);
        return;
    }

    bool permOk = true;
    if (fpId >= 0) {
        permOk = Storage::deletePermission(fpId);
    }
    bool fpOk = (fpId >= 0) ? Fingerprint::deleteFingerprint(fpId) : true;

    bool success = permOk && fpOk;
    String resp = "{\"user_id\":\"" + userId + "\",\"fingerprint_id\":" + String(fpId) +
                  ",\"success\":" + String(success ? "true" : "false") + "}";
    sendMessage("REMOVE_USER_RESPONSE", resp, msgId);
    Serial.printf("[MSG] REMOVE_USER %s(fp=%d): perm=%s fp=%s\n",
                  userId.c_str(), fpId, permOk ? "ok" : "fail", fpOk ? "ok" : "fail");

    // 更新指纹计数
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    cfg.fingerprint_count = Fingerprint::getFingerprintCount();
    Storage::saveDeviceConfig(cfg);
}

// 需求 10：按班级批量删除用户 —— 批量删除本地权限 + AS608 指纹
void MessageHandler::cmdDeleteClassUsers(const JsonObject &data, const String &msgId) {
    JsonArray fpIds = data["fingerprint_ids"].as<JsonArray>();
    if (fpIds.isNull() || fpIds.size() == 0) {
        sendError(ERR_BAD_REQUEST, "missing fingerprint_ids", msgId);
        return;
    }

    int deletedCount = 0;
    for (int id : fpIds) {
        if (id < 0) continue;
        bool permOk = Storage::deletePermission(id);
        bool fpOk = Fingerprint::deleteFingerprint(id);
        if (permOk || fpOk) {
            deletedCount++;
        }
    }

    String resp = "{\"class_id\":\"" + String(data["class_id"] | "") +
                  "\",\"deleted_count\":" + String(deletedCount) +
                  ",\"success\":true}";
    sendMessage("DELETE_CLASS_USERS_RESPONSE", resp, msgId);
    Serial.printf("[MSG] DELETE_CLASS_USERS: 删除 %d 个用户\n", deletedCount);

    // 更新指纹计数
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    cfg.fingerprint_count = Fingerprint::getFingerprintCount();
    Storage::saveDeviceConfig(cfg);
}

// 需求 5：分步录入指纹 —— 调用 fingerprint.cpp 的 enrollFingerprintStage()
void MessageHandler::cmdEnrollFpStage(const JsonObject &data, const String &msgId) {
    String stage = data["stage"] | "";
    int fpId = data["fingerprint_id"] | -1;
    if (stage.length() == 0 || fpId < 0) {
        sendError(ERR_BAD_REQUEST, "missing stage or fingerprint_id", msgId);
        return;
    }

    // 调用分步录入，返回 JSON 响应
    String stageResp = Fingerprint::enrollFingerprintStage(stage, fpId);
    // 包装为 FP_ENROLL_STAGE_RESPONSE
    String resp = "{\"fingerprint_id\":" + String(fpId) +
                  ",\"stage_response\":" + stageResp + "}";
    sendMessage("FP_ENROLL_STAGE_RESPONSE", resp, msgId);
    Serial.printf("[MSG] ENROLL_FP_STAGE %s(fp=%d): %s\n",
                  stage.c_str(), fpId, stageResp.c_str());

    // 若 verify2 成功，更新指纹计数
    if (stage == "verify2" && stageResp.indexOf("\"success\":true") >= 0) {
        DeviceConfig cfg;
        Storage::loadDeviceConfig(cfg);
        cfg.fingerprint_count = Fingerprint::getFingerprintCount();
        Storage::saveDeviceConfig(cfg);
    }
}

// 需求 10：读取容量
void MessageHandler::cmdReadCapacity(const String &msgId) {
    int used = Fingerprint::getFingerprintCount();
    String data = "{\"used\":" + String(used) +
                  ",\"max\":" + String(PERM_MAX_USERS) +
                  ",\"warn_threshold\":" + String(CAPACITY_WARN_THRESHOLD) + "}";
    sendMessage("CAPACITY_RESPONSE", data, msgId);
    Serial.printf("[MSG] READ_CAPACITY: used=%d max=%d warn=%d\n",
                  used, PERM_MAX_USERS, CAPACITY_WARN_THRESHOLD);
}

// 需求 2：取消验证 / 退出 10 秒开锁窗口
void MessageHandler::cmdCancelVerify(const String &msgId) {
    if (state == STATE_UNLOCK_WINDOW) {
        exitUnlockWindow();
        Serial.println(F("[MSG] CANCEL_VERIFY：退出开锁窗口"));
    } else if (state == STATE_WAIT_FINGER || state == STATE_WAIT_AUTH) {
        state = STATE_IDLE;
        pendingLockId = -1;
        pendingFingerprintId = -1;
        Serial.println(F("[MSG] CANCEL_VERIFY：取消验证流程"));
    }
    sendAck(msgId, "cancelled");
}

// ============================================================
// ====== SD 卡集中存储命令实现（仅根节点响应） ======
// ============================================================

bool MessageHandler::sendLargeResponse(const String &cmd, const String &dataJson,
                                       const String &msgId) {
    // 单帧上限约 1400B，大表需分多条消息发送
    // 简化：单条消息承载完整 JSON，依赖 ProtocolFrame 分片机制
    // 若 JSON 超大（>8KB），分批发送 SD_QUERY_PART
    const size_t MAX_PART = 6000;  // 单次负载上限（留余量给协议帧头）

    if (dataJson.length() <= MAX_PART) {
        // 单条返回
        return sendMessage(cmd, dataJson, msgId);
    }

    // 分批返回：SD_QUERY_PART {part, total, data}
    int totalParts = (dataJson.length() + MAX_PART - 1) / MAX_PART;
    for (int i = 0; i < totalParts; i++) {
        size_t start = i * MAX_PART;
        size_t len = MAX_PART;
        if (start + len > dataJson.length()) len = dataJson.length() - start;

        String part = "{\"part\":";
        part += String(i + 1);
        part += ",\"total\":";
        part += String(totalParts);
        part += ",\"data\":\"";
        // 转义 JSON 内部双引号
        String chunk = dataJson.substring(start, start + len);
        for (size_t j = 0; j < chunk.length(); j++) {
            if (chunk[j] == '"' || chunk[j] == '\\') {
                part += '\\';
            }
            part += chunk[j];
        }
        part += "\"}";

        String partCmd = "SD_QUERY_PART";
        sendMessage(partCmd, part, msgId);
        delay(30);  // 避免 Mesh 拥堵
    }
    return true;
}

void MessageHandler::cmdSdQuery(const JsonObject &data, const String &msgId) {
    // 仅根节点响应
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    if (!cfg.is_root) {
        sendError(ERR_PERMISSION_DENIED, "only root node has SD storage", msgId);
        return;
    }

    if (!SdStorage::isReady()) {
        sendError(ERR_INTERNAL, "sd card not ready", msgId);
        return;
    }

    String table = data["table"] | "";
    if (table.length() == 0) {
        sendError(ERR_BAD_REQUEST, "missing table name", msgId);
        return;
    }

    String outJson;
    if (!SdStorage::readTable(table, outJson)) {
        sendError(ERR_NOT_FOUND, "table not found or empty", msgId);
        return;
    }

    // 用 data 包装返回
    String response = "{\"table\":\"" + table + "\",\"json\":";
    response += outJson;
    response += "}";

    Serial.printf("[MSG] SD_QUERY %s: %u 字节\n", table.c_str(), (unsigned)response.length());
    sendLargeResponse("SD_QUERY_RESPONSE", response, msgId);
}

void MessageHandler::cmdSdSave(const JsonObject &data, const String &msgId) {
    // 仅根节点响应
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    if (!cfg.is_root) {
        sendError(ERR_PERMISSION_DENIED, "only root node has SD storage", msgId);
        return;
    }

    if (!SdStorage::isReady()) {
        sendError(ERR_INTERNAL, "sd card not ready", msgId);
        return;
    }

    String table = data["table"] | "";
    String json = data["json"] | "";
    uint32_t baseVersion = data["base_version"] | 0;

    if (table.length() == 0 || json.length() == 0) {
        sendError(ERR_BAD_REQUEST, "missing table or json", msgId);
        return;
    }

    // 乐观锁冲突检测
    if (baseVersion > 0) {
        uint32_t g, u, c, p, d, fp;
        SdStorage::readVersion(g, u, c, p, d, fp);
        uint32_t currentVer = 0;
        if (table == "users") currentVer = u;
        else if (table == "classes") currentVer = c;
        else if (table == "permissions") currentVer = p;
        else if (table == "devices") currentVer = d;
        else if (table == "fingerprints") currentVer = fp;

        if (currentVer != baseVersion) {
            String errData = "{\"error\":\"version_conflict\",\"current_version\":";
            errData += String(currentVer);
            errData += ",\"base_version\":";
            errData += String(baseVersion);
            errData += "}";
            sendMessage("SD_SAVE_RESPONSE", errData, msgId);
            Serial.printf("[MSG] SD_SAVE %s 版本冲突: base=%u current=%u\n",
                          table.c_str(), baseVersion, currentVer);
            return;
        }
    }

    bool ok = SdStorage::writeTable(table, json);
    String resp = "{\"table\":\"" + table + "\",\"result\":";
    resp += ok ? "\"success\"" : "\"fail\"";
    if (ok) {
        resp += ",\"version\":";
        resp += String(SdStorage::getGlobalVersion());
    }
    resp += "}";
    sendMessage("SD_SAVE_RESPONSE", resp, msgId);
    Serial.printf("[MSG] SD_SAVE %s: %s\n", table.c_str(), ok ? "成功" : "失败");
}

void MessageHandler::cmdSdQueryVersion(const String &msgId) {
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    if (!cfg.is_root) {
        sendError(ERR_PERMISSION_DENIED, "only root node has SD storage", msgId);
        return;
    }

    uint32_t g, u, c, p, d, fp;
    SdStorage::readVersion(g, u, c, p, d, fp);

    String data = "{";
    data += "\"global_version\":" + String(g) + ",";
    data += "\"users_version\":" + String(u) + ",";
    data += "\"classes_version\":" + String(c) + ",";
    data += "\"permissions_version\":" + String(p) + ",";
    data += "\"devices_version\":" + String(d) + ",";
    data += "\"fp_version\":" + String(fp) + ",";
    data += "\"sd_total_bytes\":" + String((unsigned long)SdStorage::getTotalBytes()) + ",";
    data += "\"sd_used_bytes\":" + String((unsigned long)SdStorage::getUsedBytes());
    data += "}";
    sendMessage("SD_VERSION_RESPONSE", data, msgId);
    Serial.printf("[MSG] SD 版本查询: global=%u\n", g);
}

void MessageHandler::cmdUploadFpTemplate(const JsonObject &data, const String &msgId) {
    // 仅根节点响应
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    if (!cfg.is_root) {
        sendError(ERR_PERMISSION_DENIED, "only root node has SD storage", msgId);
        return;
    }

    if (!SdStorage::isReady()) {
        sendError(ERR_INTERNAL, "sd card not ready", msgId);
        return;
    }

    String userId = data["user_id"] | "";
    int fingerIndex = data["finger_index"] | 1;
    String templateHex = data["template_hex"] | "";

    if (userId.length() == 0 || templateHex.length() == 0) {
        sendError(ERR_BAD_REQUEST, "missing user_id or template_hex", msgId);
        return;
    }
    if (fingerIndex < 1 || fingerIndex > FP_MAX_TEMPLATES_PER_USER) {
        sendError(ERR_BAD_REQUEST, "finger_index out of range", msgId);
        return;
    }

    // hex 解码为二进制
    size_t binLen = templateHex.length() / 2;
    if (binLen == 0 || binLen > FP_TEMPLATE_BUF_SIZE) {
        sendError(ERR_BAD_REQUEST, "template hex length invalid", msgId);
        return;
    }

    uint8_t *buf = (uint8_t *)malloc(binLen);
    if (!buf) {
        sendError(ERR_INTERNAL, "memory alloc failed", msgId);
        return;
    }

    // hex 字符串转二进制
    for (size_t i = 0; i < binLen; i++) {
        char hi = templateHex[i * 2];
        char lo = templateHex[i * 2 + 1];
        buf[i] = (hexCharToVal(hi) << 4) | hexCharToVal(lo);
    }

    bool ok = SdStorage::writeTemplate(userId, fingerIndex, buf, binLen);
    free(buf);

    String resp = "{\"user_id\":\"" + userId + "\",\"result\":";
    resp += ok ? "\"success\"" : "\"fail\"";
    resp += "}";
    sendMessage("FP_TEMPLATE_UPLOAD_RESPONSE", resp, msgId);
    Serial.printf("[MSG] 指纹模板上传 %s[%d]: %s\n",
                  userId.c_str(), fingerIndex, ok ? "成功" : "失败");
}

void MessageHandler::cmdDownloadFpTemplate(const JsonObject &data, const String &msgId) {
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    if (!cfg.is_root) {
        sendError(ERR_PERMISSION_DENIED, "only root node has SD storage", msgId);
        return;
    }

    if (!SdStorage::isReady()) {
        sendError(ERR_INTERNAL, "sd card not ready", msgId);
        return;
    }

    String userId = data["user_id"] | "";
    int fingerIndex = data["finger_index"] | 1;

    if (userId.length() == 0) {
        sendError(ERR_BAD_REQUEST, "missing user_id", msgId);
        return;
    }

    uint8_t *buf = (uint8_t *)malloc(FP_TEMPLATE_BUF_SIZE);
    if (!buf) {
        sendError(ERR_INTERNAL, "memory alloc failed", msgId);
        return;
    }

    size_t outLen = 0;
    bool ok = SdStorage::readTemplate(userId, fingerIndex, buf, FP_TEMPLATE_BUF_SIZE, outLen);

    if (!ok) {
        free(buf);
        sendError(ERR_NOT_FOUND, "template not found", msgId);
        return;
    }

    // 二进制转 hex 字符串
    String hex = "";
    hex.reserve(outLen * 2);
    const char *hexChars = "0123456789ABCDEF";
    for (size_t i = 0; i < outLen; i++) {
        hex += hexChars[(buf[i] >> 4) & 0x0F];
        hex += hexChars[buf[i] & 0x0F];
    }
    free(buf);

    String resp = "{\"user_id\":\"" + userId + "\",\"finger_index\":";
    resp += String(fingerIndex);
    resp += ",\"len\":";
    resp += String((unsigned)outLen);
    resp += ",\"template_hex\":\"";
    resp += hex;
    resp += "\"}";

    // 模板 512B → hex 1024B，单帧可承载
    sendMessage("FP_TEMPLATE_DOWNLOAD_RESPONSE", resp, msgId);
    Serial.printf("[MSG] 指纹模板下载 %s[%d]: %u 字节\n",
                  userId.c_str(), fingerIndex, (unsigned)outLen);
}

void MessageHandler::cmdDeleteFpTemplate(const JsonObject &data, const String &msgId) {
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    if (!cfg.is_root) {
        sendError(ERR_PERMISSION_DENIED, "only root node has SD storage", msgId);
        return;
    }

    if (!SdStorage::isReady()) {
        sendError(ERR_INTERNAL, "sd card not ready", msgId);
        return;
    }

    String userId = data["user_id"] | "";
    if (userId.length() == 0) {
        sendError(ERR_BAD_REQUEST, "missing user_id", msgId);
        return;
    }

    bool ok = SdStorage::deleteTemplate(userId);
    SdStorage::incrementVersion("fingerprints");

    String resp = "{\"user_id\":\"" + userId + "\",\"result\":";
    resp += ok ? "\"success\"" : "\"fail\"";
    resp += "}";
    sendMessage("FP_TEMPLATE_DELETE_RESPONSE", resp, msgId);
    Serial.printf("[MSG] 指纹模板删除 %s: %s\n",
                  userId.c_str(), ok ? "成功" : "无模板");
}

// hex 字符转数值
uint8_t MessageHandler::hexCharToVal(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    return 0;
}

// 需求 6/8：base64 解码（自包含实现，用于 DEPLOY_USER 的 fp_template 字段）
// 返回解码后的字节数（写入 outBuf），失败返回 0
size_t MessageHandler::base64Decode(const String &in, uint8_t *outBuf, size_t outBufSize) {
    // base64 解码表
    static const int8_t dec[128] = {
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,
        -1,-1,-1,-1,-1,-1,-1,-1,-1,-1,-1,62,-1,-1,-1,63,  // '+'=62, '/'=63
        52,53,54,55,56,57,58,59,60,61,-1,-1,-1,-1,-1,-1,  // '0'-'9'
        -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9,10,11,12,13,14,  // 'A'-'O'
        15,16,17,18,19,20,21,22,23,24,25,-1,-1,-1,-1,-1,  // 'P'-'Z'
        -1,26,27,28,29,30,31,32,33,34,35,36,37,38,39,40,  // 'a'-'o'
        41,42,43,44,45,46,47,48,49,50,51,-1,-1,-1,-1,-1   // 'p'-'z'
    };

    size_t outLen = 0;
    int val = 0, valb = -8;
    for (size_t i = 0; i < in.length(); i++) {
        char c = in[i];
        if (c == '=') break;  // 填充符结束
        if (c < 0 || c >= 128) continue;
        int d = dec[(uint8_t)c];
        if (d < 0) continue;  // 跳过非 base64 字符（含换行/空格）
        val = (val << 6) | d;
        valb += 6;
        if (valb >= 0) {
            if (outLen >= outBufSize) {
                return outLen;  // 缓冲已满
            }
            outBuf[outLen++] = (uint8_t)((val >> valb) & 0xFF);
            valb -= 8;
        }
    }
    return outLen;
}
