/**
 * lock_control.h - 4 路锁控制 via 74HC595 移位寄存器
 * 595 Q0-Q3: 继电器(低电平触发), Q4-Q7: 锁状态LED(高电平亮)
 */
#ifndef LOCK_CONTROL_H
#define LOCK_CONTROL_H

#include <Arduino.h>
#include "config.h"

class LockControl {
public:
    // 初始化 595 控制引脚，默认所有锁关闭
    static void init();

    // 开锁（非阻塞）：继电器 LOW 持续 LOCK_OPEN_DURATION_MS 后自动关闭
    // lockId: 0~3，返回 true 表示已触发，false 表示参数无效
    static bool openLock(int lockId);

    // 关闭指定锁
    static void closeLock(int lockId);

    // 关闭所有锁
    static void closeAllLocks();

    // 获取锁状态：1=正在开锁，0=关闭
    static void getLockStatus(bool status[LOCK_COUNT]);

    // 主循环调用，处理非阻塞自动关锁
    static void update();

    // 是否有任意锁正在开锁
    static bool anyLockActive();

private:
    static bool lockActive[LOCK_COUNT];         // 是否处于开锁激活状态
    static unsigned long lockOpenTime[LOCK_COUNT]; // 开锁触发时刻

    // 向 74HC595 移位写入 1 字节
    static void shiftOut595(uint8_t data);

    // 根据锁状态计算并更新 595 输出
    static void updateShiftRegister();
};

#endif // LOCK_CONTROL_H
