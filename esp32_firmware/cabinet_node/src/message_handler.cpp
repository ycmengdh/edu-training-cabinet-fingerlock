/**
 * message_handler.cpp - 消息处理实现（V2.0 Mesh版本）
 * 柜子本地完成指纹匹配和权限判断，传 JsonObject 引用避免两次解析
 * 新增 ACK 确认机制（msg_id 原样回传）和错误码处理
 * 适配新命令：REGISTER/TIME_SYNC/PERM_LOST/LOG_REPORT_ACK/SYNC_PERMISSIONS
 */
#include "message_handler.h"
#include "debug.h"
#include "storage.h"
#include "fingerprint.h"
#include "lock_control.h"
#include "mesh_comm.h"
#include "logger.h"
#include "message_hmac.h"
#ifdef ENABLE_SD_CARD
#include "sd_storage.h"
#endif

// 状态机超时时间
#define WAIT_FINGER_TIMEOUT_MS  30000   // 等待指纹超时 30 秒
#define PERM_LOST_REPORT_INTERVAL 60000 // PERM_LOST 上报间隔 60 秒
#define PERMISSION_SYNC_TIMEOUT_MS 30000

// 2000-01-01 00:00:00 的 Unix 时间戳
#define UNIX_2000_01_01  946684800UL

// 静态成员初始化
int MessageHandler::pendingLockId        = -1;
int MessageHandler::pendingFingerprintId = -1;
MessageHandler::VerifyState MessageHandler::state = STATE_IDLE;
unsigned long MessageHandler::stateEnterTime = 0;
int  MessageHandler::enrollFingerprintId = -1;
String MessageHandler::enrollUserId      = "";
String MessageHandler::enrollRequestMsgId = "";
int  MessageHandler::verifyFailCount     = 0;
bool MessageHandler::permLostPending     = false;
unsigned long MessageHandler::lastPermLostReport = 0;
UserPermission *MessageHandler::permissionSyncBuffer = nullptr;
bool *MessageHandler::permissionSyncReceived = nullptr;
int MessageHandler::permissionSyncExpected = 0;
int MessageHandler::permissionSyncReceivedCount = 0;
uint32_t MessageHandler::permissionSyncVersion = 0;
unsigned long MessageHandler::permissionSyncStartedAt = 0;
bool MessageHandler::permissionSyncActive = false;

void MessageHandler::init() {
    state = STATE_IDLE;
    pendingLockId = -1;
    pendingFingerprintId = -1;
    stateEnterTime = millis();
    enrollFingerprintId = -1;
    enrollRequestMsgId = "";
    verifyFailCount = 0;
    resetPermissionSync();

    // 检查权限数据是否丢失（启动时 CRC 都失败）
    if (Storage::isPermissionLost()) {
        permLostPending = true;
        Debug::println(F("[MSG] permission data lost, pending PERM_LOST report"));
    }

    Debug::println(F("[MSG] message handler init complete"));
}

MessageHandler::VerifyState MessageHandler::getState() {
    return state;
}

void MessageHandler::setState(VerifyState s) {
    Debug::printf("[MSG] state switch: %d -> %d\n", state, s);
    state = s;
    stateEnterTime = millis();
}

void MessageHandler::onKeyPressed(int lockId) {
    if (state != STATE_IDLE) {
        Debug::printf("[MSG] ignore key %d (current state %d busy)\n", lockId, state);
        return;
    }
    pendingLockId = lockId;
    setState(STATE_WAIT_FINGER);
    Debug::printf("[MSG] key %d triggered, waiting for fingerprint...\n", lockId);
}

void MessageHandler::onCancel() {
    if (state == STATE_IDLE) return;  // 空闲时忽略
    Debug::printf("[MSG] Cancel: state %d -> IDLE\n", state);
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
    Debug::printf("[MSG] error response: code=%d msg=%s\n", (int)code, message.c_str());
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
            Debug::printf("[MSG] permission expired: user=%s\n", perm.user_id.c_str());
            return true;  // 已处理（失败）
        }
        if (lockId >= 0 && lockId < LOCK_COUNT && perm.lock_perm[lockId]) {
            // 本地有权限，开锁
            LockControl::openLock(lockId);
            Logger::log(perm.user_id, fingerprintId, lockId,
                        "open", "success", "local_cache");
            Debug::printf("[MSG] offline unlock success: user=%s lock=%d\n",
                          perm.user_id.c_str(), lockId);
            return true;
        } else {
            // 本地权限不足
            Logger::log(perm.user_id, fingerprintId, lockId,
                        "open", "fail", "local_no_permission");
            Debug::printf("[MSG] offline permission denied: user=%s lock=%d\n",
                          perm.user_id.c_str(), lockId);
            return true;  // 已处理（失败）
        }
    }
    return false;  // 本地无缓存，调用方按本地权威策略拒绝
}

void MessageHandler::checkTimeout() {
    unsigned long now = millis();
    if (state == STATE_WAIT_FINGER && (now - stateEnterTime > WAIT_FINGER_TIMEOUT_MS)) {
        Debug::println(F("[MSG] wait fingerprint timeout, return to idle"));
        state = STATE_IDLE;
        pendingLockId = -1;
        pendingFingerprintId = -1;
    }
}

void MessageHandler::startEnroll(int fingerprintId, const String &userId,
                                 const String &requestMsgId) {
    enrollFingerprintId = fingerprintId;
    enrollUserId = userId;
    enrollRequestMsgId = requestMsgId;
    setState(STATE_ENROLLING);
    Debug::printf("[MSG] start enroll fingerprint: id=%d user=%s\n", fingerprintId, userId.c_str());
}

void MessageHandler::update() {
    unsigned long now = millis();

    if (permissionSyncActive &&
        now - permissionSyncStartedAt >= PERMISSION_SYNC_TIMEOUT_MS) {
        Debug::println(F("[MSG] permission sync timed out; keeping current permissions"));
        resetPermissionSync();
    }

    // 权限丢失上报（每 60 秒重试，直到 PC 处理）
    if (permLostPending && MeshComm::isConnected()) {
        if (now - lastPermLostReport >= PERM_LOST_REPORT_INTERVAL || lastPermLostReport == 0) {
            lastPermLostReport = now;
            sendMessage("PERM_LOST", "{\"reason\":\"crc_failed\"}");
            Debug::println(F("[MSG] report PERM_LOST"));
        }
    }

    switch (state) {
        case STATE_WAIT_FINGER: {
            checkTimeout();
            if (state != STATE_WAIT_FINGER) break;

            // 轮询指纹模块
            int fpId = Fingerprint::verifyFingerprint();
            if (fpId >= 0) {
                // 匹配成功
                pendingFingerprintId = fpId;
                verifyFailCount = 0;

                // Authentication is always decided by the cabinet's local
                // permission cache. The network is for management/sync only;
                // it must never be part of the unlock critical path.
                if (pendingLockId >= 0 &&
                    !tryLocalPermission(fpId, pendingLockId)) {
                    Logger::log("", fpId, pendingLockId,
                                "open", "fail", "permission_not_synced");
                    Debug::printf("[MSG] no local permission cache for fp=%d\n", fpId);
                }
                state = STATE_IDLE;
                pendingLockId = -1;
                pendingFingerprintId = -1;
            } else if (fpId == -2) {
                // 读取错误（非未匹配），不立即退出，继续等待
            }
            // fpId == -1 表示无手指，继续等待
            break;
        }

        case STATE_ENROLLING: {
            // 执行指纹录入（阻塞式，由命令触发）
            bool ok = Fingerprint::enrollFingerprint(enrollFingerprintId);
            String data = "{\"fingerprint_id\":" + String(enrollFingerprintId) +
                          ",\"user_id\":\"" + enrollUserId + "\",\"result\":\"" +
                          (ok ? "success" : "fail") + "\"";

            if (ok) {
                uint8_t templateBuf[FP_TEMPLATE_BUF_SIZE];
                size_t templateLen = 0;
                if (Fingerprint::readTemplate(enrollFingerprintId, templateBuf,
                                              sizeof(templateBuf), templateLen)) {
                    const char *hexChars = "0123456789ABCDEF";
                    String templateHex;
                    templateHex.reserve(templateLen * 2);
                    for (size_t i = 0; i < templateLen; i++) {
                        templateHex += hexChars[(templateBuf[i] >> 4) & 0x0F];
                        templateHex += hexChars[templateBuf[i] & 0x0F];
                    }
                    data += ",\"template_hex\":\"" + templateHex + "\"";
                }
            } else {
                data += ",\"message\":\"" + Fingerprint::lastError() + "\"";
            }
            data += "}";
            sendMessage("ADD_FINGERPRINT_RESULT", data, enrollRequestMsgId);

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
            enrollRequestMsgId = "";
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
        Debug::printf("[MSG] JSON parse failed: %s\n", err.c_str());
        sendError(ERR_JSON_PARSE, "json parse failed");
        return;
    }

    const char *cmd = doc["cmd"] | "";
    if (strlen(cmd) == 0) {
        Debug::println(F("[MSG] message missing cmd field"));
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

    if (!MessageHmac::verify(doc, cfg.hmac_enabled, cfg.hmac_key)) {
        sendError(ERR_PERMISSION_DENIED, "hmac verification failed", msgId);
        return;
    }

    Debug::printf("[MSG] process command: %s (msg_id=%s)\n", cmd, msgId);

    // 命令分发
    if (strcmp(cmd, "AUTH_OK") == 0) {
        cmdAuthOk(data, msgId);
    } else if (strcmp(cmd, "AUTH_FAIL") == 0) {
        cmdAuthFail(data, msgId);
    } else if (strcmp(cmd, "SYNC_PERMISSIONS") == 0) {
        cmdSyncPermissions(data, msgId);
    } else if (strcmp(cmd, "BEGIN_PERMISSION_SYNC") == 0) {
        cmdBeginPermissionSync(data, msgId);
    } else if (strcmp(cmd, "SYNC_PERMISSION") == 0) {
        cmdSyncPermission(data, msgId);
    } else if (strcmp(cmd, "COMMIT_PERMISSION_SYNC") == 0) {
        cmdCommitPermissionSync(data, msgId);
    } else if (strcmp(cmd, "CLEAR_PERMISSIONS") == 0) {
        cmdClearPermissions(data, msgId);
    } else if (strcmp(cmd, "ADD_FINGERPRINT") == 0) {
        cmdAddFingerprint(data, msgId);
    } else if (strcmp(cmd, "RESTORE_FINGERPRINT") == 0) {
        cmdRestoreFingerprint(data, msgId);
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
#ifdef ENABLE_SD_CARD
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
#endif // ENABLE_SD_CARD
    } else if (strcmp(cmd, "HEARTBEAT_ACK") == 0) {
        // 心跳回应，无需处理
    } else if (strcmp(cmd, "LOG_REPORT_ACK") == 0) {
        const char *result = data["result"] | "fail";
        Logger::handleReportAck(String(msgId), String(result));
    } else if (strcmp(cmd, "PERM_LOST_ACK") == 0) {
        // 权限丢失已确认，停止重发
        permLostPending = false;
        Debug::println(F("[MSG] PERM_LOST acknowledged by host"));
    } else {
        Debug::printf("[MSG] unknown command: %s\n", cmd);
        sendError(ERR_UNKNOWN_CMD, String("unknown command: ") + cmd, msgId);
    }
}

// ====== 命令处理实现 ======
void MessageHandler::cmdAuthOk(const JsonObject &data, const String &msgId) {
    // Legacy compatibility only. A remote AUTH_OK must never grant access;
    // SYNC_PERMISSIONS is the only supported way to update local authority.
    (void)data;
    Debug::println(F("[MSG] ignored legacy AUTH_OK; cabinet data is authoritative"));
    sendAck(msgId, "ignored_local_authority");
}

void MessageHandler::cmdAuthFail(const JsonObject &data, const String &msgId) {
    (void)data;
    Debug::println(F("[MSG] ignored legacy AUTH_FAIL; cabinet data is authoritative"));
    sendAck(msgId, "ignored_local_authority");
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
            if (perm.role != ROLE_ADMIN) perm.lock_perm[0] = false;

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

    Debug::printf("[MSG] synced %d permissions, version=%u\n", count, version);
    String respData = "{\"count\":" + String(count) +
                      ",\"version\":" + String(version) +
                      ",\"result\":\"" + (ok ? "success" : "fail") + "\"}";
    sendMessage("SYNC_ACK", respData, msgId);
}

void MessageHandler::resetPermissionSync() {
    if (permissionSyncBuffer != nullptr) delete[] permissionSyncBuffer;
    if (permissionSyncReceived != nullptr) delete[] permissionSyncReceived;
    permissionSyncBuffer = nullptr;
    permissionSyncReceived = nullptr;
    permissionSyncExpected = 0;
    permissionSyncReceivedCount = 0;
    permissionSyncVersion = 0;
    permissionSyncStartedAt = 0;
    permissionSyncActive = false;
}

bool MessageHandler::parsePermission(const JsonObject &data, UserPermission &perm) {
    perm.fingerprint_id = data["fingerprint_id"] | -1;
    perm.user_id = data["user_id"] | "";
    perm.name = data["name"] | "";
    perm.role = (UserRole)(data["role"] | (int)ROLE_STUDENT);
    perm.user_id_num = Storage::userIdToNum(perm.user_id);

    JsonObject lp = data["lock_permissions"].as<JsonObject>();
    perm.lock_perm[0] = lp["lock_0"] | false;
    perm.lock_perm[1] = lp["lock_1"] | false;
    perm.lock_perm[2] = lp["lock_2"] | false;
    perm.lock_perm[3] = lp["lock_3"] | false;
    if (perm.role != ROLE_ADMIN) perm.lock_perm[0] = false;

    const char *expireDate = data["expire_date"] | "";
    perm.expire_days = strlen(expireDate) > 0
        ? Storage::dateToDays(String(expireDate)) : 0xFFFFFFFF;
    perm.valid = perm.fingerprint_id >= 0 && perm.user_id.length() > 0;
    return perm.valid;
}

void MessageHandler::cmdBeginPermissionSync(const JsonObject &data, const String &msgId) {
    int total = data["total"] | -1;
    uint32_t version = data["version"] | 0;
    if (total < 0 || total > PERM_MAX_USERS) {
        sendError(ERR_BAD_REQUEST, "invalid permission sync total", msgId);
        return;
    }

    resetPermissionSync();
    if (total > 0) {
        permissionSyncBuffer = new UserPermission[total];
        permissionSyncReceived = new bool[total]();
        if (permissionSyncBuffer == nullptr || permissionSyncReceived == nullptr) {
            resetPermissionSync();
            sendError(ERR_INTERNAL, "permission sync allocation failed", msgId);
            return;
        }
    }
    permissionSyncExpected = total;
    permissionSyncVersion = version;
    permissionSyncStartedAt = millis();
    permissionSyncActive = true;
    sendAck(msgId, "permission_sync_started");
}

void MessageHandler::cmdSyncPermission(const JsonObject &data, const String &msgId) {
    UserPermission perm;
    if (!parsePermission(data, perm)) {
        sendError(ERR_BAD_REQUEST, "invalid permission record", msgId);
        return;
    }

    uint32_t version = data["version"] | 0;
    if (permissionSyncActive) {
        int sequence = data["sequence"] | -1;
        int total = data["total"] | -1;
        if (version != permissionSyncVersion || total != permissionSyncExpected ||
            sequence < 0 || sequence >= permissionSyncExpected ||
            permissionSyncBuffer == nullptr || permissionSyncReceived == nullptr) {
            sendError(ERR_BAD_REQUEST, "permission sync sequence mismatch", msgId);
            return;
        }
        permissionSyncBuffer[sequence] = perm;
        if (!permissionSyncReceived[sequence]) {
            permissionSyncReceived[sequence] = true;
            permissionSyncReceivedCount++;
        }
        permissionSyncStartedAt = millis();
        sendAck(msgId, "permission_staged");
        return;
    }

    // Legacy incremental updates remain supported when no transaction exists.
    bool ok = Storage::savePermission(perm, version);
    sendAck(msgId, ok ? "permission_synced" : "permission_sync_failed");
}

void MessageHandler::cmdCommitPermissionSync(const JsonObject &data, const String &msgId) {
    uint32_t version = data["version"] | 0;
    int total = data["total"] | -1;
    if (!permissionSyncActive || version != permissionSyncVersion || total != permissionSyncExpected ||
        permissionSyncReceivedCount != permissionSyncExpected) {
        Debug::printf("[MSG] incomplete permission sync: received=%d expected=%d\n",
                      permissionSyncReceivedCount, permissionSyncExpected);
        resetPermissionSync();
        sendError(ERR_BAD_REQUEST, "permission sync incomplete", msgId);
        return;
    }

    int count = permissionSyncExpected;
    bool ok = Storage::replaceAllPermissions(permissionSyncBuffer, count, version);
    resetPermissionSync();
    if (ok) permLostPending = false;

    String respData = "{\"count\":" + String(count) +
                      ",\"version\":" + String(version) +
                      ",\"result\":\"" + (ok ? "success" : "fail") + "\"}";
    sendMessage("SYNC_ACK", respData, msgId);
}

void MessageHandler::cmdClearPermissions(const JsonObject &data, const String &msgId) {
    uint32_t version = data["version"] | 0;
    bool ok = Storage::replaceAllPermissions(nullptr, 0, version);
    sendAck(msgId, ok ? "permissions_cleared" : "permissions_clear_failed");
}

void MessageHandler::cmdAddFingerprint(const JsonObject &data, const String &msgId) {
    int fpId = data["fingerprint_id"] | -1;
    String userId = data["user_id"] | "";
    bool replace = data["replace"] | false;
    if (fpId < 0) {
        sendError(ERR_FP_TEMPLATE_FORMAT, "invalid fingerprint id", msgId);
        return;
    }

    // 权限缓存中存在该 ID 并不代表传感器已经录入模板。
    // 必须查询 AS608 自身，否则“先同步权限、后录入”的正常流程会被误判为重复。
    if (Fingerprint::templateExists(fpId) && !replace) {
        sendError(ERR_FP_ID_EXISTS, "fingerprint id already exists", msgId);
        return;
    }

    sendAck(msgId, "enrolling");
    startEnroll(fpId, userId, msgId);
}

void MessageHandler::cmdRestoreFingerprint(const JsonObject &data, const String &msgId) {
    int fpId = data["fingerprint_id"] | -1;
    String userId = data["user_id"] | "";
    bool replace = data["replace"] | true;
    const char *hex = data["template_hex"] | "";

    if (fpId < 0 || fpId >= FINGER_MAX_USERS) {
        sendError(ERR_FP_TEMPLATE_FORMAT, "invalid fingerprint id", msgId);
        return;
    }
    if (state != STATE_IDLE) {
        sendError(ERR_INTERNAL, "device busy", msgId);
        return;
    }
    if (Fingerprint::templateExists(fpId) && !replace) {
        sendError(ERR_FP_ID_EXISTS, "fingerprint id already exists", msgId);
        return;
    }

    size_t hexLen = strlen(hex);
    if (hexLen == 0 || (hexLen % 2) != 0 || hexLen / 2 > FP_TEMPLATE_BUF_SIZE) {
        sendError(ERR_FP_TEMPLATE_FORMAT, "invalid template hex", msgId);
        return;
    }

    size_t binLen = hexLen / 2;
    uint8_t *buf = (uint8_t *)malloc(binLen);
    if (!buf) {
        sendError(ERR_FLASH_WRITE, "out of memory", msgId);
        return;
    }
    for (size_t i = 0; i < binLen; i++) {
        uint8_t hi = hexCharToVal(hex[i * 2]);
        uint8_t lo = hexCharToVal(hex[i * 2 + 1]);
        if (hi == 0xFF || lo == 0xFF) {
            free(buf);
            sendError(ERR_FP_TEMPLATE_FORMAT, "invalid template hex digit", msgId);
            return;
        }
        buf[i] = (uint8_t)((hi << 4) | lo);
    }

    bool ok = Fingerprint::writeTemplate(fpId, buf, binLen);
    free(buf);
    if (!ok) {
        sendError(ERR_FP_COMM_FAILED, Fingerprint::lastError().c_str(), msgId);
        return;
    }

    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    cfg.fingerprint_count = Fingerprint::getFingerprintCount();
    Storage::saveDeviceConfig(cfg);

    String respData = "{\"fingerprint_id\":" + String(fpId) +
                      ",\"user_id\":\"" + userId + "\"" +
                      ",\"result\":\"success\"}";
    sendMessage("RESTORE_FINGERPRINT_RESULT", respData, msgId);
    sendAck(msgId, "success");
    Debug::printf("[MSG] restored fingerprint id=%d user=%s\n", fpId, userId.c_str());
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
    Debug::println(F("[MSG] cleared all fingerprints and permissions"));
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
    Debug::printf("[MSG] remote control lock %d %s\n", lockId, action.c_str());
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
    if (data.containsKey("hmac_enabled")) cfg.hmac_enabled = data["hmac_enabled"].as<bool>();
    if (data.containsKey("hmac_key")) cfg.hmac_key = data["hmac_key"].as<String>();

    Storage::saveDeviceConfig(cfg);
    sendMessage("CONFIG_SAVED", "{\"result\":\"success\"}", msgId);
    Debug::println(F("[MSG] config updated"));
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
    data += "\"time_synced\":" + String(Storage::isTimeSynced() ? "true" : "false");
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
    Debug::printf("[MSG] report permission list: %d entries\n", count);
}

void MessageHandler::cmdClearLogs(const String &msgId) {
    Logger::clearAll();
    sendMessage("LOGS_CLEARED", "{\"result\":\"success\"}", msgId);
}

void MessageHandler::cmdReboot(const JsonObject &data, const String &msgId) {
    String mode = data["mode"] | "";
    Debug::printf("[MSG] preparing reboot, target mode: %s\n", mode.c_str());
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
        Debug::printf("[MSG] time synced: %u\n", timestamp);
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
    Debug::println(F("[MSG] REGISTER query responded"));
}

// ============================================================
// ====== SD 卡集中存储命令实现（仅根节点响应） ======
// ============================================================
#ifdef ENABLE_SD_CARD

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

    Debug::printf("[MSG] SD_QUERY %s: %u bytes\n", table.c_str(), (unsigned)response.length());
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
            Debug::printf("[MSG] SD_SAVE %s version conflict: base=%u current=%u\n",
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
    Debug::printf("[MSG] SD_SAVE %s: %s\n", table.c_str(), ok ? "success" : "failed");
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
    Debug::printf("[MSG] SD version query: global=%u\n", g);
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
    Debug::printf("[MSG] fingerprint template upload %s[%d]: %s\n",
                  userId.c_str(), fingerIndex, ok ? "success" : "failed");
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

    // 模板 512B -> hex 1024B，单帧可承载
    sendMessage("FP_TEMPLATE_DOWNLOAD_RESPONSE", resp, msgId);
    Debug::printf("[MSG] fingerprint template download %s[%d]: %u bytes\n",
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
    Debug::printf("[MSG] fingerprint template delete %s: %s\n",
                  userId.c_str(), ok ? "success" : "no template");
}

#endif // ENABLE_SD_CARD

// hex 字符转数值（RESTORE_FINGERPRINT 与 SD 模板编解码共用）
uint8_t MessageHandler::hexCharToVal(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    return 0xFF;
}
