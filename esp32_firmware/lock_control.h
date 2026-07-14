/**
 * lock_control.h - 4 路继电器锁控制
 * 低电平触发，非阻塞开锁（millis 计时）
 * 含权限指示灯（LED_PERM0~3）控制，以及开锁窗口期的权限校验。
 */
#ifndef LOCK_CONTROL_H
#define LOCK_CONTROL_H

#include <Arduino.h>
#include "config.h"

class LockControl {
public:
    // 初始化 4 路继电器 GPIO，默认输出 HIGH（关闭状态）；并初始化权限指示灯
    static void init();

    // 开锁（非阻塞）：输出 LOW 持续 LOCK_OPEN_DURATION_MS 后自动关闭
    // lockId: 0~3，返回 true 表示已触发开锁，false 表示参数无效或锁正忙
    // 注：若已通过 setAllowedLocks 启用窗口期权限校验，则无权限的锁返回 false
    static bool openLock(int lockId);

    // 开锁窗口期专用：开锁前先检查该锁是否有权限（allowed_locks）
    // 有权限则开锁并返回 true，无权限返回 false。不影响远程 CONTROL_LOCK 等路径。
    static bool openLockChecked(int lockId);

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

    // ====== 权限指示灯（需求 2：开锁窗口期引导灯） ======
    // 控制指定锁对应的权限指示灯亮灭
    // lockId: 0~3，on: true=亮，false=灭
    static void setPermissionLed(int lockId, bool on);

    // 关闭全部权限指示灯
    static void clearAllPermissionLeds();

    // ====== 开锁窗口期权限校验（需求 2） ======
    // 设置窗口期允许开锁的锁列表（allowed_locks），并启用 openLockChecked 校验
    static void setAllowedLocks(const bool allowed[LOCK_COUNT]);
    // 清除窗口期权限校验（退出开锁窗口时调用）
    static void clearAllowedLocks();
    // 当前是否启用了窗口期权限校验
    static bool isPermissionEnforced();
    // 查询指定锁在当前窗口期是否有权限
    static bool isLockAllowed(int lockId);

private:
    static const int lockPins[LOCK_COUNT];
    static const int permLedPins[LOCK_COUNT];  // 权限指示灯引脚
    static bool lockActive[LOCK_COUNT];      // 是否处于开锁激活状态
    static unsigned long lockOpenTime[LOCK_COUNT]; // 开锁触发时刻

    // 开锁窗口期权限校验状态
    static bool allowedLocks[LOCK_COUNT];    // 窗口期内允许开锁的锁列表
    static bool enforcePermission;           // 是否启用窗口期权限校验
};

#endif // LOCK_CONTROL_H
