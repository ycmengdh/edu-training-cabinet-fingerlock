/**
 * lock_control.cpp - 4 路继电器锁控制实现
 * 继电器低电平触发：开锁时输出 LOW，关闭时输出 HIGH
 * 权限指示灯高电平点亮（共阴接法），灭=LOW
 */
#include "lock_control.h"

const int LockControl::lockPins[LOCK_COUNT] = {LOCK0_PIN, LOCK1_PIN, LOCK2_PIN, LOCK3_PIN};
// 权限指示灯引脚表（与 LED_PERM0~3 对应）
const int LockControl::permLedPins[LOCK_COUNT] = {LED_PERM0_PIN, LED_PERM1_PIN, LED_PERM2_PIN, LED_PERM3_PIN};
bool LockControl::lockActive[LOCK_COUNT]        = {false, false, false, false};
unsigned long LockControl::lockOpenTime[LOCK_COUNT] = {0, 0, 0, 0};

// 开锁窗口期权限校验状态
bool LockControl::allowedLocks[LOCK_COUNT]  = {false, false, false, false};
bool LockControl::enforcePermission         = false;

void LockControl::init() {
    for (int i = 0; i < LOCK_COUNT; i++) {
        // 继电器引脚：低电平触发，默认 HIGH 关闭
        pinMode(lockPins[i], OUTPUT);
        digitalWrite(lockPins[i], HIGH);
        lockActive[i]    = false;
        lockOpenTime[i]  = 0;

        // 权限指示灯引脚：默认灭
        pinMode(permLedPins[i], OUTPUT);
        digitalWrite(permLedPins[i], LOW);
    }
    enforcePermission = false;
    Serial.println(F("[LOCK] 4路锁控制初始化完成（含权限指示灯）"));
}

bool LockControl::openLock(int lockId) {
    if (lockId < 0 || lockId >= LOCK_COUNT) {
        Serial.printf("[LOCK] 开锁失败：锁编号 %d 无效\n", lockId);
        return false;
    }
    // 若处于开锁窗口期且启用了权限校验，则先检查权限
    if (enforcePermission && !allowedLocks[lockId]) {
        Serial.printf("[LOCK] 开锁被拒：锁 %d 在当前窗口期无权限\n", lockId);
        return false;
    }
    if (lockActive[lockId]) {
        // 已在开锁状态，刷新计时
        lockOpenTime[lockId] = millis();
        Serial.printf("[LOCK] 锁 %d 已开启，刷新计时\n", lockId);
        return true;
    }
    digitalWrite(lockPins[lockId], LOW);  // 低电平触发开锁
    lockActive[lockId]   = true;
    lockOpenTime[lockId] = millis();
    Serial.printf("[LOCK] 开锁 %d (LOW)\n", lockId);
    return true;
}

// 开锁窗口期专用开锁：显式做权限校验（需求 5：窗口期按按钮开锁前检查权限）
bool LockControl::openLockChecked(int lockId) {
    if (lockId < 0 || lockId >= LOCK_COUNT) {
        return false;
    }
    if (!allowedLocks[lockId]) {
        Serial.printf("[LOCK] openLockChecked：锁 %d 无权限\n", lockId);
        return false;
    }
    return openLock(lockId);
}

void LockControl::closeLock(int lockId) {
    if (lockId < 0 || lockId >= LOCK_COUNT) return;
    digitalWrite(lockPins[lockId], HIGH);  // HIGH 关闭
    if (lockActive[lockId]) {
        Serial.printf("[LOCK] 关锁 %d (HIGH)\n", lockId);
    }
    lockActive[lockId] = false;
}

void LockControl::closeAllLocks() {
    for (int i = 0; i < LOCK_COUNT; i++) {
        closeLock(i);
    }
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

void LockControl::update() {
    unsigned long now = millis();
    for (int i = 0; i < LOCK_COUNT; i++) {
        if (lockActive[i] && (now - lockOpenTime[i] >= LOCK_OPEN_DURATION_MS)) {
            // 开锁时间到，自动关闭
            closeLock(i);
        }
    }
}

// ====== 权限指示灯控制（需求 2） ======
void LockControl::setPermissionLed(int lockId, bool on) {
    if (lockId < 0 || lockId >= LOCK_COUNT) return;
    digitalWrite(permLedPins[lockId], on ? HIGH : LOW);
}

void LockControl::clearAllPermissionLeds() {
    for (int i = 0; i < LOCK_COUNT; i++) {
        digitalWrite(permLedPins[i], LOW);
    }
}

// ====== 开锁窗口期权限校验（需求 2） ======
void LockControl::setAllowedLocks(const bool allowed[LOCK_COUNT]) {
    for (int i = 0; i < LOCK_COUNT; i++) {
        allowedLocks[i] = allowed[i];
    }
    enforcePermission = true;
    Serial.printf("[LOCK] 启用窗口期权限校验: [%d,%d,%d,%d]\n",
                  allowedLocks[0], allowedLocks[1], allowedLocks[2], allowedLocks[3]);
}

void LockControl::clearAllowedLocks() {
    for (int i = 0; i < LOCK_COUNT; i++) {
        allowedLocks[i] = false;
    }
    enforcePermission = false;
    Serial.println(F("[LOCK] 清除窗口期权限校验"));
}

bool LockControl::isPermissionEnforced() {
    return enforcePermission;
}

bool LockControl::isLockAllowed(int lockId) {
    if (lockId < 0 || lockId >= LOCK_COUNT) return false;
    return allowedLocks[lockId];
}
