/**
 * key_handler.cpp - 5 路按键处理实现
 * K1-K4 开锁: GPIO 47/48/45/39, K5 取消: GPIO 40
 * 上拉输入，按下为 LOW，含消抖和长按检测
 */
#include "key_handler.h"
#include "debug.h"

const int KeyHandler::keyPins[KEY_COUNT] = {KEY0_PIN, KEY1_PIN, KEY2_PIN, KEY3_PIN, KEY4_PIN};

bool KeyHandler::lastState[KEY_COUNT]                = {false, false, false, false, false};
bool KeyHandler::lastReadState[KEY_COUNT]            = {false, false, false, false, false};
unsigned long KeyHandler::lastDebounceTime[KEY_COUNT] = {0, 0, 0, 0, 0};
bool KeyHandler::pressedReported[KEY_COUNT]          = {false, false, false, false, false};
unsigned long KeyHandler::pressStartTime[KEY_COUNT]   = {0, 0, 0, 0, 0};
bool KeyHandler::longPressFired[KEY_COUNT]           = {false, false, false, false, false};
int  KeyHandler::longPressKey                        = -1;

void KeyHandler::init() {
    for (int i = 0; i < KEY_COUNT; i++) {
        pinMode(keyPins[i], INPUT_PULLUP);
        lastState[i]          = false;
        lastReadState[i]      = false;
        lastDebounceTime[i]   = 0;
        pressedReported[i]    = false;
        pressStartTime[i]     = 0;
        longPressFired[i]     = false;
    }
    longPressKey = -1;
    Debug::println(F("[KEY] 5-channel key init complete (4 lock + 1 cancel, active LOW)"));
}

bool KeyHandler::isKeyPressedRaw(int keyId) {
    if (keyId < 0 || keyId >= KEY_COUNT) return false;
    return (digitalRead(keyPins[keyId]) == LOW);
}

void KeyHandler::update() {
    unsigned long now = millis();
    longPressKey = -1;

    for (int i = 0; i < KEY_COUNT; i++) {
        bool rawPressed = isKeyPressedRaw(i);

        if (rawPressed != lastReadState[i]) {
            lastDebounceTime[i] = now;
            lastReadState[i]    = rawPressed;
        }

        if ((now - lastDebounceTime[i]) >= KEY_DEBOUNCE_MS) {
            if (rawPressed && !lastState[i]) {
                pressStartTime[i]  = now;
                longPressFired[i]  = false;
            }
            if (!rawPressed && lastState[i]) {
                pressedReported[i] = false;
                longPressFired[i]  = false;
            }
            lastState[i] = rawPressed;

            if (rawPressed && !longPressFired[i] &&
                (now - pressStartTime[i] >= KEY_LONGPRESS_MS)) {
                longPressFired[i] = true;
                longPressKey      = i;
                Debug::printf("[KEY] Long press detected: Key%d (duration %lu ms)\n", i, now - pressStartTime[i]);
            }
        }
    }
}

int KeyHandler::getKeyPressed() {
    unsigned long now = millis();
    for (int i = 0; i < KEY_COUNT; i++) {
        if (lastState[i] && !pressedReported[i] && !longPressFired[i]) {
            if ((now - pressStartTime[i]) < KEY_LONGPRESS_MS) {
                pressedReported[i] = true;
                if (i == KEY_CANCEL_INDEX) {
                    Debug::printf("[KEY] Cancel key pressed: Key%d\n", i);
                } else {
                    Debug::printf("[KEY] Lock key pressed: Key%d\n", i);
                }
                return i;
            }
        }
    }
    return -1;
}

bool KeyHandler::isLongPressDetected() {
    if (longPressKey >= 0) {
        Debug::printf("[KEY] Long press event triggered, switch mode (Key%d)\n", longPressKey);
        longPressFired[longPressKey] = true;
        int fired = longPressKey;
        longPressKey = -1;
        return true;
    }
    return false;
}
