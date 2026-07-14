/**
 * sd_storage.cpp - SD 卡集中存储实现（仅根节点）
 * 基于 Arduino-ESP32 内置 SD + FS 库，SPI 模式挂载。
 */
#include "sd_storage.h"
#include <SPI.h>
#include <SD.h>
#include <FS.h>
#include <ArduinoJson.h>

bool SdStorage::mounted = false;
static SPIClass sdSPI(VSPI);

bool SdStorage::init() {
    if (mounted) return true;

    Serial.println(F("[SD] 初始化 SD 卡（SPI 模式）..."));

    // 初始化 SPI 总线
    sdSPI.begin(SD_SPI_SCK_PIN, SD_SPI_MISO_PIN, SD_SPI_MOSI_PIN, SD_SPI_CS_PIN);

    // 挂载 SD 卡，CS 引脚 = SD_SPI_CS_PIN
    if (!SD.begin(SD_SPI_CS_PIN, sdSPI, SD_SPI_FREQ)) {
        Serial.println(F("[SD] SD 卡挂载失败！请检查：1.卡片是否插入 2.接线 3.CS引脚"));
        mounted = false;
        return false;
    }

    // 检测卡类型
    uint8_t cardType = SD.cardType();
    if (cardType == CARD_NONE) {
        Serial.println(F("[SD] 未检测到 SD 卡"));
        mounted = false;
        return false;
    }

    Serial.printf("[SD] 挂载成功 类型=%s 容量=%lluMB 已用=%lluMB\n",
                  cardType == CARD_MMC ? "MMC" : (cardType == CARD_SD ? "SD" : "SDHC"),
                  SD.cardSize() / (1024 * 1024),
                  SD.usedBytes() / (1024 * 1024));

    // 创建目录结构
    ensureDir(SD_DATA_DIR);
    ensureDir(SD_FP_DIR);

    // 首次启动初始化 version.json
    String vJson;
    if (!readTable("version", vJson)) {
        // 写入初始版本
        String init = "{\"global_version\":0,\"users_version\":0,\"classes_version\":0,"
                      "\"permissions_version\":0,\"devices_version\":0,\"fp_version\":0,"
                      "\"last_update_time\":\"\",\"last_update_source\":\"init\"}";
        writeTable("version", init);
        Serial.println(F("[SD] 已初始化 version.json"));
    }

    mounted = true;
    return true;
}

bool SdStorage::isReady() {
    return mounted;
}

// ====== 目录与路径 ======

String SdStorage::tablePath(const String &tableName) {
    return String(SD_DATA_DIR) + "/" + tableName + ".json";
}

bool SdStorage::ensureDir(const String &path) {
    if (!SD.exists(path)) {
        if (SD.mkdir(path)) {
            Serial.printf("[SD] 创建目录: %s\n", path.c_str());
            return true;
        } else {
            Serial.printf("[SD] 创建目录失败: %s\n", path.c_str());
            return false;
        }
    }
    return true;
}

// ====== 原子写入 ======

bool SdStorage::atomicWrite(const String &path, const uint8_t *data, size_t len) {
    String tmpPath = path + ".tmp";

    // 1. 写临时文件
    File f = SD.open(tmpPath, FILE_WRITE);
    if (!f) {
        Serial.printf("[SD] 打开临时文件失败: %s\n", tmpPath.c_str());
        return false;
    }
    size_t written = f.write(data, len);
    f.flush();   // 刷盘，防断电
    f.close();

    if (written != len) {
        Serial.printf("[SD] 写入不完整: %u/%u\n", (unsigned)written, (unsigned)len);
        SD.remove(tmpPath);
        return false;
    }

    // 2. 删除原文件（若存在）
    if (SD.exists(path)) {
        SD.remove(path);
    }

    // 3. 重命名临时文件为目标文件
    if (!SD.rename(tmpPath, path)) {
        Serial.printf("[SD] 重命名失败: %s -> %s\n", tmpPath.c_str(), path.c_str());
        return false;
    }

    return true;
}

// ====== JSON 表读写 ======

bool SdStorage::readTable(const String &tableName, String &outJson) {
    if (!mounted) return false;

    String path = tablePath(tableName);
    File f = SD.open(path, FILE_READ);
    if (!f) {
        return false;  // 文件不存在
    }

    // 流式读取（避免大文件一次性分配）
    outJson = "";
    outJson.reserve(f.size() + 16);
    while (f.available()) {
        // 分块读取，避免单次分配过大
        String chunk = f.readString();
        outJson += chunk;
    }
    f.close();
    return true;
}

bool SdStorage::writeTable(const String &tableName, const String &json) {
    if (!mounted) return false;

    ensureDir(SD_DATA_DIR);
    String path = tablePath(tableName);

    bool ok = atomicWrite(path, (const uint8_t *)json.c_str(), json.length());
    if (ok) {
        // 刷新版本号（version 表本身不递归）
        if (tableName != "version") {
            incrementVersion(tableName);
        }
        Serial.printf("[SD] 写入 %s 成功 (%u 字节)\n", tableName.c_str(), (unsigned)json.length());
    }
    return ok;
}

// ====== 指纹模板读写 ======

String SdStorage::getTemplateFileName(const String &userId, int index) {
    // 文件名：FP_<userId>[_index].bin，userId 中的非字母数字字符替换为 _
    String safeId = "";
    safeId.reserve(userId.length());
    for (size_t i = 0; i < userId.length(); i++) {
        char c = userId[i];
        if (isalnum((unsigned char)c)) {
            safeId += c;
        } else {
            safeId += '_';
        }
    }
    String name = "FP_";
    name += safeId;
    if (index > 1) {
        name += "_";
        name += String(index);
    }
    name += ".bin";
    return name;
}

bool SdStorage::writeTemplate(const String &userId, int index,
                              const uint8_t *data, size_t len) {
    if (!mounted) return false;

    ensureDir(SD_FP_DIR);
    String fileName = getTemplateFileName(userId, index);
    String path = String(SD_FP_DIR) + "/" + fileName;

    bool ok = atomicWrite(path, data, len);
    if (ok) {
        Serial.printf("[SD] 指纹模板写入: %s (%u 字节)\n", fileName.c_str(), (unsigned)len);
    }
    return ok;
}

bool SdStorage::readTemplate(const String &userId, int index,
                             uint8_t *outBuf, size_t bufSize, size_t &outLen) {
    if (!mounted) return false;

    String fileName = getTemplateFileName(userId, index);
    String path = String(SD_FP_DIR) + "/" + fileName;

    File f = SD.open(path, FILE_READ);
    if (!f) {
        Serial.printf("[SD] 指纹模板不存在: %s\n", fileName.c_str());
        return false;
    }

    size_t fileLen = f.size();
    if (fileLen > bufSize) {
        Serial.printf("[SD] 模板过大: %u > 缓冲 %u\n", (unsigned)fileLen, (unsigned)bufSize);
        f.close();
        return false;
    }

    outLen = f.read(outBuf, fileLen);
    f.close();
    return outLen == fileLen;
}

bool SdStorage::deleteTemplate(const String &userId) {
    if (!mounted) return false;

    String safeId = "";
    for (size_t i = 0; i < userId.length(); i++) {
        char c = userId[i];
        safeId += isalnum((unsigned char)c) ? c : '_';
    }
    String prefix = "FP_" + safeId;

    File dir = SD.open(SD_FP_DIR);
    if (!dir || !dir.isDirectory()) return false;

    int deleted = 0;
    File entry = dir.openNextFile();
    while (entry) {
        String name = entry.name();
        // 名称可能含路径前缀，取基名
        int slash = name.lastIndexOf('/');
        if (slash >= 0) name = name.substring(slash + 1);
        if (name.startsWith(prefix)) {
            entry.close();
            String fullPath = String(SD_FP_DIR) + "/" + name;
            SD.remove(fullPath);
            deleted++;
            Serial.printf("[SD] 删除模板: %s\n", name.c_str());
        }
        entry = dir.openNextFile();
    }
    dir.close();

    Serial.printf("[SD] 用户 %s 共删除 %d 个模板\n", userId.c_str(), deleted);
    return deleted > 0;
}

// ====== 版本元数据 ======

bool SdStorage::readVersion(uint32_t &globalVer, uint32_t &usersVer,
                            uint32_t &classesVer, uint32_t &permsVer,
                            uint32_t &devicesVer, uint32_t &fpVer) {
    String json;
    if (!readTable("version", json)) {
        globalVer = usersVer = classesVer = permsVer = devicesVer = fpVer = 0;
        return false;
    }

    DynamicJsonDocument doc(512);
    DeserializationError err = deserializeJson(doc, json);
    if (err) {
        globalVer = usersVer = classesVer = permsVer = devicesVer = fpVer = 0;
        return false;
    }

    globalVer  = doc["global_version"] | 0;
    usersVer   = doc["users_version"] | 0;
    classesVer = doc["classes_version"] | 0;
    permsVer   = doc["permissions_version"] | 0;
    devicesVer = doc["devices_version"] | 0;
    fpVer      = doc["fp_version"] | 0;
    return true;
}

bool SdStorage::incrementVersion(const String &tableName) {
    uint32_t g, u, c, p, d, fp;
    readVersion(g, u, c, p, d, fp);

    if (tableName == "users") u++;
    else if (tableName == "classes") c++;
    else if (tableName == "permissions") p++;
    else if (tableName == "devices") d++;
    else if (tableName == "fingerprints" || tableName == "fp") fp++;
    else return false;

    g++;  // 全局版本号始终自增

    // 构造新 version.json
    DynamicJsonDocument doc(512);
    doc["global_version"] = g;
    doc["users_version"] = u;
    doc["classes_version"] = c;
    doc["permissions_version"] = p;
    doc["devices_version"] = d;
    doc["fp_version"] = fp;

    // 时间戳
    time_t now = time(nullptr);
    String ts = (now > 1700000000) ? String((long)now) : String("");
    doc["last_update_time"] = ts;
    doc["last_update_source"] = "root_sd";

    String json;
    serializeJson(doc, json);

    // 直接原子写 version.json（避免递归 incrementVersion）
    String path = tablePath("version");
    return atomicWrite(path, (const uint8_t *)json.c_str(), json.length());
}

uint32_t SdStorage::getGlobalVersion() {
    uint32_t g, u, c, p, d, fp;
    readVersion(g, u, c, p, d, fp);
    return g;
}

// ====== SD 卡容量信息 ======

uint64_t SdStorage::getTotalBytes() {
    return mounted ? SD.cardSize() : 0;
}

uint64_t SdStorage::getUsedBytes() {
    return mounted ? SD.usedBytes() : 0;
}
