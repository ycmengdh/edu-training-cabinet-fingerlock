/**
 * message_handler.h - 消息处理模块
 * 解析 JSON 消息，处理上位机命令和本机事件
 */
#ifndef MESSAGE_HANDLER_H
#define MESSAGE_HANDLER_H

#include <Arduino.h>
#include "config.h"

class MessageHandler {
public:
    // 初始化
    static void init();

    // 处理收到的 TCP 消息（JSON 字符串）
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

private:
    // ====== 命令处理函数 ======
    static void cmdAuthOk(const String &data);
    static void cmdAuthFail(const String &data);
    static void cmdSyncPermissions(const String &data);
    static void cmdAddFingerprint(const String &data);
    static void cmdDeleteFingerprint(const String &data);
    static void cmdControlLock(const String &data);
    static void cmdReadConfig();
    static void cmdWriteConfig(const String &data);
    static void cmdReadStatus();
    static void cmdClearLogs();
    static void cmdReboot(const String &data);

    // ====== 辅助方法 ======
    // 发送指纹验证请求到上位机
    static void sendFingerVerify(int fingerprintId);

    // 检查本地缓存权限并开锁（离线模式）
    // 返回 true 表示已用本地缓存开锁
    static bool tryLocalPermission(int fingerprintId, int lockId);

    // 状态机超时检查
    static void checkTimeout();

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
};

#endif // MESSAGE_HANDLER_H
