/**
 * lock_control.cpp - 4 路继电器锁控制实现
 * 继电器低电平触发：开锁时输出 LOW，关闭时输出 HIGH
 */
#include "lock_control.h"

const int LockControl::lockPins[LOCK_COUNT] = {LOCK0_PIN, LOCK1_PIN, LOCK2_PIN, LOCK3_PIN};
bool LockControl::lockActive[LOCK_COUNT]        = {false, false, false, false};
unsigned long LockControl::lockOpenTime[LOCK_COUNT] = {0, 0, 0, 0};

void LockControl::init() {
    for (int i = 0; i < LOCK_COUNT; i++) {
        pinMode(lockPins[i], OUTPUT);
        digitalWrite(lockPins[i], HIGH);  // 低电平触发，默认 HIGH 关闭
        lockActive[i]    = false;
        lockOpenTime[i]  = 0;
    }
    Serial.println(F("[LOCK] 4路锁控制初始化完成"));
}

bool LockControl::openLock(int lockId) {
    if (lockId < 0 || lockId >= LOCK_COUNT) {
        Serial.printf("[LOCK] 开锁失败：锁编号 %d 无效\n", lockId);
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
