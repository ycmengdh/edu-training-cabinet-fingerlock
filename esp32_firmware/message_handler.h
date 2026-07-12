/**
 * message_handler.h - 消息处理模块（V2.0 Mesh版本）
 * 保留原状态机骨架（STATE_IDLE/WAIT_FINGER/WAIT_AUTH/ENROLLING）
 * 传 JsonObject 引用避免两次解析
 * 新增 ACK 确认机制（msg_id 原样回传）和错误码处理
 * 适配新命令：REGISTER/TIME_SYNC/PERM_LOST/LOG_REPORT_ACK/SYNC_PERMISSIONS
 */
#ifndef MESSAGE_HANDLER_H
#define MESSAGE_HANDLER_H

#include <Arduino.h>
#include <ArduinoJson.h>
#include "config.h"

class MessageHandler {
public:
    // 初始化
    static void init();

    // 处理收到的消息（JSON 字符串，解析一次后传 JsonObject 引用）
    static void handleIncoming(const String &message);

    // 主循环调用，处理本机事件（按键、指纹验证、状态机推进）
    static void update();

    // ====== 本机事件触发 ======
    // 按键按下：记录待开锁 ID，进入指纹验证流程
    static void onKeyPressed(int lockId);

    // 指纹验证流程的状态
    enum VerifyState {
        STATE_IDLE = 0,         // 空闲
        STATE_WAIT_FINGER,      // 等待指纹（已按键）
        STATE_WAIT_AUTH,        // 已发送验证请求，等待上位机回复
        STATE_ENROLLING         // 正在录入指纹
    };

    static VerifyState getState();
    static void setState(VerifyState s);

    // 触发录入指纹（由 ADD_FINGERPRINT 命令调用）
    static void startEnroll(int fingerprintId, const String &userId);

    // 发送带 msg_id 的消息（ACK 机制：msg_id 原样回传）
    static bool sendMessage(const String &cmd, const String &dataJson = "",
                            const String &msgId = "");

    // 发送 ACK 确认（msg_id 原样回传）
    static void sendAck(const String &msgId, const String &result = "ok");

    // 发送错误响应
    static void sendError(ErrorCode code, const String &message,
                          const String &msgId = "");

private:
    // ====== 命令处理函数（传 JsonObject 引用避免二次解析） ======
    static void cmdAuthOk(const JsonObject &data, const String &msgId);
    static void cmdAuthFail(const JsonObject &data, const String &msgId);
    static void cmdSyncPermissions(const JsonObject &data, const String &msgId);
    static void cmdAddFingerprint(const JsonObject &data, const String &msgId);
    static void cmdDeleteFingerprint(const JsonObject &data, const String &msgId);
    static void cmdControlLock(const JsonObject &data, const String &msgId);
    static void cmdReadConfig(const String &msgId);
    static void cmdWriteConfig(const JsonObject &data, const String &msgId);
    static void cmdReadStatus(const String &msgId);
    static void cmdClearLogs(const String &msgId);
    static void cmdReboot(const JsonObject &data, const String &msgId);
    static void cmdTimeSync(const JsonObject &data, const String &msgId);
    static void cmdRegister(const String &msgId);
    static void cmdReadPermissions(const String &msgId);
    static void cmdDeleteAllFingerprints(const String &msgId);

    // ====== 辅助方法 ======
    // 发送指纹验证请求到上位机
    static void sendFingerVerify(int fingerprintId);

    // 检查本地缓存权限并开锁（离线模式）
    // 返回 true 表示已用本地缓存处理（成功或失败）
    static bool tryLocalPermission(int fingerprintId, int lockId);

    // 状态机超时检查
    static void checkTimeout();

    // 检查权限过期
    static bool isPermissionExpired(const UserPermission &perm);

    // 待开锁 ID
    static int pendingLockId;
    // 待验证指纹 ID
    static int pendingFingerprintId;
    // 当前状态
    static VerifyState state;
    // 状态进入时刻
    static unsigned long stateEnterTime;
    // 录入指纹相关
    static int enrollFingerprintId;
    static String enrollUserId;
    // 指纹验证失败次数（用于告警）
    static int verifyFailCount;
    // 权限丢失待上报标志
    static bool permLostPending;
    // 上次 PERM_LOST 上报时刻
    static unsigned long lastPermLostReport;
};

#endif // MESSAGE_HANDLER_H
