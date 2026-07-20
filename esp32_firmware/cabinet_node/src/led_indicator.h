/**
 * led_indicator.h - 指纹状态 LED 指示驱动（V2.7）
 *
 * 双色 LED 指示指纹验证流程状态：
 *   - setIdentifying(): 识别中，绿灯慢闪（500ms 周期）
 *   - setSuccess():     验证成功，绿灯常亮（持续至操作窗口结束）
 *   - setFail():        验证失败，红灯闪烁 3 次后自动熄灭
 *   - setOff():         熄灭所有 LED
 *
 * 非阻塞设计：内部状态机 + 时间戳，由主循环 update() 驱动闪烁。
 * 不影响 Mesh 通信和指纹采集的实时性。
 */
#ifndef LED_INDICATOR_H
#define LED_INDICATOR_H

#include <Arduino.h>
#include "config.h"

class FpLed {
public:
    // 初始化 GPIO
    static void init();

    // ====== 状态设置（立即生效，闪烁由 update 驱动） ======
    // 识别中：绿灯慢闪
    static void setIdentifying();
    // 验证成功：绿灯常亮
    static void setSuccess();
    // 验证失败：红灯闪烁 3 次（约 1.5 秒）后自动熄灭
    static void setFail();
    // 熄灭所有 LED
    static void setOff();

    // 主循环调用，驱动闪烁动画
    static void update();

    // 当前状态查询（用于日志/调试）
    enum State {
        STATE_OFF = 0,
        STATE_IDENTIFYING,
        STATE_SUCCESS,
        STATE_FAIL
    };
    static State getState() { return state; }

private:
    static State state;
    static unsigned long stateEnterMs;   // 当前状态进入时刻
    static unsigned long lastToggleMs;   // 上次电平切换时刻
    static int failBlinkCount;           // 失败闪烁已完成的次数
    static bool greenOn;
    static bool redOn;

    // 根据 FP_LED_COMMON_ANODE 极性写 GPIO
    static void writeGreen(bool on);
    static void writeRed(bool on);
    static void setState(State s);
};

#endif // LED_INDICATOR_H
