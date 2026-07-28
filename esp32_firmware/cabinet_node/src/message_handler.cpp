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
#include "led_indicator.h"
#include "message_hmac.h"
#include "protocol_frame.h"
#include "app_protocol.h"
#include "cmd_ids.h"
#include <string.h>
#ifdef ENABLE_SD_CARD
#include "sd_storage.h"
#endif

// 状态机超时时间
#define WAIT_FINGER_TIMEOUT_MS  30000   // 等待指纹超时 30 秒（保留用于异常恢复）
#define PERM_LOST_REPORT_INTERVAL 60000 // PERM_LOST 上报间隔 60 秒
#define PERMISSION_SYNC_TIMEOUT_MS 30000
// V2.7：验证成功后的操作窗口（10 秒）
#define VERIFY_WINDOW_TIMEOUT_MS VERIFY_WINDOW_MS
// 录入后检测阶段超时（30 秒内未按下手指则判失败）
#define ENROLL_VERIFY_TIMEOUT_MS 30000
#define FP_TEST_IDLE_TIMEOUT_MS 60000UL
#define FP_TEST_POLL_INTERVAL_MS 200UL
// 新流程录入总步数：4 次采集 + 2 次录入内验证 + 1 次检测 = 7
#define ENROLL_TOTAL_STEPS 7

// 2000-01-01 00:00:00 的 Unix 时间戳
#define UNIX_2000_01_01  946684800UL

// 静态成员初始化
int MessageHandler::pendingLockId        = -1;
int MessageHandler::pendingFingerprintId = -1;
MessageHandler::VerifyState MessageHandler::state = STATE_IDLE;
unsigned long MessageHandler::stateEnterTime = 0;
UserPermission MessageHandler::verifiedPerms;
bool MessageHandler::verifiedPermsValid  = false;
int  MessageHandler::enrollFingerprintId = -1;
String MessageHandler::enrollUserId      = "";
String MessageHandler::enrollRequestMsgId = "";
String MessageHandler::enrollLastPhaseCode = "";
bool MessageHandler::enrollIsBackup      = false;
String MessageHandler::fingerprintTestToken = "";
int MessageHandler::fingerprintTestSourceId = -1;
unsigned long MessageHandler::fingerprintTestLastActivity = 0;
unsigned long MessageHandler::fingerprintTestLastPoll = 0;
unsigned long MessageHandler::fingerprintTestLastActivityReport = 0;
bool MessageHandler::fingerprintTestFingerDown = false;
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

// Rebuild legacy full JSON so existing command handlers keep working when the
// outer envelope is binary. Payload is the `data` object (JSON) when present.
static String cabinetAppViewToLegacyJson(const AppMessageView &view) {
    const char *cmdName = appCmdName(view.cmd_id);
    if (cmdName == nullptr) cmdName = "UNKNOWN";

    char did[APP_DEVICE_ID_MAX + 1];
    did[0] = '\0';
    if (view.device_id_len > 0 && view.device_id != nullptr) {
        size_t n = view.device_id_len;
        if (n > APP_DEVICE_ID_MAX) n = APP_DEVICE_ID_MAX;
        memcpy(did, view.device_id, n);
        did[n] = '\0';
    }

    String dataJson = "{}";
    if (view.payload_len > 0 && view.payload != nullptr) {
        // CONTROL_LOCK binary payload → JSON data object
        if (view.cmd_id == CMD_CONTROL_LOCK) {
            uint8_t lockId = 0, action = 0;
            if (unpackControlLock(view.payload, view.payload_len, lockId, action)) {
                dataJson = "{\"lock_id\":" + String(lockId) +
                           ",\"action\":\"" + String(action == 1 ? "close" : "open") + "\"}";
            }
        } else if (view.cmd_id == CMD_TIME_SYNC && view.payload_len >= 4) {
            uint32_t ts = rdU32(view.payload);
            dataJson = "{\"timestamp\":" + String(ts) + "}";
        } else {
            dataJson = "";
            dataJson.reserve(view.payload_len + 1);
            for (uint16_t i = 0; i < view.payload_len; i++) {
                dataJson += (char)view.payload[i];
            }
            if (dataJson.length() == 0 || (dataJson[0] != '{' && dataJson[0] != '[')) {
                dataJson = "{}";
            }
        }
    }

    String msgId = (view.msg_id != 0) ? String(view.msg_id) : String("");
    return ProtocolFrame::buildMessage(String(cmdName), String(did), dataJson, msgId);
}

void MessageHandler::handleIncomingApp(const AppMessageView &view) {
    if (view.cmd_id == CMD_HEARTBEAT_ACK || view.cmd_id == CMD_ACK) {
        // Presence / reliability only — no business action.
        return;
    }
    if (view.cmd_id == CMD_HEARTBEAT) {
        // Cabinets do not answer peer heartbeats.
        return;
    }
    String legacy = cabinetAppViewToLegacyJson(view);
    handleIncoming(legacy);
}

void MessageHandler::init() {
    // V2.7：开机即进入常态指纹轮询（不再等待按键触发）
    state = STATE_WAIT_FINGER;
    pendingLockId = -1;
    pendingFingerprintId = -1;
    stateEnterTime = millis();
    verifiedPermsValid = false;
    enrollFingerprintId = -1;
    enrollRequestMsgId = "";
    enrollIsBackup = false;
    fingerprintTestToken = "";
    fingerprintTestSourceId = -1;
    fingerprintTestLastActivity = 0;
    fingerprintTestLastPoll = 0;
    fingerprintTestLastActivityReport = 0;
    fingerprintTestFingerDown = false;
    verifyFailCount = 0;
    resetPermissionSync();

    // 检查权限数据是否丢失（启动时 CRC 都失败）
    if (Storage::isPermissionLost()) {
        permLostPending = true;
        Debug::println(F("[MSG] permission data lost, pending PERM_LOST report"));
    }

    FpLed::setOff();
    if (Fingerprint::isReady() && Fingerprint::templateExists(FP_TEMP_SLOT)) {
        Fingerprint::deleteFingerprint(FP_TEMP_SLOT);
        Debug::println(F("[MSG] cleaned temp slot 0 at startup"));
    }
    Fingerprint::setBackgroundVerifyEnabled(true);
    Debug::println(F("[MSG] message handler init complete (V2.7 windowed verify)"));
}

MessageHandler::VerifyState MessageHandler::getState() {
    return state;
}

void MessageHandler::setState(VerifyState s) {
    Debug::printf("[MSG] state switch: %d -> %d\n", state, s);
    state = s;
    stateEnterTime = millis();
    Fingerprint::setBackgroundVerifyEnabled(s == STATE_WAIT_FINGER);
}

void MessageHandler::onKeyPressed(int lockId) {
    // V2.7 流程反转：按键仅在验证窗口内生效（开锁）
    if (state == STATE_VERIFIED_WINDOW) {
        if (!verifiedPermsValid) {
            Debug::println(F("[MSG] window active but no verified perms, ignore key"));
            return;
        }
        openIfPermitted(lockId);
        return;
    }

    if (state == STATE_FINGERPRINT_TEST) {
        finishFingerprintTest("cancelled");
        return;
    }
    // 其他状态（WAIT_FINGER / ENROLLING / IDLE）忽略开锁键
    Debug::printf("[MSG] ignore key %d (current state %d, not in verify window)\n",
                  lockId, (int)state);
}

void MessageHandler::onCancel() {
    if (state == STATE_IDLE || state == STATE_WAIT_FINGER) {
        // 空闲或等待指纹时取消无意义，但允许灭灯复位
        if (state == STATE_WAIT_FINGER) {
            FpLed::setOff();
        }
        return;
    }
    Debug::printf("[MSG] Cancel: state %d -> WAIT_FINGER\n", (int)state);

    if (state == STATE_VERIFIED_WINDOW) {
        // 取消验证窗口：清空权限，回 WAIT_FINGER
        sendVerifyWindowEvent("cancel");
        verifiedPermsValid = false;
        FpLed::setOff();
        setState(STATE_WAIT_FINGER);
        return;
    }

    if (state == STATE_ENROLLING || state == STATE_ENROLL_VERIFY) {
        if (state == STATE_ENROLL_VERIFY) {
            // 检测阶段取消：清理临时槽
            Fingerprint::deleteFingerprint(FP_TEMP_SLOT);
        } else {
            Fingerprint::enrollAbort("user_cancelled");
        }
        if (enrollRequestMsgId.length() > 0) {
            String data = "{\"fingerprint_id\":" + String(enrollFingerprintId) +
                          ",\"user_id\":\"" + enrollUserId +
                          "\",\"is_backup\":" + String(enrollIsBackup ? "true" : "false") +
                          ",\"result\":\"fail\",\"message\":\"user_cancelled\"}";
            sendMessage("ADD_FINGERPRINT_RESULT", data, enrollRequestMsgId);
        }
        enrollFingerprintId = -1;
        enrollUserId = "";
        enrollRequestMsgId = "";
        enrollLastPhaseCode = "";
        enrollIsBackup = false;
        FpLed::setOff();
        setState(STATE_WAIT_FINGER);
    }
}

// ====== 消息发送（通过 MeshComm，二进制信封 + data JSON 负载） ======
bool MessageHandler::sendMessage(const String &cmd, const String &dataJson,
                                 const String &msgId) {
    return MeshComm::sendMessage(cmd, dataJson, msgId);
}

void MessageHandler::sendAck(const String &msgId, const String &result) {
    if (msgId.length() == 0) return;
    uint16_t mid = (uint16_t)msgId.toInt();
    uint8_t pl[48];
    int pln = packAck(pl, (int)sizeof(pl), mid, 0, result.c_str());
    if (pln > 0 &&
        MeshComm::sendApp(CMD_ACK, mid, APP_FLAG_IS_ACK, pl, (uint16_t)pln, nullptr)) {
        return;
    }
    String data = "{\"result\":\"" + result + "\"}";
    sendMessage("ACK", data, msgId);
}

void MessageHandler::sendError(ErrorCode code, const String &message,
                               const String &msgId) {
    uint16_t mid = msgId.length() > 0 ? (uint16_t)msgId.toInt() : 0;
    uint8_t pl[160];
    int pln = packError(pl, (int)sizeof(pl), mid, (uint16_t)code, message.c_str());
    if (pln > 0 &&
        MeshComm::sendApp(CMD_ERROR, mid, APP_FLAG_IS_ERROR, pl, (uint16_t)pln, nullptr)) {
        Debug::printf("[MSG] error response: code=%d msg=%s\n", (int)code, message.c_str());
        return;
    }
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

// ====== V2.7：窗口化验证辅助方法 ======
// 验证成功后载入权限到窗口态（不立即开锁）
bool MessageHandler::loadVerifiedPermission(int as608Id) {
    // ID=0 是临时录入槽位，永远无开锁权限
    if (as608Id == FP_TEMP_SLOT) {
        Debug::println(F("[MSG] rejected temp slot id=0 (no permission)"));
        return false;
    }
    UserPermission perm;
    if (Storage::findPermissionByAs608Id(as608Id, perm) && perm.valid) {
        // 检查权限是否过期
        if (isPermissionExpired(perm)) {
            Logger::log(perm.user_id, as608Id, -1,
                        "open", "fail", "permission_expired");
            Debug::printf("[MSG] permission expired: user=%s\n", perm.user_id.c_str());
            return false;
        }
        verifiedPerms = perm;
        verifiedPermsValid = true;
        return true;
    }
    return false;
}

// 在验证窗口态按键时尝试开锁
bool MessageHandler::openIfPermitted(int lockId) {
    if (!verifiedPermsValid) return false;
    if (lockId >= 0 && lockId < LOCK_COUNT && verifiedPerms.lock_perm[lockId]) {
        // 有权限，开锁
        LockControl::openLock(lockId);
        Logger::log(verifiedPerms.user_id, verifiedPerms.local_fp_id, lockId,
                    "open", "success",
                    verifiedPerms.is_backup ? "local_backup" : "local_cache");
        Debug::printf("[MSG] windowed unlock success: user=%s lock=%d backup=%d\n",
                      verifiedPerms.user_id.c_str(), lockId,
                      verifiedPerms.is_backup ? 1 : 0);
        // 开锁后结束窗口
        sendVerifyWindowEvent("unlocked", lockId);
        verifiedPermsValid = false;
        FpLed::setOff();
        setState(STATE_WAIT_FINGER);
        return true;
    } else {
        // 本地权限不足（窗口未结束，可继续按其他有权限的锁）
        Logger::log(verifiedPerms.user_id, verifiedPerms.local_fp_id, lockId,
                    "open", "fail", "no_permission_in_window");
        Debug::printf("[MSG] no permission for lock %d in window (user=%s)\n",
                      lockId, verifiedPerms.user_id.c_str());
        // 红灯短闪一次提示无权限（不结束窗口）
        FpLed::setFail();
        return false;
    }
}

// 上报验证窗口事件（进入/退出/超时/取消/开锁）
void MessageHandler::sendVerifyWindowEvent(const char *event, int lockId) {
    String userId = verifiedPermsValid ? verifiedPerms.user_id : String("");
    int fpId = verifiedPermsValid ? verifiedPerms.local_fp_id : -1;
    String data = "{\"event\":\"" + String(event) + "\"";
    data += ",\"user_id\":\"" + userId + "\"";
    data += ",\"fingerprint_id\":" + String(fpId);
    if (lockId >= 0) {
        data += ",\"lock_id\":" + String(lockId);
    }
    data += "}";
    sendMessage("VERIFY_WINDOW_EVENT", data);
}

void MessageHandler::checkTimeout() {
    unsigned long now = millis();
    // V2.7：验证窗口超时（10 秒未操作）
    if (state == STATE_VERIFIED_WINDOW &&
        (now - stateEnterTime > VERIFY_WINDOW_TIMEOUT_MS)) {
        Debug::println(F("[MSG] verify window timeout, return to WAIT_FINGER"));
        sendVerifyWindowEvent("timeout");
        if (verifiedPermsValid) {
            Logger::log(verifiedPerms.user_id, verifiedPerms.local_fp_id, -1,
                        "open", "fail", "window_timeout");
        }
        verifiedPermsValid = false;
        FpLed::setOff();
        setState(STATE_WAIT_FINGER);
    }
    // WAIT_FINGER 异常恢复（30 秒无任何事件，复位 LED）
    if (state == STATE_WAIT_FINGER &&
        (now - stateEnterTime > WAIT_FINGER_TIMEOUT_MS)) {
        // 不退出 WAIT_FINGER（常态轮询），仅复位 LED 到识别中
        FpLed::setIdentifying();
        stateEnterTime = now;  // 重置计时
    }
}

void MessageHandler::startEnroll(int fingerprintId, const String &userId,
                                 const String &requestMsgId) {
    // 新流程：固定录入到临时槽 FP_TEMP_SLOT(0)，检测通过后迁移到分配的真实 ID
    enrollFingerprintId = fingerprintId;
    enrollUserId = userId;
    enrollRequestMsgId = requestMsgId;
    enrollLastPhaseCode = "";
    Fingerprint::enrollBegin(FP_TEMP_SLOT);
    setState(STATE_ENROLLING);
    Debug::printf("[MSG] start enroll to temp slot %d, user=%s (4+2 steps + verify)\n",
                  FP_TEMP_SLOT, userId.c_str());
    // 立即上报第一步提示
    String prog = "{\"phase\":\"place_1\",\"step\":1,\"total\":";
    prog += String(ENROLL_TOTAL_STEPS);
    prog += ",\"hint\":\"请将手指按在指纹头上（第1/4次）\","
            "\"fingerprint_id\":0}";
    sendMessage("ENROLL_PROGRESS", prog, requestMsgId);
    enrollLastPhaseCode = "place_1";
}

// 发送录入进度帧（封装 JSON 拼接）
void MessageHandler::sendEnrollProgress(const char *phase, int step, int total,
                                        const char *hint) {
    String prog = "{\"phase\":\"";
    prog += phase;
    prog += "\",\"step\":";
    prog += String(step);
    prog += ",\"total\":";
    prog += String(total);
    prog += ",\"hint\":\"";
    for (const char *p = hint; p && *p; ++p) {
        if (*p == '"' || *p == '\\') prog += '\\';
        prog += *p;
    }
    prog += "\",\"fingerprint_id\":0}";
    sendMessage("ENROLL_PROGRESS", prog, enrollRequestMsgId);
}

void MessageHandler::sendFingerprintTestEvent(const char *event, int confidence) {
    String data = "{\"event\":\"" + String(event != nullptr ? event : "unknown") + "\"";
    data += ",\"test_token\":\"" + fingerprintTestToken + "\"";
    data += ",\"fingerprint_id\":" + String(fingerprintTestSourceId);
    data += ",\"confidence\":" + String(confidence);
    data += ",\"idle_timeout_seconds\":60}";
    sendMessage("FINGERPRINT_TEST_EVENT", data);
}

void MessageHandler::finishFingerprintTest(const char *event) {
    if (state == STATE_FINGERPRINT_TEST && event != nullptr) {
        sendFingerprintTestEvent(event);
    }
    Fingerprint::deleteFingerprint(FP_TEMP_SLOT);
    fingerprintTestToken = "";
    fingerprintTestSourceId = -1;
    fingerprintTestLastActivity = 0;
    fingerprintTestLastPoll = 0;
    fingerprintTestLastActivityReport = 0;
    fingerprintTestFingerDown = false;
    FpLed::setOff();
    setState(STATE_WAIT_FINGER);
}

// 录入流程结束（含检测）：构造 ADD_FINGERPRINT_RESULT 回报，清理状态
void MessageHandler::finishEnrollWithVerify(bool verifyOk) {
    bool ok = verifyOk;
    String data = "{\"fingerprint_id\":" + String(enrollFingerprintId) +
                  ",\"user_id\":\"" + enrollUserId + "\"" +
                  ",\"is_backup\":" + String(enrollIsBackup ? "true" : "false") +
                  ",\"result\":\"" + (ok ? "success" : "fail") + "\"";
    if (ok) {
        // 读取迁移后真实 ID 的模板，上报给上位机备份
        uint8_t templateBuf[FP_TEMPLATE_BUF_SIZE];
        size_t templateLen = 0;
        if (enrollFingerprintId != FP_TEMP_SLOT &&
            Fingerprint::readTemplate(enrollFingerprintId, templateBuf,
                                      sizeof(templateBuf), templateLen) &&
            templateLen > 0) {
            const char *hexChars = "0123456789ABCDEF";
            String templateHex;
            templateHex.reserve(templateLen * 2);
            for (size_t i = 0; i < templateLen; i++) {
                templateHex += hexChars[(templateBuf[i] >> 4) & 0x0F];
                templateHex += hexChars[templateBuf[i] & 0x0F];
            }
            data += ",\"template_hex\":\"" + templateHex + "\"";
        }
        data += ",\"local_fp_id\":" + String(enrollFingerprintId);

        // 副指纹：写入本地权限表（主指纹的权限由上位机后续 SYNC_PERMISSION 下发）
        if (enrollIsBackup) {
            UserPermission backupPerm;
            if (Storage::findPrimaryPermission(enrollUserId, backupPerm)) {
                backupPerm.local_fp_id = enrollFingerprintId;
                backupPerm.fingerprint_id = enrollFingerprintId;
                backupPerm.is_backup = true;
                backupPerm.valid = true;
            } else {
                backupPerm.fingerprint_id = enrollFingerprintId;
                backupPerm.local_fp_id = enrollFingerprintId;
                backupPerm.is_backup = true;
                backupPerm.user_id = enrollUserId;
                backupPerm.user_id_num = Storage::userIdToNum(enrollUserId);
                backupPerm.name = "";
                backupPerm.role = ROLE_STUDENT;
                for (int i = 0; i < LOCK_COUNT; i++) {
                    backupPerm.lock_perm[i] = false;
                }
                backupPerm.expire_days = 0xFFFFFFFF;
                backupPerm.valid = true;
            }
            data += Storage::addBackupFingerprint(backupPerm) ? ",\"backup_saved\":true"
                                                              : ",\"backup_saved\":false";
        }

        DeviceConfig cfg;
        Storage::loadDeviceConfig(cfg);
        cfg.fingerprint_count = Fingerprint::getFingerprintCount();
        Storage::saveDeviceConfig(cfg);
    } else {
        data += ",\"message\":\"" + Fingerprint::lastError() + "\"";
    }
    data += "}";
    sendMessage("ADD_FINGERPRINT_RESULT", data, enrollRequestMsgId);
    // 清理录入状态，回到常态轮询
    enrollFingerprintId = -1;
    enrollUserId = "";
    enrollRequestMsgId = "";
    enrollLastPhaseCode = "";
    enrollIsBackup = false;
    FpLed::setOff();
    setState(STATE_WAIT_FINGER);
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

            // V2.7：常态轮询指纹模块（不再由按键触发）
            // 识别中：绿灯慢闪
            if (FpLed::getState() != FpLed::STATE_IDENTIFYING &&
                FpLed::getState() != FpLed::STATE_FAIL) {
                FpLed::setIdentifying();
            }

            // AS608 commands can consume three consecutive 1s UART timeouts.
            // The background task owns idle scanning so Mesh/UART stays responsive.
            int as608Id = -1;
            if (Fingerprint::takeBackgroundVerifyResult(as608Id)) {
                // 匹配成功：尝试载入权限进入 10s 窗口
                pendingFingerprintId = as608Id;
                verifyFailCount = 0;

                // Authentication is always decided by the cabinet's local
                // permission cache. The network is for management/sync only;
                // it must never be part of the unlock critical path.
                if (loadVerifiedPermission(as608Id)) {
                    // 权限载入成功：进入 10s 操作窗口，绿灯常亮
                    FpLed::setSuccess();
                    setState(STATE_VERIFIED_WINDOW);
                    sendVerifyWindowEvent("enter");
                    Debug::printf("[MSG] fingerprint verified: as608_id=%d user=%s, enter 10s window\n",
                                  as608Id, verifiedPerms.user_id.c_str());
                } else {
                    // 模板存在但权限缓存未同步或已过期：红灯闪烁
                    Logger::log("", as608Id, -1,
                                "open", "fail", "permission_not_synced");
                    Debug::printf("[MSG] no local permission cache for as608_id=%d\n", as608Id);
                    FpLed::setFail();
                    // setFail 完成后会自动回 OFF，update() 下一轮会重新 setIdentifying
                }
            }
            break;
        }

        case STATE_VERIFIED_WINDOW: {
            checkTimeout();
            // 窗口期内保持绿灯常亮（由 FpLed::update 维护）
            // 按键事件由 onKeyPressed 处理
            break;
        }

        case STATE_ENROLLING: {
            // 非阻塞分步录入：4 次采集 + 2 次录入内验证（存到临时槽 ID=0）
            bool changed = Fingerprint::enrollTick();
            EnrollPhase ph = Fingerprint::enrollPhase();
            const char *code = Fingerprint::enrollPhaseCode();

            if (changed && code != nullptr && enrollLastPhaseCode != code) {
                enrollLastPhaseCode = code;
                if (ph != ENROLL_DONE_OK && ph != ENROLL_DONE_FAIL) {
                    String prog = "{\"phase\":\"";
                    prog += code;
                    prog += "\",\"step\":";
                    prog += String(Fingerprint::enrollStepIndex());
                    prog += ",\"total\":";
                    prog += String(ENROLL_TOTAL_STEPS);
                    prog += ",\"hint\":\"";
                    // 简单转义引号
                    const char *hint = Fingerprint::enrollPhaseHint();
                    for (const char *p = hint; p && *p; ++p) {
                        if (*p == '"' || *p == '\\') prog += '\\';
                        prog += *p;
                    }
                    prog += "\",\"fingerprint_id\":0";
                    prog += ",\"is_backup\":";
                    prog += enrollIsBackup ? "true" : "false";
                    prog += "}";
                    sendMessage("ENROLL_PROGRESS", prog, enrollRequestMsgId);
                }
            }

            if (ph == ENROLL_DONE_OK) {
                if (enrollIsBackup) {
                    // 副指纹：保持原逻辑，直接走完成上报（副指纹有自己的 local_fp_id 分配）
                    finishEnrollWithVerify(true);
                } else {
                    // 主指纹：录入到临时槽成功，进入检测阶段
                    Debug::println(F("[MSG] enroll to temp slot done, enter verify phase"));
                    sendEnrollProgress("verify", 5, ENROLL_TOTAL_STEPS,
                                       "录入完成，请再按一次手指进行检测");
                    enrollLastPhaseCode = "verify";
                    setState(STATE_ENROLL_VERIFY);
                }
            } else if (ph == ENROLL_DONE_FAIL) {
                // 录入失败：清理临时槽，直接回报失败
                if (!enrollIsBackup) {
                    Fingerprint::deleteFingerprint(FP_TEMP_SLOT);
                }
                finishEnrollWithVerify(false);
            }
            break;
        }

        case STATE_ENROLL_VERIFY: {
            // 录入后检测：用户再按一次手指，与临时槽 ID=0 做 1:1 比对
            if (millis() - stateEnterTime > ENROLL_VERIFY_TIMEOUT_MS) {
                Debug::println(F("[MSG] enroll verify timeout"));
                Fingerprint::deleteFingerprint(FP_TEMP_SLOT);
                sendEnrollProgress("verify_timeout", 5, ENROLL_TOTAL_STEPS,
                                   "检测超时，已取消本次录入");
                finishEnrollWithVerify(false);
                break;
            }
            int vr = Fingerprint::verifyOnSlot(FP_TEMP_SLOT);
            if (vr == 1) {
                // 检测通过：优先使用上位机分配的全局 ID；旧上位机回退为本机分配。
                int newId = enrollFingerprintId > FP_TEMP_SLOT
                    ? enrollFingerprintId : Storage::allocLocalFpId();
                if (newId <= FP_TEMP_SLOT || newId >= FINGER_MAX_USERS) {
                    Debug::println(F("[MSG] no free fp slot"));
                    Fingerprint::deleteFingerprint(FP_TEMP_SLOT);
                    sendEnrollProgress("verify_fail", 5, ENROLL_TOTAL_STEPS,
                                       "指纹槽位已满，无法录入");
                    finishEnrollWithVerify(false);
                    break;
                }
                if (!Fingerprint::copyTemplate(FP_TEMP_SLOT, newId)) {
                    Debug::println(F("[MSG] copyTemplate failed"));
                    Fingerprint::deleteFingerprint(FP_TEMP_SLOT);
                    sendEnrollProgress("verify_fail", 5, ENROLL_TOTAL_STEPS,
                                       "模板迁移失败，请重试");
                    finishEnrollWithVerify(false);
                    break;
                }
                // 迁移成功，删除临时槽
                Fingerprint::deleteFingerprint(FP_TEMP_SLOT);
                sendEnrollProgress("verify_ok", 7, ENROLL_TOTAL_STEPS,
                                   "检测通过，录入成功");
                // 记录分配的真实 ID，供 finishEnrollWithVerify 回报
                enrollFingerprintId = newId;
                finishEnrollWithVerify(true);
            } else if (vr == 0) {
                // 无手指或不匹配：继续等待（非阻塞）
                // 不匹配时 verifyOnSlot 返回 0，但这里无法区分"无手指"和"不匹配"
                // 为避免误判，0 视为"还没按"，继续等。真正的"不匹配"会在超时后判失败。
                break;
            } else {
                // 通信错误
                Debug::printf("[MSG] verifyOnSlot error: %s\n", Fingerprint::lastError().c_str());
                Fingerprint::deleteFingerprint(FP_TEMP_SLOT);
                sendEnrollProgress("verify_fail", 5, ENROLL_TOTAL_STEPS,
                                   "检测通信失败，请重试");
                finishEnrollWithVerify(false);
            }
            break;
        }

        case STATE_FINGERPRINT_TEST: {
            if (now - fingerprintTestLastActivity >= FP_TEST_IDLE_TIMEOUT_MS) {
                Debug::println(F("[MSG] fingerprint test idle timeout"));
                finishFingerprintTest("timeout");
                break;
            }
            if (now - fingerprintTestLastPoll < FP_TEST_POLL_INTERVAL_MS) break;
            fingerprintTestLastPoll = now;

            bool fingerDetected = false;
            int confidence = 0;
            int result = Fingerprint::verifyOnSlot(
                FP_TEMP_SLOT, &fingerDetected, &confidence);
            if (!fingerDetected) {
                if (fingerprintTestFingerDown) {
                    fingerprintTestFingerDown = false;
                    FpLed::setIdentifying();
                }
                break;
            }

            // 只要持续检测到手指就刷新 60 秒无操作计时；同一次按压只上报一次。
            fingerprintTestLastActivity = millis();
            if (fingerprintTestFingerDown) {
                if (now - fingerprintTestLastActivityReport >= 5000) {
                    fingerprintTestLastActivityReport = now;
                    sendFingerprintTestEvent("activity");
                }
                break;
            }
            fingerprintTestFingerDown = true;
            fingerprintTestLastActivityReport = now;
            if (result == 1) {
                FpLed::setSuccess();
                sendFingerprintTestEvent("matched", confidence);
            } else if (result == 0) {
                FpLed::setFail();
                sendFingerprintTestEvent("not_matched");
            } else {
                FpLed::setFail();
                sendFingerprintTestEvent("read_error");
            }
            break;
        }

        case STATE_IDLE:
        default:
            // V2.7：IDLE 仅在异常时出现，自动恢复到 WAIT_FINGER
            setState(STATE_WAIT_FINGER);
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
    } else if (strcmp(cmd, "START_FINGERPRINT_TEST") == 0) {
        cmdStartFingerprintTest(data, msgId);
    } else if (strcmp(cmd, "STOP_FINGERPRINT_TEST") == 0) {
        cmdStopFingerprintTest(data, msgId);
    } else if (strcmp(cmd, "ADD_BACKUP_FINGERPRINT") == 0) {
        cmdAddBackupFingerprint(data, msgId);
    } else if (strcmp(cmd, "DELETE_BACKUP_FINGERPRINT") == 0) {
        cmdDeleteBackupFingerprint(data, msgId);
    } else if (strcmp(cmd, "BACKUP_FP_LIST_REQUEST") == 0) {
        cmdBackupFpListRequest(msgId);
    } else if (strcmp(cmd, "CONTROL_LOCK") == 0) {
        cmdControlLock(data, msgId);
    } else if (strcmp(cmd, "READ_CONFIG") == 0) {
        cmdReadConfig(msgId);
    } else if (strcmp(cmd, "WRITE_CONFIG") == 0) {
        cmdWriteConfig(data, msgId);
    } else if (strcmp(cmd, "READ_STATUS") == 0) {
        cmdReadStatus(msgId);
    } else if (strcmp(cmd, "READ_PERMISSIONS") == 0) {
        cmdReadPermissions(data, msgId);
    } else if (strcmp(cmd, "CHECK_FINGERPRINT") == 0) {
        cmdCheckFingerprint(data, msgId);
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
    } else if (strcmp(cmd, "CANCEL_ENROLL") == 0) {
        // 上位机取消录入
        onCancel();
        sendAck(msgId, "cancelled");
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
            // V2.7：主指纹的 local_fp_id 默认等于 fingerprint_id（AS608 物理槽位）
            perm.local_fp_id = perm.fingerprint_id;
            perm.is_backup = false;

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
    // V2.7：主指纹的 local_fp_id 默认等于 fingerprint_id
    perm.local_fp_id = perm.fingerprint_id;
    perm.is_backup = false;

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
    UserPermission existing;
    if (Storage::findPrimaryPermission(perm.user_id, existing) &&
        existing.local_fp_id != perm.local_fp_id) {
        Fingerprint::deleteFingerprint(existing.local_fp_id);
        Storage::deletePermission(existing.local_fp_id);
        Debug::printf("[MSG] replaced stale primary fingerprint user=%s old=%d new=%d\n",
                      perm.user_id.c_str(), existing.local_fp_id, perm.local_fp_id);
    }
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
    // 固件固定录入到临时槽 ID=0；检测通过后迁移到上位机分配的全局 ID。
    // fingerprint_id=0 仅用于兼容旧上位机，此时回退为本机分配。
    String userId = data["user_id"] | "";
    int requestedId = data["fingerprint_id"] | 0;
    if (requestedId < 0 || requestedId >= FINGER_MAX_USERS) {
        sendError(ERR_FP_TEMPLATE_FORMAT, "invalid target fingerprint id", msgId);
        return;
    }

    // V2.7：录入主指纹时允许从 WAIT_FINGER 进入（常态轮询态）
    if (state != STATE_WAIT_FINGER && state != STATE_IDLE) {
        sendError(ERR_INTERNAL, "device busy", msgId);
        return;
    }

    // 临时槽 ID=0 若有残留模板，先清理
    if (Fingerprint::templateExists(FP_TEMP_SLOT)) {
        Fingerprint::deleteFingerprint(FP_TEMP_SLOT);
        Debug::println(F("[MSG] cleaned temp slot 0 before enroll"));
    }

    sendAck(msgId, "enrolling");
    enrollIsBackup = false;  // 主指纹
    startEnroll(requestedId, userId, msgId);
}

void MessageHandler::cmdStartFingerprintTest(const JsonObject &data,
                                             const String &msgId) {
    if (state != STATE_WAIT_FINGER && state != STATE_IDLE) {
        sendError(ERR_INTERNAL, "device busy", msgId);
        return;
    }

    const char *hex = data["template_hex"] | "";
    size_t hexLen = strlen(hex);
    if (hexLen < 256 || (hexLen & 1U) != 0 ||
        hexLen / 2 > FP_TEMPLATE_BUF_SIZE) {
        sendError(ERR_FP_TEMPLATE_FORMAT, "invalid fingerprint test template", msgId);
        return;
    }

    size_t templateLen = hexLen / 2;
    uint8_t *templateBuf = (uint8_t *)malloc(templateLen);
    if (templateBuf == nullptr) {
        sendError(ERR_FLASH_WRITE, "out of memory", msgId);
        return;
    }
    for (size_t index = 0; index < templateLen; ++index) {
        uint8_t hi = hexCharToVal(hex[index * 2]);
        uint8_t lo = hexCharToVal(hex[index * 2 + 1]);
        if (hi == 0xFF || lo == 0xFF) {
            free(templateBuf);
            sendError(ERR_FP_TEMPLATE_FORMAT, "invalid fingerprint test hex", msgId);
            return;
        }
        templateBuf[index] = (uint8_t)((hi << 4) | lo);
    }

    Fingerprint::deleteFingerprint(FP_TEMP_SLOT);
    bool stored = Fingerprint::writeTemplate(
        FP_TEMP_SLOT, templateBuf, templateLen);
    free(templateBuf);
    if (!stored) {
        Fingerprint::deleteFingerprint(FP_TEMP_SLOT);
        sendError(ERR_FLASH_WRITE, Fingerprint::lastError(), msgId);
        return;
    }

    fingerprintTestToken = data["test_token"] | "";
    fingerprintTestSourceId = data["fingerprint_id"] | -1;
    fingerprintTestLastActivity = millis();
    fingerprintTestLastPoll = 0;
    fingerprintTestLastActivityReport = 0;
    fingerprintTestFingerDown = false;
    FpLed::setIdentifying();
    setState(STATE_FINGERPRINT_TEST);
    sendAck(msgId, "fingerprint_test_started");
    sendFingerprintTestEvent("started");
    Debug::printf("[MSG] fingerprint test started source_id=%d\n",
                  fingerprintTestSourceId);
}

void MessageHandler::cmdStopFingerprintTest(const JsonObject &data,
                                            const String &msgId) {
    String requestedToken = data["test_token"] | "";
    if (state == STATE_FINGERPRINT_TEST && requestedToken.length() > 0 &&
        requestedToken != fingerprintTestToken) {
        sendError(ERR_BAD_REQUEST, "fingerprint test token mismatch", msgId);
        return;
    }
    if (state == STATE_FINGERPRINT_TEST) {
        finishFingerprintTest("stopped");
    } else {
        Fingerprint::deleteFingerprint(FP_TEMP_SLOT);
    }
    sendAck(msgId, "fingerprint_test_stopped");
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
    // V2.7：允许从 WAIT_FINGER 进入
    if (state != STATE_WAIT_FINGER && state != STATE_IDLE) {
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
    sendAck(msgId, "fingerprint_deleted");
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

// ====== V2.7 副指纹命令 ======
// ADD_BACKUP_FINGERPRINT: 在本机录入副指纹（仅本机生效，不上报 SD 卡）
// data: {user_id, lock_permissions?: {lock_0..lock_3}, expire_date?: "YYYY-MM-DD"}
void MessageHandler::cmdAddBackupFingerprint(const JsonObject &data, const String &msgId) {
    String userId = data["user_id"] | "";
    if (userId.length() == 0) {
        sendError(ERR_BAD_REQUEST, "missing user_id", msgId);
        return;
    }
    if (state != STATE_WAIT_FINGER && state != STATE_IDLE) {
        sendError(ERR_INTERNAL, "device busy", msgId);
        return;
    }

    // 检查该用户是否已有副指纹
    UserPermission existing;
    // 通过遍历本地权限表检查（findPrimaryPermission 只查主指纹，这里需要查副指纹）
    UserPermission tmpList[PERM_MAX_USERS];
    int backupCount = Storage::listBackupFingerprints(tmpList, PERM_MAX_USERS);
    uint32_t uidNum = Storage::userIdToNum(userId);
    for (int i = 0; i < backupCount; i++) {
        if (tmpList[i].user_id_num == uidNum) {
            sendError(ERR_FP_BACKUP_EXISTS, "backup fingerprint already exists for this user", msgId);
            return;
        }
    }

    // 分配本地 AS608 槽位
    int localId = Storage::allocLocalFpId();
    if (localId < 0) {
        sendError(ERR_FP_BACKUP_LIMIT, "no free AS608 slot for backup fingerprint", msgId);
        return;
    }

    // 检查 AS608 该槽位是否已被占用（理论上 allocLocalFpId 已排除，但双重保险）
    if (Fingerprint::templateExists(localId)) {
        // 槽位有模板但权限表无记录：先删除残留模板
        Fingerprint::deleteFingerprint(localId);
        Debug::printf("[MSG] cleaned orphan template at slot %d before backup enroll\n", localId);
    }

    sendAck(msgId, "enrolling_backup");

    // 走录入流程（标记 is_backup=true）
    enrollFingerprintId = localId;
    enrollUserId = userId;
    enrollRequestMsgId = msgId;
    enrollLastPhaseCode = "";
    enrollIsBackup = true;

    // 解析可选的权限参数（未提供则录入后继承主指纹权限）
    // 权限在录入完成后由 update() 的 ENROLL_DONE_OK 分支写入

    Fingerprint::enrollBegin(localId);
    setState(STATE_ENROLLING);
    Debug::printf("[MSG] start enroll BACKUP fingerprint: local_id=%d user=%s\n",
                  localId, userId.c_str());

    // 立即上报第一步提示
    String prog = "{\"phase\":\"place_1\",\"step\":1,\"total\":6,"
                  "\"hint\":\"请将手指按在指纹头上（副指纹录入 1/4）\","
                  "\"fingerprint_id\":" + String(localId) +
                  ",\"is_backup\":true}";
    sendMessage("ENROLL_PROGRESS", prog, msgId);
    enrollLastPhaseCode = "place_1";
}

// DELETE_BACKUP_FINGERPRINT: 删除指定用户的本机副指纹
// data: {user_id}
void MessageHandler::cmdDeleteBackupFingerprint(const JsonObject &data, const String &msgId) {
    String userId = data["user_id"] | "";
    if (userId.length() == 0) {
        sendError(ERR_BAD_REQUEST, "missing user_id", msgId);
        return;
    }

    // 查找该用户的副指纹记录以获取 AS608 槽位
    UserPermission tmpList[PERM_MAX_USERS];
    int backupCount = Storage::listBackupFingerprints(tmpList, PERM_MAX_USERS);
    uint32_t uidNum = Storage::userIdToNum(userId);
    int targetLocalId = -1;
    for (int i = 0; i < backupCount; i++) {
        if (tmpList[i].user_id_num == uidNum) {
            targetLocalId = tmpList[i].local_fp_id;
            break;
        }
    }
    if (targetLocalId < 0) {
        sendError(ERR_FP_BACKUP_NOT_FOUND, "no backup fingerprint for this user", msgId);
        return;
    }

    // 从 AS608 删除模板
    bool as608Ok = Fingerprint::deleteFingerprint(targetLocalId);
    // 从本地权限表删除记录
    bool storageOk = Storage::deleteBackupFingerprint(userId);

    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    cfg.fingerprint_count = Fingerprint::getFingerprintCount();
    Storage::saveDeviceConfig(cfg);

    String respData = "{\"user_id\":\"" + userId + "\"" +
                      ",\"local_fp_id\":" + String(targetLocalId) +
                      ",\"as608_deleted\":" + String(as608Ok ? "true" : "false") +
                      ",\"storage_deleted\":" + String(storageOk ? "true" : "false") +
                      ",\"result\":\"" + (storageOk ? "success" : "fail") + "\"}";
    sendMessage("DELETE_BACKUP_FINGERPRINT_RESULT", respData, msgId);
    sendAck(msgId, storageOk ? "backup_deleted" : "backup_delete_failed");
    Debug::printf("[MSG] deleted backup fingerprint: user=%s local_id=%d\n",
                  userId.c_str(), targetLocalId);
}

// BACKUP_FP_LIST_REQUEST: 上报本机所有副指纹清单
void MessageHandler::cmdBackupFpListRequest(const String &msgId) {
    UserPermission tmpList[PERM_MAX_USERS];
    int count = Storage::listBackupFingerprints(tmpList, PERM_MAX_USERS);

    String data = "{\"count\":" + String(count) + ",\"backups\":[";
    for (int i = 0; i < count; i++) {
        if (i > 0) data += ",";
        data += "{\"user_id\":\"" + tmpList[i].user_id + "\"";
        data += ",\"user_id_num\":" + String(tmpList[i].user_id_num);
        data += ",\"local_fp_id\":" + String(tmpList[i].local_fp_id);
        data += ",\"role\":" + String((int)tmpList[i].role);
        data += ",\"lock_permissions\":{";
        for (int j = 0; j < LOCK_COUNT; j++) {
            if (j > 0) data += ",";
            data += "\"lock_" + String(j) + "\":" + String(tmpList[i].lock_perm[j] ? "true" : "false");
        }
        data += "}}";
    }
    data += "]}";
    sendMessage("BACKUP_FP_LIST", data, msgId);
    sendAck(msgId, "backup_list_sent");
    Debug::printf("[MSG] reported %d backup fingerprints\n", count);
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
            // Return the command result before Flash erase/write and NVS
            // persistence.  Lock control ACK latency must not depend on log IO.
            sendAck(msgId, action);
            Logger::log("remote", -1, lockId, "open", "success", "remote_control");
        } else {
            sendError(ERR_LOCK_HARDWARE, "lock open failed", msgId);
            return;
        }
    } else {
        LockControl::closeLock(lockId);
        sendAck(msgId, action);
        Logger::log("remote", -1, lockId, "close", "success", "remote_control");
    }
    Debug::printf("[MSG] remote control lock %d %s\n", lockId, action.c_str());
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
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);

    uint8_t lockMask = 0;
    for (uint8_t i = 0; i < LOCK_COUNT; ++i) {
        if (lockStatus[i]) lockMask |= (uint8_t)(1U << i);
    }
    uint8_t flags = 0;
    if (Storage::isTimeSynced()) flags |= 0x01;
    if (Storage::loadWorkMode() == MODE_MESH) flags |= 0x02;

    uint8_t payload[24];
    int payloadLen = packCabinetStatus(
        payload, (int)sizeof(payload), millis() / 1000, lockMask,
        (uint8_t)MeshComm::getMeshLayer(), flags,
        (uint16_t)cfg.fingerprint_count,
        (uint16_t)Storage::getPermissionCount(),
        Storage::getPermissionVersion(),
        (uint16_t)MeshComm::getSendFailureCount(),
        (uint16_t)MeshComm::getQueueFullCount(),
        (int8_t)MeshComm::getLinkRssi(),
        (uint8_t)MeshComm::getApAssocExpireSeconds(),
        (uint16_t)Fingerprint::getBackgroundVerifyMaxMs());
    uint16_t mid = (uint16_t)msgId.toInt();
    if (payloadLen > 0 && mid != 0) {
        MeshComm::sendApp(CMD_STATUS_RESPONSE, mid, 0, payload,
                          (uint16_t)payloadLen, nullptr);
    }
}

void MessageHandler::cmdReadPermissions(const JsonObject &data, const String &msgId) {
    String userId = data["user_id"] | "";
    UserPermission perm;
    bool found = userId.length() > 0 && Storage::findPrimaryPermission(userId, perm);
    String response = "{\"count\":" + String(Storage::getPermissionCount()) +
                      ",\"version\":" + String(Storage::getPermissionVersion()) +
                      ",\"user_id\":\"" + userId + "\"" +
                      ",\"found\":" + String(found ? "true" : "false");
    if (found) {
        response += ",\"fingerprint_id\":" + String(perm.fingerprint_id);
        response += ",\"role\":" + String((int)perm.role);
        response += ",\"lock_0\":" + String(perm.lock_perm[0] ? "true" : "false");
        response += ",\"lock_1\":" + String(perm.lock_perm[1] ? "true" : "false");
        response += ",\"lock_2\":" + String(perm.lock_perm[2] ? "true" : "false");
        response += ",\"lock_3\":" + String(perm.lock_perm[3] ? "true" : "false");
    }
    response += "}";
    sendMessage("PERMISSIONS_RESPONSE", response, msgId);
}

static uint32_t templateCrc32(const uint8_t *data, size_t len) {
    uint32_t crc = 0xFFFFFFFFU;
    for (size_t i = 0; i < len; ++i) {
        crc ^= data[i];
        for (uint8_t bit = 0; bit < 8; ++bit) {
            crc = (crc >> 1) ^ ((crc & 1U) ? 0xEDB88320U : 0U);
        }
    }
    return crc ^ 0xFFFFFFFFU;
}

void MessageHandler::cmdCheckFingerprint(const JsonObject &data, const String &msgId) {
    int fingerprintId = data["fingerprint_id"] | -1;
    uint32_t expected = data["expected_crc32"] | 0;
    if (fingerprintId <= 0 || fingerprintId >= FINGER_MAX_USERS) {
        sendError(ERR_BAD_REQUEST, "invalid fingerprint id", msgId);
        return;
    }

    bool exists = Fingerprint::templateExists(fingerprintId);
    uint32_t actual = 0;
    bool readable = false;
    if (exists) {
        uint8_t *buffer = (uint8_t *)malloc(FP_TEMPLATE_BUF_SIZE);
        if (buffer != nullptr) {
            size_t length = 0;
            readable = Fingerprint::readTemplate(
                fingerprintId, buffer, FP_TEMPLATE_BUF_SIZE, length);
            if (readable) actual = templateCrc32(buffer, length);
            free(buffer);
        }
    }
    bool matches = exists && readable && expected != 0 && actual == expected;
    String response = "{\"fingerprint_id\":" + String(fingerprintId) +
                      ",\"exists\":" + String(exists ? "true" : "false") +
                      ",\"readable\":" + String(readable ? "true" : "false") +
                      ",\"matches\":" + String(matches ? "true" : "false") +
                      ",\"expected_crc32\":" + String(expected) +
                      ",\"actual_crc32\":" + String(actual) + "}";
    sendMessage("FINGERPRINT_CHECK_RESPONSE", response, msgId);
}

void MessageHandler::cmdClearLogs(const String &msgId) {
    Logger::clearAll();
    sendMessage("LOGS_CLEARED", "{\"result\":\"success\"}", msgId);
}

void MessageHandler::cmdReboot(const JsonObject &data, const String &msgId) {
    String mode = data["mode"] | "";
    Debug::printf("[MSG] preparing reboot (cabinet remains Mesh+UART0), requested mode: %s\n",
                  mode.c_str());
    sendMessage("REBOOT_ACK", "{\"result\":\"rebooting\"}", msgId);
    delay(500);
    Storage::saveWorkMode(MODE_MESH);
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
    data += "\"mesh_layer\":" + String(MeshComm::getMeshLayer()) + ",";
    data += "\"free_heap\":" + String(ESP.getFreeHeap()) + ",";
    data += "\"mesh_send_failures\":" + String(MeshComm::getSendFailureCount()) + ",";
    data += "\"mesh_queue_full\":" + String(MeshComm::getQueueFullCount()) + ",";
    data += "\"mesh_recoveries\":" + String(MeshComm::getRecoveryCount());
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
        sendError(ERR_SD_NOT_READY, "sd card not ready", msgId);
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
        sendError(ERR_SD_NOT_READY, "sd card not ready", msgId);
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
        sendError(ERR_SD_NOT_READY, "sd card not ready", msgId);
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
        sendError(ERR_SD_NOT_READY, "sd card not ready", msgId);
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
        sendError(ERR_SD_NOT_READY, "sd card not ready", msgId);
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
