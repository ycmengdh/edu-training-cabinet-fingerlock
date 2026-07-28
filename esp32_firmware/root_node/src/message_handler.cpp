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
#include "cmd_ids.h"
#include "app_protocol.h"
#include "mem_pool.h"
#include <mbedtls/base64.h>
#include <string.h>
#include <stdlib.h>
#ifdef ENABLE_SD_CARD
#include "sd_storage.h"
static void syncPermissionsToCabinet(const uint8_t *mac, const char *deviceId);
static uint32_t composePermissionVersion(uint32_t usersVersion,
                                         uint32_t permissionsVersion) {
    uint32_t value = 2166136261U;
    value = (value ^ usersVersion) * 16777619U;
    value = (value ^ permissionsVersion) * 16777619U;
    return value == 0 ? 1U : value;
}
#endif

// Rebuild a legacy full JSON message so existing handlers keep working while
// the outer envelope is binary. Payload is treated as the `data` object bytes
// (JSON) or "{}" when empty.
static String appViewToLegacyJson(const AppMessageView &view) {
    const char *cmdName = appCmdName(view.cmd_id);
    if (cmdName == nullptr) cmdName = "UNKNOWN";

    char did[APP_DEVICE_ID_MAX + 1];
    did[0] = '\0';
    if (view.device_id_len > 0 && view.device_id != nullptr) {
        size_t n = view.device_id_len;
        if (n > APP_DEVICE_ID_MAX) n = APP_DEVICE_ID_MAX;
        memcpy(did, view.device_id, n);
        did[n] = '\0';
    }

    String dataJson = "{}";
    if (view.payload_len > 0 && view.payload != nullptr) {
        // Binary control payloads must be expanded before JSON dispatch.
        if (view.cmd_id == CMD_TIME_SYNC && view.payload_len >= 4) {
            uint32_t ts = rdU32(view.payload);
            dataJson = "{\"timestamp\":" + String(ts) + "}";
        } else if (view.cmd_id == CMD_CONTROL_LOCK) {
            uint8_t lockId = 0, action = 0;
            if (unpackControlLock(view.payload, view.payload_len, lockId, action)) {
                dataJson = "{\"lock_id\":" + String(lockId) +
                           ",\"action\":\"" + String(action == 1 ? "close" : "open") + "\"}";
            }
        } else {
            // Prefer UTF-8 JSON object payload for complex cmds.
            dataJson = "";
            dataJson.reserve(view.payload_len + 1);
            for (uint16_t i = 0; i < view.payload_len; i++) {
                dataJson += (char)view.payload[i];
            }
            if (dataJson.length() == 0 || (dataJson[0] != '{' && dataJson[0] != '[')) {
                dataJson = "{}";
            }
        }
    }

    String msgId = (view.msg_id != 0) ? String(view.msg_id) : String("");
    return ProtocolFrame::buildMessage(String(cmdName), String(did), dataJson, msgId);
}

void MessageHandler::init() {
    Debug::println(F("[MSG] root message handler init complete"));
}

void MessageHandler::handleIncomingApp(const AppMessageView &view) {
    // Binary control-plane shortcuts that need no JSON doc.
    if (view.cmd_id == CMD_HEARTBEAT_ACK || view.cmd_id == CMD_ACK) {
        return;
    }
    // Hybrid: convert to legacy JSON and reuse existing dispatch (SD/config/etc).
    String legacy = appViewToLegacyJson(view);
    handleIncoming(legacy);
}

void MessageHandler::handleMeshMessageApp(const uint8_t *fromMac, const uint8_t *appMsg, uint16_t len) {
    if (appMsg == nullptr || len == 0) return;
    AppMessageView view;
    if (!appDecode(appMsg, (int)len, view)) return;

    char did[APP_DEVICE_ID_MAX + 1];
    did[0] = '\0';
    if (view.device_id_len > 0 && view.device_id != nullptr) {
        size_t n = view.device_id_len;
        if (n > APP_DEVICE_ID_MAX) n = APP_DEVICE_ID_MAX;
        memcpy(did, view.device_id, n);
        did[n] = '\0';
    }
    if (did[0] == '\0') return;

    if (view.cmd_id == CMD_HEARTBEAT) {
        // Binary HEARTBEAT_ACK — no JSON, no SD.
        uint8_t pl[8];
        int pln = packAck(pl, (int)sizeof(pl), view.msg_id, 0, "ok");
        if (pln < 0) pln = 0;
        uint8_t out[128];
        String rootMac = MeshComm::getMeshMac();
        int n = appEncode(out, (int)sizeof(out), CMD_HEARTBEAT_ACK, view.msg_id, 0,
                          APP_FLAG_IS_ACK, did, rootMac.c_str(), pl, (uint16_t)pln, 0);
        if (n > 0 && !MeshComm::sendToNodeApp(fromMac, out, (uint16_t)n)) {
            Debug::printf("[MSG] binary HEARTBEAT_ACK to %s failed\n", did);
        }
        return;
    }

    // Only commands with Root-side effects go through the heavy JSON path.
    // STATUS_RESPONSE / STATUS_REPORT / enroll results etc. are pure uplink
    // forwards — converting them used to allocate a 16KB DynamicJsonDocument
    // on every cabinet reply and stall the Mesh main loop.
    if (view.cmd_id != CMD_REGISTER &&
        view.cmd_id != CMD_LOG_REPORT &&
        view.cmd_id != CMD_STATUS_REPORT &&
        view.cmd_id != CMD_PERM_LOST) {
        return;
    }

    String legacy = appViewToLegacyJson(view);
    handleMeshMessage(fromMac, legacy);
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

    // 心跳必须形成双向闭环。Root 的 ACK 既证明下行可达，也让柜子能区分
    // “仍关联着父节点”和“Root 应用层确实能收发消息”。心跳不触碰 SD。
    if (strcmp(cmd, "HEARTBEAT") == 0) {
        // Binary HEARTBEAT_ACK (align with handleMeshMessageApp path).
        uint16_t mid = (uint16_t)(doc["msg_id"] | 0);
        uint8_t pl[16];
        int pln = packAck(pl, (int)sizeof(pl), mid, 0, "ok");
        if (pln < 0) pln = 0;
        uint8_t out[160];
        String rootMac = MeshComm::getMeshMac();
        int n = appEncode(out, (int)sizeof(out), CMD_HEARTBEAT_ACK,
                          mid == 0 ? appNextMsgId() : mid, 0, APP_FLAG_IS_ACK,
                          deviceId, rootMac.c_str(), pl, (uint16_t)pln, 0);
        if (n <= 0 || !MeshComm::sendToNodeApp(fromMac, out, (uint16_t)n)) {
            Debug::printf("[MSG] HEARTBEAT_ACK to %s failed\n", deviceId);
        }
        return;
    }

#ifdef ENABLE_SD_CARD
    if (strcmp(cmd, "LOG_REPORT") == 0) {
        // Access logs are runtime-only. Acknowledge so cabinets stop retrying.
        uint16_t mid = (uint16_t)(doc["msg_id"] | 0);
        const char *payload = "{\"result\":\"success\"}";
        uint8_t out[192];
        String rootMac = MeshComm::getMeshMac();
        int n = appEncode(out, (int)sizeof(out), CMD_LOG_REPORT_ACK,
                          mid == 0 ? appNextMsgId() : mid, 0, APP_FLAG_IS_ACK,
                          deviceId, rootMac.c_str(),
                          (const uint8_t *)payload, (uint16_t)strlen(payload), 0);
        if (n > 0) {
            MeshComm::sendToNodeApp(fromMac, out, (uint16_t)n);
        } else {
            MeshComm::sendToNode(fromMac, ProtocolFrame::buildMessage(
                "LOG_REPORT_ACK", deviceId, "{\"result\":\"success\"}",
                String(doc["msg_id"] | "")));
        }
    } else if (strcmp(cmd, "REGISTER") == 0) {
        // Presence is runtime state owned by MeshBridge's route table. Persisting
        // online/offline transitions makes SD latency part of the heartbeat path
        // and can stall every cabinet. Device business metadata remains managed
        // by the existing host <-> SD synchronization flow.

        // REGISTER is also a reconnect announcement, so it must be idempotent.
        // Only cabinets behind the Root's authoritative version need a full
        // permission transaction; equal versions must not rewrite cabinet Flash.
        bool hasCabinetPermVersion = doc["data"].containsKey("perm_version");
        uint32_t cabinetPermVersion = doc["data"]["perm_version"] | 0U;
        uint32_t globalVersion, usersVersion, classesVersion, permissionsVersion;
        uint32_t devicesVersion, fingerprintVersion, logsVersion;
        SdStorage::readVersion(globalVersion, usersVersion, classesVersion,
                               permissionsVersion, devicesVersion,
                               fingerprintVersion, logsVersion);
        uint32_t rootPermVersion = composePermissionVersion(
            usersVersion, permissionsVersion);
        if (!hasCabinetPermVersion || cabinetPermVersion != rootPermVersion) {
            syncPermissionsToCabinet(fromMac, deviceId);
        } else if (cabinetPermVersion == rootPermVersion) {
            Debug::printf("[MSG] permission sync skipped for %s: version=%u current\n",
                          deviceId, cabinetPermVersion);
        }

        time_t currentTime = time(nullptr);
        if (currentTime > 1700000000) {
            String timeData = "{\"timestamp\":" + String((uint32_t)currentTime) + "}";
            uint8_t out[160];
            String rootMac = MeshComm::getMeshMac();
            int n = appEncode(out, (int)sizeof(out), CMD_TIME_SYNC, appNextMsgId(), 0, 0,
                              deviceId, rootMac.c_str(),
                              (const uint8_t *)timeData.c_str(), (uint16_t)timeData.length(), 0);
            if (n > 0) {
                MeshComm::sendToNodeApp(fromMac, out, (uint16_t)n);
            } else {
                MeshComm::sendToNode(fromMac, ProtocolFrame::buildMessage(
                    "TIME_SYNC", deviceId, timeData));
            }
        }
    } else if (strcmp(cmd, "STATUS_REPORT") == 0) {
        // STATUS_REPORT 不写 SD 卡：实时状态由上位机收到原始消息时直接处理，
        // last_seen 由 MeshBridge 路由表 lastSeen 字段维护（7s 过期）。
        // Device presence remains in the route table and is never persisted here.
        // 高频状态路径不输出日志，避免 USB 日志反压 Mesh 主循环。
    }
#endif
}

void MessageHandler::handleDeviceOffline(const String &deviceId) {
    (void)deviceId;
}

#ifdef ENABLE_SD_CARD
static bool isAllowedTable(const String &table) {
    return table == "version" || table == "users" || table == "classes" ||
           table == "permissions" || table == "role_permissions" ||
           table == "devices" || table == "fingerprints" || table == "logs";
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
    if (table == "fingerprints") return fingerprintVersion;
    if (table == "logs") return logsVersion;
    return globalVersion;
}

// Deferred permission sync queue — avoids deep stack + long blocking on REGISTER.
// Documents live on heap so loopTask stack stays shallow.
struct PendingPermSync {
    uint8_t mac[6];
    char deviceId[APP_DEVICE_ID_MAX + 1];
    bool active;
};

static PendingPermSync s_permQueue[MESH_MAX_NODE];
static uint8_t s_permQHead = 0;
static uint8_t s_permQTail = 0;
static uint8_t s_permQCount = 0;

static DynamicJsonDocument *s_usersDoc = nullptr;
static DynamicJsonDocument *s_rolesDoc = nullptr;
static DynamicJsonDocument *s_overridesDoc = nullptr;
static uint8_t s_syncMac[6];
static char s_syncDeviceId[APP_DEVICE_ID_MAX + 1];
static uint32_t s_syncVersion = 0;
static int s_syncTotal = 0;
static int s_syncSequence = 0;
static int s_syncUserIndex = 0;
static uint8_t s_syncPhase = 0; // 0 idle, 1 begin, 2 rows, 3 commit
static unsigned long s_syncNextMs = 0;
static unsigned long s_syncNotBeforeMs = 0;
static bool s_syncAllSent = true;
static bool s_permissionDataDirty = false;
static unsigned long s_permissionDataChangedAt = 0;

static bool queuePermissionSync(const uint8_t *mac, const char *deviceId) {
    if (mac == nullptr || deviceId == nullptr || deviceId[0] == '\0') return false;
    if (s_syncPhase != 0 &&
        strncmp(s_syncDeviceId, deviceId, APP_DEVICE_ID_MAX) == 0) {
        memcpy(s_syncMac, mac, 6);
        return true;
    }
    for (uint8_t i = 0; i < MESH_MAX_NODE; i++) {
        if (s_permQueue[i].active &&
            strncmp(s_permQueue[i].deviceId, deviceId, APP_DEVICE_ID_MAX) == 0) {
            memcpy(s_permQueue[i].mac, mac, 6);
            return true;
        }
    }
    if (s_permQCount >= MESH_MAX_NODE) {
        Debug::printf("[MSG] permission sync queue full, drop %s\n", deviceId);
        return false;
    }
    PendingPermSync &slot = s_permQueue[s_permQTail];
    memset(&slot, 0, sizeof(slot));
    memcpy(slot.mac, mac, 6);
    strncpy(slot.deviceId, deviceId, APP_DEVICE_ID_MAX);
    slot.deviceId[APP_DEVICE_ID_MAX] = '\0';
    slot.active = true;
    s_permQTail = (uint8_t)((s_permQTail + 1) % MESH_MAX_NODE);
    s_permQCount++;
    return true;
}

static void schedulePermissionSyncAfterDataChange(const String &table) {
    if (table != "users" && table != "permissions" &&
        table != "role_permissions") return;
    s_permissionDataDirty = true;
    s_permissionDataChangedAt = millis();
}

static void queuePermissionSyncForOnlineCabinets() {
    int known = MeshBridge::getRouteKnownCount();
    int queued = 0;
    for (int index = 0; index < known; index++) {
        char deviceId[APP_DEVICE_ID_MAX + 1];
        uint8_t mac[6];
        if (!MeshBridge::getRouteDeviceId(index, deviceId, sizeof(deviceId)) ||
            !MeshBridge::lookupRoute(String(deviceId), mac)) continue;
        if (queuePermissionSync(mac, deviceId)) queued++;
    }
    Debug::printf("[MSG] permission data changed: queued %d online cabinets\n", queued);
}

static bool sendCabinetApp(const uint8_t *mac, const char *deviceId,
                           uint16_t cmdId, const String &dataObj) {
    uint8_t *out = MemPool::meshTxScratch();
    size_t cap = MemPool::meshTxScratchSize();
    if (out == nullptr || cap < 64) return false;
    String rootMac = MeshComm::getMeshMac();
    int n = appEncode(out, (int)cap, cmdId, appNextMsgId(), 0, 0,
                      deviceId, rootMac.c_str(),
                      (const uint8_t *)dataObj.c_str(), (uint16_t)dataObj.length(), 0);
    if (n <= 0) return false;
    return MeshComm::sendToNodeApp(mac, out, (uint16_t)n);
}

static void freeSyncDocs() {
    if (s_usersDoc) { delete s_usersDoc; s_usersDoc = nullptr; }
    if (s_rolesDoc) { delete s_rolesDoc; s_rolesDoc = nullptr; }
    if (s_overridesDoc) { delete s_overridesDoc; s_overridesDoc = nullptr; }
}

static bool startNextPermissionSync() {
    if (s_syncPhase != 0) return false;
    if (s_permQCount == 0) return false;
    if ((int32_t)(millis() - s_syncNotBeforeMs) < 0) return false;

    PendingPermSync slot = s_permQueue[s_permQHead];
    s_permQueue[s_permQHead].active = false;
    s_permQHead = (uint8_t)((s_permQHead + 1) % MESH_MAX_NODE);
    s_permQCount--;
    if (!slot.active) return false;

    memcpy(s_syncMac, slot.mac, 6);
    strncpy(s_syncDeviceId, slot.deviceId, APP_DEVICE_ID_MAX);
    s_syncDeviceId[APP_DEVICE_ID_MAX] = '\0';

    freeSyncDocs();
    s_usersDoc = new DynamicJsonDocument(24576);
    s_rolesDoc = new DynamicJsonDocument(4096);
    s_overridesDoc = new DynamicJsonDocument(12288);
    if (!s_usersDoc || !s_rolesDoc || !s_overridesDoc) {
        freeSyncDocs();
        Debug::printf("[MSG] permission sync to %s aborted: OOM\n", s_syncDeviceId);
        return false;
    }

    uint32_t globalVersion, usersVersion, classesVersion, permissionsVersion;
    uint32_t devicesVersion, fingerprintVersion, logsVersion;
    SdStorage::readVersion(globalVersion, usersVersion, classesVersion,
                            permissionsVersion, devicesVersion,
                            fingerprintVersion, logsVersion);

    String usersJson, roleJson, permissionsJson;
    if (!SdStorage::readTable("users", usersJson) ||
        !SdStorage::readTable("role_permissions", roleJson) ||
        !SdStorage::readTable("permissions", permissionsJson) ||
        deserializeJson(*s_usersDoc, usersJson) || !s_usersDoc->is<JsonArray>() ||
        deserializeJson(*s_rolesDoc, roleJson) || !s_rolesDoc->is<JsonArray>() ||
        deserializeJson(*s_overridesDoc, permissionsJson) || !s_overridesDoc->is<JsonArray>()) {
        freeSyncDocs();
        Debug::printf("[MSG] permission sync to %s aborted: root data unavailable\n",
                      s_syncDeviceId);
        return false;
    }

    int total = 0;
    for (JsonObject user : s_usersDoc->as<JsonArray>()) {
        int fingerprintId = user["fingerprint_id"] | -1;
        const char *userId = user["user_id"] | "";
        bool enabled = user["enabled"] | true;
        if (enabled && fingerprintId >= 0 && strlen(userId) > 0) total++;
    }
    if (total > PERM_MAX_USERS) {
        freeSyncDocs();
        Debug::printf("[MSG] permission sync to %s aborted: %d users exceed limit\n",
                      s_syncDeviceId, total);
        return false;
    }

    s_syncVersion = composePermissionVersion(usersVersion, permissionsVersion);
    s_syncTotal = total;
    s_syncSequence = 0;
    s_syncUserIndex = 0;
    s_syncAllSent = true;
    s_syncPhase = 1;
    s_syncNextMs = millis();
    return true;
}

static void processPermissionSyncStep() {
    if (s_syncPhase == 0) {
        startNextPermissionSync();
        return;
    }
    unsigned long now = millis();
    if (now < s_syncNextMs) return;

    if (s_syncPhase == 1) {
        String beginData = "{\"version\":" + String(s_syncVersion) +
                           ",\"total\":" + String(s_syncTotal) + "}";
        s_syncAllSent = sendCabinetApp(s_syncMac, s_syncDeviceId,
                                       CMD_BEGIN_PERMISSION_SYNC, beginData);
        s_syncPhase = s_syncAllSent ? 2 : 0;
        s_syncNextMs = now + PERM_SYNC_INTER_ROW_MS;
        if (!s_syncAllSent) {
            freeSyncDocs();
            Debug::printf("[MSG] permission transaction to %s: begin failed\n",
                          s_syncDeviceId);
        }
        return;
    }

    if (s_syncPhase == 2) {
        if (!s_usersDoc || !s_rolesDoc || !s_overridesDoc) {
            s_syncPhase = 0;
            return;
        }
        JsonArray users = s_usersDoc->as<JsonArray>();
        JsonArray roles = s_rolesDoc->as<JsonArray>();
        JsonArray overrides = s_overridesDoc->as<JsonArray>();
        int userCount = (int)users.size();

        while (s_syncUserIndex < userCount) {
            JsonObject user = users[s_syncUserIndex++];
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
            if (strcmp(role, "admin") != 0) lockPermissions[0] = false;

            int roleCode = strcmp(role, "admin") == 0 ? (int)ROLE_ADMIN :
                           (strcmp(role, "teacher") == 0 ? (int)ROLE_TEACHER : (int)ROLE_STUDENT);
            DynamicJsonDocument permissionDoc(768);
            permissionDoc["version"] = s_syncVersion;
            permissionDoc["total"] = s_syncTotal;
            permissionDoc["sequence"] = s_syncSequence++;
            permissionDoc["fingerprint_id"] = fingerprintId;
            permissionDoc["user_id"] = userId;
            permissionDoc["name"] = user["name"] | "";
            permissionDoc["role"] = roleCode;
            JsonObject lockObject = permissionDoc.createNestedObject("lock_permissions");
            lockObject["lock_0"] = lockPermissions[0];
            lockObject["lock_1"] = lockPermissions[1];
            lockObject["lock_2"] = lockPermissions[2];
            lockObject["lock_3"] = lockPermissions[3];
            String data;
            serializeJson(permissionDoc, data);
            s_syncAllSent = sendCabinetApp(s_syncMac, s_syncDeviceId,
                                           CMD_SYNC_PERMISSION, data) && s_syncAllSent;
            s_syncNextMs = now + PERM_SYNC_INTER_ROW_MS;
            if (!s_syncAllSent) {
                freeSyncDocs();
                s_syncPhase = 0;
                Debug::printf("[MSG] permission transaction to %s: row send failed\n",
                              s_syncDeviceId);
            }
            return; // one row per step
        }

        s_syncPhase = 3;
        s_syncNextMs = now + PERM_SYNC_INTER_ROW_MS;
        return;
    }

    if (s_syncPhase == 3) {
        if (s_syncAllSent) {
            String commitData = "{\"version\":" + String(s_syncVersion) +
                                ",\"total\":" + String(s_syncTotal) + "}";
            s_syncAllSent = sendCabinetApp(s_syncMac, s_syncDeviceId,
                                           CMD_COMMIT_PERMISSION_SYNC, commitData);
        }
        Debug::printf("[MSG] permission transaction to %s: %d records, version=%u, %s\n",
                      s_syncDeviceId, s_syncTotal, s_syncVersion,
                      s_syncAllSent ? "sent" : "aborted");
        freeSyncDocs();
        s_syncPhase = 0;
        s_syncNotBeforeMs = now + PERM_SYNC_INTER_NODE_MS;
        return;
    }
}

static void syncPermissionsToCabinet(const uint8_t *mac, const char *deviceId) {
    // Queue only — heavy work runs in MessageHandler::update() with shallow stack.
    if (queuePermissionSync(mac, deviceId)) {
        Debug::printf("[MSG] permission sync queued for %s\n", deviceId);
    }
}
#endif

bool MessageHandler::sendMessage(const String &cmd, const String &dataJson,
                                 const String &msgId) {
    // MeshComm deliberately has no dependency on the root-only bridge. Root
    // responses therefore go directly through the uplink bridge here.
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);

    uint16_t cmdId = appCmdIdFromName(cmd.c_str());
    if (cmdId != 0) {
        uint16_t mid = 0;
        if (msgId.length() > 0) mid = (uint16_t)msgId.toInt();
        if (mid == 0) mid = appNextMsgId();

        uint8_t flags = 0;
        if (cmdId == CMD_ACK || cmdId == CMD_HEARTBEAT_ACK ||
            cmdId == CMD_SD_SAVE_RESPONSE || cmdId == CMD_SD_VERSION_RESPONSE ||
            cmdId == CMD_FP_TEMPLATE_UPLOAD_RESPONSE ||
            cmdId == CMD_FP_TEMPLATE_DOWNLOAD_RESPONSE ||
            cmdId == CMD_FP_TEMPLATE_DELETE_RESPONSE) {
            flags |= APP_FLAG_IS_ACK;
        }
        if (cmdId == CMD_ERROR) flags |= APP_FLAG_IS_ERROR;

        const String &data = dataJson.length() > 0 ? dataJson : String("{}");
        // Uplink allows larger app payloads (A5 frame fragments). Prefer binary always.
        if (data.length() <= APP_MAX_PAYLOAD_FRAME) {
            int need = (int)(APP_ENVELOPE_MIN + data.length() + 32);
            uint8_t *scratch = MemPool::meshTxScratch();
            size_t scratchSize = MemPool::meshTxScratchSize();
            uint8_t *out = scratch;
            int outSize = (int)scratchSize;
            uint8_t *heapOut = nullptr;
            if (out == nullptr || outSize < need) {
                heapOut = (uint8_t *)malloc((size_t)need);
                out = heapOut;
                outSize = need;
            }
            if (out != nullptr) {
                String selfMac = MeshComm::getMeshMac();
                int n = appEncode(out, outSize, cmdId, mid, 0, flags,
                                  cfg.device_id.c_str(), selfMac.c_str(),
                                  (const uint8_t *)data.c_str(), (uint16_t)data.length(), 0);
                bool ok = false;
                if (n > 0) ok = MeshBridge::sendToUplinkBytes(out, (uint16_t)n);
                if (heapOut) free(heapOut);
                if (ok) return true;
            } else if (heapOut) {
                free(heapOut);
            }
        } else {
            Debug::printf("[MSG] uplink payload too large cmd=%s len=%u (use PART)\n",
                          cmd.c_str(), (unsigned)data.length());
            return false;
        }
    }

    // Unknown cmd: do not emit full JSON.
    Debug::printf("[MSG] unknown/unencodable cmd '%s' dropped\n", cmd.c_str());
    return false;
}

void MessageHandler::sendAck(const String &msgId, const String &result) {
    if (msgId.length() == 0) return;
    // Binary ACK packer when possible
    uint16_t mid = (uint16_t)msgId.toInt();
    uint8_t pl[48];
    int pln = packAck(pl, (int)sizeof(pl), mid, 0, result.c_str());
    if (pln > 0) {
        DeviceConfig cfg;
        Storage::loadDeviceConfig(cfg);
        String selfMac = MeshComm::getMeshMac();
        uint8_t out[128];
        int n = appEncode(out, (int)sizeof(out), CMD_ACK, mid, 0, APP_FLAG_IS_ACK,
                          cfg.device_id.c_str(), selfMac.c_str(), pl, (uint16_t)pln, 0);
        if (n > 0 && MeshBridge::sendToUplinkBytes(out, (uint16_t)n)) return;
    }
    String data = "{\"result\":\"" + result + "\"}";
    sendMessage("ACK", data, msgId);
}

void MessageHandler::sendError(ErrorCode code, const String &message,
                               const String &msgId) {
    uint16_t mid = msgId.length() > 0 ? (uint16_t)msgId.toInt() : 0;
    uint8_t pl[160];
    int pln = packError(pl, (int)sizeof(pl), mid, (uint16_t)code, message.c_str());
    if (pln > 0) {
        DeviceConfig cfg;
        Storage::loadDeviceConfig(cfg);
        String selfMac = MeshComm::getMeshMac();
        uint8_t out[256];
        int n = appEncode(out, (int)sizeof(out), CMD_ERROR, mid, 0, APP_FLAG_IS_ERROR,
                          cfg.device_id.c_str(), selfMac.c_str(), pl, (uint16_t)pln, 0);
        if (n > 0 && MeshBridge::sendToUplinkBytes(out, (uint16_t)n)) {
            Debug::printf("[MSG] error response: code=%d msg=%s\n", (int)code, message.c_str());
            return;
        }
    }
    String data = "{\"error_code\":" + String((int)code) + ",";
    data += "\"message\":\"" + message + "\"}";
    sendMessage("ERROR", data, msgId);
    Debug::printf("[MSG] error response: code=%d msg=%s\n", (int)code, message.c_str());
}

void MessageHandler::update() {
#ifdef ENABLE_SD_CARD
    if (s_permissionDataDirty && millis() - s_permissionDataChangedAt >= 1000 &&
        s_syncPhase == 0 && s_permQCount == 0) {
        s_permissionDataDirty = false;
        queuePermissionSyncForOnlineCabinets();
    }
    // Drain deferred permission sync (one step / loop to keep stack shallow).
    processPermissionSyncStep();
#endif
}

// ====== Command dispatch ======
void MessageHandler::handleIncoming(const String &message) {
    DynamicJsonDocument doc(49152);
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
    } else if (strcmp(cmd, "SD_QUERY_PART_ACK") == 0) {
        // Host optional part-ack: currently windowed fire-and-forget on root.
        // Accept silently so unknown-cmd errors do not surface during large queries.
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
        // Prefer binary TIME_SYNC envelope for cabinets; JSON fallback if encode fails.
        int propagated = 0;
        uint8_t out[160];
        String rootMac = MeshComm::getMeshMac();
        int n = appEncode(out, (int)sizeof(out), CMD_TIME_SYNC, appNextMsgId(), 0,
                          APP_FLAG_BROADCAST, "", rootMac.c_str(),
                          (const uint8_t *)timeData.c_str(), (uint16_t)timeData.length(), 0);
        if (n > 0) {
            propagated = MeshBridge::broadcastToCabinetsApp(out, (uint16_t)n);
        } else {
            propagated = MeshBridge::broadcastToCabinets(
                ProtocolFrame::buildMessage("TIME_SYNC", "", timeData));
        }
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
    data += "\"mesh_link_rssi\":" + String(MeshComm::getLinkRssi()) + ",";
    data += "\"mesh_assoc_expire\":" + String(MeshComm::getApAssocExpireSeconds()) + ",";
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
    // Windowed send: fire up to SD_PART_WINDOW parts, then yield briefly.
    // PC still reassembles by part index; PART_ACK is optional (see host).
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

        if (!sendMessage("SD_QUERY_PART", part, msgId)) {
            Debug::printf("[MSG] SD_QUERY_PART %d/%d send failed\n", i + 1, totalParts);
            return false;
        }
        // Pace parts: every SD_PART_WINDOW packets, longer pause so USB/CDC
        // and host reassembly can catch up without filling TX buffers.
        if (((i + 1) % SD_PART_WINDOW) == 0) {
            delay(80);
            yield();
        } else {
            delay(25);
        }
    }
    return true;
}

void MessageHandler::cmdSdQuery(const JsonObject &data, const String &msgId) {
    if (!SdStorage::isReady()) {
        sendError(ERR_SD_NOT_READY, "sd card not ready", msgId);
        return;
    }

    String table = data["table"] | "";
    if (!isAllowedTable(table)) {
        sendError(ERR_BAD_REQUEST, "table is not allowed", msgId);
        return;
    }

    // Missing/empty tables are valid business state (e.g. classes=[]).
    // Always answer with a JSON array so host SD->business.db pull can proceed.
    String outJson;
    if (!SdStorage::readTable(table, outJson) || outJson.length() == 0) {
        outJson = "[]";
        Debug::printf("[MSG] SD_QUERY %s: empty/missing -> []\n", table.c_str());
    } else {
        char first = 0;
        for (unsigned i = 0; i < outJson.length(); i++) {
            char ch = outJson[i];
            if (ch != ' ' && ch != '\t' && ch != '\r' && ch != '\n') {
                first = ch;
                break;
            }
        }
        if (first != '[' && first != '{') {
            outJson = "[]";
            Debug::printf("[MSG] SD_QUERY %s: non-json content -> []\n", table.c_str());
        }
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
        sendError(ERR_SD_NOT_READY, "sd card not ready", msgId);
        return;
    }

    String table = data["table"] | "";
    String json = data["json"] | "";
    uint32_t baseVersion = data["base_version"] | 0;
    bool enforceVersion = data["enforce_version"] | false;

    if (!isAllowedTable(table)) {
        sendError(ERR_BAD_REQUEST, "table is not allowed", msgId);
        return;
    }

    bool chunked = data.containsKey("chunk_base64");
    String uploadId = data["upload_id"] | "";
    uint32_t partIndex = data["part_index"] | 0;
    uint32_t partTotal = data["part_total"] | 0;
    uint32_t totalBytes = data["total_bytes"] | 0;
    String chunkBase64 = data["chunk_base64"] | "";

    if (chunked) {
        if (uploadId.length() == 0 || uploadId.length() > 40 ||
            partTotal == 0 || partIndex >= partTotal || totalBytes == 0 ||
            chunkBase64.length() == 0 || chunkBase64.length() > 3600) {
            String bad = "{\"table\":\"" + table +
                         "\",\"result\":\"fail\",\"error\":\"invalid_chunk\"}";
            sendMessage("SD_SAVE_RESPONSE", bad, msgId);
            return;
        }

        bool knownChunk = SdStorage::isTableUploadChunkKnown(
            table, uploadId, partIndex, partTotal);
        if (!knownChunk && (enforceVersion || baseVersion > 0)) {
            uint32_t currentVer = getTableVersion(table);
            if (currentVer != baseVersion) {
                String conflict = "{\"error\":\"version_conflict\",\"current_version\":";
                conflict += String(currentVer);
                conflict += ",\"base_version\":" + String(baseVersion) + "}";
                sendMessage("SD_SAVE_RESPONSE", conflict, msgId);
                Debug::printf("[MSG] SD_SAVE %s chunk version conflict: base=%u current=%u\n",
                              table.c_str(), baseVersion, currentVer);
                return;
            }
        }

        size_t decodedCapacity = (chunkBase64.length() / 4U) * 3U + 3U;
        uint8_t *decoded = (uint8_t *)malloc(decodedCapacity);
        size_t decodedLength = 0;
        int decodeResult = decoded == nullptr ? -1 : mbedtls_base64_decode(
            decoded, decodedCapacity, &decodedLength,
            (const unsigned char *)chunkBase64.c_str(), chunkBase64.length());
        if (decodeResult != 0 || decodedLength == 0) {
            if (decoded) free(decoded);
            String bad = "{\"table\":\"" + table +
                         "\",\"result\":\"fail\",\"error\":\"base64_decode_failed\"}";
            sendMessage("SD_SAVE_RESPONSE", bad, msgId);
            return;
        }

        uint32_t expectedPart = 0;
        TableUploadChunkResult chunkResult = SdStorage::writeTableChunk(
            table, uploadId, partIndex, partTotal, totalBytes,
            decoded, decodedLength, expectedPart);
        free(decoded);

        String response = "{\"table\":\"" + table + "\",\"upload_id\":\"" +
                          uploadId + "\",\"part_index\":" + String(partIndex) +
                          ",\"part_total\":" + String(partTotal);
        if (chunkResult == TableUploadChunkResult::Complete) {
            response += ",\"result\":\"success\",\"version\":";
            response += String(getTableVersion(table));
            schedulePermissionSyncAfterDataChange(table);
        } else if (chunkResult == TableUploadChunkResult::Accepted ||
                   chunkResult == TableUploadChunkResult::Duplicate) {
            response += ",\"result\":\"part_ok\",\"expected_part\":";
            response += String(expectedPart);
        } else if (chunkResult == TableUploadChunkResult::OutOfOrder) {
            response += ",\"result\":\"fail\",\"error\":\"out_of_order\",\"expected_part\":";
            response += String(expectedPart);
        } else if (chunkResult == TableUploadChunkResult::Invalid) {
            response += ",\"result\":\"fail\",\"error\":\"invalid_chunk\"";
        } else {
            response += ",\"result\":\"fail\",\"error\":\"sd_write_failed\"";
        }
        response += "}";
        sendMessage("SD_SAVE_RESPONSE", response, msgId);
        return;
    }

    if (json.length() == 0) {
        sendError(ERR_BAD_REQUEST, "missing json", msgId);
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
    if (ok) schedulePermissionSyncAfterDataChange(table);
    String resp = "{\"table\":\"" + table + "\",\"result\":";
    resp += ok ? "\"success\"" : "\"fail\"";
    if (ok) {
        resp += ",\"version\":";
        resp += String(getTableVersion(table));
        resp += ",\"global_version\":";
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
        sendError(ERR_SD_NOT_READY, "sd card not ready", msgId);
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
        sendError(ERR_SD_NOT_READY, "sd card not ready", msgId);
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
        sendError(ERR_SD_NOT_READY, "sd card not ready", msgId);
        return;
    }

    String userId = data["user_id"] | "";
    int fingerIndex = data["finger_index"] | 0;
    if (userId.length() == 0) {
        sendError(ERR_BAD_REQUEST, "missing user_id", msgId);
        return;
    }

    if (fingerIndex < 0 || fingerIndex > FP_MAX_TEMPLATES_PER_USER) {
        sendError(ERR_BAD_REQUEST, "finger_index out of range", msgId);
        return;
    }
    bool ok = fingerIndex > 0
        ? SdStorage::deleteTemplate(userId, fingerIndex)
        : SdStorage::deleteTemplate(userId);
    if (ok) SdStorage::incrementVersion("fingerprints");

    String resp = "{\"user_id\":\"" + userId + "\",\"finger_index\":";
    resp += String(fingerIndex);
    resp += ",\"result\":";
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
