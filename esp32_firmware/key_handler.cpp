/**
 * key_handler.cpp - 4 路按键处理实现
 * 上拉输入，按下为 LOW，含消抖和长按检测
 */
#include "key_handler.h"

const int KeyHandler::keyPins[KEY_COUNT] = {KEY0_PIN, KEY1_PIN, KEY2_PIN, KEY3_PIN};

bool KeyHandler::lastState[KEY_COUNT]            = {false, false, false, false};
bool KeyHandler::lastReadState[KEY_COUNT]        = {false, false, false, false};
unsigned long KeyHandler::lastDebounceTime[KEY_COUNT] = {0, 0, 0, 0};
bool KeyHandler::pressedReported[KEY_COUNT]      = {false, false, false, false};
unsigned long KeyHandler::pressStartTime[KEY_COUNT]   = {0, 0, 0, 0};
bool KeyHandler::longPressFired[KEY_COUNT]       = {false, false, false, false};
int  KeyHandler::longPressKey                    = -1;

void KeyHandler::init() {
    for (int i = 0; i < KEY_COUNT; i++) {
        pinMode(keyPins[i], INPUT_PULLUP);  // 上拉输入
        lastState[i]          = false;  // false 表示释放
        lastReadState[i]      = false;
        lastDebounceTime[i]   = 0;
        pressedReported[i]    = false;
        pressStartTime[i]     = 0;
        longPressFired[i]     = false;
    }
    longPressKey = -1;
    Serial.println(F("[KEY] 4路按键初始化完成（上拉输入，低电平有效）"));
}

bool KeyHandler::isKeyPressedRaw(int keyId) {
    if (keyId < 0 || keyId >= KEY_COUNT) return false;
    // LOW = 按下，返回 true
    return (digitalRead(keyPins[keyId]) == LOW);
}

void KeyHandler::update() {
    unsigned long now = millis();
    longPressKey = -1;  // 每轮重置，仅在该轮检测到时置位

    for (int i = 0; i < KEY_COUNT; i++) {
        bool rawPressed = isKeyPressedRaw(i);  // 当前原始读数（true=按下）

        // 消抖：原始读数与上次稳定读数不同时，重置消抖计时
        if (rawPressed != lastReadState[i]) {
            lastDebounceTime[i] = now;
            lastReadState[i]    = rawPressed;
        }

        // 消抖稳定后更新稳定状态
        if ((now - lastDebounceTime[i]) >= KEY_DEBOUNCE_MS) {
            // 从释放变到按下
            if (rawPressed && !lastState[i]) {
                pressStartTime[i]  = now;
                longPressFired[i]  = false;
            }
            // 从按下变到释放
            if (!rawPressed && lastState[i]) {
                // 释放时复位按下上报标志，允许下次重新上报
                pressedReported[i] = false;
                longPressFired[i]  = false;
            }
            lastState[i] = rawPressed;

            // 长按检测：按下持续超过 KEY_LONGPRESS_MS 且未触发过
            if (rawPressed && !longPressFired[i] &&
                (now - pressStartTime[i] >= KEY_LONGPRESS_MS)) {
                longPressFired[i] = true;
                longPressKey      = i;
                Serial.printf("[KEY] 检测到长按: Key%d (持续 %lu ms)\n", i, now - pressStartTime[i]);
            }
        }
    }
}

int KeyHandler::getKeyPressed() {
    // 返回稳定按下且未上报过的按键编号
    unsigned long now = millis();
    for (int i = 0; i < KEY_COUNT; i++) {
        if (lastState[i] && !pressedReported[i] && !longPressFired[i]) {
            // 确保不是长按过程中误触发短按
            if ((now - pressStartTime[i]) < KEY_LONGPRESS_MS) {
                pressedReported[i] = true;
                Serial.printf("[KEY] 按键按下: Key%d\n", i);
                return i;
            }
        }
    }
    return -1;
}

bool KeyHandler::isLongPressDetected() {
    // update() 中已检测并设置 longPressKey
    if (longPressKey >= 0) {
        Serial.printf("[KEY] 长按事件触发，切换模式 (Key%d)\n", longPressKey);
        // 标记已处理，避免重复触发（直到按键释放后在 update 中复位）
        longPressFired[longPressKey] = true;
        int fired = longPressKey;
        longPressKey = -1;
        return true;
    }
    return false;
}
