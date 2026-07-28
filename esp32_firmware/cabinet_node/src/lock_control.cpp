/**
 * lock_control.cpp - 4 路锁控制 via 74HC595 移位寄存器
 * 接线: DS=GPIO4, STCP=GPIO15, SHCP=GPIO16
 * 595 Q0-Q3: 锁1-4 状态 LED(高电平亮, LOW=灭)
 * 595 Q4-Q7: 锁1-4 继电器(高电平开锁, LOW=关锁)
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
bool LockControl::ledHint[LOCK_COUNT]               = {false, false, false, false};
bool LockControl::blinkPhaseOn                      = false;
unsigned long LockControl::lastBlinkToggleMs        = 0;

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
// Bit 0-3: Lock1-4 LED   (1=亮 HIGH, 0=灭 LOW)
// Bit 4-7: Lock1-4 继电器 (1=开锁 HIGH, 0=关闭 LOW)
// 优先级：开锁常亮 > 权限提示慢闪 > 熄灭
void LockControl::updateShiftRegister() {
    uint8_t data = 0;
    for (int i = 0; i < LOCK_COUNT; i++) {
        if (lockActive[i]) {
            // 开锁: 继电器 HIGH(1), LED 常亮
            data |= (1 << (LOCK_RELAY_BIT_BASE + i));
            data |= (1 << (LOCK_LED_BIT_BASE + i));
        } else if (ledHint[i] && blinkPhaseOn) {
            // 验证窗口提示: 仅 LED 慢闪，继电器保持关
            data |= (1 << (LOCK_LED_BIT_BASE + i));
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
        ledHint[i]      = false;
    }
    blinkPhaseOn = false;
    lastBlinkToggleMs = millis();
    // 初始状态: 所有锁关闭
    updateShiftRegister();
    Debug::println(F("[LOCK] 4-channel lock control init via 74HC595"));
}

bool LockControl::openLock(int lockId) {
    if (lockId < 0 || lockId >= LOCK_COUNT) {
        Debug::printf("[LOCK] Open lock failed: lock ID %d invalid\n", lockId);
        return false;
    }
    if (lockActive[lockId]) {
        lockOpenTime[lockId] = millis();
        Debug::printf("[LOCK] Lock %d already open, refreshing timer\n", lockId);
        return true;
    }
    lockActive[lockId]   = true;
    lockOpenTime[lockId] = millis();
    // 开锁后该路 LED 改常亮；其它提示位由 clearPermissionHint 清理
    updateShiftRegister();
    Debug::printf("[LOCK] Open lock %d (relay HIGH, LED on)\n", lockId);
    return true;
}

void LockControl::closeLock(int lockId) {
    if (lockId < 0 || lockId >= LOCK_COUNT) return;
    if (lockActive[lockId]) {
        Debug::printf("[LOCK] Close lock %d (relay LOW, LED off)\n", lockId);
    }
    lockActive[lockId] = false;
    updateShiftRegister();
}

void LockControl::closeAllLocks() {
    for (int i = 0; i < LOCK_COUNT; i++) {
        lockActive[i] = false;
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
        if (lockActive[i] && (now - lockOpenTime[i] >= LOCK_OPEN_DURATION_MS)) {
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
