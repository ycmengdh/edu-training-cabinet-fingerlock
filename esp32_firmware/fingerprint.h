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

    // 采集并录入指纹（需求 5：4 次录入 + 2 次验证 的 6 步流程）
    // 步骤 1-4：采集 4 次指纹图像并生成特征，存入 AS608 特征缓冲 1-4
    // 步骤 5  ：合并 4 个特征为模板，进行第 1 次验证比对（fingerFastSearch）
    // 步骤 6  ：第 2 次验证比对，两次都通过才 storeModel 保存
    // id: 指纹存储位置 0~FINGER_MAX_USERS-1
    // 返回：成功 true，失败 false（任何步骤失败均不保存）
    static bool enrollFingerprint(int id);

    // 分步录入指纹（需求 5）：支持上位机通过 ENROLL_FP_STAGE 命令逐步驱动
    // stage 取值：
    //   "acquire1"~"acquire4"：采集图像 + 生成特征（特征 1-4，存入 AS608 缓冲 1-4）
    //   "verify1"            ：合并 4 个特征为模板 + 第 1 次验证比对（fingerFastSearch）
    //   "verify2"            ：第 2 次验证比对 + storeModel(id) 保存
    // id : 指纹存储位置 0~FINGER_MAX_USERS-1
    // 返回：JSON 字符串 {"stage":"...","success":true/false,"error":"..."}
    static String enrollFingerprintStage(const String &stage, int id);

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

    // 将模板数据写入 AS608 指定 ID（用于从 SD 卡恢复 / DEPLOY_USER 下发模板）
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

    // 采集一次图像并生成特征，存入 AS608 指定特征缓冲槽 slot(1~4)
    // 返回 FINGERPRINT_OK 表示成功，其余为失败码
    static uint8_t acquireFeature(int slot);

    // 分步录入跨阶段状态：记录 verify1 是否已通过（verify2 前置条件）
    static bool stageVerify1Passed;
    // 分步录入跨阶段状态：记录当前正在录入的 id（verify2 storeModel 用）
    static int  stageCurrentId;
};

#endif // FINGERPRINT_H
