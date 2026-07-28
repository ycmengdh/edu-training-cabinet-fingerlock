/**
 * mesh_comm.cpp - ESP-MESH 自组网通信层实现
 * 替换原 tcp_comm，支持 Root/子节点两种角色
 * Root 节点：MESH_ROOT，桥接上行链路由 main.cpp 处理
 * 子节点：MESH_NODE 主链路 + UART0 直连备用链路，两条链路始终并行
 */
#include "mesh_comm.h"
#include "debug.h"
#include "storage.h"
#include "protocol_frame.h"
#include "mem_pool.h"
#include "app_protocol.h"
#include "serial_uplink.h"
#include <ArduinoJson.h>
#include "reliable_tx.h"
// #include "mesh_bridge.h"  // Moved to main.cpp
#include <WiFi.h>
#include <esp_wifi.h>
#include <esp_mesh.h>
#include <esp_event.h>
#include <esp_mac.h>
#include <esp_heap_caps.h>

// Pure-mesh (USB uplink) must never leave the STA interface hunting a phantom
// router SSID - that produces endless NO_AP_FOUND spam and is not required for
// fixed-root Mesh softAP networking.
static bool s_pureMeshNoRouter = false;

// 注：原 disableStaHunting() 已移除。它曾在 esp_mesh_start() + set_self_organized()
// 之后调用 WiFi.disconnect / esp_wifi_disconnect / esp_wifi_set_config /
// esp_wifi_scan_stop，违反 Espressif 官方警告："self-organized 启用后，应用层
// 不得在 esp_mesh_start() 与 esp_mesh_stop() 之间调用任何 WiFi API，否则会打断
// Mesh 协议栈内部的 WiFi 状态机"，实测导致柜子频繁 PARENT_DISCONNECTED
// reason=202 (MESH_INTERNAL)。纯 Mesh 下 cfg.router.ssid_len=0 已让协议栈不扫
// 外部热点，不需要应用层干预 STA 状态。

// Submit to the Mesh queue without blocking the application loop. The Mesh
// stack copies the packet before returning; P2P traffic is then retried by the
// stack while best-effort traffic remains bounded by queue admission.
static const int MESH_SEND_RETRY_COUNT = 3;
static const int MESH_UPSTREAM_SEND_RETRY_COUNT = 8;
static const TickType_t MESH_SEND_RETRY_DELAY = pdMS_TO_TICKS(20);
static const int MESH_RX_PER_UPDATE = 8;
static const int MESH_EVENT_LOGS_PER_UPDATE = 4;
static const unsigned long REGISTER_RETRY_INTERVAL_MS = 5000UL;
static const unsigned long ROOT_RECOVERY_INTERVAL_MS = 15000UL;
// 强制重关联阈值引用配置常量（config_common.h: MESH_FORCE_REASSOC_MS=30s）。
static const unsigned long FORCE_REASSOC_AFTER_MS = MESH_FORCE_REASSOC_MS;
static const unsigned long MESH_RESTART_AFTER_MS = 180000UL;

static uint32_t meshSendFailureCount = 0;
static uint32_t meshQueueFullCount = 0;
static uint32_t meshRecoveryCount = 0;
static unsigned long lastMeshSendFailureLog = 0;
static unsigned long parentDisconnectedSince = 0;
static unsigned long rootUnreachableSince = 0;
static unsigned long lastRootRecoveryTime = 0;
static unsigned long lastForcedReconnectTime = 0;
static TaskHandle_t meshReceiveTaskHandle = nullptr;

static const char *wifiDisconnectReasonName(uint8_t reason) {
    switch (reason) {
        case WIFI_REASON_TIMEOUT: return "TIMEOUT";
        case WIFI_REASON_NO_AP_FOUND: return "NO_AP_FOUND";
        case WIFI_REASON_AUTH_FAIL: return "AUTH_FAIL";
        case WIFI_REASON_ASSOC_FAIL: return "ASSOC_FAIL";
        case WIFI_REASON_BEACON_TIMEOUT: return "BEACON_TIMEOUT";
        case WIFI_REASON_ASSOC_EXPIRE: return "ASSOC_EXPIRE";
        case WIFI_REASON_ASSOC_LEAVE: return "ASSOC_LEAVE";
        case WIFI_REASON_ROAMING: return "ROAMING";
        default: return "OTHER";
    }
}

// Spread periodic traffic deterministically across its full interval. With
// 100 cabinets this prevents boot/reconnect from producing synchronized
// heartbeat and REGISTER bursts while preserving the same low latency bound.
static unsigned long macPhaseMs(const uint8_t *mac, unsigned long intervalMs) {
    if (mac == nullptr || intervalMs == 0) return 0;
    uint32_t hash = 2166136261UL;
    for (int i = 0; i < 6; ++i) {
        hash ^= mac[i];
        hash *= 16777619UL;
    }
    return (unsigned long)(hash % intervalMs);
}

static unsigned long phasedLastSend(unsigned long now, const uint8_t *mac,
                                    unsigned long intervalMs) {
    unsigned long phase = macPhaseMs(mac, intervalMs);
    return now - (intervalMs - phase);
}

static bool isRetryableMeshSendError(esp_err_t err) {
    return err == ESP_ERR_MESH_QUEUE_FULL ||
           err == ESP_ERR_MESH_NO_MEMORY ||
           err == ESP_ERR_MESH_XMIT ||
           err == ESP_ERR_MESH_TIMEOUT;
}

static esp_err_t boundedMeshSend(const mesh_addr_t *to,
                                 const mesh_data_t *data, int flags) {
    esp_err_t err = ESP_FAIL;
    const int retryCount = to == nullptr
        ? MESH_UPSTREAM_SEND_RETRY_COUNT
        : MESH_SEND_RETRY_COUNT;
    for (int attempt = 0; attempt < retryCount; ++attempt) {
        err = esp_mesh_send(to, data, flags | MESH_DATA_NONBLOCK, NULL, 0);
        if (err == ESP_OK) return ESP_OK;
        if (err == ESP_ERR_MESH_QUEUE_FULL) meshQueueFullCount++;
        if (!isRetryableMeshSendError(err) || attempt + 1 >= retryCount) {
            break;
        }
        vTaskDelay(MESH_SEND_RETRY_DELAY);
    }
    meshSendFailureCount++;
    unsigned long now = millis();
    if (lastMeshSendFailureLog == 0 || now - lastMeshSendFailureLog >= 1000UL) {
        lastMeshSendFailureLog = now;
        mesh_tx_pending_t pending = {};
        esp_err_t pendingErr = esp_mesh_get_tx_pending(&pending);
        Debug::printf("[MESH] send failed err=%s flags=0x%X tos=%d tx=%d/%d/%d/%d pending=%s\n",
                      esp_err_to_name(err), flags, (int)data->tos,
                      pending.to_parent, pending.to_parent_p2p,
                      pending.to_child, pending.to_child_p2p,
                      esp_err_to_name(pendingErr));
    }
    return err;
}

// ====== 静态成员初始化 ======
bool        MeshComm::meshStarted       = false;
bool        MeshComm::meshConnected     = false;
bool        MeshComm::isRootNode        = false;
int         MeshComm::meshLayer         = 0;
uint8_t     MeshComm::meshParentMac[6]  = {0, 0, 0, 0, 0, 0};
uint8_t     MeshComm::meshSelfMac[6]    = {0, 0, 0, 0, 0, 0};
int         MeshComm::childCount        = 0;
uint8_t     MeshComm::rootMac[6]        = {0, 0, 0, 0, 0, 0};
bool        MeshComm::rootMacKnown      = false;
bool        MeshComm::registeredWithRoot = false;
unsigned long MeshComm::lastHeartbeatTime   = 0;
unsigned long MeshComm::unansweredHeartbeatSince = 0;
bool        MeshComm::rootResponseTimedOut = false;
unsigned long MeshComm::lastReconnectTime   = 0;
unsigned long MeshComm::lastRegisterAttemptTime = 0;
int         MeshComm::reconnectAttempt  = 0;
int         MeshComm::reconnectDelays[5] = {5000, 10000, 20000, 40000, 60000};
MeshComm::MessageCallback     MeshComm::msgCb     = nullptr;
MeshComm::MeshMessageCallback MeshComm::meshMsgCb = nullptr;
MeshComm::PeerConnectionCallback MeshComm::peerConnectionCb = nullptr;
void       *MeshComm::msgQueue = nullptr;
void       *MeshComm::eventLogQueue = nullptr;

// 调试模式（UART0 协议帧）内部状态
static bool        debugUartReady = false;
static bool        debugHostSeen = false;
static unsigned long lastDebugAnnounce = 0;
static unsigned long lastDebugHostRx = 0;
static const unsigned long DEBUG_ANNOUNCE_INTERVAL_MS = 3000;

// A cabinet can receive commands from Mesh and UART0 at the same time. Keep a
// short message-id route table so synchronous and delayed replies return over
// the same physical link as the request that created them.
enum CabinetRoute : uint8_t {
    CAB_ROUTE_AUTO = 0,
    CAB_ROUTE_MESH,
    CAB_ROUTE_UART0
};

struct ReplyRouteEntry {
    uint16_t msgId;
    CabinetRoute route;
    unsigned long seenAt;
};

static ReplyRouteEntry s_replyRoutes[16] = {};
static CabinetRoute s_activeIngressRoute = CAB_ROUTE_AUTO;
static uint16_t s_activeIngressMsgId = 0;
static uint16_t s_activeIngressCmdId = 0;
static bool s_routeCorrelatedSend = false;
static const unsigned long REPLY_ROUTE_TTL_MS = 10UL * 60UL * 1000UL;

static const int RESPONSE_CACHE_SLOTS = 8;
static const unsigned long RESPONSE_CACHE_TTL_MS = 30000UL;
struct ResponseCacheEntry {
    uint16_t requestMsgId;
    uint16_t requestCmdId;
    uint16_t responseLen;
    CabinetRoute route;
    unsigned long storedAt;
    uint8_t response[MESH_RX_BUFFER_SIZE];
};
static ResponseCacheEntry *s_responseCache = nullptr;
static uint32_t s_duplicateReplayCount = 0;

static void rememberReplyRoute(uint16_t msgId, CabinetRoute route) {
    if (msgId == 0 || route == CAB_ROUTE_AUTO) return;
    unsigned long now = millis();
    int target = -1;
    int oldest = 0;
    for (int i = 0; i < (int)(sizeof(s_replyRoutes) / sizeof(s_replyRoutes[0])); i++) {
        if (s_replyRoutes[i].msgId == msgId || s_replyRoutes[i].msgId == 0) {
            target = i;
            break;
        }
        if ((unsigned long)(now - s_replyRoutes[i].seenAt) >
            (unsigned long)(now - s_replyRoutes[oldest].seenAt)) {
            oldest = i;
        }
    }
    if (target < 0) target = oldest;
    s_replyRoutes[target].msgId = msgId;
    s_replyRoutes[target].route = route;
    s_replyRoutes[target].seenAt = now;
}

static CabinetRoute findReplyRoute(uint16_t msgId) {
    if (msgId == 0) return CAB_ROUTE_AUTO;
    unsigned long now = millis();
    for (ReplyRouteEntry &entry : s_replyRoutes) {
        if (entry.msgId == 0) continue;
        if ((unsigned long)(now - entry.seenAt) > REPLY_ROUTE_TTL_MS) {
            entry.msgId = 0;
            continue;
        }
        if (entry.msgId == msgId) return entry.route;
    }
    return CAB_ROUTE_AUTO;
}

static CabinetRoute selectReplyRoute(uint16_t msgId) {
    CabinetRoute remembered = findReplyRoute(msgId);
    if (remembered != CAB_ROUTE_AUTO) return remembered;
    if (s_activeIngressRoute != CAB_ROUTE_AUTO &&
        (s_activeIngressMsgId == 0 || msgId == 0 || msgId == s_activeIngressMsgId)) {
        return s_activeIngressRoute;
    }
    return CAB_ROUTE_AUTO;
}

static void cacheResponse(CabinetRoute route, const uint8_t *response,
                          uint16_t responseLen) {
    if (s_responseCache == nullptr || route == CAB_ROUTE_AUTO ||
        s_activeIngressMsgId == 0 ||
        s_activeIngressCmdId == 0 || response == nullptr || responseLen == 0 ||
        responseLen >= MESH_RX_BUFFER_SIZE) {
        return;
    }

    unsigned long now = millis();
    int target = -1;
    int oldest = 0;
    for (int i = 0; i < RESPONSE_CACHE_SLOTS; ++i) {
        ResponseCacheEntry &entry = s_responseCache[i];
        if ((entry.requestMsgId == s_activeIngressMsgId &&
             entry.requestCmdId == s_activeIngressCmdId && entry.route == route) ||
            entry.requestMsgId == 0 || now - entry.storedAt > RESPONSE_CACHE_TTL_MS) {
            target = i;
            break;
        }
        if (now - entry.storedAt > now - s_responseCache[oldest].storedAt) {
            oldest = i;
        }
    }
    if (target < 0) target = oldest;
    ResponseCacheEntry &entry = s_responseCache[target];
    entry.requestMsgId = s_activeIngressMsgId;
    entry.requestCmdId = s_activeIngressCmdId;
    entry.responseLen = responseLen;
    entry.route = route;
    entry.storedAt = now;
    memcpy(entry.response, response, responseLen);
}

static bool replayCachedResponse(uint16_t requestMsgId, uint16_t requestCmdId,
                                 CabinetRoute route) {
    if (s_responseCache == nullptr || requestMsgId == 0 || requestCmdId == 0 ||
        route == CAB_ROUTE_AUTO) return false;
    unsigned long now = millis();
    for (int i = 0; i < RESPONSE_CACHE_SLOTS; ++i) {
        ResponseCacheEntry &entry = s_responseCache[i];
        if (entry.requestMsgId == 0) continue;
        if (now - entry.storedAt > RESPONSE_CACHE_TTL_MS) {
            entry.requestMsgId = 0;
            continue;
        }
        if (entry.requestMsgId != requestMsgId ||
            entry.requestCmdId != requestCmdId || entry.route != route) {
            continue;
        }
        bool previousCorrelated = s_routeCorrelatedSend;
        s_routeCorrelatedSend = true;
        bool sent = MeshComm::sendAppRaw(entry.response, entry.responseLen);
        s_routeCorrelatedSend = previousCorrelated;
        if (sent) s_duplicateReplayCount++;
        // A cache hit proves this request already executed. A transient replay
        // send failure must not fall through and execute a non-idempotent
        // command again; the host's next retry will replay the same response.
        return true;
    }
    return false;
}

// ====== 初始化 ======
void MeshComm::init() {
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    isRootNode = cfg.is_root;

    if (!isRootNode && s_responseCache == nullptr) {
        s_responseCache = (ResponseCacheEntry *)heap_caps_calloc(
            RESPONSE_CACHE_SLOTS, sizeof(ResponseCacheEntry),
            MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
        if (s_responseCache == nullptr) {
            s_responseCache = (ResponseCacheEntry *)calloc(
                RESPONSE_CACHE_SLOTS, sizeof(ResponseCacheEntry));
        }
    }

    ProtocolFrame::init();

    if (isRootNode) {
        // Root：仅 Mesh（上位机上行由 MeshBridge 走 USB/AP/STA）
        Debug::println(F("[MESH] === Root Mesh init ==="));
        initMesh();
    } else {
        // 柜子固定并行运行 Mesh + UART0，UART0 不再作为互斥工作模式。
        Debug::println(F("[MESH] === cabinet MESH + UART0 host ==="));
        initUartHost();
        initMesh();
    }

    unsigned long now = millis();
    lastHeartbeatTime = isRootNode
        ? now
        : phasedLastSend(now, meshSelfMac, MESH_HEARTBEAT_INTERVAL);
    if (!isRootNode && lastRegisterAttemptTime == 0) {
        lastRegisterAttemptTime = phasedLastSend(
            now, meshSelfMac, REGISTER_RETRY_INTERVAL_MS);
    }
    unansweredHeartbeatSince = 0;
    rootResponseTimedOut = false;
    lastReconnectTime = 0;
    reconnectAttempt = 0;
    parentDisconnectedSince = 0;
    rootUnreachableSince = 0;
    lastRootRecoveryTime = 0;
    lastForcedReconnectTime = 0;
}

// ====== Mesh 初始化 ======
// Root is fixed as MESH_ROOT and cabinets select it as their parent. ESP-MESH
// still requires every participant to carry the same 2.4 GHz infrastructure
// router credentials, even when application uplink traffic uses USB/UART.
bool MeshComm::initMesh() {
    DeviceConfig devCfg;
    Storage::loadDeviceConfig(devCfg);
    isRootNode = devCfg.is_root;

    // WiFi initialization: Mesh requires both STA and softAP interfaces.
    // WiFi.disconnect(true, ...) disables STA again and leaves AP-only mode,
    // so clear the old STA profile without turning the radio/interface off.
    // 关闭 persistent，避免把失败的热点凭据写回 NVS 后反复 begin。
    WiFi.persistent(false);
    WiFi.setAutoReconnect(false);
    WiFi.mode(WIFI_AP_STA);
    // eraseap=false：只断当前连接，不拆 softAP；第二个参数 erase 凭据防旧 SSID 重连
    WiFi.disconnect(false /*wifioff*/, true /*eraseap credentials*/);
    delay(100);

    esp_err_t err = esp_mesh_init();
    if (err != ESP_OK) {
        Debug::printf("[MESH] esp_mesh_init failed: %s\n", esp_err_to_name(err));
        return false;
    }

    // 注册 Mesh 事件（幂等：重复注册会覆盖）
    esp_event_handler_register(MESH_EVENT, ESP_EVENT_ANY_ID, &meshEventHandler, NULL);

    mesh_cfg_t cfg = MESH_INIT_CONFIG_DEFAULT();

    // 信道：优先 NVS，避免根/柜配置不一致
    uint8_t channel = devCfg.mesh_channel > 0 ? devCfg.mesh_channel : MESH_CHANNEL;
    cfg.channel = channel;
    // 无路由器固定信道，禁止跳频导致双方搜不到
    cfg.allow_channel_switch = false;

    uint8_t meshId[6] = MESH_ID;
    memcpy(cfg.mesh_id.addr, meshId, 6);

    // Router fields:
    // - Only fill a real wifi_ssid when STA uplink / infrastructure router is needed.
    // - Pure Mesh (Root USB uplink, cabinets join Mesh softAP) must NOT install any
    //   STA SSID. A virtual SSID like "MESH_NET" still makes the WiFi STA state
    //   machine scan forever → NO_AP_FOUND every ~3s. That is the log spam you see.
    // - Prefer ssid_len=0 for pure mesh; if set_config rejects it, fall back to a
    //   placeholder AND immediately clear/disable STA hunting after mesh_start.
    memset(cfg.router.ssid, 0, sizeof(cfg.router.ssid));
    memset(cfg.router.password, 0, sizeof(cfg.router.password));
    cfg.router.ssid_len = 0;
    cfg.router.allow_router_switch = false;
    s_pureMeshNoRouter = true;
    // Cabinet networking is pure ESP-MESH. Ignore stale router credentials on
    // cabinets; only a Root explicitly configured for STA uplink may use them.
    bool needInfrastructureRouter =
        isRootNode && devCfg.uplink_mode == UPLINK_STA &&
        devCfg.wifi_ssid.length() > 0;
    if (needInfrastructureRouter) {
        s_pureMeshNoRouter = false;
        const char *routerSsid = devCfg.wifi_ssid.c_str();
        size_t ssidLen = strlen(routerSsid);
        if (ssidLen >= sizeof(cfg.router.ssid)) ssidLen = sizeof(cfg.router.ssid) - 1;
        memcpy(cfg.router.ssid, routerSsid, ssidLen);
        cfg.router.ssid_len = (uint8_t)ssidLen;
        strncpy((char *)cfg.router.password, devCfg.wifi_password.c_str(),
                sizeof(cfg.router.password) - 1);
        Debug::printf("[MESH] infrastructure router SSID=%s (STA uplink/router mode)\n",
                      devCfg.wifi_ssid.c_str());
    } else {
        Debug::println(F("[MESH] pure mesh: router SSID empty (no external WiFi hunt)"));
        if (isRootNode && devCfg.wifi_ssid.length() > 0 &&
            devCfg.uplink_mode != UPLINK_STA) {
            Debug::println(F("[MESH] ignore NVS wifi_ssid on Root USB uplink"));
        }
    }

    // Mesh softAP 密码（组网密钥）
    cfg.mesh_ap.max_connection = MESH_AP_MAX_CONNECTION;
    memset(cfg.mesh_ap.password, 0, sizeof(cfg.mesh_ap.password));
    const char *meshPass = (devCfg.mesh_password.length() > 0)
        ? devCfg.mesh_password.c_str()
        : MESH_PASSWORD;
    size_t meshPassLen = strlen(meshPass);
    if (channel > 13 || meshPassLen < 8 || meshPassLen > 63) {
        Debug::printf("[MESH] invalid config: channel=%u mesh_password_len=%u\n",
                      channel, (unsigned)meshPassLen);
        return false;
    }
    strncpy((char *)cfg.mesh_ap.password, meshPass, sizeof(cfg.mesh_ap.password) - 1);

    Debug::printf("[MESH] config role=%s channel=%u router_len=%u mesh_pass_len=%u\n",
                  isRootNode ? "ROOT" : "CHILD", channel,
                  cfg.router.ssid_len, (unsigned)meshPassLen);

    err = esp_mesh_set_config(&cfg);
    if (err != ESP_OK && s_pureMeshNoRouter && cfg.router.ssid_len == 0) {
        // ESP-IDF 4.4 拒绝空 router SSID。用占位 SSID 让 set_config 通过，
        // 但在 esp_mesh_start() 之前清空 STA profile（合规：start 前可调 WiFi API），
        // 避免 start 后协议栈用占位 SSID 扫描外部热点导致
        // reason=201 (WIFI_REASON_NO_AP_FOUND) 与关联超时。
        static const char kVirtualSsid[] = "MESH_NET";
        memset(cfg.router.ssid, 0, sizeof(cfg.router.ssid));
        memcpy(cfg.router.ssid, kVirtualSsid, sizeof(kVirtualSsid) - 1);
        cfg.router.ssid_len = (uint8_t)(sizeof(kVirtualSsid) - 1);
        Debug::printf("[MESH] set_config rejected empty router (%s); retry placeholder SSID\n",
                      esp_err_to_name(err));
        err = esp_mesh_set_config(&cfg);
    }
    if (err != ESP_OK) {
        Debug::printf("[MESH] esp_mesh_set_config failed: %s\n", esp_err_to_name(err));
        return false;
    }

    esp_mesh_set_ap_authmode(WIFI_AUTH_WPA2_PSK);
    esp_mesh_set_max_layer(MESH_MAX_LAYER);
    err = esp_mesh_set_capacity_num(MESH_NETWORK_CAPACITY);
    if (err != ESP_OK) {
        Debug::printf("[MESH] set capacity=%d failed: %s\n",
                      MESH_NETWORK_CAPACITY, esp_err_to_name(err));
        return false;
    }
    // Default XON queue is 32. With six children per parent it leaves only
    // about two packets of flow-control window per child. A 64-packet window
    // absorbs heartbeat/ACK and business bursts without the RAM cost of 128.
    err = esp_mesh_set_xon_qsize(64);
    if (err != ESP_OK) {
        Debug::printf("[MESH] set xon queue=64 failed: %s\n", esp_err_to_name(err));
        return false;
    }
    // Every participant must use the same Fixed Root setting. This disables
    // voting while still allowing child nodes to discover/select a parent.
    err = esp_mesh_fix_root(true);
    if (err != ESP_OK) {
        Debug::printf("[MESH] esp_mesh_fix_root(true) failed: %s\n",
                      esp_err_to_name(err));
        return false;
    }
    // AP association expiry is a stack-level lifetime, not an application
    // heartbeat timeout. Ten seconds caused periodic reason=4 disconnects
    // even while business traffic was active. Presence remains governed by
    // the 3s heartbeat / 7s ACK timeout, so keep the radio association stable.
    err = esp_mesh_set_ap_assoc_expire(120);
    int actualAssocExpire = esp_mesh_get_ap_assoc_expire();
    Debug::printf("[MESH] AP assoc expire requested=120s actual=%ds result=%s\n",
                  actualAssocExpire, esp_err_to_name(err));
    if (err != ESP_OK) {
        Debug::println(F("[MESH] continuing with stack default AP association expiry"));
    }

    // 禁用 Mesh 低功耗。默认 PS 开启时柜子在 beacon 间隔的大部分时间休眠，
    // beacon 丢失 -> BEACON_TIMEOUT 频繁断开。柜子市电供电，功耗不敏感，
    // 禁用 PS 让设备始终活跃，keepalive 最稳。根节点 duty 恒为 100 无副作用。
    // 官方文档：此 API 必须在 esp_mesh_start() 之前调用。
    err = esp_mesh_disable_ps();
    if (err != ESP_OK) {
        Debug::printf("[MESH] disable_ps failed: %s\n", esp_err_to_name(err));
    }

    // 纯 Mesh 下占位 SSID "MESH_NET" 已写入 cfg.router.ssid。在 esp_mesh_start()
    // 之前清空 STA profile，避免 start 后协议栈用占位 SSID 扫描外部热点。
    // 这一步在 start 之前调用 WiFi API，符合官方"self-organized 启用前可调 WiFi API"的要求。
    if (s_pureMeshNoRouter) {
        wifi_config_t staCfg;
        memset(&staCfg, 0, sizeof(staCfg));
        esp_wifi_set_config(WIFI_IF_STA, &staCfg);
        esp_err_t channelErr = esp_wifi_set_channel(
            channel, WIFI_SECOND_CHAN_NONE);
        WiFi.setAutoReconnect(false);
        Debug::printf("[MESH] STA profile cleared; restore channel=%u result=%s\n",
                      channel, esp_err_to_name(channelErr));
    }

    // set_type must be called BEFORE esp_mesh_start so the stack brings up
    // the right role on start. set_self_organized MUST be called AFTER
    // esp_mesh_start per ESP-IDF docs; calling it before start is a no-op
    // and leaves the node trying to elect a parent (Root gets spurious
    // PARENT_DISCONNECTED events, children never attach).
    if (isRootNode) {
        err = esp_mesh_set_type(MESH_ROOT);
        if (err != ESP_OK) {
            Debug::printf("[MESH] set_type(ROOT) failed: %s\n", esp_err_to_name(err));
        }
    }

    err = esp_mesh_send_block_time(100);
    if (err != ESP_OK) {
        Debug::printf("[MESH] set send block time failed: %s\n", esp_err_to_name(err));
    }

    err = esp_mesh_start();
    if (err != ESP_OK) {
        Debug::printf("[MESH] esp_mesh_start failed: %s\n", esp_err_to_name(err));
        return false;
    }

    meshStarted = true;

    // Configure self-organized mode AFTER esp_mesh_start per ESP-IDF docs.
    // Root does NOT call set_self_organized: it's already MESH_ROOT +
    // fix_root(true), and calling set_self_organized on root can trigger
    // spurious ROOT_SWITCHED events that destabilize the network.
    // Child enables self-organization with parent selection (allow=true)
    // so the stack scans for the matching Mesh AP and attaches.
    if (isRootNode) {
        // Root 启动后即可服务；子节点等 PARENT_CONNECTED
        meshConnected = true;
        meshLayer = 1;
        // 纯 Mesh + USB 上行：cfg.router.ssid_len=0 已让协议栈不扫外部热点，
        // 不需要应用层调 WiFi API 干预 STA（官方禁止 self-organized 启用后调 WiFi API）。
        Debug::printf("[MESH] node type: FIXED ROOT, channel=%u, pass_src=%s uplink=%u\n",
                      channel,
                      devCfg.mesh_password.length() > 0 ? "nvs" : "default",
                      (unsigned)devCfg.uplink_mode);
    } else {
        err = esp_mesh_set_self_organized(true, true);
        if (err != ESP_OK) {
            Debug::printf("[MESH] set_self_organized(true,true) failed: %s\n",
                          esp_err_to_name(err));
        }
        // 柜节点：纯 Mesh 下 cfg.router.ssid_len=0 已让协议栈不扫外部热点，
        // 不需要应用层调 WiFi API 干预 STA（官方禁止 self-organized 启用后调 WiFi API）。
        Debug::printf("[MESH] node type: CHILD, channel=%u, pass_src=%s\n",
                      channel,
                      devCfg.mesh_password.length() > 0 ? "nvs" : "default");
    }

    if (msgQueue == nullptr) {
        // ACK + LOG_REPORT + heartbeat can arrive in a short burst after an
        // unlock.  Deeper queue (MESH_RX_QUEUE_DEPTH) reduces burst loss.
        msgQueue = xQueueCreate(MESH_RX_QUEUE_DEPTH, sizeof(MeshMessage));
    }
    // Event log queue: defer meshEventHandler's log output to the main loop
    // (sys_evt task stack is too small for Debug::printf -> encode chain)
    if (eventLogQueue == nullptr) {
        eventLogQueue = xQueueCreate(16, sizeof(EventLogEntry));
    }
    // mesh_rx 任务栈 12KB：虽然只做 esp_mesh_recv + xQueueSend，但 ESP-MESH
    // 协议栈底层在 recv 路径上可能使用一定栈。8KB 临界，12KB 留余量避免
    // 长时间运行后偶发栈溢出。
    BaseType_t taskResult = xTaskCreatePinnedToCore(
        meshReceiveTask, "mesh_rx", 12288, NULL, 5, &meshReceiveTaskHandle, 0);
    if (taskResult != pdPASS) {
        meshReceiveTaskHandle = nullptr;
        Debug::println(F("[MESH] failed to create receive task"));
        esp_mesh_stop();
        meshStarted = false;
        return false;
    }

    esp_wifi_get_mac(WIFI_IF_STA, meshSelfMac);
    uint8_t actualChannel = 0;
    wifi_second_chan_t actualSecondary = WIFI_SECOND_CHAN_NONE;
    esp_wifi_get_channel(&actualChannel, &actualSecondary);
    Debug::printf("[MESH] Mesh started ok, configured_channel=%u actual_channel=%u, MAC=%s, root=%s\n",
                  channel, actualChannel, macToString(meshSelfMac).c_str(),
                  isRootNode ? "yes" : "no");
    return true;
}

// ====== Mesh 事件处理器 ======
void MeshComm::pushEventLog(int32_t eventId, const uint8_t *mac, uint8_t reason) {
    if (eventLogQueue == nullptr) return;
    EventLogEntry entry;
    entry.event_id = eventId;
    entry.child_count = childCount;
    entry.mesh_layer = meshLayer;
    entry.reason = reason;
    if (mac != nullptr) {
        memcpy(entry.mac, mac, 6);
    } else {
        memset(entry.mac, 0, 6);
    }
    // Non-blocking: if queue is full, drop oldest by design (event log is best-effort)
    xQueueSend((QueueHandle_t)eventLogQueue, &entry, 0);
}

void MeshComm::drainEventLog() {
    if (eventLogQueue == nullptr) return;
    EventLogEntry entry;
    int processed = 0;
    while (processed < MESH_EVENT_LOGS_PER_UPDATE &&
           xQueueReceive((QueueHandle_t)eventLogQueue, &entry, 0) == pdTRUE) {
        processed++;
        switch (entry.event_id) {
            case MESH_EVENT_STARTED:
                Debug::println(F("[MESH] event: Mesh started"));
                break;
            case MESH_EVENT_STOPPED:
                Debug::println(F("[MESH] event: Mesh stopped"));
                break;
            case MESH_EVENT_PARENT_CONNECTED:
                Debug::printf("[MESH] event: PARENT_CONNECTED parent=%s layer=%d childCount=%d\n",
                              macToString(entry.mac).c_str(),
                              entry.mesh_layer, entry.child_count);
                break;
            case MESH_EVENT_PARENT_DISCONNECTED:
                Debug::printf("[MESH] event: PARENT_DISCONNECTED parent=%s reason=%d(%s) childCount=%d\n",
                              macToString(entry.mac).c_str(),
                              entry.reason, wifiDisconnectReasonName(entry.reason),
                              entry.child_count);
                break;
            case MESH_EVENT_CHILD_CONNECTED:
                Debug::printf("[MESH] event: CHILD_CONNECTED child=%s (total %d)\n",
                              macToString(entry.mac).c_str(),
                              entry.child_count);
                if (peerConnectionCb != nullptr) {
                    peerConnectionCb(entry.mac, true);
                }
                break;
            case MESH_EVENT_CHILD_DISCONNECTED:
                Debug::printf("[MESH] event: CHILD_DISCONNECTED child=%s (remaining %d)\n",
                              macToString(entry.mac).c_str(),
                              entry.child_count);
                if (peerConnectionCb != nullptr) {
                    peerConnectionCb(entry.mac, false);
                }
                break;
            case MESH_EVENT_ROOT_ADDRESS:
                Debug::printf("[MESH] event: Root address=%s\n",
                              macToString(entry.mac).c_str());
                break;
            case MESH_EVENT_CHANNEL_SWITCH:
                Debug::println(F("[MESH] event: channel switch"));
                break;
            default:
                Debug::printf("[MESH] event: id=%d\n", (int)entry.event_id);
                break;
        }
    }
}

void MeshComm::meshEventHandler(void *arg, esp_event_base_t event_base,
                                int32_t event_id, void *event_data) {
    // CRITICAL: This runs in the sys_evt task whose stack is only ~2304B by
    // default. Calling Debug::printf -> sendFramed -> ProtocolFrame::encode
    // -> Serial.flush here overflows that stack and crashes the system
    // (CORRUPT HEAP + stack canary). All logging is deferred to
    // drainEventLog() called from the main loop.
    switch (event_id) {
        case MESH_EVENT_STARTED:
            if (isRootNode) {
                meshConnected = true;
                meshLayer = 1;
                esp_wifi_get_mac(WIFI_IF_STA, rootMac);
                rootMacKnown = true;
            }
            pushEventLog(event_id);
            break;

        case MESH_EVENT_STOPPED:
            meshConnected = false;
            if (!isRootNode) {
                registeredWithRoot = false;
                rootMacKnown = false;
            }
            pushEventLog(event_id);
            break;

        case MESH_EVENT_PARENT_CONNECTED: {
            meshConnected = true;
            reconnectAttempt = 0;
            parentDisconnectedSince = 0;
            lastHeartbeatTime = phasedLastSend(
                millis(), meshSelfMac, MESH_HEARTBEAT_INTERVAL);
            // 根节点重启场景：根节点 reboot 后路由表清空，柜子已 registeredWithRoot=true
            // 不会重发 REGISTER → 根节点路由表直到下次 HEARTBEAT 才能重建。
            // 解决：PARENT_CONNECTED 时检查距上次 REGISTER 是否 > 30 秒，
            // 若是则重置 registeredWithRoot，让主循环立即重发 REGISTER 重建路由。
            // 30 秒冷却窗口防止 Mesh 链路频繁 flap 时 REGISTER 风暴。
            {
                unsigned long now = millis();
                if (now - lastRegisterAttemptTime > 30000UL) {
                    registeredWithRoot = false;
                }
            }
            mesh_addr_t parent;
            if (esp_mesh_get_parent_bssid(&parent) == ESP_OK) {
                memcpy(meshParentMac, parent.addr, 6);
            }
            meshLayer = esp_mesh_get_layer();
            if (meshLayer <= 1) {
                memcpy(rootMac, meshParentMac, 6);
                rootMacKnown = true;
            }
            // 详细日志：传入 parent MAC 便于排查 Mesh 协议层 flap
            pushEventLog(event_id, meshParentMac);
            break;
        }

        case MESH_EVENT_PARENT_DISCONNECTED: {
            // Root is fixed and has no parent; ignore spurious parent disconnect
            // events (the stack still emits them during initial parent scan).
            if (isRootNode) {
                break;
            }
            // Child: only clear meshConnected so HEARTBEAT pauses. Keep
            // registeredWithRoot and rootMacKnown across short link
            // fluctuations — Mesh self-heals quickly, and re-sending REGISTER
            // every disconnect would flood the Root and cause it to watchdog
            // reset. If the parent really doesn't come back, a long loss of
            // HEARTBEAT_ACK is the proper signal to re-register.
            meshConnected = false;
            if (parentDisconnectedSince == 0) {
                parentDisconnectedSince = millis();
                lastReconnectTime = 0;
            }
            // The physical parent is gone, so the application-level "stale
            // Root route" recovery no longer applies. Let ESP-MESH scan on
            // its own and start a fresh heartbeat window after reconnect.
            rootResponseTimedOut = false;
            unansweredHeartbeatSince = 0;
            rootUnreachableSince = 0;
            lastRootRecoveryTime = 0;
            reconnectAttempt = 0;
            // 详细日志：传入 parent MAC + reason code 便于排查 1/0 抖动根因
            // event_data 是 wifi_event_sta_disconnected_t，含 bssid[6] 和 reason
            uint8_t parentMac[6] = {0};
            uint8_t reason = 0;
            if (event_data != nullptr) {
                wifi_event_sta_disconnected_t *disc =
                    (wifi_event_sta_disconnected_t *)event_data;
                memcpy(parentMac, disc->bssid, 6);
                reason = disc->reason;
            }
            pushEventLog(event_id, parentMac, reason);
            break;
        }

        case MESH_EVENT_CHILD_CONNECTED: {
            childCount++;
            // 详细日志：传入 child MAC 便于 Root 端追踪哪个柜子关联上来
            uint8_t childMac[6] = {0};
            if (event_data != nullptr) {
                wifi_event_ap_staconnected_t *cc =
                    (wifi_event_ap_staconnected_t *)event_data;
                memcpy(childMac, cc->mac, 6);
            }
            pushEventLog(event_id, childMac);
            break;
        }

        case MESH_EVENT_CHILD_DISCONNECTED: {
            if (childCount > 0) childCount--;
            // 详细日志：传入 child MAC 便于 Root 端追踪哪个柜子掉线
            uint8_t childMac[6] = {0};
            if (event_data != nullptr) {
                wifi_event_ap_stadisconnected_t *dc =
                    (wifi_event_ap_stadisconnected_t *)event_data;
                memcpy(childMac, dc->mac, 6);
            }
            pushEventLog(event_id, childMac);
            break;
        }

        case MESH_EVENT_ROOT_ADDRESS: {
            mesh_addr_t *rootAddr = (mesh_addr_t*)event_data;
            if (rootAddr != nullptr) {
                memcpy(rootMac, rootAddr->addr, 6);
                rootMacKnown = true;
                pushEventLog(event_id, rootAddr->addr);
            } else {
                pushEventLog(event_id);
            }
            break;
        }

        case MESH_EVENT_CHANNEL_SWITCH:
            pushEventLog(event_id);
            break;

        default:
            pushEventLog(event_id);
            break;
    }
}

// ====== Mesh 接收任务 ======
void MeshComm::meshReceiveTask(void *arg) {
    mesh_addr_t from;
    mesh_data_t data;
    static uint8_t rxBuffer[MESH_RX_BUFFER_SIZE];
    int flag = 0;

    Debug::println(F("[MESH] receive task started"));

    while (true) {
        data.data = rxBuffer;
        data.size = MESH_RX_BUFFER_SIZE;
        flag = 0;

        esp_err_t err = esp_mesh_recv(&from, &data, portMAX_DELAY, &flag, NULL, 0);
        if (err == ESP_OK && data.size > 0 && data.size < MESH_RX_BUFFER_SIZE) {
            // 转发到主循环队列
            MeshMessage msg;
            memcpy(msg.fromMac, from.addr, 6);
            int copyLen = data.size;
            if (copyLen >= MESH_RX_BUFFER_SIZE) copyLen = MESH_RX_BUFFER_SIZE - 1;
            memcpy(msg.json, data.data, copyLen);
            msg.json[copyLen] = '\0';
            msg.length = copyLen;

            if (msgQueue != nullptr) {
                // 等待 100ms：主循环每次 update 会清空队列，正常情况不会满。
                // 队列满时阻塞 100ms 而不是立即丢弃，避免业务消息丢失。
                xQueueSend((QueueHandle_t)msgQueue, &msg, pdMS_TO_TICKS(100));
            }
        }
    }
}

// Rebuild only the cabinet's Mesh stack when a parent association survives a
// Root reboot but the application route does not. UART0 and all cabinet
// business state remain online throughout this recovery.
bool MeshComm::restartCabinetMeshStack() {
    if (isRootNode) return false;

    Debug::println(F("[MESH] rebuilding cabinet Mesh stack; UART0 stays online"));

    if (meshReceiveTaskHandle != nullptr) {
        vTaskDelete(meshReceiveTaskHandle);
        meshReceiveTaskHandle = nullptr;
    }

    // Stop self-organization before deinit so no scan/parent callback races
    // the new stack. These calls are deliberately outside the Mesh event task.
    esp_err_t organizeErr = esp_mesh_set_self_organized(false, false);
    esp_err_t stopErr = esp_mesh_stop();
    esp_event_handler_unregister(MESH_EVENT, ESP_EVENT_ANY_ID, &MeshComm::meshEventHandler);
    esp_err_t deinitErr = esp_mesh_deinit();

    meshStarted = false;
    meshConnected = false;
    meshLayer = 0;
    childCount = 0;
    memset(meshParentMac, 0, sizeof(meshParentMac));
    memset(rootMac, 0, sizeof(rootMac));
    rootMacKnown = false;
    registeredWithRoot = false;
    if (msgQueue != nullptr) xQueueReset((QueueHandle_t)msgQueue);
    if (eventLogQueue != nullptr) xQueueReset((QueueHandle_t)eventLogQueue);

    Debug::printf("[MESH] stack teardown organize=%s stop=%s deinit=%s\n",
                  esp_err_to_name(organizeErr), esp_err_to_name(stopErr),
                  esp_err_to_name(deinitErr));
    delay(100);

    bool ok = MeshComm::initMesh();
    unsigned long now = millis();
    MeshComm::lastHeartbeatTime = phasedLastSend(
        now, MeshComm::meshSelfMac, MESH_HEARTBEAT_INTERVAL);
    MeshComm::unansweredHeartbeatSince = 0;
    MeshComm::rootResponseTimedOut = false;
    MeshComm::lastReconnectTime = 0;
    MeshComm::reconnectAttempt = 0;
    MeshComm::lastRegisterAttemptTime = 0;
    parentDisconnectedSince = ok ? now : 0;
    rootUnreachableSince = 0;
    lastRootRecoveryTime = 0;
    lastForcedReconnectTime = now;

    Debug::printf("[MESH] cabinet Mesh stack rebuild %s\n", ok ? "started" : "failed");
    return ok;
}

// ====== 主循环更新 ======
void MeshComm::update() {
    unsigned long now = millis();

    // Mesh 接收队列
    // IMPORTANT: binary app envelopes contain embedded 0x00 (LE integers).
    // Never construct Arduino String from raw bytes without length — it truncates.
    if (msgQueue != nullptr) {
        MeshMessage msg;
        int processed = 0;
        while (processed < MESH_RX_PER_UPDATE &&
               xQueueReceive((QueueHandle_t)msgQueue, &msg, 0) == pdTRUE) {
            processed++;
            uint16_t n = msg.length;
            if (n >= MESH_RX_BUFFER_SIZE) n = MESH_RX_BUFFER_SIZE - 1;
            // Copy into a length-preserving String (ESP32 Arduino String has
            // String(const char*, unsigned int) constructor).
            String raw((const char *)msg.json, n);
            processReceivedMessage(msg.fromMac, raw);
        }
    }

    ReliableTx::update();

    // 注：原主循环 STA 守卫（每 10s 调 esp_wifi_scan_stop / set_config / disconnect）
    // 已移除。它在 self-organized 启用后调用 WiFi API，违反 Espressif 官方警告，
    // 实测导致柜子频繁 PARENT_DISCONNECTED reason=202 (MESH_INTERNAL)。
    // 纯 Mesh 下 cfg.router.ssid_len=0 已让协议栈不扫外部热点，无需应用层干预。
    // WiFi.setAutoReconnect(false) 已在 initMesh() 的 esp_mesh_start() 之前设置。

    // 柜子：始终维护 UART0 主机协议口（Debug 或 Mesh 都开）
    if (!isRootNode && debugUartReady) {
        updateUartHost();
    }

    if (!meshStarted) {
        return;
    }

    // ====== Mesh mode ======
    if (!isRootNode && !meshConnected) {
        if (parentDisconnectedSince == 0) {
            parentDisconnectedSince = now;
        }

        // esp_mesh_set_self_organized(true, true) owns scanning and parent
        // selection. Repeatedly calling set_self_organized + esp_mesh_connect
        // here creates AUTH_EXPIRE/AUTH_FAIL loops and leaks WiFi/Mesh buffers.
        // Only report the passive wait; the stack reconnects by itself.
        if (lastReconnectTime == 0 || now - lastReconnectTime >= 30000UL) {
            lastReconnectTime = now;
            Debug::printf("[MESH] waiting for self-organized parent, offline_ms=%lu\n",
                          now - parentDisconnectedSince);
        }

        if (now - parentDisconnectedSince >= MESH_RESTART_AFTER_MS) {
            Debug::println(F("[MESH] parent unavailable for 180s; self-organized scan continues (UART0 stays online)"));
            parentDisconnectedSince = now;
            lastReconnectTime = 0;
        }
    }

    // A parent association alone is insufficient: the cabinet must receive
    // Root application traffic.  Flush a stuck upstream queue, re-register,
    // and finally force a fresh parent association if ACKs remain absent.
    if (!isRootNode && meshConnected && rootResponseTimedOut) {
        if (rootUnreachableSince == 0) rootUnreachableSince = now;

        if (lastRootRecoveryTime == 0 ||
            now - lastRootRecoveryTime >= ROOT_RECOVERY_INTERVAL_MS) {
            lastRootRecoveryTime = now;
            esp_err_t flushErr = esp_mesh_flush_upstream_packets();
            registeredWithRoot = false;
            meshRecoveryCount++;
            Debug::printf("[MESH] Root link recovery: flush=%s send_fail=%u queue_full=%u\n",
                          esp_err_to_name(flushErr), meshSendFailureCount,
                          meshQueueFullCount);
        }

        // 兜底机制：仅当 Root 应用层持续 30s 无响应、
        // 且 Mesh 协议栈自愈失败时，原地重建柜子的 Mesh 栈。Root 重启后旧的
        // parent association 可能仍显示 connected，但上行路由已经失效；单纯
        // disconnect/reconnect 无法可靠清掉该状态。UART0 不参与重建，始终在线。
        if (meshConnected &&
            now - rootUnreachableSince >= FORCE_REASSOC_AFTER_MS &&
            (lastForcedReconnectTime == 0 ||
             now - lastForcedReconnectTime >= FORCE_REASSOC_AFTER_MS)) {
            lastForcedReconnectTime = now;
            meshRecoveryCount++;
            restartCabinetMeshStack();
            return;
        }

        if (now - rootUnreachableSince >= MESH_RESTART_AFTER_MS) {
            Debug::println(F("[MESH] Root application link unavailable for 180s; self-organized recovery continues"));
            rootUnreachableSince = now;
        }
    }

    // REGISTER：仅 Mesh 协议层已连上时发，避免 PARENT_DISCONNECTED 期间 REGISTER 风暴
    if (!isRootNode && meshConnected) {
        if (!rootMacKnown) {
            meshLayer = esp_mesh_get_layer();
            if (meshLayer <= 1 &&
                (meshParentMac[0]|meshParentMac[1]|meshParentMac[2]|
                 meshParentMac[3]|meshParentMac[4]|meshParentMac[5]) != 0) {
                memcpy(rootMac, meshParentMac, 6);
                rootMacKnown = true;
                Debug::printf("[MESH] root learned from parent: %s\n",
                              macToString(rootMac).c_str());
            }
        }
        if (!registeredWithRoot &&
            (lastRegisterAttemptTime == 0 ||
             now - lastRegisterAttemptTime >= REGISTER_RETRY_INTERVAL_MS)) {
            DeviceConfig cfg;
            Storage::loadDeviceConfig(cfg);
            MemPool::noteHeapSample();
            String selfMac = macToString(meshSelfMac);
            String data = "{\"device_name\":\"" + cfg.device_name +
                          "\",\"is_root\":false,\"firmware_version\":\"" FIRMWARE_VERSION "\","
                          "\"role\":\"cabinet\"," +
                          "\"mesh_mac\":\"" + selfMac + "\"," +
                          "\"mesh_layer\":" + String(meshLayer) + "," +
                          "\"free_heap\":" + String(MemPool::freeInternalHeap()) + "," +
                          "\"free_psram\":" + String(MemPool::freePsram()) + "," +
                          "\"min_free_heap\":" + String(MemPool::minFreeInternalHeap()) + "," +
                          "\"largest_free_block\":" + String(MemPool::largestFreeBlock()) + "," +
                          "\"mesh_send_failures\":" + String(meshSendFailureCount) + "," +
                           "\"mesh_queue_full\":" + String(meshQueueFullCount) + "," +
                           "\"mesh_recoveries\":" + String(meshRecoveryCount) + "," +
                           "\"perm_version\":" + String(cfg.perm_version) + "}";
            lastRegisterAttemptTime = now;
            registeredWithRoot = sendControlAppToMesh(
                CMD_REGISTER, appNextMsgId(),
                (const uint8_t *)data.c_str(), (uint16_t)data.length());
            if (registeredWithRoot) {
                Debug::println(F("[MESH] cabinet REGISTER sent to Root"));
            }
        }
    }

    // HEARTBEAT：二进制应用信封（packHeartbeat），Root 回复 CMD_HEARTBEAT_ACK。
    // 连续 7s 无任何 Root 下行消息时，应用层判为不可达并触发 REGISTER 重建。
    if (!isRootNode && meshConnected && meshStarted &&
        now - lastHeartbeatTime >= MESH_HEARTBEAT_INTERVAL) {
        lastHeartbeatTime = now;
        MemPool::noteHeapSample();
        uint8_t hbPl[24];
        int hbLen = packHeartbeat(hbPl, (int)sizeof(hbPl),
                                  MemPool::freeInternalHeap(),
                                  MemPool::freePsram(),
                                  (uint16_t)MemPool::minFreeInternalHeap(),
                                  (uint8_t)meshLayer,
                                  0,
                                  (uint16_t)meshSendFailureCount,
                                  (uint16_t)meshQueueFullCount,
                                  (uint16_t)meshRecoveryCount);
        if (hbLen > 0) {
            sendControlAppToMesh(CMD_HEARTBEAT, appNextMsgId(),
                                 hbPl, (uint16_t)hbLen);
        }
        // 无论本次 send 是否成功都启动超时计时：持续发送失败本身就说明
        // Root 应用链路不可达，不能继续仅凭 parent association 报在线。
        if (unansweredHeartbeatSince == 0) {
            unansweredHeartbeatSince = now;
        } else if (now - unansweredHeartbeatSince >= MESH_ROUTE_TIMEOUT_MS) {
            if (!rootResponseTimedOut) {
                Debug::println(F("[MESH] Root heartbeat ACK timeout; re-registering"));
                // Spread a Root-reboot recovery burst across the full REGISTER
                // interval. With 100 cabinets this avoids one synchronized wave.
                lastRegisterAttemptTime = phasedLastSend(
                    now, meshSelfMac, REGISTER_RETRY_INTERVAL_MS);
            }
            rootResponseTimedOut = true;
            if (rootUnreachableSince == 0) {
                rootUnreachableSince = now;
            }
            registeredWithRoot = false;
            // 限制 REGISTER 重试为每个超时窗口一次，避免主循环发送风暴。
            unansweredHeartbeatSince = now;
        }
    }
}

// ====== 发送消息 ======
bool MeshComm::sendMessage(const String &cmd, const String &dataJson,
                           const String &msgId) {
    // Prefer binary app envelope; payload is the data object (JSON or empty {}).
    uint16_t cmdId = appCmdIdFromName(cmd.c_str());
    if (cmdId != 0) {
        uint16_t mid = 0;
        if (msgId.length() > 0) {
            mid = (uint16_t)msgId.toInt();
        }
        if (mid == 0) mid = appNextMsgId();

        uint8_t flags = 0;
        if (cmdId == CMD_ACK || cmdId == CMD_HEARTBEAT_ACK || cmdId == CMD_SYNC_ACK) {
            flags |= APP_FLAG_IS_ACK;
        }
        if (cmdId == CMD_ERROR) flags |= APP_FLAG_IS_ERROR;

        const String &data = dataJson.length() > 0 ? dataJson : String("{}");
        if (data.length() > APP_MAX_PAYLOAD) {
            // Mesh MTU cannot carry oversized single payloads. Caller must use PART.
            Debug::printf("[MESH] oversized app payload cmd=%s len=%u dropped (use PART)\n",
                          cmd.c_str(), (unsigned)data.length());
            return false;
        }
        bool previousCorrelated = s_routeCorrelatedSend;
        s_routeCorrelatedSend = msgId.length() > 0 ||
                                s_activeIngressRoute != CAB_ROUTE_AUTO;
        bool ok = sendApp(cmdId, mid, flags,
                          (const uint8_t *)data.c_str(), (uint16_t)data.length(), nullptr);
        s_routeCorrelatedSend = previousCorrelated;
        return ok;
    }

    // Unknown cmd string: do not emit legacy full JSON.
    Debug::printf("[MESH] unknown cmd name '%s' dropped (register in cmd_ids)\n", cmd.c_str());
    return false;
}

bool MeshComm::sendRaw(const String &json) {
    if (isRootNode) return false; // Root uplink uses MeshBridge

    // Legacy JSON is retained only for compatibility. Synchronous UART0
    // responses still follow the active request; autonomous traffic prefers
    // Mesh and uses UART0 only when the Mesh application link is unavailable.
    CabinetRoute route = s_activeIngressRoute;
    if (route == CAB_ROUTE_UART0 ||
        (route == CAB_ROUTE_AUTO && !isMeshConnected() && isUartHostConnected())) {
        return uartHostSendRaw(json);
    }
    if (!isMeshConnected()) {
        Debug::println(F("[MESH] legacy send failed: no active Mesh/UART0 route"));
        return false;
    }

    // Root MAC is retained for diagnostics, but a cabinet-to-root send must
    // use to=NULL.  ESP-MESH then follows the current upstream route even
    // after a parent/root change.
    if (!rootMacKnown) {
        if (esp_mesh_get_layer() <= 1) {
            mesh_addr_t parent;
            if (esp_mesh_get_parent_bssid(&parent) == ESP_OK) {
                memcpy(rootMac, parent.addr, 6);
                rootMacKnown = true;
            }
        }
    }

    if (json.length() >= MESH_RX_BUFFER_SIZE) {
        Debug::printf("[MESH] payload too large for Mesh MTU: %u\n",
                      (unsigned)json.length());
        return false;
    }
    size_t copyLen = json.length();
    if (copyLen >= MESH_RX_BUFFER_SIZE) copyLen = MESH_RX_BUFFER_SIZE - 1;
    static uint8_t sendBuf[MESH_RX_BUFFER_SIZE];
    memcpy(sendBuf, json.c_str(), copyLen);
    mesh_data_t data;
    data.data = sendBuf;
    data.size = (uint16_t)copyLen;
    data.proto = MESH_PROTO_JSON;
    data.tos = MESH_TOS_P2P;
    return boundedMeshSend(nullptr, &data, 0) == ESP_OK;
}

bool MeshComm::sendAppRawToUart(const uint8_t *appMsg, uint16_t len) {
    if (appMsg == nullptr || len == 0) return false;
    if (!debugUartReady) return false;
    uint8_t *frameBuf = MemPool::frameTxBuf();
    size_t poolSize = MemPool::frameTxBufSize();
    if (frameBuf == nullptr) return false;
    int cap = ProtocolFrame::getEncodedCapacityBytes(len);
    if (cap < 0 || (size_t)cap > poolSize) {
        Debug::println(F("[MESH] UART0 binary frame exceeds TX pool"));
        return false;
    }
    int frameLen = ProtocolFrame::encodeBytes(appMsg, len, frameBuf, (int)poolSize);
    if (frameLen < 0) return false;
    return SerialUplink::write(frameBuf, (size_t)frameLen);
}

bool MeshComm::sendControlAppToMesh(uint16_t cmdId, uint16_t msgId,
                                    const uint8_t *payload, uint16_t payloadLen) {
    if (isRootNode || !meshStarted || !meshConnected) return false;

    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    String selfMac = macToString(meshSelfMac);
    uint8_t *scratch = MemPool::meshTxScratch();
    size_t scratchSize = MemPool::meshTxScratchSize();
    if (scratch == nullptr) return false;

    int encoded = appEncode(
        scratch, (int)scratchSize, cmdId, msgId, 0, 0,
        cfg.device_id.c_str(), selfMac.c_str(), payload, payloadLen, 0);
    if (encoded < 0) return false;

    // REGISTER/HEARTBEAT repair the Root application route. They must use the
    // associated Mesh transport even while rootResponseTimedOut is true.
    return sendAppRawToMesh(scratch, (uint16_t)encoded);
}

bool MeshComm::sendAppRawToMesh(const uint8_t *appMsg, uint16_t len) {
    if (appMsg == nullptr || len == 0) return false;
    if (isRootNode) return false; // Root uplink uses MeshBridge
    if (!meshStarted || !meshConnected) return false;
    if (len >= MESH_RX_BUFFER_SIZE) {
        Debug::printf("[MESH] app payload too large: %u\n", (unsigned)len);
        return false;
    }

    const mesh_proto_t proto = MESH_PROTO_BIN;
    static uint8_t sendBuf[MESH_RX_BUFFER_SIZE];
    memcpy(sendBuf, appMsg, len);
    mesh_data_t data;
    data.data = sendBuf;
    data.size = len;
    data.proto = proto;
    data.tos = MESH_TOS_P2P;
    return boundedMeshSend(nullptr, &data, 0) == ESP_OK;
}

// Binary app envelope -> original request route. Device-originated traffic
// uses Mesh as primary and UART0 only when Mesh is unavailable and a host has
// already proved it can speak the protocol.
bool MeshComm::sendAppRaw(const uint8_t *appMsg, uint16_t len) {
    if (appMsg == nullptr || len == 0 || isRootNode) return false;

    uint16_t msgId = 0;
    AppMessageView view;
    if (appDecode(appMsg, (int)len, view)) msgId = view.msg_id;
    bool isAckOrError = appDecode(appMsg, (int)len, view) &&
                        (view.flags & (APP_FLAG_IS_ACK | APP_FLAG_IS_ERROR));
    CabinetRoute route = (s_routeCorrelatedSend || isAckOrError ||
                           s_activeIngressRoute != CAB_ROUTE_AUTO)
        ? selectReplyRoute(msgId) : CAB_ROUTE_AUTO;

    if (route == CAB_ROUTE_UART0) {
        cacheResponse(route, appMsg, len);
        return sendAppRawToUart(appMsg, len);
    }
    if (route == CAB_ROUTE_MESH) {
        cacheResponse(route, appMsg, len);
        return sendAppRawToMesh(appMsg, len);
    }

    if (isMeshConnected()) {
        cacheResponse(CAB_ROUTE_MESH, appMsg, len);
        return sendAppRawToMesh(appMsg, len);
    }
    if (isUartHostConnected()) {
        cacheResponse(CAB_ROUTE_UART0, appMsg, len);
        return sendAppRawToUart(appMsg, len);
    }
    return false;
}

bool MeshComm::sendToNodeApp(const uint8_t *mac, const uint8_t *appMsg, uint16_t len) {
    if (!meshStarted || !isRootNode || mac == nullptr || appMsg == nullptr || len == 0) {
        return false;
    }
    if (len >= MESH_RX_BUFFER_SIZE) {
        Debug::printf("[MESH] root app payload too large: %u\n", (unsigned)len);
        return false;
    }
    mesh_addr_t dest;
    memcpy(dest.addr, mac, 6);
    // Copy to static buffer so caller buffers can be reused immediately.
    static uint8_t sendBuf[MESH_RX_BUFFER_SIZE];
    memcpy(sendBuf, appMsg, len);
    mesh_data_t data;
    data.data = sendBuf;
    data.size = (int)len;
    data.proto = MESH_PROTO_BIN;
    // Root downlink stays best-effort at the Mesh stack. P2P retransmission on
    // this IDF build retains heartbeat ACKs until the internal pool is exhausted
    // (ESP_ERR_MESH_NO_MEMORY). Application msg_id replay handles business retry.
    data.tos = MESH_TOS_DEF;
    // ESP-IDF requires FROMDS for Root -> internal-node traffic. P2P is the
    // route-search flag for peer sends from non-Root nodes and can strand the
    // downlink in the wrong Mesh queue when used by a fixed Root.
    esp_err_t err = boundedMeshSend(&dest, &data, MESH_DATA_FROMDS);
    if (err != ESP_OK) {
        Debug::printf("[MESH] sendToNodeApp failed: %s\n", esp_err_to_name(err));
        return false;
    }
    return true;
}

bool MeshComm::sendApp(uint16_t cmdId, uint16_t msgId, uint8_t flags,
                       const uint8_t *payload, uint16_t payloadLen,
                       const char *deviceIdOverride) {
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    const char *did = (deviceIdOverride != nullptr) ? deviceIdOverride : cfg.device_id.c_str();
    // source_id = STA MAC：上位机用 MAC 做节点唯一身份，device_id 仍作业务/路由名
    String selfMac = macToString(meshSelfMac);
    uint8_t *scratch = MemPool::meshTxScratch();
    size_t scratchSize = MemPool::meshTxScratchSize();
    if (scratch == nullptr) return false;
    const char *sourceId = cmdId == CMD_STATUS_RESPONSE ? nullptr : selfMac.c_str();
    int n = appEncode(scratch, (int)scratchSize, cmdId, msgId, 0, flags,
                      did, sourceId, payload, payloadLen, 0);
    if (n < 0) return false;
    return sendAppRaw(scratch, (uint16_t)n);
}

// Root 专用：向指定子节点发送消息
bool MeshComm::sendToNode(const uint8_t *mac, const String &json) {
    if (!meshStarted || !isRootNode) {
        Debug::println(F("[MESH] sendToNode failed: not Root or Mesh not started"));
        return false;
    }

    mesh_addr_t dest;
    memcpy(dest.addr, mac, 6);
    mesh_data_t data;
    if (json.length() >= MESH_RX_BUFFER_SIZE) {
        Debug::printf("[MESH] root payload too large for Mesh MTU: %u\n",
                      (unsigned)json.length());
        return false;
    }
    data.data = (uint8_t*)json.c_str();
    data.size = json.length();
    data.proto = MESH_PROTO_JSON;
    data.tos = MESH_TOS_DEF;

    esp_err_t err = boundedMeshSend(&dest, &data, MESH_DATA_FROMDS);
    if (err != ESP_OK) {
        Debug::printf("[MESH] sendToNode failed: %s\n", esp_err_to_name(err));
        return false;
    }
    return true;
}

// ====== 消息回调设置 ======
void MeshComm::setMessageCallback(MessageCallback cb) {
    msgCb = cb;
}

void MeshComm::setMeshMessageCallback(MeshMessageCallback cb) {
    meshMsgCb = cb;
}

void MeshComm::setPeerConnectionCallback(PeerConnectionCallback cb) {
    peerConnectionCb = cb;
}

// ====== 状态查询 ======
bool MeshComm::isConnected() {
    return isMeshConnected() || isUartHostConnected();
}

bool MeshComm::isMeshConnected() {
    return meshConnected && (isRootNode || !rootResponseTimedOut);
}

bool MeshComm::isUartHostReady() {
    return debugUartReady;
}

bool MeshComm::isUartHostConnected() {
    return !isRootNode && debugUartReady && debugHostSeen &&
           millis() - lastDebugHostRx < UART_HOST_TIMEOUT_MS;
}

WorkMode MeshComm::getMode() {
    return isRootNode ? Storage::loadWorkMode() : MODE_MESH;
}

bool MeshComm::isRoot() {
    return isRootNode;
}

int MeshComm::getMeshLayer() {
    return meshLayer;
}

String MeshComm::getMeshParentMac() {
    return macToString(meshParentMac);
}

String MeshComm::getMeshMac() {
    return macToString(meshSelfMac);
}

int MeshComm::getChildCount() {
    return childCount;
}

void MeshComm::triggerReconnect() {
    if (!isRootNode) {
        // State only: ESP-MESH owns parent selection. Do not reset the
        // 30-second passive-wait log timer on every failed scan event.
        reconnectAttempt = 0;
    }
}

int MeshComm::getCrcErrorCount() {
    return ProtocolFrame::getCrcErrorCount();
}

uint32_t MeshComm::getSendFailureCount() {
    return meshSendFailureCount;
}

uint32_t MeshComm::getQueueFullCount() {
    return meshQueueFullCount;
}

uint32_t MeshComm::getRecoveryCount() {
    return meshRecoveryCount;
}

uint32_t MeshComm::getDuplicateReplayCount() {
    return s_duplicateReplayCount;
}

int MeshComm::getLinkRssi() {
    if (!meshStarted) return -127;
    if (!isRootNode) {
        int rssi = -127;
        return esp_wifi_sta_get_rssi(&rssi) == ESP_OK ? rssi : -127;
    }

    wifi_sta_list_t stations = {};
    if (esp_wifi_ap_get_sta_list(&stations) != ESP_OK || stations.num == 0) {
        return -127;
    }
    int weakestRssi = stations.sta[0].rssi;
    for (int index = 1; index < stations.num; ++index) {
        if (stations.sta[index].rssi < weakestRssi) {
            weakestRssi = stations.sta[index].rssi;
        }
    }
    return weakestRssi;
}

int MeshComm::getApAssocExpireSeconds() {
    return meshStarted ? esp_mesh_get_ap_assoc_expire() : 0;
}

// ====== 柜子 UART0 主机协议口（与根节点 USB 上行同协议） ======
// 物理口：ESP32-S3 UART0 默认 U0TXD=GPIO43 / U0RXD=GPIO44
// 波特率：UPLINK_USB_BAUD（921600），帧：0xA5 0x5A + CRC16
// Mesh 模式下也常开，便于不经 Mesh 单柜联调上位机

static void markUartHostSeen() {
    debugHostSeen = true;
    lastDebugHostRx = millis();
}

static void uartHostHandlePlainTextProbe(uint8_t b) {
    static char line[16];
    static uint8_t pos = 0;
    if (b == '\r') return;
    if (b == '\n') {
        line[pos < sizeof(line) ? pos : (sizeof(line) - 1)] = 0;
        if (strcasecmp(line, "PING") == 0 || strcasecmp(line, "AT") == 0) {
            markUartHostSeen();
            SerialUplink::writeText("PONG\r\n");
        } else if (strcasecmp(line, "HELP") == 0) {
            markUartHostSeen();
            SerialUplink::writeText("OK CABINET_UART0_FRAME=HEX baud=921600 same_as_root\r\n");
        }
        pos = 0;
        return;
    }
    if (pos + 1 < sizeof(line) && b >= 0x20 && b < 0x7F) {
        line[pos++] = (char)b;
    } else {
        pos = 0;
    }
}

void MeshComm::uartHostAnnounceRegister() {
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    String selfMac = macToString(meshSelfMac);
    // 字段与根节点 REGISTER 对齐；source_id=MAC 供上位机做节点唯一键
    String data = "{\"device_name\":\"" + cfg.device_name +
                  "\",\"is_root\":false,\"firmware_version\":\"" FIRMWARE_VERSION "\","
                  "\"uplink\":\"uart0\",\"role\":\"cabinet\","
                  "\"mesh_mac\":\"" + selfMac + "\","
                  "\"perm_version\":" + String(cfg.perm_version) + ","
                  "\"sd_ready\":false}";

    uint8_t *scratch = MemPool::meshTxScratch();
    size_t scratchSize = MemPool::meshTxScratchSize();
    uint16_t msgId = appNextMsgId();
    int n = scratch == nullptr ? -1 :
        appEncode(scratch, (int)scratchSize, CMD_REGISTER, msgId, 0, 0,
                  cfg.device_id.c_str(), selfMac.c_str(),
                  (const uint8_t *)data.c_str(), (uint16_t)data.length(), 0);
    if (n <= 0) {
        Debug::println(F("[MESH] UART0 REGISTER binary encode failed"));
    } else if (!sendAppRawToUart(scratch, (uint16_t)n)) {
        Debug::println(F("[MESH] UART0 REGISTER frame send failed"));
    }
    lastDebugAnnounce = millis();
}

bool MeshComm::initUartHost() {
    // UART0 starts before esp_mesh_init(), so read the deterministic STA MAC
    // directly instead of announcing 00:00:00:00:00:00 on the first frame.
    esp_read_mac(meshSelfMac, ESP_MAC_WIFI_STA);
    ProtocolFrame::resetDecoder();
    debugUartReady = true;
    debugHostSeen = false;
    lastDebugAnnounce = 0;
    lastDebugHostRx = 0;

    // 与根节点 USB 上行一致：日志封成 LOG 帧，不打断协议解析
    Debug::setFraming(true);

    Debug::printf("[MESH] UART0 host ready baud=%d TX=43 RX=44 (same protocol as root USB)\n",
                  UPLINK_USB_BAUD);
    // 明文启动标记：与 ROOT_BOOT 同风格，上位机/串口助手可直接看见
    Serial.printf("\r\n[CABINET_BOOT] UART0-SERIAL ALIVE (GPIO43/44)\r\n");
    Serial.printf("\r\n[CABINET_BOOT] PROTOCOL READY; baud=%d; frame=A5 5A\r\n",
                  UPLINK_USB_BAUD);
    Serial.flush();

    uartHostAnnounceRegister();
    return true;
}

void MeshComm::updateUartHost() {
    if (!debugUartReady) return;

    uartHostProcessIncoming();

    unsigned long now = millis();
    if (debugHostSeen && now - lastDebugHostRx >= UART_HOST_TIMEOUT_MS) {
        debugHostSeen = false;
        lastDebugAnnounce = 0;
    }
    // 上位机未回协议前周期性 REGISTER（同根节点 announce）
    if (!debugHostSeen &&
        (lastDebugAnnounce == 0 ||
         now - lastDebugAnnounce >= DEBUG_ANNOUNCE_INTERVAL_MS)) {
        uartHostAnnounceRegister();
    }

}


// Convert legacy full JSON message into binary app envelope for UART0 host.
static int uartLegacyJsonToApp(const String &json, uint8_t *out, int outSize) {
    if (out == nullptr || outSize < APP_ENVELOPE_MIN || json.length() < 8) return -1;
    if (json[0] != '{') return -1;
    DynamicJsonDocument doc(json.length() + 512);
    if (deserializeJson(doc, json)) return -1;
    const char *cmd = doc["cmd"] | "";
    if (cmd[0] == '\0') return -1;
    uint16_t cmdId = appCmdIdFromName(cmd);
    if (cmdId == 0) return -1;
    uint16_t mid = 0;
    if (!doc["msg_id"].isNull()) {
        if (doc["msg_id"].is<const char*>() || doc["msg_id"].is<String>())
            mid = (uint16_t)atoi(doc["msg_id"] | "0");
        else
            mid = (uint16_t)(doc["msg_id"] | 0);
    }
    if (mid == 0) mid = appNextMsgId();
    const char *deviceId = doc["device_id"] | "";
    const char *sourceId = doc["source_device_id"] | "";
    if (sourceId[0] == '\0') sourceId = doc["data"]["mesh_mac"] | "";
    String dataPayload = "{}";
    if (!doc["data"].isNull()) {
        dataPayload = "";
        serializeJson(doc["data"], dataPayload);
        if (dataPayload.length() == 0) dataPayload = "{}";
    }
    if ((int)dataPayload.length() > (FRAGMENT_REASSEMBLY_BUF - 64)) return -1;
    uint8_t flags = 0;
    if (cmdId == CMD_ACK || cmdId == CMD_HEARTBEAT_ACK || cmdId == CMD_SYNC_ACK)
        flags |= APP_FLAG_IS_ACK;
    if (cmdId == CMD_ERROR) flags |= APP_FLAG_IS_ERROR;
    return appEncode(out, outSize, cmdId, mid, 0, flags,
                     deviceId[0] ? deviceId : nullptr,
                     sourceId[0] ? sourceId : nullptr,
                     (const uint8_t *)dataPayload.c_str(),
                     (uint16_t)dataPayload.length(), 0);
}

bool MeshComm::uartHostSendRaw(const String &raw) {
    if (!debugUartReady) {
        return false;
    }

    // Unified UART0 host path: always frame a binary app envelope.
    // If caller still passes legacy full JSON, convert first.
    const uint8_t *payload = (const uint8_t *)raw.c_str();
    int payloadLen = (int)raw.length();
    uint8_t stackApp[1600];
    uint8_t *heapApp = nullptr;
    bool looksJson = payloadLen > 0 && payload[0] == (uint8_t)'{';
    bool looksBin = payloadLen >= APP_ENVELOPE_MIN &&
                    payload[0] == APP_MAGIC_0 && payload[1] == APP_MAGIC_1;

    if (looksJson && !looksBin) {
        int n = uartLegacyJsonToApp(raw, stackApp, (int)sizeof(stackApp));
        if (n <= 0) {
            heapApp = (uint8_t *)malloc(FRAGMENT_REASSEMBLY_BUF);
            if (heapApp != nullptr)
                n = uartLegacyJsonToApp(raw, heapApp, FRAGMENT_REASSEMBLY_BUF);
        }
        if (n <= 0) {
            if (heapApp) free(heapApp);
            Serial.println(F("[MESH] UART0 refused legacy JSON (convert failed)"));
            return false;
        }
        if (heapApp != nullptr && n > (int)sizeof(stackApp)) {
            payload = heapApp;
            payloadLen = n;
        } else if (heapApp != nullptr && n > 0) {
            // prefer stack when small; free heap
            memcpy(stackApp, heapApp, n);
            free(heapApp);
            heapApp = nullptr;
            payload = stackApp;
            payloadLen = n;
        } else {
            payload = stackApp;
            payloadLen = n;
        }
    }

    int frameCapacity = ProtocolFrame::getEncodedCapacityBytes(payloadLen);
    if (frameCapacity < 0) {
        if (heapApp) free(heapApp);
        Serial.println(F("[MESH] UART0 message exceeds frame reassembly limit"));
        return false;
    }
    uint8_t *frameBuf = MemPool::frameTxBuf();
    size_t poolSize = MemPool::frameTxBufSize();
    bool heapOwned = false;
    if (frameBuf == nullptr || (size_t)frameCapacity > poolSize) {
        frameBuf = (uint8_t *)malloc((size_t)frameCapacity);
        if (frameBuf == nullptr) {
            if (heapApp) free(heapApp);
            Serial.println(F("[MESH] UART0 frame buffer allocation failed"));
            return false;
        }
        heapOwned = true;
        poolSize = (size_t)frameCapacity;
    }
    int frameLen = ProtocolFrame::encodeBytes(payload, payloadLen, frameBuf, (int)poolSize);
    if (frameLen < 0) {
        Serial.println(F("[MESH] UART0 send: frame encode failed"));
        if (heapOwned) free(frameBuf);
        if (heapApp) free(heapApp);
        return false;
    }
    bool ok = SerialUplink::write(frameBuf, (size_t)frameLen);
    if (heapOwned) free(frameBuf);
    if (heapApp) free(heapApp);
    return ok;
}

void MeshComm::uartHostProcessIncoming() {
    // decodeBytes avoids 0x00 truncation when host speaks binary app envelopes.
    static uint8_t payloadBuf[FRAGMENT_REASSEMBLY_BUF];
    while (Serial.available()) {
        uint8_t byte = Serial.read();
        uartHostHandlePlainTextProbe(byte);
        int outLen = 0;
        if (ProtocolFrame::decodeBytes(byte, payloadBuf, (int)sizeof(payloadBuf), outLen)) {
            markUartHostSeen();
            if (msgCb && outLen > 0) {
                String raw((const char *)payloadBuf, (unsigned int)outLen);
                AppMessageView view;
                uint16_t msgId = 0;
                uint16_t cmdId = 0;
                if (appDecode(payloadBuf, outLen, view)) {
                    msgId = view.msg_id;
                    cmdId = view.cmd_id;
                }
                rememberReplyRoute(msgId, CAB_ROUTE_UART0);
                if (replayCachedResponse(msgId, cmdId, CAB_ROUTE_UART0)) continue;
                CabinetRoute previousRoute = s_activeIngressRoute;
                uint16_t previousMsgId = s_activeIngressMsgId;
                uint16_t previousCmdId = s_activeIngressCmdId;
                s_activeIngressRoute = CAB_ROUTE_UART0;
                s_activeIngressMsgId = msgId;
                s_activeIngressCmdId = cmdId;
                msgCb(raw);
                s_activeIngressRoute = previousRoute;
                s_activeIngressMsgId = previousMsgId;
                s_activeIngressCmdId = previousCmdId;
            }
        }
    }
}

// ====== 处理收到的 Mesh 消息 ======
void MeshComm::processReceivedMessage(const uint8_t *fromMac, const String &json) {
    // Binary envelope or legacy JSON. Heartbeat traffic stays quiet in logs.
    // `json` may hold binary bytes (length-aware String); c_str() is OK only with length.
    bool quiet = false;
    AppMessageView binView;
    const uint8_t *raw = (const uint8_t *)json.c_str();
    int rawLen = (int)json.length();
    bool isBinary = rawLen >= APP_ENVELOPE_MIN && appDecode(raw, rawLen, binView);
    if (isBinary) {
        // High-frequency / response path stays quiet: USB LOG framing must never
        // run ahead of business uplink (STATUS_RESPONSE used to arrive after the
        // host timeout because Debug::printf blocked Serial TX first).
        quiet = (binView.cmd_id == CMD_HEARTBEAT ||
                 binView.cmd_id == CMD_HEARTBEAT_ACK ||
                 binView.cmd_id == CMD_ACK ||
                 binView.cmd_id == CMD_DEBUG_LOG ||
                 binView.cmd_id == CMD_STATUS_REPORT ||
                 binView.cmd_id == CMD_STATUS_RESPONSE ||
                 binView.cmd_id == CMD_REGISTER);
    } else {
        // Legacy JSON only - safe to scan as text
        quiet = json.indexOf("\"cmd\":\"HEARTBEAT\"") >= 0 ||
                json.indexOf("\"cmd\":\"HEARTBEAT_ACK\"") >= 0 ||
                json.indexOf("\"cmd\":\"ACK\"") >= 0 ||
                json.indexOf("\"cmd\":\"LOG\"") >= 0 ||
                json.indexOf("\"cmd\":\"STATUS_REPORT\"") >= 0 ||
                json.indexOf("\"cmd\":\"STATUS_RESPONSE\"") >= 0 ||
                json.indexOf("\"cmd\":\"REGISTER\"") >= 0;
    }

    if (isRootNode) {
        // Root: deliver to bridge FIRST (uplink + side-effects), log after.
        // Logging before meshMsgCb added multi-hundred-ms USB backpressure and
        // made STATUS_RESPONSE miss host 5s timeouts under load.
        if (meshMsgCb) {
            meshMsgCb(fromMac, json);
        }
        if (!quiet) {
            if (isBinary) {
                Debug::printf("[MESH] received app from %s: cmd=0x%04X (%s) len=%d\n",
                              macToString(fromMac).c_str(), binView.cmd_id,
                              appCmdName(binView.cmd_id) ? appCmdName(binView.cmd_id) : "?",
                              rawLen);
            } else {
                Debug::printf("[MESH] received message from %s len=%u\n",
                              macToString(fromMac).c_str(), (unsigned)rawLen);
            }
        }
    } else {
        // 任意一条来自 Root 的 Mesh 消息都能证明双向应用层链路可达。
        unansweredHeartbeatSince = 0;
        rootResponseTimedOut = false;
        rootUnreachableSince = 0;
        lastRootRecoveryTime = 0;
        lastForcedReconnectTime = 0;
        if (isBinary && replayCachedResponse(binView.msg_id, binView.cmd_id,
                                             CAB_ROUTE_MESH)) {
            return;
        }
        // 子节点：先处理业务应答（可能走 Mesh 回传），再打日志，避免 USB/UART0
        // 日志抢在 STATUS_RESPONSE 发送之前占用 TX 与主循环。
        if (msgCb) {
            uint16_t msgId = isBinary ? binView.msg_id : 0;
            // Legacy JSON often has no numeric msg_id; still pin reply to Mesh
            // for the duration of this callback via s_activeIngressRoute.
            if (msgId != 0) {
                rememberReplyRoute(msgId, CAB_ROUTE_MESH);
            }
            CabinetRoute previousRoute = s_activeIngressRoute;
            uint16_t previousMsgId = s_activeIngressMsgId;
            uint16_t previousCmdId = s_activeIngressCmdId;
            s_activeIngressRoute = CAB_ROUTE_MESH;
            s_activeIngressMsgId = msgId;
            s_activeIngressCmdId = isBinary ? binView.cmd_id : 0;
            msgCb(json);
            s_activeIngressRoute = previousRoute;
            s_activeIngressMsgId = previousMsgId;
            s_activeIngressCmdId = previousCmdId;
        }
        if (!quiet) {
            if (isBinary) {
                Debug::printf("[MESH] rx app cmd=0x%04X (%s) len=%d\n",
                              binView.cmd_id,
                              appCmdName(binView.cmd_id) ? appCmdName(binView.cmd_id) : "?",
                              rawLen);
            } else {
                Debug::printf("[MESH] rx legacy len=%u\n", (unsigned)rawLen);
            }
        }
    }
}

// ====== 辅助方法 ======
String MeshComm::macToString(const uint8_t *mac) {
    char buf[18];
    snprintf(buf, sizeof(buf), "%02X:%02X:%02X:%02X:%02X:%02X",
             mac[0], mac[1], mac[2], mac[3], mac[4], mac[5]);
    return String(buf);
}

void MeshComm::parseMacString(const String &str, uint8_t *mac) {
    // 支持 "XX:XX:XX:XX:XX:XX" 或 "XXXXXXXXXXXX" 格式
    if (str.length() == 17 && str[2] == ':') {
        // 带冒号格式
        for (int i = 0; i < 6; i++) {
            mac[i] = (uint8_t)strtol(str.substring(i * 3, i * 3 + 2).c_str(), NULL, 16);
        }
    } else if (str.length() == 12) {
        // 无分隔符格式
        for (int i = 0; i < 6; i++) {
            mac[i] = (uint8_t)strtol(str.substring(i * 2, i * 2 + 2).c_str(), NULL, 16);
        }
    } else {
        memset(mac, 0, 6);
    }
}
