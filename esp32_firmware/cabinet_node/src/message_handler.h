/**
 * message_handler.h - 消息处理模块（V2.7 窗口化验证版本）
 * 状态机：常态轮询 / 验证窗口 / 录入 / 临时槽指纹测试
 *
 * V2.7 流程反转：先验证指纹 -> 10 秒操作窗口 -> 按键开锁
 *   - STATE_WAIT_FINGER: 常态轮询 AS608，匹配成功则载入权限进入窗口态
 *   - STATE_VERIFIED_WINDOW: 10s 内按键开锁；超时/取消回 IDLE
 *   - STATE_ENROLLING: 主/副指纹录入
 *
 * 开锁鉴权只读取本地权限缓存；网络仅用于管理/同步。
 */
#ifndef MESSAGE_HANDLER_H
#define MESSAGE_HANDLER_H

#include <Arduino.h>
#include <ArduinoJson.h>
#include "config.h"
#include "app_protocol.h"

class MessageHandler {
public:
    // 初始化
    static void init();

    // 处理收到的消息（JSON 字符串，解析一次后传 JsonObject 引用）
    static void handleIncoming(const String &message);

    // Binary app envelope (hybrid: complex payloads may still be JSON data)
    static void handleIncomingApp(const AppMessageView &view);

    // 主循环调用，处理本机事件（按键、指纹验证、状态机推进）
    static void update();

    // ====== 本机事件触发 ======
    // 按键按下：在 STATE_VERIFIED_WINDOW 中开锁；其他状态忽略（V2.7 流程反转）
    static void onKeyPressed(int lockId);

    // 取消键按下：中止当前指纹验证窗口或录入流程，回到 IDLE
    static void onCancel();

    // 指纹验证流程的状态
    enum VerifyState {
        STATE_IDLE = 0,           // 空闲（初始态，立即进入 WAIT_FINGER）
        STATE_WAIT_FINGER,        // 常态轮询指纹（V2.7：不再由按键触发）
        STATE_VERIFIED_WINDOW,    // 验证成功后的 10s 操作窗口
        STATE_ENROLLING,          // 正在录入指纹（主/副）-> 录到临时槽 ID=0
        STATE_ENROLL_VERIFY,      // 录入完成后的检测阶段：再按一次验证
        STATE_FINGERPRINT_TEST    // 上位机显式启动的临时槽 0 测试模式
    };

    static VerifyState getState();
    static void setState(VerifyState s);

    // 触发录入指纹（由 ADD_FINGERPRINT 命令调用）
    static void startEnroll(int fingerprintId, const String &userId,
                            const String &requestMsgId);

    // 发送带 msg_id 的消息（ACK 机制：msg_id 原样回传）
    static bool sendMessage(const String &cmd, const String &dataJson = "",
                            const String &msgId = "");

    // 发送 ACK 确认（msg_id 原样回传）
    static void sendAck(const String &msgId, const String &result = "ok");

    // 发送错误响应
    static void sendError(ErrorCode code, const String &message,
                          const String &msgId = "");

    // 发送大体积 JSON 响应（自动分片，用于 SD_QUERY 返回大表）
    static bool sendLargeResponse(const String &cmd, const String &dataJson,
                                  const String &msgId = "");

private:
    // ====== 命令处理函数（传 JsonObject 引用避免二次解析） ======
    static void cmdAuthOk(const JsonObject &data, const String &msgId);
    static void cmdAuthFail(const JsonObject &data, const String &msgId);
    static void cmdSyncPermissions(const JsonObject &data, const String &msgId);
    static void cmdBeginPermissionSync(const JsonObject &data, const String &msgId);
    static void cmdSyncPermission(const JsonObject &data, const String &msgId);
    static void cmdCommitPermissionSync(const JsonObject &data, const String &msgId);
    static void cmdClearPermissions(const JsonObject &data, const String &msgId);
    static void cmdAddFingerprint(const JsonObject &data, const String &msgId);
    static void cmdRestoreFingerprint(const JsonObject &data, const String &msgId);
    static void cmdDeleteFingerprint(const JsonObject &data, const String &msgId);
    static void cmdControlLock(const JsonObject &data, const String &msgId);
    static void cmdReadConfig(const String &msgId);
    static void cmdWriteConfig(const JsonObject &data, const String &msgId);
    static void cmdReadStatus(const String &msgId);
    static void cmdClearLogs(const String &msgId);
    static void cmdReboot(const JsonObject &data, const String &msgId);
    static void cmdTimeSync(const JsonObject &data, const String &msgId);
    static void cmdRegister(const String &msgId);
    static void cmdReadPermissions(const JsonObject &data, const String &msgId);
    static void cmdCheckFingerprint(const JsonObject &data, const String &msgId);
    static void cmdDeleteAllFingerprints(const String &msgId);
    static void cmdStartFingerprintTest(const JsonObject &data, const String &msgId);
    static void cmdStopFingerprintTest(const JsonObject &data, const String &msgId);
    // V2.7 副指纹命令
    static void cmdAddBackupFingerprint(const JsonObject &data, const String &msgId);
    static void cmdDeleteBackupFingerprint(const JsonObject &data, const String &msgId);
    static void cmdBackupFpListRequest(const String &msgId);

    // ====== SD 卡集中存储命令（仅根节点响应） ======
#ifdef ENABLE_SD_CARD
    // SD_QUERY：读取整张表 {table:"users"} -> 返回表 JSON
    static void cmdSdQuery(const JsonObject &data, const String &msgId);
    // SD_SAVE：保存整张表 {table:"users", json:"...", base_version:567} → 乐观锁写入
    static void cmdSdSave(const JsonObject &data, const String &msgId);
    // SD_QUERY_VERSION：查询版本号
    static void cmdSdQueryVersion(const String &msgId);
    // UPLOAD_FP_TEMPLATE：上传指纹模板 {user_id, finger_index, template_hex} → 存 SD 卡
    static void cmdUploadFpTemplate(const JsonObject &data, const String &msgId);
    // DOWNLOAD_FP_TEMPLATE：下载指纹模板 {user_id, finger_index} → 返回模板
    static void cmdDownloadFpTemplate(const JsonObject &data, const String &msgId);
    // DELETE_FP_TEMPLATE：删除用户所有指纹模板 {user_id}
    static void cmdDeleteFpTemplate(const JsonObject &data, const String &msgId);
#endif // ENABLE_SD_CARD

    // ====== 辅助方法 ======
    // V2.7：验证成功后载入权限到窗口态（不立即开锁）
    static bool loadVerifiedPermission(int as608Id);
    // V2.7：在窗口态按键时尝试开锁
    static bool openIfPermitted(int lockId);
    // 上报验证窗口事件（进入/退出/超时/取消）
    static void sendVerifyWindowEvent(const char *event, int lockId = -1);
    // 录入检测完成：模板从临时槽迁移到分配的真实 ID，回报结果
    static void finishEnrollWithVerify(bool verifyOk);
    // 发送录入进度帧
    static void sendEnrollProgress(const char *phase, int step, int total,
                                   const char *hint);
    static void sendFingerprintTestEvent(const char *event, int confidence = 0);
    static void finishFingerprintTest(const char *event);

    // 状态机超时检查
    static void checkTimeout();

    // 检查权限过期
    static bool isPermissionExpired(const UserPermission &perm);

    // hex 字符转数值（SD 卡指纹模板 hex 编解码用）
    static uint8_t hexCharToVal(char c);
    static bool parsePermission(const JsonObject &data, UserPermission &perm);
    static void resetPermissionSync();

    // 待开锁 ID（V2.7：仅录入流程复用，验证窗口改用 verifiedPerms）
    static int pendingLockId;
    // 待验证指纹 ID
    static int pendingFingerprintId;
    // 当前状态
    static VerifyState state;
    // 状态进入时刻
    static unsigned long stateEnterTime;
    // V2.7：验证窗口期内已验证的权限记录
    static UserPermission verifiedPerms;
    static bool verifiedPermsValid;
    // 录入指纹相关
    static int enrollFingerprintId;
    static String enrollUserId;
    static String enrollRequestMsgId;
    static String enrollLastPhaseCode; // 避免重复上报同一阶段
    // V2.7：录入是否为副指纹
    static bool enrollIsBackup;
    static String fingerprintTestToken;
    static int fingerprintTestSourceId;
    static unsigned long fingerprintTestLastActivity;
    static unsigned long fingerprintTestLastPoll;
    static unsigned long fingerprintTestLastActivityReport;
    static bool fingerprintTestFingerDown;
    // 指纹验证失败次数（用于告警）
    static int verifyFailCount;
    // 权限丢失待上报标志
    static bool permLostPending;
    // 上次 PERM_LOST 上报时刻
    static unsigned long lastPermLostReport;
    static UserPermission *permissionSyncBuffer;
    static bool *permissionSyncReceived;
    static int permissionSyncExpected;
    static int permissionSyncReceivedCount;
    static uint32_t permissionSyncVersion;
    static unsigned long permissionSyncStartedAt;
    static bool permissionSyncActive;
};

#endif // MESSAGE_HANDLER_H
