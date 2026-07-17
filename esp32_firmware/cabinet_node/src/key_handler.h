/**
 * key_handler.h - 5 路按键处理
 * Key1-4: 开锁键, Key5: 取消键
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

    // 检测当前按下的按键，返回 0~4，无按键返回 -1
    // Key0-3: 开锁键, Key4: 取消键
    static int getKeyPressed();

    // 检测是否有任意按键被长按达到 10 秒
    static bool isLongPressDetected();

    // 主循环调用，更新按键状态
    static void update();

    // 获取指定按键的原始电平（false=按下，true=释放）
    static bool isKeyPressedRaw(int keyId);

private:
    static const int keyPins[KEY_COUNT];
    static bool lastState[KEY_COUNT];
    static bool lastReadState[KEY_COUNT];
    static unsigned long lastDebounceTime[KEY_COUNT];
    static bool pressedReported[KEY_COUNT];
    static unsigned long pressStartTime[KEY_COUNT];
    static bool longPressFired[KEY_COUNT];
    static int  longPressKey;
};

#endif // KEY_HANDLER_H
