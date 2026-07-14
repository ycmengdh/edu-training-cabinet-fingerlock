/**
 * fingerprint.h - AS608 指纹模块驱动
 * 基于 Adafruit_Fingerprint 库，使用 UART2
 */
#ifndef FINGERPRINT_H
#define FINGERPRINT_H

#include <Arduino.h>
#include <Adafruit_Fingerprint.h>
#include "config.h"

class Fingerprint {
public:
    // 初始化指纹模块（UART2）
    static bool init();

    // 采集并录入指纹（采集两次特征后合并存储）
    // id: 指纹存储位置 0~FINGER_MAX_USERS-1
    // 返回：成功 true，失败 false
    static bool enrollFingerprint(int id);

    // 验证指纹，返回指纹 ID
    // 返回 >=0 为匹配到的指纹 ID，-1 表示未匹配，-2 表示读取失败
    static int verifyFingerprint();

    // 删除指定 ID 的指纹
    static bool deleteFingerprint(int id);

    // 清空指纹库中所有指纹
    static bool deleteAllFingerprints();

    // 获取已存储指纹数量
    static int getFingerprintCount();

    // 读取指定 ID 的模板数据（用于上传到 SD 卡备份）
    // id: AS608 中的指纹 ID
    // outBuf: 输出缓冲，至少 FP_TEMPLATE_SIZE 字节
    // outLen: 返回实际读取长度
    static bool readTemplate(int id, uint8_t *outBuf, size_t bufSize, size_t &outLen);

    // 将模板数据写入 AS608 指定 ID（用于从 SD 卡恢复）
    // data/len: 模板二进制数据（应为 512 字节）
    static bool writeTemplate(int id, const uint8_t *data, size_t len);

    // 指纹模块是否就绪
    static bool isReady();

    // 获取最后一次错误描述
    static String lastError();

private:
    static HardwareSerial  serial2;
    static Adafruit_Fingerprint finger;
    static bool ready;
    static String errorMsg;

    // 等待手指按下，返回采集结果
    static uint8_t waitForFinger(int stage);
};

#endif // FINGERPRINT_H
