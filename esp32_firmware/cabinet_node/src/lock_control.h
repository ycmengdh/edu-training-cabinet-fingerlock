/**
 * lock_control.h - 4 路锁控制 via 74HC595 移位寄存器
 * 595 Q0-Q3: 锁状态 LED，Q4-Q7: 继电器（均高电平有效）
 *
 * LED 三种用途：
 *   - 开锁时常亮（跟随继电器）
 *   - 验证窗口内：有权限的锁慢闪提示可按
 *   - 空闲时熄灭
 */
#ifndef LOCK_CONTROL_H
#define LOCK_CONTROL_H

#include <Arduino.h>
#include "config.h"

class LockControl {
public:
    // 初始化 595 控制引脚，默认所有锁关闭
    static void init();

    // 开锁（非阻塞）：继电器 HIGH 持续 LOCK_OPEN_DURATION_MS 后自动关闭
    // lockId: 0~3，返回 true 表示已触发，false 表示参数无效
    static bool openLock(int lockId);

    // 关闭指定锁
    static void closeLock(int lockId);

    // 关闭所有锁
    static void closeAllLocks();

    // 获取锁状态：1=正在开锁，0=关闭
    static void getLockStatus(bool status[LOCK_COUNT]);

    // 主循环调用，处理非阻塞自动关锁 + 权限提示灯慢闪
    static void update();

    // 是否有任意锁正在开锁
    static bool anyLockActive();

    // 验证窗口：按权限位启动对应锁 LED 慢闪（继电器不受影响）
    static void setPermissionHint(const bool perms[LOCK_COUNT]);
    // 结束验证窗口 / 开锁后：关闭所有提示慢闪
    static void clearPermissionHint();

private:
    static bool lockActive[LOCK_COUNT];         // 是否处于开锁激活状态
    static unsigned long lockOpenTime[LOCK_COUNT]; // 开锁触发时刻
    static bool ledHint[LOCK_COUNT];            // 验证窗口权限提示
    static bool blinkPhaseOn;                   // 慢闪当前相位（亮/灭）
    static unsigned long lastBlinkToggleMs;     // 上次慢闪切换时刻

    // 向 74HC595 移位写入 1 字节
    static void shiftOut595(uint8_t data);

    // 根据锁状态 + 提示灯相位计算并更新 595 输出
    static void updateShiftRegister();

    static bool anyHintActive();
};

#endif // LOCK_CONTROL_H
