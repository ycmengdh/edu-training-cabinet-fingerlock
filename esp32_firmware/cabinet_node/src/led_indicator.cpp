/**
 * led_indicator.cpp - 指纹状态 LED 指示驱动实现（V2.7）
 *
 * 双色 LED（绿 GPIO41 / 红 GPIO38）指示指纹验证状态。
 * 非阻塞状态机，由主循环 update() 驱动。
 */
#include "led_indicator.h"
#include "debug.h"

FpLed::State FpLed::state = FpLed::STATE_OFF;
unsigned long FpLed::stateEnterMs = 0;
unsigned long FpLed::lastToggleMs = 0;
int FpLed::failBlinkCount = 0;
bool FpLed::greenOn = false;
bool FpLed::redOn = false;

void FpLed::writeGreen(bool on) {
    greenOn = on;
#if FP_LED_COMMON_ANODE
    digitalWrite(FP_LED_GREEN_PIN, on ? LOW : HIGH);
#else
    digitalWrite(FP_LED_GREEN_PIN, on ? HIGH : LOW);
#endif
}

void FpLed::writeRed(bool on) {
    redOn = on;
#if FP_LED_COMMON_ANODE
    digitalWrite(FP_LED_RED_PIN, on ? LOW : HIGH);
#else
    digitalWrite(FP_LED_RED_PIN, on ? HIGH : LOW);
#endif
}

void FpLed::setState(State s) {
    if (state == s) return;
    Debug::printf("[FP_LED] state %d -> %d\n", (int)state, (int)s);
    state = s;
    stateEnterMs = millis();
    lastToggleMs = millis();
    failBlinkCount = 0;
}

void FpLed::init() {
    pinMode(FP_LED_GREEN_PIN, OUTPUT);
    pinMode(FP_LED_RED_PIN, OUTPUT);
    writeGreen(false);
    writeRed(false);
    state = STATE_OFF;
    stateEnterMs = millis();
    lastToggleMs = millis();
    failBlinkCount = 0;
    Debug::printf("[FP_LED] init: green=GPIO%d red=GPIO%d common_anode=%d\n",
                  FP_LED_GREEN_PIN, FP_LED_RED_PIN, FP_LED_COMMON_ANODE);
}

void FpLed::setIdentifying() {
    setState(STATE_IDENTIFYING);
    // 识别中：绿灯亮起开始慢闪
    writeGreen(true);
    writeRed(false);
}

void FpLed::setSuccess() {
    setState(STATE_SUCCESS);
    // 成功：绿灯常亮，红灯灭
    writeGreen(true);
    writeRed(false);
}

void FpLed::setFail() {
    setState(STATE_FAIL);
    // 失败：红灯亮起开始闪烁
    writeGreen(false);
    writeRed(true);
}

void FpLed::setOff() {
    setState(STATE_OFF);
    writeGreen(false);
    writeRed(false);
}

void FpLed::update() {
    unsigned long now = millis();
    switch (state) {
        case STATE_OFF:
            // 无操作
            break;

        case STATE_IDENTIFYING: {
            // 识别中：绿灯慢闪（500ms 周期 = 250ms 亮 + 250ms 灭）
            if (now - lastToggleMs >= FP_LED_IDENTIFY_HALF_MS) {
                lastToggleMs = now;
                writeGreen(!greenOn);
            }
            break;
        }

        case STATE_SUCCESS:
            // 成功：绿灯常亮，无需切换
            if (!greenOn) writeGreen(true);
            if (redOn) writeRed(false);
            break;

        case STATE_FAIL: {
            // 失败：红灯闪烁 3 次（每次 250ms 亮 + 250ms 灭 = 500ms）
            // 3 次后自动熄灭回到 OFF
            if (now - lastToggleMs >= FP_LED_BLINK_HALF_MS) {
                lastToggleMs = now;
                if (redOn) {
                    writeRed(false);
                    failBlinkCount++;
                    if (failBlinkCount >= FP_LED_FAIL_BLINK_COUNT) {
                        // 闪烁完成，回到 OFF
                        setState(STATE_OFF);
                        writeGreen(false);
                        writeRed(false);
                    }
                } else {
                    writeRed(true);
                }
            }
            break;
        }
    }
}
