/**
 * fingerprint.cpp - AS608 指纹模块驱动实现
 * 供电：GPIO21 控制上电，GPIO42 读状态；UART2：TX=17, RX=18
 * 分步录入：4 次采集（两对特征，第二对覆盖提高质量）+ 2 次 1:1 验证
 */
#include "fingerprint.h"
#include "debug.h"
#include <freertos/FreeRTOS.h>
#include <freertos/queue.h>
#include <freertos/semphr.h>
#include <freertos/task.h>

namespace {
SemaphoreHandle_t sensorMutex = nullptr;
QueueHandle_t backgroundResultQueue = nullptr;
TaskHandle_t backgroundVerifyTaskHandle = nullptr;
volatile bool backgroundVerifyEnabled = false;
volatile uint32_t backgroundVerifyMaxMs = 0;
volatile uint32_t backgroundVerifyErrorCount = 0;

void ensureSensorMutex() {
    if (sensorMutex == nullptr) sensorMutex = xSemaphoreCreateMutex();
}

class SensorGuard {
public:
    SensorGuard() : locked(false) {
        ensureSensorMutex();
        locked = sensorMutex != nullptr &&
                 xSemaphoreTake(sensorMutex, portMAX_DELAY) == pdTRUE;
    }
    ~SensorGuard() {
        if (locked) xSemaphoreGive(sensorMutex);
    }
    bool acquired() const { return locked; }

private:
    bool locked;
};

void backgroundVerifyTask(void *) {
    for (;;) {
        if (!backgroundVerifyEnabled || !Fingerprint::isReady()) {
            vTaskDelay(pdMS_TO_TICKS(20));
            continue;
        }

        unsigned long startedAt = millis();
        int result = Fingerprint::verifyFingerprint();
        uint32_t elapsed = (uint32_t)(millis() - startedAt);
        if (elapsed > backgroundVerifyMaxMs) backgroundVerifyMaxMs = elapsed;
        if (result == -2) backgroundVerifyErrorCount++;

        if (backgroundVerifyEnabled && result >= 0 && backgroundResultQueue != nullptr) {
            xQueueOverwrite(backgroundResultQueue, &result);
        }
        vTaskDelay(pdMS_TO_TICKS(200));
    }
}

bool ensureBackgroundVerifyTask() {
    ensureSensorMutex();
    if (backgroundResultQueue == nullptr) {
        backgroundResultQueue = xQueueCreate(1, sizeof(int));
    }
    if (backgroundResultQueue == nullptr) return false;
    if (backgroundVerifyTaskHandle != nullptr) return true;

    BaseType_t created = xTaskCreatePinnedToCore(
        backgroundVerifyTask, "fp_idle", 4096, nullptr, 1,
        &backgroundVerifyTaskHandle, 1);
    if (created != pdPASS) {
        backgroundVerifyTaskHandle = nullptr;
        return false;
    }
    return true;
}
}  // namespace

HardwareSerial Fingerprint::serial2(2);
Adafruit_Fingerprint Fingerprint::finger(&serial2);
bool Fingerprint::ready = false;
String Fingerprint::errorMsg = "";
EnrollPhase Fingerprint::phase = ENROLL_IDLE;
int Fingerprint::enrollId = -1;
unsigned long Fingerprint::phaseEnterMs = 0;
int Fingerprint::verifyOkCount = 0;

void Fingerprint::setPower(bool on) {
    pinMode(FINGER_PWR_PIN, OUTPUT);
    digitalWrite(FINGER_PWR_PIN, on ? FINGER_PWR_ON_LEVEL : FINGER_PWR_OFF_LEVEL);
    Debug::printf("[FINGER] power %s (GPIO%d=%s)\n",
                  on ? "ON" : "OFF",
                  FINGER_PWR_PIN,
                  (on ? FINGER_PWR_ON_LEVEL : FINGER_PWR_OFF_LEVEL) == HIGH ? "HIGH" : "LOW");
}

bool Fingerprint::isPowered() {
    pinMode(FINGER_PWR_STATUS_PIN, INPUT);
    // 反馈极性独立于控制脚，见 FINGER_PWR_STATUS_ON_LEVEL
    return digitalRead(FINGER_PWR_STATUS_PIN) == FINGER_PWR_STATUS_ON_LEVEL;
}

bool Fingerprint::init() {
    SensorGuard guard;
    if (!guard.acquired()) {
        ready = false;
        errorMsg = "指纹互斥锁初始化失败";
        return false;
    }
    // DM900 手册·前言 A：上电前必须先把 UART_TX/RX 拉低，否则模块可能启动失败
    driveUartPinsLow();

    setPower(true);
    delay(FINGER_PWR_PRELOW_MS);   // 拉低保持 10ms 再放开 UART

    // 手册：8 数据位、2 停止位、无校验（8N2），半双工 3.3V TTL
    serial2.begin(FINGER_UART_BAUD, SERIAL_8N2, FINGER_RX_PIN, FINGER_TX_PIN);
    finger.begin(FINGER_UART_BAUD);

    bool pwrOk = isPowered();
    Debug::printf("[FINGER] power status GPIO%d=%s (%s)\n",
                  FINGER_PWR_STATUS_PIN,
                  digitalRead(FINGER_PWR_STATUS_PIN) == HIGH ? "HIGH" : "LOW",
                  pwrOk ? "powered" : "not powered / polarity mismatch");
    if (!pwrOk) {
        Debug::println(F("[FINGER] power status unexpected, still try UART handshake"));
    }

    // 等待模块稳定（手册：冷启动 ~130-150ms，需 >=200ms 或检测 0x55）
    delay(FINGER_PWR_STABLE_MS);

    // 手册 4.8：上电初始化完成后模块主动发一个 0x55；读到即就绪
    if (waitHandshakeByte(FINGER_HANDSHAKE_TIMEOUT_MS)) {
        Debug::printf("[FINGER] handshake byte 0x%02X seen\n", FINGER_HANDSHAKE_BYTE);
    } else {
        Debug::println(F("[FINGER] handshake byte not seen, fallback to delay"));
    }

    // checkPassword() 返回原始确认码：0x00=OK / 0x01=通信错误 / 0x13=密码错误
    // 失败重试，区分"无应答"与"密码错误"
    for (int attempt = 1; attempt <= FINGER_INIT_RETRY; attempt++) {
        uint8_t r = probeModule();
        if (r == FINGERPRINT_OK) {
            ready = true;
            finger.getParameters();
            Debug::printf("[FINGER] Fingerprint module init success, model=0x%04X, capacity=%d\n",
                          finger.system_id, finger.capacity);
            return true;
        }
        if (r == FINGERPRINT_PASSFAIL) {
            // 密码错误：重试无意义
            ready = false;
            errorMsg = "指纹模块密码错误，请确认模块口令";
            Debug::printf("[FINGER] password mismatch (r=0x%02X), stop retry\n", r);
            break;
        }
        // 通信错误/超时：打印并重试
        Debug::printf("[FINGER] probe failed attempt %d/%d, r=0x%02X\n",
                      attempt, FINGER_INIT_RETRY, r);
        if (attempt < FINGER_INIT_RETRY) {
            delay(FINGER_RETRY_DELAY_MS);
        }
    }

    ready = false;
    errorMsg = "未检测到指纹模块或通信失败";
    Debug::println(F("[FINGER] Fingerprint module init failed!"));
    return false;
}

// 上电前把指纹 UART 的 TX/RX 引脚置为输出低电平（DM900 手册要求）
void Fingerprint::driveUartPinsLow() {
    pinMode(FINGER_TX_PIN, OUTPUT);
    pinMode(FINGER_RX_PIN, OUTPUT);
    digitalWrite(FINGER_TX_PIN, LOW);
    digitalWrite(FINGER_RX_PIN, LOW);
}

// 读模块上电后主动发出的 0x55 就绪字节；超时未读到返回 false（不致命）
bool Fingerprint::waitHandshakeByte(uint16_t timeoutMs) {
    unsigned long deadline = millis() + timeoutMs;
    while (millis() < deadline) {
        if (serial2.available()) {
            int b = serial2.read();
            if (b == FINGER_HANDSHAKE_BYTE) return true;
            // 读到其它字节（如残留）继续等
        } else {
            delay(1);
        }
    }
    return false;
}

// 发送 PS_VfyPwd(0x13) 默认口令 0x00000000，返回原始确认码用于区分错误类型。
// 注意：Adafruit 库 checkPassword() 是 private，这里用公开的 packet API 自行组帧。
// Adafruit_Fingerprint_Packet 无默认构造函数，按库内 GET_CMD_PACKET 宏的做法：
// 先构造命令包，发完后用同一对象作为接收缓冲（getStructuredPacket 按指针覆写）。
// 返回值：0x00=成功 / 0x01=通信错误(超时/帧错) / 0x13=密码错误
uint8_t Fingerprint::probeModule() {
    uint8_t cmd[] = { FINGERPRINT_VERIFYPASSWORD,
                      0x00, 0x00, 0x00, 0x00 };  // 默认口令 0x00000000
    Adafruit_Fingerprint_Packet pkt(FINGERPRINT_COMMANDPACKET, sizeof(cmd), cmd);
    finger.writeStructuredPacket(pkt);
    uint8_t r = finger.getStructuredPacket(&pkt);  // 复用 pkt 作接收缓冲
    if (r != FINGERPRINT_OK) return r;             // 超时/帧错 -> 0x01/0xFE/0xFF
    if (pkt.type != FINGERPRINT_ACKPACKET) return FINGERPRINT_PACKETRECIEVEERR;
    return pkt.data[0];                            // 模块确认码：0x00 / 0x13 等
}

bool Fingerprint::isReady() { return ready; }
String Fingerprint::lastError() { return errorMsg; }
EnrollPhase Fingerprint::enrollPhase() { return phase; }

void Fingerprint::setPhase(EnrollPhase p) {
    phase = p;
    phaseEnterMs = millis();
}

int Fingerprint::enrollStepIndex() {
    switch (phase) {
        case ENROLL_PLACE_1: case ENROLL_LIFT_1: return 1;
        case ENROLL_PLACE_2: case ENROLL_LIFT_2: return 2;
        case ENROLL_PLACE_3: case ENROLL_LIFT_3: return 3;
        case ENROLL_PLACE_4: case ENROLL_CREATE_STORE: return 4;
        case ENROLL_VERIFY_1: return 5;
        case ENROLL_VERIFY_2: return 6;
        case ENROLL_DONE_OK: return 6;
        default: return 0;
    }
}

int Fingerprint::enrollStepTotal() { return 6; }

const char *Fingerprint::enrollPhaseCode() {
    switch (phase) {
        case ENROLL_PLACE_1: return "place_1";
        case ENROLL_LIFT_1: return "lift_1";
        case ENROLL_PLACE_2: return "place_2";
        case ENROLL_LIFT_2: return "lift_2";
        case ENROLL_PLACE_3: return "place_3";
        case ENROLL_LIFT_3: return "lift_3";
        case ENROLL_PLACE_4: return "place_4";
        case ENROLL_CREATE_STORE: return "storing";
        case ENROLL_VERIFY_1: return "verify_1";
        case ENROLL_VERIFY_2: return "verify_2";
        case ENROLL_DONE_OK: return "success";
        case ENROLL_DONE_FAIL: return "fail";
        default: return "idle";
    }
}

const char *Fingerprint::enrollPhaseHint() {
    switch (phase) {
        case ENROLL_PLACE_1: return "请将手指按在指纹头上（第1/4次）";
        case ENROLL_LIFT_1: return "请抬起手指";
        case ENROLL_PLACE_2: return "请再次按下同一手指（第2/4次）";
        case ENROLL_LIFT_2: return "请抬起手指";
        case ENROLL_PLACE_3: return "请再次按下同一手指（第3/4次）";
        case ENROLL_LIFT_3: return "请抬起手指";
        case ENROLL_PLACE_4: return "请最后一次按下同一手指（第4/4次）";
        case ENROLL_CREATE_STORE: return "正在生成并保存指纹模板…";
        case ENROLL_VERIFY_1: return "请再按一次手指进行验证（第1/2次）";
        case ENROLL_VERIFY_2: return "请再按一次手指进行验证（第2/2次）";
        case ENROLL_DONE_OK: return "录入成功";
        case ENROLL_DONE_FAIL: return errorMsg.length() ? errorMsg.c_str() : "录入失败";
        default: return "";
    }
}

void Fingerprint::enrollBegin(int id) {
    enrollId = id;
    verifyOkCount = 0;
    errorMsg = "";
    if (!ready) {
        errorMsg = "指纹模块未就绪";
        setPhase(ENROLL_DONE_FAIL);
        return;
    }
    if (id < 0 || id >= FINGER_MAX_USERS) {
        errorMsg = "指纹 ID 超出范围";
        setPhase(ENROLL_DONE_FAIL);
        return;
    }
    Debug::printf("[FINGER] enrollBegin id=%d (4 capture + 2 verify)\n", id);
    setPhase(ENROLL_PLACE_1);
}

void Fingerprint::enrollAbort(const char *reason) {
    errorMsg = reason ? reason : "cancelled";
    setPhase(ENROLL_DONE_FAIL);
    enrollId = -1;
    verifyOkCount = 0;
}

bool Fingerprint::fingerPresent() {
    uint8_t r = finger.getImage();
    return r == FINGERPRINT_OK;
}

bool Fingerprint::captureToSlot(uint8_t slot) {
    uint8_t r = finger.getImage();
    if (r == FINGERPRINT_NOFINGER) return false;
    if (r != FINGERPRINT_OK) {
        // 图像质量差等：本轮不算成功，继续等
        return false;
    }
    r = finger.image2Tz(slot);
    if (r != FINGERPRINT_OK) {
        errorMsg = "图像转特征失败，请重按";
        return false;
    }
    return true;
}

bool Fingerprint::enrollTick() {
    SensorGuard guard;
    if (!guard.acquired()) return false;
    if (phase == ENROLL_IDLE || phase == ENROLL_DONE_OK || phase == ENROLL_DONE_FAIL) {
        return false;
    }

    // 单阶段最长 45s 超时
    if (millis() - phaseEnterMs > 45000UL) {
        errorMsg = "等待超时";
        setPhase(ENROLL_DONE_FAIL);
        return true;
    }

    EnrollPhase before = phase;

    switch (phase) {
        case ENROLL_PLACE_1:
            if (captureToSlot(1)) setPhase(ENROLL_LIFT_1);
            break;
        case ENROLL_LIFT_1:
            if (!fingerPresent()) setPhase(ENROLL_PLACE_2);
            break;
        case ENROLL_PLACE_2:
            if (captureToSlot(2)) {
                // 第一对特征先 createModel 检查是否匹配，不存盘
                uint8_t r = finger.createModel();
                if (r != FINGERPRINT_OK) {
                    errorMsg = (r == FINGERPRINT_ENROLLMISMATCH)
                        ? "两次指纹不匹配，请从头再来"
                        : "特征合并失败，请重试";
                    setPhase(ENROLL_DONE_FAIL);
                } else {
                    setPhase(ENROLL_LIFT_2);
                }
            }
            break;
        case ENROLL_LIFT_2:
            if (!fingerPresent()) setPhase(ENROLL_PLACE_3);
            break;
        case ENROLL_PLACE_3:
            // 第二对特征覆盖缓冲，提高模板质量
            if (captureToSlot(1)) setPhase(ENROLL_LIFT_3);
            break;
        case ENROLL_LIFT_3:
            if (!fingerPresent()) setPhase(ENROLL_PLACE_4);
            break;
        case ENROLL_PLACE_4:
            if (captureToSlot(2)) setPhase(ENROLL_CREATE_STORE);
            break;
        case ENROLL_CREATE_STORE: {
            uint8_t r = finger.createModel();
            if (r != FINGERPRINT_OK) {
                errorMsg = (r == FINGERPRINT_ENROLLMISMATCH)
                    ? "后两次指纹不匹配，请从头再来"
                    : "特征合并失败";
                setPhase(ENROLL_DONE_FAIL);
                break;
            }
            r = finger.storeModel(enrollId);
            if (r != FINGERPRINT_OK) {
                errorMsg = "存储指纹失败";
                setPhase(ENROLL_DONE_FAIL);
                break;
            }
            Debug::printf("[FINGER] stored model id=%d, start verify\n", enrollId);
            setPhase(ENROLL_VERIFY_1);
            break;
        }
        case ENROLL_VERIFY_1:
        case ENROLL_VERIFY_2: {
            // 非阻塞验证：有手指再匹配
            uint8_t r = finger.getImage();
            if (r == FINGERPRINT_NOFINGER) break;
            if (r != FINGERPRINT_OK) break;
            r = finger.image2Tz();
            if (r != FINGERPRINT_OK) break;
            r = finger.fingerSearch();
            if (r == FINGERPRINT_OK && finger.fingerID == enrollId) {
                verifyOkCount++;
                Debug::printf("[FINGER] verify ok count=%d id=%d conf=%d\n",
                              verifyOkCount, finger.fingerID, finger.confidence);
                // 等抬起再进下一阶段，避免连读
                unsigned long t0 = millis();
                while (fingerPresent() && millis() - t0 < 3000) delay(50);
                if (phase == ENROLL_VERIFY_1) {
                    setPhase(ENROLL_VERIFY_2);
                } else {
                    setPhase(ENROLL_DONE_OK);
                }
            } else {
                // 验证失败：删除刚存的模板，整流程失败
                finger.deleteModel(enrollId);
                errorMsg = "验证未通过，请重新录入";
                setPhase(ENROLL_DONE_FAIL);
            }
            break;
        }
        default:
            break;
    }

    return phase != before;
}

uint8_t Fingerprint::waitForFinger(int stage) {
    uint8_t result = 0;
    int retry = 0;
    while (retry < 600) {
        result = finger.getImage();
        if (result == FINGERPRINT_OK) {
            uint8_t conv = (stage == 0) ? finger.image2Tz(1) : finger.image2Tz(2);
            if (conv == FINGERPRINT_OK) {
                Debug::printf("[FINGER] Feature capture %d success\n", stage + 1);
                return FINGERPRINT_OK;
            } else {
                errorMsg = "图像转特征失败";
                return conv;
            }
        } else if (result == FINGERPRINT_NOFINGER) {
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
    // 阻塞兼容路径：内部跑完分步状态机
    enrollBegin(id);
    while (phase != ENROLL_DONE_OK && phase != ENROLL_DONE_FAIL) {
        enrollTick();
        delay(30);
        yield();
    }
    return phase == ENROLL_DONE_OK;
}

int Fingerprint::verifyFingerprint() {
    SensorGuard guard;
    if (!guard.acquired()) {
        errorMsg = "指纹互斥锁不可用";
        return -2;
    }
    if (!ready) {
        errorMsg = "指纹模块未就绪";
        return -2;
    }

    uint8_t r = finger.getImage();
    if (r == FINGERPRINT_NOFINGER) return -1;
    if (r != FINGERPRINT_OK) {
        errorMsg = "读取图像失败";
        return -2;
    }

    r = finger.image2Tz();
    if (r != FINGERPRINT_OK) {
        errorMsg = "图像转特征失败";
        return -2;
    }

    r = finger.fingerSearch();
    if (r == FINGERPRINT_OK) {
        Debug::printf("[FINGER] Match success: ID=%d, confidence=%d\n",
                      finger.fingerID, finger.confidence);
        return finger.fingerID;
    } else if (r == FINGERPRINT_NOTFOUND) {
        errorMsg = "未找到匹配指纹";
        return -1;
    } else {
        errorMsg = "搜索失败";
        return -2;
    }
}

void Fingerprint::setBackgroundVerifyEnabled(bool enabled) {
    if (enabled && !ensureBackgroundVerifyTask()) {
        Debug::println(F("[FINGER] background verify task unavailable"));
        backgroundVerifyEnabled = false;
        return;
    }
    backgroundVerifyEnabled = enabled;
    if (backgroundResultQueue != nullptr) xQueueReset(backgroundResultQueue);
}

bool Fingerprint::takeBackgroundVerifyResult(int &fingerprintId) {
    if (backgroundResultQueue == nullptr) return false;
    return xQueueReceive(backgroundResultQueue, &fingerprintId, 0) == pdTRUE;
}

uint32_t Fingerprint::getBackgroundVerifyMaxMs() {
    return backgroundVerifyMaxMs;
}

uint32_t Fingerprint::getBackgroundVerifyErrorCount() {
    return backgroundVerifyErrorCount;
}

bool Fingerprint::deleteFingerprint(int id) {
    SensorGuard guard;
    if (!guard.acquired()) return false;
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
    return false;
}

bool Fingerprint::deleteAllFingerprints() {
    SensorGuard guard;
    if (!guard.acquired()) return false;
    if (!ready) {
        errorMsg = "指纹模块未就绪";
        return false;
    }
    uint8_t r = finger.emptyDatabase();
    if (r == FINGERPRINT_OK) {
        Debug::println(F("[FINGER] All fingerprints cleared"));
        return true;
    }
    errorMsg = "清空指纹库失败";
    return false;
}

int Fingerprint::getFingerprintCount() {
    SensorGuard guard;
    if (!guard.acquired()) return 0;
    if (!ready) return 0;
    finger.getTemplateCount();
    return finger.templateCount;
}

bool Fingerprint::templateExists(int id) {
    SensorGuard guard;
    if (!guard.acquired()) return false;
    if (!ready || id < 0) return false;
    uint8_t r = finger.loadModel(id);
    return r == FINGERPRINT_OK;
}

// AS608：主机 -> 模块 下载特征文件到 CharBuffer（指令 0x09 DownChar）
#ifndef FINGERPRINT_DOWNCHAR
#define FINGERPRINT_DOWNCHAR 0x09
#endif

static void as608FlushRx(HardwareSerial &uart) {
    while (uart.available()) {
        (void)uart.read();
    }
}

static bool as608ReadExact(HardwareSerial &uart, uint8_t *buf, size_t n,
                           unsigned long deadlineMs) {
    size_t got = 0;
    while (got < n && millis() < deadlineMs) {
        if (uart.available()) {
            buf[got++] = (uint8_t)uart.read();
        } else {
            delay(1);
        }
    }
    return got == n;
}

// 原始数据包（payload 可超过 Adafruit_Packet 的 64B 限制）
static void as608WriteRawPacket(HardwareSerial &uart, uint8_t type,
                                const uint8_t *payload, uint16_t len) {
    uint16_t wireLen = (uint16_t)(len + 2);
    uint16_t sum = (uint16_t)type + (uint16_t)(wireLen >> 8) + (uint16_t)(wireLen & 0xFF);
    for (uint16_t i = 0; i < len; i++) {
        sum = (uint16_t)(sum + payload[i]);
    }

    uart.write((uint8_t)0xEF);
    uart.write((uint8_t)0x01);
    uart.write((uint8_t)0xFF);
    uart.write((uint8_t)0xFF);
    uart.write((uint8_t)0xFF);
    uart.write((uint8_t)0xFF);
    uart.write(type);
    uart.write((uint8_t)(wireLen >> 8));
    uart.write((uint8_t)(wireLen & 0xFF));
    for (uint16_t i = 0; i < len; i++) {
        uart.write(payload[i]);
    }
    uart.write((uint8_t)(sum >> 8));
    uart.write((uint8_t)(sum & 0xFF));
    uart.flush();
}

static uint8_t as608WaitAck(Adafruit_Fingerprint &finger, uint16_t timeoutMs) {
    uint8_t dummy[1] = {0};
    Adafruit_Fingerprint_Packet packet(FINGERPRINT_ACKPACKET, 1, dummy);
    if (finger.getStructuredPacket(&packet, timeoutMs) != FINGERPRINT_OK) {
        return FINGERPRINT_PACKETRECIEVEERR;
    }
    if (packet.type != FINGERPRINT_ACKPACKET) {
        return FINGERPRINT_PACKETRECIEVEERR;
    }
    return packet.data[0];
}

// 模块 -> 主机：getModel 后解析数据包，提取纯 512 字节模板（剥离 EF01 包头）
static bool as608ReceiveTemplateBytes(Adafruit_Fingerprint &finger,
                                      HardwareSerial &uart,
                                      uint8_t *outBuf, size_t bufSize, size_t &outLen) {
    outLen = 0;
    if (outBuf == nullptr || bufSize < FP_TEMPLATE_SIZE) return false;
    memset(outBuf, 0, FP_TEMPLATE_SIZE);

    as608FlushRx(uart);
    // getModel = UpChar(0x08)，模块随后连续发 data/end 包
    if (finger.getModel() != FINGERPRINT_OK) return false;

    size_t filled = 0;
    unsigned long deadline = millis() + 5000;
    while (filled < FP_TEMPLATE_SIZE && millis() < deadline) {
        // 同步帧头 0xEF 0x01
        bool found = false;
        while (millis() < deadline) {
            if (!uart.available()) {
                delay(1);
                continue;
            }
            uint8_t b = (uint8_t)uart.read();
            if (b != 0xEF) continue;
            if (!as608ReadExact(uart, &b, 1, deadline)) break;
            if (b == 0x01) {
                found = true;
                break;
            }
        }
        if (!found) break;

        uint8_t hdr[7]; // addr[4] + type + len_hi + len_lo
        if (!as608ReadExact(uart, hdr, 7, deadline)) break;
        uint8_t type = hdr[4];
        uint16_t wireLen = ((uint16_t)hdr[5] << 8) | hdr[6];
        if (wireLen < 2 || wireLen > 300) break;
        uint16_t payloadLen = (uint16_t)(wireLen - 2);

        uint8_t payload[300];
        if (payloadLen > sizeof(payload)) break;
        if (!as608ReadExact(uart, payload, payloadLen, deadline)) break;
        uint8_t csum[2];
        if (!as608ReadExact(uart, csum, 2, deadline)) break;
        (void)csum;

        if (type == FINGERPRINT_DATAPACKET || type == FINGERPRINT_ENDDATAPACKET) {
            size_t copy = payloadLen;
            if (filled + copy > FP_TEMPLATE_SIZE) {
                copy = FP_TEMPLATE_SIZE - filled;
            }
            memcpy(outBuf + filled, payload, copy);
            filled += copy;
            if (type == FINGERPRINT_ENDDATAPACKET) break;
        }
    }

    outLen = filled;
    return filled >= FP_TEMPLATE_SIZE;
}

// 主机 -> 模块：DownChar(0x09) 把纯模板写入 CharBuffer1
static bool as608SendTemplateBytes(Adafruit_Fingerprint &finger,
                                   HardwareSerial &uart,
                                   const uint8_t *data, size_t len,
                                   uint16_t packetLen) {
    if (data == nullptr || len == 0) return false;
    if (packetLen != 32 && packetLen != 64 && packetLen != 128 && packetLen != 256) {
        packetLen = 128;
    }

    as608FlushRx(uart);

    // DownChar -> CharBuffer1
    uint8_t cmdPayload[2] = {FINGERPRINT_DOWNCHAR, 0x01};
    Adafruit_Fingerprint_Packet cmdPkt(FINGERPRINT_COMMANDPACKET, 2, cmdPayload);
    finger.writeStructuredPacket(cmdPkt);
    uint8_t conf = as608WaitAck(finger, 1000);
    if (conf != FINGERPRINT_OK) {
        Debug::printf("[FINGER] DownChar ack=0x%02X\n", conf);
        return false;
    }

    size_t useLen = len > FP_TEMPLATE_SIZE ? FP_TEMPLATE_SIZE : len;
    size_t totalSend = ((useLen + packetLen - 1) / packetLen) * packetLen;
    uint8_t chunkBuf[256];

    for (size_t offset = 0; offset < totalSend; offset += packetLen) {
        memset(chunkBuf, 0, packetLen);
        if (offset < useLen) {
            size_t copyN = useLen - offset;
            if (copyN > packetLen) copyN = packetLen;
            memcpy(chunkBuf, data + offset, copyN);
        }
        bool last = (offset + packetLen >= totalSend);
        uint8_t type = last ? FINGERPRINT_ENDDATAPACKET : FINGERPRINT_DATAPACKET;
        as608WriteRawPacket(uart, type, chunkBuf, packetLen);
        delay(5);
    }

    // 部分模块在收完数据后回 ACK；收不到也不立刻失败，交由 storeModel 校验
    conf = as608WaitAck(finger, 300);
    if (conf != FINGERPRINT_OK && conf != FINGERPRINT_PACKETRECIEVEERR) {
        Debug::printf("[FINGER] template data ack=0x%02X\n", conf);
        return false;
    }
    return true;
}

bool Fingerprint::readTemplate(int id, uint8_t *outBuf, size_t bufSize, size_t &outLen) {
    SensorGuard guard;
    if (!guard.acquired()) return false;
    outLen = 0;
    if (!ready || outBuf == nullptr || bufSize < FP_TEMPLATE_SIZE) return false;
    if (finger.loadModel(id) != FINGERPRINT_OK) {
        errorMsg = "加载模板失败";
        return false;
    }
    if (!as608ReceiveTemplateBytes(finger, serial2, outBuf, bufSize, outLen)) {
        errorMsg = "下载模板失败";
        outLen = 0;
        return false;
    }
    if (outLen > bufSize) outLen = bufSize;
    Debug::printf("[FINGER] readTemplate id=%d len=%u\n", id, (unsigned)outLen);
    return true;
}

bool Fingerprint::writeTemplate(int id, const uint8_t *data, size_t len) {
    SensorGuard guard;
    if (!guard.acquired()) return false;
    if (!ready || data == nullptr || len < 128) {
        errorMsg = "写模板参数无效";
        return false;
    }
    if (id < 0 || id >= FINGER_MAX_USERS) {
        errorMsg = "指纹 ID 超出范围";
        return false;
    }

    // 读取模块包长，按包分片下发
    finger.getParameters();
    uint16_t packetLen = finger.packet_len;
    if (packetLen != 32 && packetLen != 64 && packetLen != 128 && packetLen != 256) {
        packetLen = 128;
    }

    // DownChar 写入 CharBuffer1，再 storeModel 落到指定槽位
    if (!as608SendTemplateBytes(finger, serial2, data, len, packetLen)) {
        errorMsg = "模块拒绝接收模板数据";
        return false;
    }

    uint8_t r = finger.storeModel((uint16_t)id);
    if (r != FINGERPRINT_OK) {
        errorMsg = "存储模板到槽位失败";
        Debug::printf("[FINGER] writeTemplate storeModel(%d) r=0x%02X\n", id, r);
        return false;
    }

    Debug::printf("[FINGER] writeTemplate id=%d len=%u ok\n", id, (unsigned)len);
    return true;
}

// 模板迁移：loadModel(fromId) 加载到 char buffer -> storeModel(toId) 存到目标槽位
bool Fingerprint::copyTemplate(int fromId, int toId) {
    SensorGuard guard;
    if (!guard.acquired()) return false;
    if (!ready) {
        errorMsg = "指纹模块未就绪";
        return false;
    }
    uint8_t r = finger.loadModel(fromId);
    if (r != FINGERPRINT_OK) {
        errorMsg = "加载源模板失败";
        Debug::printf("[FINGER] copyTemplate loadModel(%d) failed r=0x%02X\n", fromId, r);
        return false;
    }
    r = finger.storeModel(toId);
    if (r != FINGERPRINT_OK) {
        errorMsg = "存储目标模板失败";
        Debug::printf("[FINGER] copyTemplate storeModel(%d) failed r=0x%02X\n", toId, r);
        return false;
    }
    Debug::printf("[FINGER] copyTemplate %d -> %d ok\n", fromId, toId);
    return true;
}

// 录入后检测：用户再按一次手指，与 id 槽位做 1:1 比对
// 返回: 1=匹配成功, 0=无手指/不匹配, -1=通信错误
int Fingerprint::verifyOnSlot(int id, bool *fingerDetected, int *confidence) {
    SensorGuard guard;
    if (!guard.acquired()) return -1;
    if (fingerDetected != nullptr) *fingerDetected = false;
    if (confidence != nullptr) *confidence = 0;
    if (!ready) return -1;
    uint8_t r = finger.getImage();
    if (r == FINGERPRINT_NOFINGER) return 0;
    if (r != FINGERPRINT_OK) {
        errorMsg = "读取图像失败";
        return -1;
    }
    if (fingerDetected != nullptr) *fingerDetected = true;
    r = finger.image2Tz(1);
    if (r != FINGERPRINT_OK) {
        errorMsg = "图像转特征失败";
        return -1;
    }
    // 加载目标模板到 char buffer 2，然后做 1:1 比对
    r = finger.loadModel(id);
    if (r != FINGERPRINT_OK) {
        errorMsg = "加载模板失败";
        return -1;
    }
    // Adafruit 库无公开 match()，用 fingerSearch 全库搜索后检查 fingerID 是否等于 id
    r = finger.fingerSearch(1);
    if (r == FINGERPRINT_OK && finger.fingerID == (uint16_t)id) {
        if (confidence != nullptr) *confidence = finger.confidence;
        Debug::printf("[FINGER] verifyOnSlot id=%d match conf=%d\n", id, finger.confidence);
        return 1;
    }
    if (r == FINGERPRINT_NOTFOUND) {
        Debug::printf("[FINGER] verifyOnSlot id=%d not matched\n", id);
        return 0;
    }
    errorMsg = "比对失败";
    return -1;
}
