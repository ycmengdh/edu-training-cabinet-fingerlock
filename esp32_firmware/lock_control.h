/**
 * lock_control.h - 4 路继电器锁控制
 * 低电平触发，非阻塞开锁（millis 计时）
 */
#ifndef LOCK_CONTROL_H
#define LOCK_CONTROL_H

#include <Arduino.h>
#include "config.h"

class LockControl {
public:
    // 初始化 4 路继电器 GPIO，默认输出 HIGH（关闭状态）
    static void init();

    // 开锁（非阻塞）：输出 LOW 持续 LOCK_OPEN_DURATION_MS 后自动关闭
    // lockId: 0~3，返回 true 表示已触发开锁，false 表示参数无效或锁正忙
    static bool openLock(int lockId);

    // 关闭指定锁
    static void closeLock(int lockId);

    // 关闭所有锁
    static void closeAllLocks();

    // 获取锁状态：返回 4 位状态，1=正在开锁，0=关闭
    // 结果通过 status[4] 输出
    static void getLockStatus(bool status[LOCK_COUNT]);

    // 主循环调用，处理非阻塞自动关锁
    static void update();

    // 是否有任意锁正在开锁
    static bool anyLockActive();

private:
    static const int lockPins[LOCK_COUNT];
    static bool lockActive[LOCK_COUNT];      // 是否处于开锁激活状态
    static unsigned long lockOpenTime[LOCK_COUNT]; // 开锁触发时刻
};

#endif // LOCK_CONTROL_H
