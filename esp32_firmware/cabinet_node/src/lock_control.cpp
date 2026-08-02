/**
 * lock_control.cpp - 4 路锁控制 via 74HC595 移位寄存器
 * 接线: DS=GPIO4, STCP=GPIO15, SHCP=GPIO16
 * 595 继电器: Lock1-4 分别使用 OUT5/OUT6/OUT7/OUT8（Q4/Q5/Q6/Q7）
 * 595 状态灯: Lock1-4 分别使用 OUT4/OUT3/OUT2/OUT1（Q3/Q2/Q1/Q0）
 * 每次 openLock/closeLock 后更新整个 595 输出
 *
 * 硬件实测极性（与早期“低电平开锁”注释相反）：
 *   待机/关锁: 低4位=0 且 高4位=0 → 0x00（锁关、灯灭）
 *   开锁:     对应继电器位=1, 对应 LED 位=1
 *   验证窗口: 有权限的锁 LED 慢闪（继电器保持关）
 */
#include "lock_control.h"
#include "debug.h"

bool LockControl::lockActive[LOCK_COUNT]            = {false, false, false, false};
unsigned long LockControl::lockOpenTime[LOCK_COUNT]  = {0, 0, 0, 0};
unsigned long LockControl::lockActiveStartTime[LOCK_COUNT] = {0, 0, 0, 0};
bool LockControl::ledHint[LOCK_COUNT]               = {false, false, false, false};
bool LockControl::blinkPhaseOn                      = false;
unsigned long LockControl::lastBlinkToggleMs        = 0;
static const uint8_t RELAY_BIT_BY_LOCK_ID[LOCK_COUNT] = {4, 5, 6, 7};
static const uint8_t LED_BIT_BY_LOCK_ID[LOCK_COUNT]   = {3, 2, 1, 0};

// ====== 74HC595 位移写入 ======
void LockControl::shiftOut595(uint8_t data) {
    digitalWrite(SHIFT_STCP_PIN, LOW);
    // MSB first: Q7 先出
    for (int i = 7; i >= 0; i--) {
        digitalWrite(SHIFT_SHCP_PIN, LOW); 
        digitalWrite(SHIFT_DS_PIN, (data >> i) & 0x01);
        digitalWrite(SHIFT_SHCP_PIN, HIGH); 
    } 
    digitalWrite(SHIFT_STCP_PIN, HIGH);
}

// ====== 根据锁状态 + 提示灯相位计算 8 位输出并刷新 595 ======
// Lock1-4 继电器位: Q4/Q5/Q6/Q7 (OUT5/OUT6/OUT7/OUT8)
// Lock1-4 LED 位: Q3/Q2/Q1/Q0 (OUT4/OUT3/OUT2/OUT1)
// 优先级：开锁常亮 > 权限提示慢闪 > 熄灭
void LockControl::updateShiftRegister() {
    uint8_t data = 0;
    for (int i = 0; i < LOCK_COUNT; i++) {
        int relayBit = RELAY_BIT_BY_LOCK_ID[i];
        int ledBit = LED_BIT_BY_LOCK_ID[i];
        if (lockActive[i]) {
            // 开锁: 继电器 HIGH(1), LED 常亮
            data |= (1 << relayBit);
            data |= (1 << ledBit);
        } else if (ledHint[i] && blinkPhaseOn) {
            // 验证窗口提示: 仅 LED 慢闪，继电器保持关
            data |= (1 << ledBit);
        }
    }
    shiftOut595(data);
}

bool LockControl::anyHintActive() {
    for (int i = 0; i < LOCK_COUNT; i++) {
        if (ledHint[i]) return true;
    }
    return false;
}

// ====== 初始化 ======
void LockControl::init() {
    pinMode(SHIFT_DS_PIN,   OUTPUT);
    pinMode(SHIFT_STCP_PIN, OUTPUT);
    pinMode(SHIFT_SHCP_PIN, OUTPUT);
    digitalWrite(SHIFT_DS_PIN,   LOW);
    digitalWrite(SHIFT_STCP_PIN, LOW);
    digitalWrite(SHIFT_SHCP_PIN, LOW);

    for (int i = 0; i < LOCK_COUNT; i++) {
        lockActive[i]   = false;
        lockOpenTime[i] = 0;
        lockActiveStartTime[i] = 0;
        ledHint[i]      = false;
    }
    blinkPhaseOn = false;
    lastBlinkToggleMs = millis();
    // 初始状态: 所有锁关闭
    updateShiftRegister();
    Debug::println(F("[LOCK] Mapping: Lock1-4 relay OUT4/OUT3/OUT2/OUT1, LED OUT5/OUT6/OUT7/OUT8"));
}

bool LockControl::openLock(int lockId) {
    if (lockId < 0 || lockId >= LOCK_COUNT) {
        Debug::printf("[LOCK] Open lock failed: lock ID %d invalid\n", lockId);
        return false;
    }
    if (lockActive[lockId]) {
        lockOpenTime[lockId] = millis();
        Debug::printf("[LOCK] Lock%d already open, refreshing timer\n", lockId + 1);
        return true;
    }
    lockActive[lockId]   = true;
    lockOpenTime[lockId] = millis();
    lockActiveStartTime[lockId] = lockOpenTime[lockId];
    // 开锁后该路 LED 改常亮；其它提示位由 clearPermissionHint 清理
    updateShiftRegister();
    Debug::printf("[LOCK] Open Lock%d (relay OUT%d HIGH, LED OUT%d on)\n",
                  lockId + 1, 5 + lockId, 4 - lockId);
    return true;
}

void LockControl::closeLock(int lockId) {
    if (lockId < 0 || lockId >= LOCK_COUNT) return;
    if (lockActive[lockId]) {
        Debug::printf("[LOCK] Close Lock%d (relay OUT%d LOW, LED OUT%d off)\n",
                      lockId + 1, 5 + lockId, 4 - lockId);
    }
    lockActive[lockId] = false;
    lockOpenTime[lockId] = 0;
    lockActiveStartTime[lockId] = 0;
    updateShiftRegister();
}

void LockControl::closeAllLocks() {
    for (int i = 0; i < LOCK_COUNT; i++) {
        lockActive[i] = false;
        lockOpenTime[i] = 0;
        lockActiveStartTime[i] = 0;
    }
    updateShiftRegister();
}

void LockControl::getLockStatus(bool status[LOCK_COUNT]) {
    for (int i = 0; i < LOCK_COUNT; i++) {
        status[i] = lockActive[i];
    }
}

bool LockControl::anyLockActive() {
    for (int i = 0; i < LOCK_COUNT; i++) {
        if (lockActive[i]) return true;
    }
    return false;
}

void LockControl::setPermissionHint(const bool perms[LOCK_COUNT]) {
    if (perms == nullptr) {
        clearPermissionHint();
        return;
    }
    int count = 0;
    for (int i = 0; i < LOCK_COUNT; i++) {
        ledHint[i] = perms[i];
        if (ledHint[i]) count++;
    }
    blinkPhaseOn = true;  // 立刻亮起一拍，用户马上能看到可开的锁
    lastBlinkToggleMs = millis();
    updateShiftRegister();
    Debug::printf("[LOCK] permission hint ON count=%d mask=[%d%d%d%d]\n",
                  count,
                  ledHint[0] ? 1 : 0, ledHint[1] ? 1 : 0,
                  ledHint[2] ? 1 : 0, ledHint[3] ? 1 : 0);
}

void LockControl::clearPermissionHint() {
    bool any = anyHintActive();
    for (int i = 0; i < LOCK_COUNT; i++) {
        ledHint[i] = false;
    }
    blinkPhaseOn = false;
    updateShiftRegister();
    if (any) {
        Debug::println(F("[LOCK] permission hint OFF"));
    }
}

void LockControl::update() {
    unsigned long now = millis();
    for (int i = 0; i < LOCK_COUNT; i++) {
        if (!lockActive[i]) continue;

        unsigned long activeMs = now - lockActiveStartTime[i];
        if (activeMs >= LOCK_FORCE_OFF_MS) {
            Debug::printf("[LOCK][PROTECT] Force close Lock%d after %lu ms\n", i + 1, activeMs);
            closeLock(i);
        } else if (now - lockOpenTime[i] >= LOCK_OPEN_DURATION_MS) {
            closeLock(i);
        }
    }

    // 验证窗口：有权限的锁 LED 慢闪（不驱动继电器）
    if (anyHintActive() &&
        (now - lastBlinkToggleMs >= LOCK_LED_HINT_HALF_MS)) {
        lastBlinkToggleMs = now;
        blinkPhaseOn = !blinkPhaseOn;
        updateShiftRegister();
    }
}
