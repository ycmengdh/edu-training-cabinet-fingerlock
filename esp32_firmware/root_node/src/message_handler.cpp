/**
 * message_handler.cpp - Root Node message handler implementation
 * Handles commands targeted at the root node itself (routed by MeshBridge).
 * Cabinet node commands (CONTROL_LOCK, SYNC_PERMISSION, etc.) are forwarded
 * directly by MeshBridge based on device_id routing table. Legacy AUTH_* is
 * ignored by the cabinet because local permission data is authoritative.
 */
#include "message_handler.h"
#include "debug.h"
#include "storage.h"
#include "mesh_comm.h"
#include "mesh_bridge.h"
#include "protocol_frame.h"
#include "message_hmac.h"
#ifdef ENABLE_SD_CARD
#include "sd_storage.h"
static void syncPermissionsToCabinet(const uint8_t *mac, const char *deviceId);
#endif

void MessageHandler::init() {
    Debug::println(F("[MSG] root message handler init complete"));
}

void MessageHandler::handleMeshMessage(const uint8_t *fromMac, const String &message) {
    DynamicJsonDocument doc(16384);
    DeserializationError err = deserializeJson(doc, message);
    if (err) {
        Debug::printf("[MSG] child JSON parse failed: %s\n", err.c_str());
        return;
    }

    const char *cmd = doc["cmd"] | "";
    const char *deviceId = doc["device_id"] | "";
    if (strlen(deviceId) == 0) return;

#ifdef ENABLE_SD_CARD
    if (strcmp(cmd, "LOG_REPORT") == 0) {
        JsonArray sourceLogs = doc["data"]["logs"].as<JsonArray>();
        DynamicJsonDocument logsDoc(16384);
        JsonArray logs = logsDoc.to<JsonArray>();
        for (JsonObject source : sourceLogs) {
            JsonObject target = logs.createNestedObject();
            for (JsonPair pair : source) target[pair.key()] = pair.value();
            target["device_id"] = deviceId;
        }

        String logsJson;
        serializeJson(logs, logsJson);
        bool stored = SdStorage::appendLogs(logsJson);

        // A cabinet may discard its local batch only after root persistence.
        String ack = ProtocolFrame::buildMessage(
            "LOG_REPORT_ACK", deviceId,
            stored ? "{\"result\":\"success\"}" : "{\"result\":\"fail\"}",
            String(doc["msg_id"] | ""));
        MeshComm::sendToNode(fromMac, ack);
    } else if (strcmp(cmd, "REGISTER") == 0 ||
               strcmp(cmd, "STATUS_REPORT") == 0 ||
               strcmp(cmd, "HEARTBEAT") == 0) {
        String devicesJson;
        DynamicJsonDocument devicesDoc(32768);
        JsonArray devices = devicesDoc.to<JsonArray>();
        if (SdStorage::readTable("devices", devicesJson) && devicesJson.length() > 0) {
            devicesDoc.clear();
            if (deserializeJson(devicesDoc, devicesJson)) {
                devicesDoc.clear();
                devices = devicesDoc.to<JsonArray>();
            } else {
                devices = devicesDoc.as<JsonArray>();
            }
        }

        JsonObject record;
        for (JsonObject candidate : devices) {
            if (String((const char *)(candidate["device_id"] | "")) == deviceId) {
                record = candidate;
                break;
            }
        }
        if (record.isNull()) record = devices.createNestedObject();
        record["device_id"] = deviceId;
        record["device_name"] = doc["data"]["device_name"] | deviceId;
        record["is_root"] = false;
        record["online"] = true;
        record["last_seen"] = (uint32_t)time(nullptr);
        record["mesh_mac"] = MeshComm::macToString(fromMac);
        if (doc["data"].containsKey("firmware_version")) {
            record["firmware_version"] = doc["data"]["firmware_version"];
        }
        if (strcmp(cmd, "STATUS_REPORT") == 0) {
            JsonObject status = record.createNestedObject("status");
            for (JsonPair pair : doc["data"].as<JsonObject>()) status[pair.key()] = pair.value();
        }

        String output;
        serializeJson(devices, output);
        SdStorage::writeTable("devices", output);

        if (strcmp(cmd, "REGISTER") == 0) {
            syncPermissionsToCabinet(fromMac, deviceId);

            time_t currentTime = time(nullptr);
            if (currentTime > 1700000000) {
                String timeData = "{\"timestamp\":" + String((uint32_t)currentTime) + "}";
                MeshComm::sendToNode(fromMac, ProtocolFrame::buildMessage(
                    "TIME_SYNC", deviceId, timeData));
            }
        }
    }
#endif
}

void MessageHandler::handleDeviceOffline(const String &deviceId) {
#ifdef ENABLE_SD_CARD
    String devicesJson;
    DynamicJsonDocument devicesDoc(32768);
    if (!SdStorage::readTable("devices", devicesJson) ||
        deserializeJson(devicesDoc, devicesJson) || !devicesDoc.is<JsonArray>()) {
        Debug::printf("[MSG] cannot mark %s offline: devices table unavailable\n",
                      deviceId.c_str());
        return;
    }

    bool changed = false;
    for (JsonObject record : devicesDoc.as<JsonArray>()) {
        if (String((const char *)(record["device_id"] | "")) == deviceId) {
            if (record["online"] | false) {
                record["online"] = false;
                record["offline_time"] = (uint32_t)time(nullptr);
                changed = true;
            }
            break;
        }
    }
    if (changed) {
        String output;
        serializeJson(devicesDoc, output);
        if (!SdStorage::writeTable("devices", output)) {
            Debug::printf("[MSG] failed to persist offline state for %s\n", deviceId.c_str());
        }
    }
#else
    (void)deviceId;
#endif
}

#ifdef ENABLE_SD_CARD
static bool isAllowedTable(const String &table) {
    return table == "version" || table == "users" || table == "classes" ||
           table == "permissions" || table == "role_permissions" ||
           table == "devices" || table == "logs";
}

static uint32_t getTableVersion(const String &table) {
    uint32_t globalVersion, usersVersion, classesVersion, permissionsVersion;
    uint32_t devicesVersion, fingerprintVersion, logsVersion;
    SdStorage::readVersion(globalVersion, usersVersion, classesVersion,
                           permissionsVersion, devicesVersion,
                           fingerprintVersion, logsVersion);
    if (table == "users") return usersVersion;
    if (table == "classes") return classesVersion;
    if (table == "permissions" || table == "role_permissions") return permissionsVersion;
    if (table == "devices") return devicesVersion;
    if (table == "logs") return logsVersion;
    return globalVersion;
}

static void syncPermissionsToCabinet(const uint8_t *mac, const char *deviceId) {
    uint32_t globalVersion, usersVersion, classesVersion, permissionsVersion;
    uint32_t devicesVersion, fingerprintVersion, logsVersion;
    SdStorage::readVersion(globalVersion, usersVersion, classesVersion,
                            permissionsVersion, devicesVersion,
                            fingerprintVersion, logsVersion);

    String usersJson, roleJson, permissionsJson;
    DynamicJsonDocument usersDoc(32768);
    DynamicJsonDocument rolesDoc(8192);
    DynamicJsonDocument overridesDoc(16384);
    if (!SdStorage::readTable("users", usersJson) ||
        !SdStorage::readTable("role_permissions", roleJson) ||
        !SdStorage::readTable("permissions", permissionsJson) ||
        deserializeJson(usersDoc, usersJson) || !usersDoc.is<JsonArray>() ||
        deserializeJson(rolesDoc, roleJson) || !rolesDoc.is<JsonArray>() ||
        deserializeJson(overridesDoc, permissionsJson) || !overridesDoc.is<JsonArray>()) {
        Debug::printf("[MSG] permission sync to %s aborted: root data unavailable\n", deviceId);
        return;
    }

    JsonArray users = usersDoc.as<JsonArray>();
    JsonArray roles = rolesDoc.as<JsonArray>();
    JsonArray overrides = overridesDoc.as<JsonArray>();
    int total = 0;
    for (JsonObject user : users) {
        int fingerprintId = user["fingerprint_id"] | -1;
        const char *userId = user["user_id"] | "";
        bool enabled = user["enabled"] | true;
        if (enabled && fingerprintId >= 0 && strlen(userId) > 0) total++;
    }
    if (total > PERM_MAX_USERS) {
        Debug::printf("[MSG] permission sync to %s aborted: %d users exceed limit\n",
                      deviceId, total);
        return;
    }

    String beginData = "{\"version\":" + String(globalVersion) +
                       ",\"total\":" + String(total) + "}";
    bool allSent = MeshComm::sendToNode(mac, ProtocolFrame::buildMessage(
        "BEGIN_PERMISSION_SYNC", deviceId, beginData));

    int sequence = 0;
    for (JsonObject user : users) {
        int fingerprintId = user["fingerprint_id"] | -1;
        String userId = user["user_id"] | "";
        bool enabled = user["enabled"] | true;
        if (!enabled || fingerprintId < 0 || userId.length() == 0) continue;

        const char *role = user["role"] | "student";
        bool lockPermissions[4] = {false, false, false, false};
        for (JsonObject roleItem : roles) {
            if (String((const char *)(roleItem["role"] | "")) == role) {
                lockPermissions[0] = roleItem["lock_0"] | false;
                lockPermissions[1] = roleItem["lock_1"] | false;
                lockPermissions[2] = roleItem["lock_2"] | false;
                lockPermissions[3] = roleItem["lock_3"] | false;
                break;
            }
        }
        for (JsonObject overrideItem : overrides) {
            if (String((const char *)(overrideItem["user_id"] | "")) != userId) continue;
            int lockId = overrideItem["lock_id"] | -1;
            if (lockId >= 0 && lockId < LOCK_COUNT) {
                lockPermissions[lockId] = overrideItem["has_access"] | false;
            }
        }

        // Defense in depth: malformed or manually edited root data must not
        // grant the system lock to a teacher or student.
        if (strcmp(role, "admin") != 0) lockPermissions[0] = false;

        DynamicJsonDocument permissionDoc(1024);
        permissionDoc["version"] = globalVersion;
        permissionDoc["total"] = total;
        permissionDoc["sequence"] = sequence++;
        permissionDoc["fingerprint_id"] = fingerprintId;
        permissionDoc["user_id"] = userId;
        permissionDoc["name"] = user["name"] | "";
        permissionDoc["role"] = strcmp(role, "admin") == 0 ? (int)ROLE_ADMIN :
                                  (strcmp(role, "teacher") == 0 ? (int)ROLE_TEACHER : (int)ROLE_STUDENT);
        JsonObject lockObject = permissionDoc.createNestedObject("lock_permissions");
        lockObject["lock_0"] = lockPermissions[0];
        lockObject["lock_1"] = lockPermissions[1];
        lockObject["lock_2"] = lockPermissions[2];
        lockObject["lock_3"] = lockPermissions[3];
        String data;
        serializeJson(permissionDoc, data);
        allSent = MeshComm::sendToNode(mac, ProtocolFrame::buildMessage(
            "SYNC_PERMISSION", deviceId, data)) && allSent;
    }

    if (allSent) {
        String commitData = "{\"version\":" + String(globalVersion) +
                            ",\"total\":" + String(total) + "}";
        allSent = MeshComm::sendToNode(mac, ProtocolFrame::buildMessage(
            "COMMIT_PERMISSION_SYNC", deviceId, commitData));
    }
    Debug::printf("[MSG] permission transaction to %s: %d records, version=%u, %s\n",
                  deviceId, total, globalVersion, allSent ? "sent" : "aborted");
}
#endif

bool MessageHandler::sendMessage(const String &cmd, const String &dataJson,
                                 const String &msgId) {
    // MeshComm deliberately has no dependency on the root-only bridge. Root
    // responses therefore go directly through the uplink bridge here.
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    String json = ProtocolFrame::buildMessage(cmd, cfg.device_id, dataJson, msgId);
    return MeshBridge::sendToUplink(json);
}

void MessageHandler::sendAck(const String &msgId, const String &result) {
    if (msgId.length() == 0) return;
    String data = "{\"result\":\"" + result + "\"}";
    sendMessage("ACK", data, msgId);
}

void MessageHandler::sendError(ErrorCode code, const String &message,
                               const String &msgId) {
    String data = "{\"error_code\":" + String((int)code) + ",";
    data += "\"message\":\"" + message + "\"}";
    sendMessage("ERROR", data, msgId);
    Debug::printf("[MSG] error response: code=%d msg=%s\n", (int)code, message.c_str());
}

void MessageHandler::update() {
    // Root node has no state machine to update (no fingerprint/lock)
}

// ====== Command dispatch ======
void MessageHandler::handleIncoming(const String &message) {
    DynamicJsonDocument doc(65536);
    DeserializationError err = deserializeJson(doc, message);
    if (err) {
        Debug::printf("[MSG] JSON parse failed: %s\n", err.c_str());
        sendError(ERR_JSON_PARSE, "json parse failed");
        return;
    }

    const char *cmd = doc["cmd"] | "";
    if (strlen(cmd) == 0) {
        Debug::println(F("[MSG] message missing cmd field"));
        sendError(ERR_UNKNOWN_CMD, "missing cmd field");
        return;
    }

    const char *msgId = doc["msg_id"] | "";
    const char *did = doc["device_id"] | "";

    // Check device_id (broadcast commands with empty device_id are accepted)
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    if (strlen(did) > 0 && strcmp(did, cfg.device_id.c_str()) != 0) {
        return;  // Not for this device
    }

    JsonObject data = doc["data"].as<JsonObject>();

    if (!MessageHmac::verify(doc, cfg.hmac_enabled, cfg.hmac_key)) {
        sendError(ERR_PERMISSION_DENIED, "hmac verification failed", msgId);
        return;
    }

    Debug::printf("[MSG] process command: %s (msg_id=%s)\n", cmd, msgId);

    if (strcmp(cmd, "REGISTER") == 0) {
        cmdRegister(msgId);
    } else if (strcmp(cmd, "TIME_SYNC") == 0) {
        cmdTimeSync(data, msgId);
    } else if (strcmp(cmd, "READ_CONFIG") == 0) {
        cmdReadConfig(msgId);
    } else if (strcmp(cmd, "WRITE_CONFIG") == 0) {
        cmdWriteConfig(data, msgId);
    } else if (strcmp(cmd, "READ_STATUS") == 0) {
        cmdReadStatus(msgId);
    } else if (strcmp(cmd, "REBOOT") == 0) {
        cmdReboot(data, msgId);
#ifdef ENABLE_SD_CARD
    } else if (strcmp(cmd, "SD_QUERY") == 0) {
        cmdSdQuery(data, msgId);
    } else if (strcmp(cmd, "SD_SAVE") == 0) {
        cmdSdSave(data, msgId);
    } else if (strcmp(cmd, "SD_QUERY_VERSION") == 0) {
        cmdSdQueryVersion(msgId);
    } else if (strcmp(cmd, "UPLOAD_FP_TEMPLATE") == 0) {
        cmdUploadFpTemplate(data, msgId);
    } else if (strcmp(cmd, "DOWNLOAD_FP_TEMPLATE") == 0) {
        cmdDownloadFpTemplate(data, msgId);
    } else if (strcmp(cmd, "DELETE_FP_TEMPLATE") == 0) {
        cmdDeleteFpTemplate(data, msgId);
#endif
    } else if (strcmp(cmd, "HEARTBEAT_ACK") == 0) {
        // No action needed
    } else if (strcmp(cmd, "BRIDGE_READY") == 0) {
        // No action needed
    } else {
        Debug::printf("[MSG] unknown command: %s\n", cmd);
        sendError(ERR_UNKNOWN_CMD, String("unknown command: ") + cmd, msgId);
    }
}

// ====== Command implementations ======

void MessageHandler::cmdRegister(const String &msgId) {
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    String data = "{";
    data += "\"device_id\":\"" + cfg.device_id + "\",";
    data += "\"device_name\":\"" + cfg.device_name + "\",";
    data += "\"is_root\":true,";
    data += "\"firmware_version\":\"" FIRMWARE_VERSION "\",";
    data += "\"mesh_mac\":\"" + MeshComm::getMeshMac() + "\",";
    data += "\"mesh_layer\":" + String(MeshComm::getMeshLayer()) + ",";
    data += "\"child_count\":" + String(MeshComm::getChildCount()) + ",";
    data += "\"route_count\":" + String(MeshBridge::getRouteCount()) + ",";
#ifdef ENABLE_SD_CARD
    data += "\"sd_ready\":" + String(SdStorage::isReady() ? "true" : "false");
#else
    data += "\"sd_ready\":false";
#endif
    data += "}";
    // Use REGISTER for both announcement and request/response so the host can
    // discover the root even when it connected after boot.
    sendMessage("REGISTER", data, msgId);
    Debug::println(F("[MSG] REGISTER query responded"));
}

void MessageHandler::cmdTimeSync(const JsonObject &data, const String &msgId) {
    uint32_t timestamp = data["timestamp"] | 0;
    if (timestamp > 0) {
        Storage::setUnixTime(timestamp);
        Debug::printf("[MSG] time synced: %u\n", timestamp);

        String timeData = "{\"timestamp\":" + String(timestamp) + "}";
        int propagated = MeshBridge::broadcastToCabinets(
            ProtocolFrame::buildMessage("TIME_SYNC", "", timeData));
        Debug::printf("[MSG] time propagated to %d cabinets\n", propagated);
        sendAck(msgId, "time_synced");
    } else {
        sendError(ERR_UNKNOWN_CMD, "invalid timestamp", msgId);
    }
}

void MessageHandler::cmdReadConfig(const String &msgId) {
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    String data = "{";
    data += "\"device_id\":\"" + cfg.device_id + "\",";
    data += "\"device_name\":\"" + cfg.device_name + "\",";
    data += "\"is_root\":true,";
    data += "\"work_mode\":\"" + String(cfg.work_mode == MODE_MESH ? "mesh" : "debug") + "\",";
    data += "\"uplink_mode\":" + String((int)cfg.uplink_mode) + ",";
    data += "\"mesh_channel\":" + String(cfg.mesh_channel) + ",";
    data += "\"wifi_ssid\":\"" + cfg.wifi_ssid + "\",";
    data += "\"server_ip\":\"" + cfg.server_ip + "\",";
    data += "\"server_port\":" + String(cfg.server_port) + ",";
    data += "\"firmware_version\":\"" FIRMWARE_VERSION "\"";
    data += "}";
    sendMessage("CONFIG_RESPONSE", data, msgId);
}

void MessageHandler::cmdWriteConfig(const JsonObject &data, const String &msgId) {
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);

    if (data.containsKey("device_id"))   cfg.device_id = data["device_id"].as<String>();
    if (data.containsKey("device_name")) cfg.device_name = data["device_name"].as<String>();
    if (data.containsKey("uplink_mode")) cfg.uplink_mode = (UplinkMode)(data["uplink_mode"] | 0);
    if (data.containsKey("mesh_channel")) cfg.mesh_channel = data["mesh_channel"] | MESH_CHANNEL;
    if (data.containsKey("mesh_password")) cfg.mesh_password = data["mesh_password"].as<String>();
    if (data.containsKey("wifi_ssid"))   cfg.wifi_ssid = data["wifi_ssid"].as<String>();
    if (data.containsKey("wifi_password")) cfg.wifi_password = data["wifi_password"].as<String>();
    if (data.containsKey("server_ip"))   cfg.server_ip = data["server_ip"].as<String>();
    if (data.containsKey("server_port")) cfg.server_port = data["server_port"] | UPLINK_TCP_PORT;
    if (data.containsKey("hmac_enabled")) cfg.hmac_enabled = data["hmac_enabled"].as<bool>();
    if (data.containsKey("hmac_key")) cfg.hmac_key = data["hmac_key"].as<String>();

    Storage::saveDeviceConfig(cfg);
    sendMessage("CONFIG_SAVED", "{\"result\":\"success\"}", msgId);
    Debug::println(F("[MSG] config updated"));
}

void MessageHandler::cmdReadStatus(const String &msgId) {
    String data = "{";
    data += "\"uptime\":" + String(millis() / 1000) + ",";
    data += "\"mesh_layer\":" + String(MeshComm::getMeshLayer()) + ",";
    data += "\"child_count\":" + String(MeshComm::getChildCount()) + ",";
    data += "\"route_count\":" + String(MeshBridge::getRouteCount()) + ",";
    data += "\"uplink_connected\":" + String(MeshBridge::isUplinkConnected() ? "true" : "false") + ",";
    data += "\"work_mode\":\"" + String(Storage::loadWorkMode() == MODE_MESH ? "mesh" : "debug") + "\",";
#ifdef ENABLE_SD_CARD
    data += "\"sd_ready\":" + String(SdStorage::isReady() ? "true" : "false") + ",";
    data += "\"sd_total\":" + String((unsigned long)SdStorage::getTotalBytes()) + ",";
    data += "\"sd_used\":" + String((unsigned long)SdStorage::getUsedBytes()) + ",";
#endif
    data += "\"time_synced\":" + String(Storage::isTimeSynced() ? "true" : "false");
    data += "}";
    sendMessage("STATUS_RESPONSE", data, msgId);
}

void MessageHandler::cmdReboot(const JsonObject &data, const String &msgId) {
    String mode = data["mode"] | "";
    Debug::printf("[MSG] preparing reboot, target mode: %s\n", mode.c_str());
    sendMessage("REBOOT_ACK", "{\"result\":\"rebooting\"}", msgId);
    delay(500);
    if (mode == "debug") {
        Storage::saveWorkMode(MODE_DEBUG);
    } else if (mode == "mesh") {
        Storage::saveWorkMode(MODE_MESH);
    }
    ESP.restart();
}

// ============================================================
// ====== SD card data center commands ======
// ============================================================
#ifdef ENABLE_SD_CARD

static size_t utf8SafePartLength(const String &text, size_t start, size_t maxLength) {
    size_t remaining = text.length() - start;
    size_t length = maxLength < remaining ? maxLength : remaining;

    // String indexes are byte indexes. Do not end a part in the middle of a
    // UTF-8 continuation sequence, otherwise the PC decoder will replace the
    // split character before the parts can be reassembled.
    while (length > 0 && start + length < text.length()) {
        uint8_t next = (uint8_t)text[start + length];
        if ((next & 0xC0) != 0x80) break;
        length--;
    }
    return length;
}

bool MessageHandler::sendLargeResponse(const String &cmd, const String &dataJson,
                                       const String &msgId) {
    // One SD_QUERY_PART must fit into a single 1400-byte ESP frame.
    const size_t MAX_PART = 500;

    if (dataJson.length() <= MAX_PART) {
        return sendMessage(cmd, dataJson, msgId);
    }

    int totalParts = 0;
    size_t offset = 0;
    while (offset < dataJson.length()) {
        size_t len = utf8SafePartLength(dataJson, offset, MAX_PART);
        if (len == 0) return false;
        offset += len;
        totalParts++;
    }

    offset = 0;
    for (int i = 0; i < totalParts; i++) {
        size_t start = offset;
        size_t len = utf8SafePartLength(dataJson, start, MAX_PART);
        if (len == 0) return false;
        offset += len;

        String part = "{\"part\":";
        part += String(i + 1);
        part += ",\"total\":";
        part += String(totalParts);
        part += ",\"data\":\"";
        String chunk = dataJson.substring(start, start + len);
        for (size_t j = 0; j < chunk.length(); j++) {
            if (chunk[j] == '"' || chunk[j] == '\\') {
                part += '\\';
            }
            part += chunk[j];
        }
        part += "\"}";

        sendMessage("SD_QUERY_PART", part, msgId);
        delay(30);
    }
    return true;
}

void MessageHandler::cmdSdQuery(const JsonObject &data, const String &msgId) {
    if (!SdStorage::isReady()) {
        sendError(ERR_INTERNAL, "sd card not ready", msgId);
        return;
    }

    String table = data["table"] | "";
    if (!isAllowedTable(table)) {
        sendError(ERR_BAD_REQUEST, "table is not allowed", msgId);
        return;
    }

    String outJson;
    if (!SdStorage::readTable(table, outJson)) {
        sendError(ERR_NOT_FOUND, "table not found or empty", msgId);
        return;
    }

    String response = "{\"table\":\"" + table + "\",\"version\":";
    response += String(getTableVersion(table));
    response += ",\"json\":";
    response += outJson;
    response += "}";

    Debug::printf("[MSG] SD_QUERY %s: %u bytes\n", table.c_str(), (unsigned)response.length());
    sendLargeResponse("SD_QUERY_RESPONSE", response, msgId);
}

void MessageHandler::cmdSdSave(const JsonObject &data, const String &msgId) {
    if (!SdStorage::isReady()) {
        sendError(ERR_INTERNAL, "sd card not ready", msgId);
        return;
    }

    String table = data["table"] | "";
    String json = data["json"] | "";
    uint32_t baseVersion = data["base_version"] | 0;
    bool enforceVersion = data["enforce_version"] | false;

    if (!isAllowedTable(table) || json.length() == 0) {
        sendError(ERR_BAD_REQUEST, "missing table or json", msgId);
        return;
    }

    // Optimistic lock version conflict check
    // New clients explicitly enforce version checks, including the initial
    // version 0. Keep the old base_version=0 behavior for legacy clients.
    if (enforceVersion || baseVersion > 0) {
        uint32_t g, u, c, p, d, fp, logs;
        SdStorage::readVersion(g, u, c, p, d, fp, logs);
        uint32_t currentVer = 0;
        if (table == "users") currentVer = u;
        else if (table == "classes") currentVer = c;
        else if (table == "permissions") currentVer = p;
        else if (table == "role_permissions") currentVer = p;
        else if (table == "devices") currentVer = d;
        else if (table == "fingerprints") currentVer = fp;
        else if (table == "logs") currentVer = logs;

        if (currentVer != baseVersion) {
            String errData = "{\"error\":\"version_conflict\",\"current_version\":";
            errData += String(currentVer);
            errData += ",\"base_version\":";
            errData += String(baseVersion);
            errData += "}";
            sendMessage("SD_SAVE_RESPONSE", errData, msgId);
            Debug::printf("[MSG] SD_SAVE %s version conflict: base=%u current=%u\n",
                          table.c_str(), baseVersion, currentVer);
            return;
        }
    }

    bool ok = SdStorage::writeTable(table, json);
    String resp = "{\"table\":\"" + table + "\",\"result\":";
    resp += ok ? "\"success\"" : "\"fail\"";
    if (ok) {
        resp += ",\"version\":";
        resp += String(SdStorage::getGlobalVersion());
    }
    resp += "}";
    sendMessage("SD_SAVE_RESPONSE", resp, msgId);
    Debug::printf("[MSG] SD_SAVE %s: %s\n", table.c_str(), ok ? "success" : "failed");
}

void MessageHandler::cmdSdQueryVersion(const String &msgId) {
    uint32_t g, u, c, p, d, fp, logs;
    SdStorage::readVersion(g, u, c, p, d, fp, logs);

    String data = "{";
    data += "\"global_version\":" + String(g) + ",";
    data += "\"users_version\":" + String(u) + ",";
    data += "\"classes_version\":" + String(c) + ",";
    data += "\"permissions_version\":" + String(p) + ",";
    data += "\"devices_version\":" + String(d) + ",";
    data += "\"fp_version\":" + String(fp) + ",";
    data += "\"logs_version\":" + String(logs) + ",";
    data += "\"sd_total_bytes\":" + String((unsigned long)SdStorage::getTotalBytes()) + ",";
    data += "\"sd_used_bytes\":" + String((unsigned long)SdStorage::getUsedBytes());
    data += "}";
    sendMessage("SD_VERSION_RESPONSE", data, msgId);
    Debug::printf("[MSG] SD version query: global=%u\n", g);
}

void MessageHandler::cmdUploadFpTemplate(const JsonObject &data, const String &msgId) {
    if (!SdStorage::isReady()) {
        sendError(ERR_INTERNAL, "sd card not ready", msgId);
        return;
    }

    String userId = data["user_id"] | "";
    int fingerIndex = data["finger_index"] | 1;
    String templateHex = data["template_hex"] | "";

    if (userId.length() == 0 || templateHex.length() == 0) {
        sendError(ERR_BAD_REQUEST, "missing user_id or template_hex", msgId);
        return;
    }
    if (fingerIndex < 1 || fingerIndex > FP_MAX_TEMPLATES_PER_USER) {
        sendError(ERR_BAD_REQUEST, "finger_index out of range", msgId);
        return;
    }

    if ((templateHex.length() % 2) != 0) {
        sendError(ERR_BAD_REQUEST, "template hex length must be even", msgId);
        return;
    }

    size_t binLen = templateHex.length() / 2;
    if (binLen == 0 || binLen > FP_TEMPLATE_BUF_SIZE) {
        sendError(ERR_BAD_REQUEST, "template hex length invalid", msgId);
        return;
    }

    uint8_t *buf = (uint8_t *)malloc(binLen);
    if (!buf) {
        sendError(ERR_INTERNAL, "memory alloc failed", msgId);
        return;
    }

    for (size_t i = 0; i < binLen; i++) {
        char hi = templateHex[i * 2];
        char lo = templateHex[i * 2 + 1];
        bool validHi = (hi >= '0' && hi <= '9') || (hi >= 'A' && hi <= 'F') || (hi >= 'a' && hi <= 'f');
        bool validLo = (lo >= '0' && lo <= '9') || (lo >= 'A' && lo <= 'F') || (lo >= 'a' && lo <= 'f');
        if (!validHi || !validLo) {
            free(buf);
            sendError(ERR_BAD_REQUEST, "template hex contains invalid character", msgId);
            return;
        }
        buf[i] = (hexCharToVal(hi) << 4) | hexCharToVal(lo);
    }

    bool ok = SdStorage::writeTemplate(userId, fingerIndex, buf, binLen);
    free(buf);

    String resp = "{\"user_id\":\"" + userId + "\",\"result\":";
    resp += ok ? "\"success\"" : "\"fail\"";
    resp += "}";
    sendMessage("FP_TEMPLATE_UPLOAD_RESPONSE", resp, msgId);
    Debug::printf("[MSG] fingerprint template upload %s[%d]: %s\n",
                  userId.c_str(), fingerIndex, ok ? "success" : "failed");
}

void MessageHandler::cmdDownloadFpTemplate(const JsonObject &data, const String &msgId) {
    if (!SdStorage::isReady()) {
        sendError(ERR_INTERNAL, "sd card not ready", msgId);
        return;
    }

    String userId = data["user_id"] | "";
    int fingerIndex = data["finger_index"] | 1;

    if (userId.length() == 0) {
        sendError(ERR_BAD_REQUEST, "missing user_id", msgId);
        return;
    }

    uint8_t *buf = (uint8_t *)malloc(FP_TEMPLATE_BUF_SIZE);
    if (!buf) {
        sendError(ERR_INTERNAL, "memory alloc failed", msgId);
        return;
    }

    size_t outLen = 0;
    bool ok = SdStorage::readTemplate(userId, fingerIndex, buf, FP_TEMPLATE_BUF_SIZE, outLen);

    if (!ok) {
        free(buf);
        sendError(ERR_NOT_FOUND, "template not found", msgId);
        return;
    }

    String hex = "";
    hex.reserve(outLen * 2);
    const char *hexChars = "0123456789ABCDEF";
    for (size_t i = 0; i < outLen; i++) {
        hex += hexChars[(buf[i] >> 4) & 0x0F];
        hex += hexChars[buf[i] & 0x0F];
    }
    free(buf);

    String resp = "{\"user_id\":\"" + userId + "\",\"finger_index\":";
    resp += String(fingerIndex);
    resp += ",\"len\":";
    resp += String((unsigned)outLen);
    resp += ",\"template_hex\":\"";
    resp += hex;
    resp += "\"}";

    sendMessage("FP_TEMPLATE_DOWNLOAD_RESPONSE", resp, msgId);
    Debug::printf("[MSG] fingerprint template download %s[%d]: %u bytes\n",
                  userId.c_str(), fingerIndex, (unsigned)outLen);
}

void MessageHandler::cmdDeleteFpTemplate(const JsonObject &data, const String &msgId) {
    if (!SdStorage::isReady()) {
        sendError(ERR_INTERNAL, "sd card not ready", msgId);
        return;
    }

    String userId = data["user_id"] | "";
    if (userId.length() == 0) {
        sendError(ERR_BAD_REQUEST, "missing user_id", msgId);
        return;
    }

    bool ok = SdStorage::deleteTemplate(userId);
    if (ok) SdStorage::incrementVersion("fingerprints");

    String resp = "{\"user_id\":\"" + userId + "\",\"result\":";
    resp += ok ? "\"success\"" : "\"fail\"";
    resp += "}";
    sendMessage("FP_TEMPLATE_DELETE_RESPONSE", resp, msgId);
    Debug::printf("[MSG] fingerprint template delete %s: %s\n",
                  userId.c_str(), ok ? "success" : "no template");
}

uint8_t MessageHandler::hexCharToVal(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    return 0;
}

#endif // ENABLE_SD_CARD
