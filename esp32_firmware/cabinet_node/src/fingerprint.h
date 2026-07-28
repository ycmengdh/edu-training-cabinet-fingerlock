/**
 * fingerprint.h - AS608 指纹模块驱动
 * 基于 Adafruit_Fingerprint 库，使用 UART2
 * 上电：GPIO21 控制，GPIO42 读状态
 * 支持非阻塞分步录入（4 次采集 + 2 次验证）
 */
#ifndef FINGERPRINT_H
#define FINGERPRINT_H

#include <Arduino.h>
#include <Adafruit_Fingerprint.h>
#include "config.h"

// 分步录入阶段（由 MessageHandler 驱动）
enum EnrollPhase : uint8_t {
    ENROLL_IDLE = 0,
    ENROLL_PLACE_1,      // 第1次按下
    ENROLL_LIFT_1,       // 第1次松开
    ENROLL_PLACE_2,      // 第2次按下
    ENROLL_LIFT_2,       // 第2次松开
    ENROLL_PLACE_3,      // 第3次按下（第二对特征，提高质量）
    ENROLL_LIFT_3,       // 第3次松开
    ENROLL_PLACE_4,      // 第4次按下
    ENROLL_CREATE_STORE, // 合并并存储
    ENROLL_VERIFY_1,     // 验证第1次
    ENROLL_VERIFY_2,     // 验证第2次
    ENROLL_DONE_OK,
    ENROLL_DONE_FAIL
};

class Fingerprint {
public:
    static bool init();
    static void setPower(bool on);
    static bool isPowered();

    // 兼容旧接口：阻塞式两次采集（内部仍可用）
    static bool enrollFingerprint(int id);

    // ===== 非阻塞分步录入 =====
    static void enrollBegin(int id);
    // 每 loop 调用一次；返回 true 表示阶段变化（应上报进度）
    static bool enrollTick();
    static EnrollPhase enrollPhase();
    static const char *enrollPhaseHint();   // 给人看的提示
    static const char *enrollPhaseCode();   // 给上位机的 code
    static int enrollStepIndex();          // 1..6 进度
    static int enrollStepTotal();          // 6 = 4采+2验
    static void enrollAbort(const char *reason = "cancelled");

    static int verifyFingerprint();
    static void setBackgroundVerifyEnabled(bool enabled);
    static bool takeBackgroundVerifyResult(int &fingerprintId);
    static uint32_t getBackgroundVerifyMaxMs();
    static uint32_t getBackgroundVerifyErrorCount();
    static bool deleteFingerprint(int id);
    static bool deleteAllFingerprints();
    static int getFingerprintCount();
    static bool templateExists(int id);
    static bool readTemplate(int id, uint8_t *outBuf, size_t bufSize, size_t &outLen);
    static bool writeTemplate(int id, const uint8_t *data, size_t len);
    // 将一个槽位的模板复制到另一个槽位（loadModel(from) -> storeModel(to)）
    static bool copyTemplate(int fromId, int toId);
    // 录入后检测：让用户再按一次，与指定槽位做 1:1 比对
    // 返回: 1=匹配成功, 0=不匹配/无手指, -1=通信错误。
    // fingerDetected 用于测试模式区分无手指和不匹配；confidence 返回匹配分数。
    static int verifyOnSlot(int id, bool *fingerDetected = nullptr,
                            int *confidence = nullptr);
    static bool isReady();
    static String lastError();

private:
    static HardwareSerial  serial2;
    static Adafruit_Fingerprint finger;
    static bool ready;
    static String errorMsg;

    static EnrollPhase phase;
    static int enrollId;
    static unsigned long phaseEnterMs;
    static int verifyOkCount;

    static uint8_t waitForFinger(int stage); // 阻塞旧路径
    static void setPhase(EnrollPhase p);
    static bool captureToSlot(uint8_t slot); // 非阻塞尝试一次
    static bool fingerPresent();

    // ---- init() 辅助（DM900 手册上电/握手时序）----
    static void driveUartPinsLow();                  // 上电前把 TX/RX 拉低
    static bool waitHandshakeByte(uint16_t timeoutMs); // 读模块上电后主动发的 0x55
    static uint8_t probeModule();                    // checkPassword() 原始确认码
};

#endif // FINGERPRINT_H
