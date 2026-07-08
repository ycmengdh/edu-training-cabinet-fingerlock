/**
 * fingerprint.cpp - AS608 指纹模块驱动实现
 */
#include "fingerprint.h"

HardwareSerial Fingerprint::serial2(2);
Adafruit_Fingerprint Fingerprint::finger(&serial2);
bool Fingerprint::ready = false;
String Fingerprint::errorMsg = "";

bool Fingerprint::init() {
    // 初始化 UART2：ESP32 TX=GPIO17, RX=GPIO16
    serial2.begin(FINGER_UART_BAUD, SERIAL_8N1, FINGER_RX_PIN, FINGER_TX_PIN);
    finger.begin(FINGER_UART_BAUD);

    // 验证模块握手
    if (finger.verifyPassword() == FINGERPRINT_OK) {
        ready = true;
        Serial.printf("[FINGER] 指纹模块初始化成功，型号=0x%02X%02X, 容量=%d\n",
                      finger.libraryModel >> 8, finger.libraryModel & 0xFF,
                      finger.capacity);
        return true;
    } else {
        ready = false;
        errorMsg = "未检测到指纹模块或密码错误";
        Serial.println(F("[FINGER] 指纹模块初始化失败！"));
        return false;
    }
}

bool Fingerprint::isReady() {
    return ready;
}

String Fingerprint::lastError() {
    return errorMsg;
}

uint8_t Fingerprint::waitForFinger(int stage) {
    // stage 0: 第一次采集特征，stage 1: 第二次采集特征
    uint8_t result = 0;
    int retry = 0;
    while (retry < 600) {  // 最多等待约 60 秒
        result = finger.getImage();
        if (result == FINGERPRINT_OK) {
            // 检测到手指图像
            uint8_t conv = (stage == 0) ? finger.image2Tz(1) : finger.image2Tz(2);
            if (conv == FINGERPRINT_OK) {
                Serial.printf("[FINGER] 第 %d 次特征采集成功\n", stage + 1);
                return FINGERPRINT_OK;
            } else {
                errorMsg = "图像转特征失败";
                return conv;
            }
        } else if (result == FINGERPRINT_NOFINGER) {
            // 等待手指
            retry++;
            delay(100);
        } else {
            errorMsg = "读取图像失败";
            return result;
        }
    }
    errorMsg = "等待超时";
    return FINGERPRINT_NOFINGER;
}

bool Fingerprint::enrollFingerprint(int id) {
    if (!ready) {
        errorMsg = "指纹模块未就绪";
        return false;
    }
    if (id < 0 || id >= FINGER_MAX_USERS) {
        errorMsg = "指纹 ID 超出范围";
        return false;
    }

    Serial.printf("[FINGER] 开始录入指纹 ID=%d\n", id);
    Serial.println(F("[FINGER] 请将手指放上..."));

    // 第一次采集
    uint8_t r = waitForFinger(0);
    if (r != FINGERPRINT_OK) {
        Serial.printf("[FINGER] 第一次采集失败: %s\n", errorMsg.c_str());
        return false;
    }

    Serial.println(F("[FINGER] 请移开手指..."));
    delay(2000);

    // 第二次采集
    Serial.println(F("[FINGER] 请再次放上同一手指..."));
    r = waitForFinger(1);
    if (r != FINGERPRINT_OK) {
        Serial.printf("[FINGER] 第二次采集失败: %s\n", errorMsg.c_str());
        return false;
    }

    // 合并特征并存储
    r = finger.createModel();
    if (r != FINGERPRINT_OK) {
        errorMsg = (r == FINGERPRINT_ENROLLMISMATCH) ? "两次指纹不匹配" : "合并特征失败";
        Serial.printf("[FINGER] %s\n", errorMsg.c_str());
        return false;
    }

    r = finger.storeModel(id);
    if (r != FINGERPRINT_OK) {
        errorMsg = "存储指纹失败";
        Serial.printf("[FINGER] 存储指纹失败, code=%d\n", r);
        return false;
    }

    Serial.printf("[FINGER] 指纹 ID=%d 录入成功\n", id);
    return true;
}

int Fingerprint::verifyFingerprint() {
    if (!ready) {
        errorMsg = "指纹模块未就绪";
        return -2;
    }

    uint8_t r = finger.getImage();
    if (r == FINGERPRINT_NOFINGER) {
        return -1;  // 没有手指，正常等待
    }
    if (r != FINGERPRINT_OK) {
        errorMsg = "读取图像失败";
        return -2;
    }

    r = finger.image2Tz();
    if (r != FINGERPRINT_OK) {
        errorMsg = "图像转特征失败";
        return -2;
    }

    // 在指纹库中搜索匹配
    r = finger.fingerSearch();
    if (r == FINGERPRINT_OK) {
        Serial.printf("[FINGER] 匹配成功: ID=%d, 置信度=%d\n",
                      finger.fingerID, finger.confidence);
        return finger.fingerID;
    } else if (r == FINGERPRINT_NOTFOUND) {
        Serial.println(F("[FINGER] 未找到匹配指纹"));
        errorMsg = "未找到匹配指纹";
        return -1;
    } else {
        errorMsg = "搜索失败";
        return -2;
    }
}

bool Fingerprint::deleteFingerprint(int id) {
    if (!ready) {
        errorMsg = "指纹模块未就绪";
        return false;
    }
    uint8_t r = finger.deleteModel(id);
    if (r == FINGERPRINT_OK) {
        Serial.printf("[FINGER] 已删除指纹 ID=%d\n", id);
        return true;
    }
    errorMsg = "删除指纹失败";
    Serial.printf("[FINGER] 删除指纹失败, code=%d\n", r);
    return false;
}

bool Fingerprint::deleteAllFingerprints() {
    if (!ready) {
        errorMsg = "指纹模块未就绪";
        return false;
    }
    uint8_t r = finger.emptyDatabase();
    if (r == FINGERPRINT_OK) {
        Serial.println(F("[FINGER] 已清空指纹库"));
        return true;
    }
    errorMsg = "清空指纹库失败";
    return false;
}

int Fingerprint::getFingerprintCount() {
    if (!ready) return 0;
    // 读取模板数量
    uint8_t r = finger.getTemplateCount();
    if (r == FINGERPRINT_OK) {
        return finger.templateCount;
    }
    return 0;
}
