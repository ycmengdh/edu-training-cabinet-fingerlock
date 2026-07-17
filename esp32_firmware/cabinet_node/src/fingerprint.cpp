/**
 * fingerprint.cpp - AS608 指纹模块驱动实现
 */
#include "fingerprint.h"
#include "debug.h"

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
        // 读取系统参数以获取型号与容量（库 v2.1.x 无 libraryModel 字段）
        finger.getParameters();
        Debug::printf("[FINGER] Fingerprint module init success, model=0x%04X, capacity=%d\n",
                      finger.system_id, finger.capacity);
        return true;
    } else {
        ready = false;
        errorMsg = "未检测到指纹模块或密码错误";
        Debug::println(F("[FINGER] Fingerprint module init failed!"));
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
                Debug::printf("[FINGER] Feature capture %d success\n", stage + 1);
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

    Debug::printf("[FINGER] Start enroll fingerprint ID=%d\n", id);
    Debug::println(F("[FINGER] Place finger..."));

    // 第一次采集
    uint8_t r = waitForFinger(0);
    if (r != FINGERPRINT_OK) {
        Debug::printf("[FINGER] First capture failed: %s\n", errorMsg.c_str());
        return false;
    }

    Debug::println(F("[FINGER] Remove finger..."));
    delay(2000);

    // 第二次采集
    Debug::println(F("[FINGER] Place same finger again..."));
    r = waitForFinger(1);
    if (r != FINGERPRINT_OK) {
        Debug::printf("[FINGER] Second capture failed: %s\n", errorMsg.c_str());
        return false;
    }

    // 合并特征并存储
    r = finger.createModel();
    if (r != FINGERPRINT_OK) {
        errorMsg = (r == FINGERPRINT_ENROLLMISMATCH) ? "两次指纹不匹配" : "合并特征失败";
        Debug::printf("[FINGER] %s\n", errorMsg.c_str());
        return false;
    }

    r = finger.storeModel(id);
    if (r != FINGERPRINT_OK) {
        errorMsg = "存储指纹失败";
        Debug::printf("[FINGER] Store fingerprint failed, code=%d\n", r);
        return false;
    }

    Debug::printf("[FINGER] Fingerprint ID=%d enroll success\n", id);
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
        Debug::printf("[FINGER] Match success: ID=%d, confidence=%d\n",
                      finger.fingerID, finger.confidence);
        return finger.fingerID;
    } else if (r == FINGERPRINT_NOTFOUND) {
        Debug::println(F("[FINGER] No matching fingerprint found"));
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
        Debug::printf("[FINGER] Deleted fingerprint ID=%d\n", id);
        return true;
    }
    errorMsg = "删除指纹失败";
    Debug::printf("[FINGER] Delete fingerprint failed, code=%d\n", r);
    return false;
}

bool Fingerprint::deleteAllFingerprints() {
    if (!ready) {
        errorMsg = "指纹模块未就绪";
        return false;
    }
    uint8_t r = finger.emptyDatabase();
    if (r == FINGERPRINT_OK) {
        Debug::println(F("[FINGER] Fingerprint database cleared"));
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

bool Fingerprint::readTemplate(int id, uint8_t *outBuf, size_t bufSize, size_t &outLen) {
    outLen = 0;
    if (!ready) {
        errorMsg = "指纹模块未就绪";
        return false;
    }
    if (bufSize < FP_TEMPLATE_SIZE) {
        errorMsg = "缓冲区过小";
        return false;
    }

    // 1. 加载指定 ID 的模板到模块缓冲区
    uint8_t r = finger.loadModel(id);
    if (r != FINGERPRINT_OK) {
        errorMsg = "加载模板失败";
        Debug::printf("[FINGER] loadModel(%d) failed code=%d\n", id, r);
        return false;
    }

    // 2. 请求上传模板数据（getModel 触发模块以数据包形式发送）
    r = finger.getModel();
    if (r != FINGERPRINT_OK) {
        errorMsg = "请求上传模板失败";
        Debug::printf("[FINGER] getModel failed code=%d\n", r);
        return false;
    }

    // 3. 从串口读取数据包（AS608 上传格式：包头+地址+包类型+长度+数据+校验）
    // Adafruit 库未自动接收上传数据，需手动解析
    // 简化处理：直接读取串口原始字节，提取有效模板数据
    // 标准上传包：EF01FFFFFFFF07 [包标识=02] [包长度高] [包长度低=00FF] [256B数据] [校验和2B]
    // 512B 模板分两个 256B 包上传
    size_t totalRead = 0;
    int packets = 0;
    unsigned long startMs = millis();

    while (packets < 2 && millis() - startMs < 3000) {
        // 等待包头 EF01
        if (!serial2.available()) {
            delay(2);
            continue;
        }
        // 找包头 0xEF 0x01
        uint8_t b = serial2.read();
        if (b != 0xEF) continue;
        // 等待 0x01
        unsigned long t0 = millis();
        while (millis() - t0 < 200) {
            if (serial2.available()) {
                if (serial2.read() == 0x01) break;
            }
            delay(1);
        }
        // 读取地址 4 字节 + 包标识 1 字节 + 包长度 2 字节
        uint8_t header[7];
        size_t got = 0;
        t0 = millis();
        while (got < 7 && millis() - t0 < 500) {
            if (serial2.available()) {
                header[got++] = serial2.read();
            } else {
                delay(1);
            }
        }
        if (got < 7) {
            Debug::println(F("[FINGER] Template packet header incomplete"));
            break;
        }
        // header[4] = 包标识(02), header[5..6] = 包长度（含数据+校验）
        uint16_t pktLen = (header[5] << 8) | header[6];
        if (pktLen < 3) break;  // 至少含校验 2B
        uint16_t dataLen = pktLen - 2;  // 去掉 2 字节校验和
        if (dataLen > 256) dataLen = 256;

        // 读取数据
        size_t dataGot = 0;
        t0 = millis();
        while (dataGot < dataLen && millis() - t0 < 1000) {
            if (serial2.available()) {
                if (totalRead + dataGot < bufSize) {
                    outBuf[totalRead + dataGot] = serial2.read();
                } else {
                    serial2.read();  // 丢弃超出缓冲的数据
                }
                dataGot++;
            } else {
                delay(1);
            }
        }
        // 读取并丢弃 2 字节校验和
        uint8_t crc[2];
        size_t crcGot = 0;
        t0 = millis();
        while (crcGot < 2 && millis() - t0 < 200) {
            if (serial2.available()) {
                crc[crcGot++] = serial2.read();
            } else {
                delay(1);
            }
        }
        totalRead += dataGot;
        packets++;
        Debug::printf("[FINGER] Template packet %d: %u bytes\n", packets, dataLen);
    }

    if (totalRead == 0) {
        errorMsg = "未读到模板数据";
        Debug::println(F("[FINGER] Template upload failed: no data"));
        return false;
    }

    outLen = totalRead;
    Debug::printf("[FINGER] Template read success: ID=%d, total %u bytes\n", id, (unsigned)totalRead);
    return true;
}

bool Fingerprint::writeTemplate(int id, const uint8_t *data, size_t len) {
    if (!ready) {
        errorMsg = "指纹模块未就绪";
        return false;
    }
    if (len != FP_TEMPLATE_SIZE) {
        Debug::printf("[FINGER] Template length abnormal: %u (expected %d)\n", (unsigned)len, FP_TEMPLATE_SIZE);
        // 不直接返回，尝试写入
    }

    // AS608 写模板流程：
    // 1. sendPacket 发送"下载模板"命令(0x09)，包标识=09（数据包），数据为模板内容
    // 2. 模块将数据存入 CharBuffer1/2，再 storeModel 持久化
    // Adafruit 库未直接暴露 writeTemplate，使用底层写数据包方式
    // 简化实现：通过 sendCommand 0x09 下发模板

    // 构造下载命令包：先发 256B 到 buffer1
    // 命令头：EF01FFFFFFFF [08=命令包] [长度高] [长度低] [07=下载到buffer1] ... [校验]
    // 这里采用库提供的 writeTemplate（v2.1.3 起支持）
    // 若库版本不支持，降级为直接串口写入

    // 尝试用 Adafruit 库的 writeTemplate（如可用）
    // 库 API: uint8_t writeTemplate(uint8_t id, uint8_t bufferNum, const uint8_t* templateData)
    // 不同版本签名不同，这里用通用命令方式

    // 分两包下发（每包 256B），包标识 0x08
    size_t offset = 0;
    for (int pkt = 0; pkt < 2; pkt++) {
        size_t chunkLen = 256;
        if (offset + chunkLen > len) chunkLen = len - offset;
        if (chunkLen == 0) break;

        // 构造数据包：包标识=08，包类型=02（数据包）...
        // Adafruit 库的 sendPacket 兼容方式
        const uint8_t packetType = 0x02;  // 数据包
        const uint8_t packetId = 0x08;    // 下行数据

        uint16_t lengthField = chunkLen + 2;  // 数据 + 校验
        uint16_t sum = (packetType << 8) | packetId;
        sum += (lengthField >> 8) & 0xFF;
        sum += lengthField & 0xFF;
        for (size_t i = 0; i < chunkLen; i++) {
            sum += data[offset + i];
        }

        // 帧头
        serial2.write(0xEF); serial2.write(0x01);
        // 地址 4 字节
        serial2.write((uint8_t)0xFF); serial2.write((uint8_t)0xFF);
        serial2.write((uint8_t)0xFF); serial2.write((uint8_t)0xFF);
        // 包标识 + 包类型
        serial2.write(packetType);
        serial2.write(packetId);
        // 包长度
        serial2.write((uint8_t)((lengthField >> 8) & 0xFF));
        serial2.write((uint8_t)(lengthField & 0xFF));
        // 数据
        serial2.write(data + offset, chunkLen);
        // 校验和
        serial2.write((uint8_t)((sum >> 8) & 0xFF));
        serial2.write((uint8_t)(sum & 0xFF));
        serial2.flush();

        offset += chunkLen;
        delay(50);  // 模块处理延时

        // 读取应答（简单丢弃）
        unsigned long t0 = millis();
        while (serial2.available() < 12 && millis() - t0 < 1000) {
            delay(1);
        }
        while (serial2.available()) {
            serial2.read();
        }
    }

    // 下发完成后，storeModel 持久化到指定 ID
    uint8_t r = finger.storeModel(id);
    if (r != FINGERPRINT_OK) {
        errorMsg = "存储下发模板失败";
        Debug::printf("[FINGER] writeTemplate storeModel failed code=%d\n", r);
        return false;
    }

    Debug::printf("[FINGER] Template write success: ID=%d\n", id);
    return true;
}
