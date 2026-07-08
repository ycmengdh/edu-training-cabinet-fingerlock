/**
 * key_handler.h - 4 路按键处理
 * 上拉输入，低电平有效，含消抖和长按检测
 */
#ifndef KEY_HANDLER_H
#define KEY_HANDLER_H

#include <Arduino.h>
#include "config.h"

class KeyHandler {
public:
    // 初始化按键 GPIO（上拉输入）
    static void init();

    // 检测当前按下的按键，返回 0~3，无按键返回 -1
    // 内部带消抖，每次按下只返回一次
    static int getKeyPressed();

    // 检测是否有任意按键被长按达到 10 秒
    // 返回 true 表示检测到长按（用于切换 AP/STA 模式）
    // 长按触发一次后需释放才能再次触发
    static bool isLongPressDetected();

    // 主循环调用，更新按键状态
    static void update();

    // 获取指定按键的原始电平（false=按下，true=释放）
    static bool isKeyPressedRaw(int keyId);

private:
    static const int keyPins[KEY_COUNT];
    static bool lastState[KEY_COUNT];          // 上次稳定状态
    static bool lastReadState[KEY_COUNT];      // 上次原始读数
    static unsigned long lastDebounceTime[KEY_COUNT]; // 消抖计时
    static bool pressedReported[KEY_COUNT];    // 是否已上报按下
    static unsigned long pressStartTime[KEY_COUNT];   // 按下起始时刻
    static bool longPressFired[KEY_COUNT];     // 长按是否已触发
    static int  longPressKey;                  // 触发长按的按键编号，-1 表示无
};

#endif // KEY_HANDLER_H
