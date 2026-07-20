/**
 * mesh_comm.cpp - ESP-MESH 自组网通信层实现
 * 替换原 tcp_comm，支持 Root/子节点两种角色 + 调试模式
 * Root 节点：MESH_ROOT，桥接上行链路由 main.cpp 处理
 * 子节点：MESH_NODE，通过 esp_mesh_send 向 Root 发送消息
 * 调试模式：UART0 串口协议帧直连上位机（与根节点 USB 上行同协议）
 */
#include "mesh_comm.h"
#include "debug.h"
#include "storage.h"
#include "protocol_frame.h"
#include "mem_pool.h"
#include "app_protocol.h"
#include "reliable_tx.h"
// #include "mesh_bridge.h"  // Moved to main.cpp
#include <WiFi.h>
#include <esp_wifi.h>
#include <esp_mesh.h>
#include <esp_event.h>

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

// esp_mesh_send() is blocking unless MESH_DATA_NONBLOCK is supplied.  Keep
// every application send bounded: a congested Mesh TX queue must never stop
// the Arduino loop (heartbeats, lock ACKs and recovery all run there).
static const int MESH_SEND_RETRY_COUNT = 3;
static const TickType_t MESH_SEND_RETRY_DELAY = pdMS_TO_TICKS(20);
static const unsigned long REGISTER_RETRY_INTERVAL_MS = 5000UL;
static const unsigned long ROOT_RECOVERY_INTERVAL_MS = 15000UL;
// 强制重关联阈值引用配置常量（config_common.h: MESH_FORCE_REASSOC_MS=120s）。
static const unsigned long FORCE_REASSOC_AFTER_MS = MESH_FORCE_REASSOC_MS;
static const unsigned long MESH_RESTART_AFTER_MS = 180000UL;

static uint32_t meshSendFailureCount = 0;
static uint32_t meshQueueFullCount = 0;
static uint32_t meshRecoveryCount = 0;
static unsigned long parentDisconnectedSince = 0;
static unsigned long rootUnreachableSince = 0;
static unsigned long lastRootRecoveryTime = 0;
static unsigned long lastForcedReconnectTime = 0;

static bool isRetryableMeshSendError(esp_err_t err) {
    return err == ESP_ERR_MESH_QUEUE_FULL ||
           err == ESP_ERR_MESH_NO_MEMORY ||
           err == ESP_ERR_MESH_TIMEOUT;
}

static esp_err_t boundedMeshSend(const mesh_addr_t *to,
                                 const mesh_data_t *data, int flags) {
    esp_err_t err = ESP_FAIL;
    for (int attempt = 0; attempt < MESH_SEND_RETRY_COUNT; ++attempt) {
        err = esp_mesh_send(to, data, flags | MESH_DATA_NONBLOCK, NULL, 0);
        if (err == ESP_OK) return ESP_OK;
        if (err == ESP_ERR_MESH_QUEUE_FULL) meshQueueFullCount++;
        if (!isRetryableMeshSendError(err) || attempt + 1 >= MESH_SEND_RETRY_COUNT) {
            break;
        }
        vTaskDelay(MESH_SEND_RETRY_DELAY);
    }
    meshSendFailureCount++;
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
void       *MeshComm::msgQueue = nullptr;
void       *MeshComm::eventLogQueue = nullptr;

// 调试模式（UART0 协议帧）内部状态
static bool        debugUartReady = false;
static bool        debugHostSeen = false;
static unsigned long lastDebugAnnounce = 0;
static const unsigned long DEBUG_ANNOUNCE_INTERVAL_MS = 3000;

// ====== 初始化 ======
void MeshComm::init() {
    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    isRootNode = cfg.is_root;

    ProtocolFrame::init();

    if (isRootNode) {
        // Root：仅 Mesh（上位机上行由 MeshBridge 走 USB/AP/STA）
        Debug::println(F("[MESH] === Root Mesh init ==="));
        initMesh();
    } else if (cfg.work_mode == MODE_DEBUG) {
        // 柜子 Debug：只开 UART0 协议，不组 Mesh
        Debug::println(F("[MESH] === cabinet DEBUG: UART0 host only ==="));
        initUartHost();
    } else {
        // 柜子 Mesh：组网 + 常开 UART0（单柜直连上位机，协议同根节点）
        Debug::println(F("[MESH] === cabinet MESH + UART0 host ==="));
        initUartHost();
        initMesh();
    }

    lastHeartbeatTime = millis();
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
    bool needInfrastructureRouter =
        (devCfg.wifi_ssid.length() > 0) &&
        (!isRootNode || devCfg.uplink_mode == UPLINK_STA);
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
        // reason=201 MESH_NO_MEMORY + reason=106 关联超时。
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
    // Every participant must use the same Fixed Root setting. This disables
    // voting while still allowing child nodes to discover/select a parent.
    err = esp_mesh_fix_root(true);
    if (err != ESP_OK) {
        Debug::printf("[MESH] esp_mesh_fix_root(true) failed: %s\n",
                      esp_err_to_name(err));
        return false;
    }
    // softAP 关联超时：必须大于 HEARTBEAT 间隔 (60s)，否则空闲柜子会被
    // 踢掉导致 CHILD_DISCONNECTED/CONNECTED 抖动。设为 120s 留 60s 余量。
    // ESP-MESH 默认值 300s 偏长（故障柜子要等 5 分钟才被清理），120s 平衡。
    esp_mesh_set_ap_assoc_expire(120);

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
        WiFi.setAutoReconnect(false);
        Debug::println(F("[MESH] STA profile cleared before mesh start (pure mesh)"));
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

    // Safety net for any future call site that accidentally omits NONBLOCK.
    // This API must be configured before esp_mesh_start().
    // 官方建议环境差时 ≥5s，避免 esp_mesh_send 频繁超时导致 sendAppRaw failed。
    // 实际发送都用 MESH_DATA_NONBLOCK，不会阻塞主循环。
    err = esp_mesh_send_block_time(5000);
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
    xTaskCreatePinnedToCore(meshReceiveTask, "mesh_rx", 12288, NULL, 5, NULL, 0);

    esp_wifi_get_mac(WIFI_IF_STA, meshSelfMac);
    Debug::printf("[MESH] Mesh started ok, channel=%u, MAC=%s, root=%s\n",
                  channel, macToString(meshSelfMac).c_str(),
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
    while (xQueueReceive((QueueHandle_t)eventLogQueue, &entry, 0) == pdTRUE) {
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
                Debug::printf("[MESH] event: PARENT_DISCONNECTED parent=%s reason=%d childCount=%d\n",
                              macToString(entry.mac).c_str(),
                              entry.reason, entry.child_count);
                break;
            case MESH_EVENT_CHILD_CONNECTED:
                Debug::printf("[MESH] event: CHILD_CONNECTED child=%s (total %d)\n",
                              macToString(entry.mac).c_str(),
                              entry.child_count);
                break;
            case MESH_EVENT_CHILD_DISCONNECTED:
                Debug::printf("[MESH] event: CHILD_DISCONNECTED child=%s (remaining %d)\n",
                              macToString(entry.mac).c_str(),
                              entry.child_count);
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
            }
            triggerReconnect();
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

// ====== 主循环更新 ======
void MeshComm::update() {
    unsigned long now = millis();

    // Mesh 接收队列
    // IMPORTANT: binary app envelopes contain embedded 0x00 (LE integers).
    // Never construct Arduino String from raw bytes without length — it truncates.
    if (msgQueue != nullptr) {
        MeshMessage msg;
        while (xQueueReceive((QueueHandle_t)msgQueue, &msg, 0) == pdTRUE) {
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

    WorkMode mode = Storage::loadWorkMode();
    if (mode == MODE_DEBUG || !meshStarted) {
        return;
    }

    // ====== Mesh 模式 ======
    if (!isRootNode && !meshConnected) {
        if (parentDisconnectedSince == 0) {
            parentDisconnectedSince = now;
        }
        int delayIdx = reconnectAttempt;
        if (delayIdx >= 5) delayIdx = 4;
        if (now - lastReconnectTime >= (unsigned long)reconnectDelays[delayIdx]) {
            lastReconnectTime = now;
            reconnectAttempt++;
            // Self-organized Mesh normally reconnects by itself, but an
            // explicit bounded kick recovers a stalled parent-selection
            // state without requiring a cabinet power cycle.
            esp_err_t organizeErr = esp_mesh_set_self_organized(true, true);
            esp_err_t connectErr = esp_mesh_connect();
            meshRecoveryCount++;
            Debug::printf("[MESH] parent recovery try=%d interval=%d ms organize=%s connect=%s\n",
                          reconnectAttempt, reconnectDelays[delayIdx],
                          esp_err_to_name(organizeErr), esp_err_to_name(connectErr));
        }

        if (now - parentDisconnectedSince >= MESH_RESTART_AFTER_MS) {
            Debug::println(F("[MESH] parent unavailable for 180s; restarting cabinet"));
            delay(50);
            ESP.restart();
        }
    }

    // A parent association alone is insufficient: the cabinet must receive
    // Root application traffic.  Flush a stuck upstream queue, re-register,
    // and finally force a fresh parent association if ACKs remain absent.
    if (!isRootNode && rootResponseTimedOut) {
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

        // 兜底机制：仅当 Root 应用层长时间（MESH_FORCE_REASSOC_MS=120s）无响应、
        // 且 Mesh 协议栈自愈失败时，才强制断开 parent 重关联。正常 flap 由协议栈
        // 自行处理，不进这里。原 60s 偏激进，会打断协议栈自愈导致 flap 加剧。
        if (meshConnected &&
            now - rootUnreachableSince >= FORCE_REASSOC_AFTER_MS &&
            (lastForcedReconnectTime == 0 ||
             now - lastForcedReconnectTime >= FORCE_REASSOC_AFTER_MS)) {
            lastForcedReconnectTime = now;
            meshRecoveryCount++;
            Debug::println(F("[MESH] Root still silent; forcing parent reassociation"));
            esp_mesh_disconnect();
            meshConnected = false;
            if (parentDisconnectedSince == 0) parentDisconnectedSince = now;
            triggerReconnect();
        }

        if (now - rootUnreachableSince >= MESH_RESTART_AFTER_MS) {
            Debug::println(F("[MESH] Root application link unavailable for 180s; restarting cabinet"));
            delay(50);
            ESP.restart();
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
                          "\"mesh_recoveries\":" + String(meshRecoveryCount) + "}";
            lastRegisterAttemptTime = now;
            registeredWithRoot = sendMessage("REGISTER", data);
            if (registeredWithRoot) {
                Debug::println(F("[MESH] cabinet REGISTER sent to Root"));
            }
        }
    }

    // HEARTBEAT：二进制应用信封（packHeartbeat），Root 回复 CMD_HEARTBEAT_ACK。
    // 连续 30s 无任何 Root 下行消息时，应用层判为不可达并触发 REGISTER 重建。
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
            sendApp(CMD_HEARTBEAT, appNextMsgId(), 0, hbPl, (uint16_t)hbLen, nullptr);
        }
        // 无论本次 send 是否成功都启动超时计时：持续发送失败本身就说明
        // Root 应用链路不可达，不能继续仅凭 parent association 报在线。
        if (unansweredHeartbeatSince == 0) {
            unansweredHeartbeatSince = now;
        } else if (now - unansweredHeartbeatSince >= MESH_ROUTE_TIMEOUT_MS) {
            if (!rootResponseTimedOut) {
                Debug::println(F("[MESH] Root heartbeat ACK timeout; re-registering"));
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
            // Oversized data: fall back to legacy full JSON (rare; SD uses PART).
            DeviceConfig cfg;
            Storage::loadDeviceConfig(cfg);
            return sendRaw(ProtocolFrame::buildMessage(cmd, cfg.device_id, data, msgId));
        }
        return sendApp(cmdId, mid, flags,
                       (const uint8_t *)data.c_str(), (uint16_t)data.length(), nullptr);
    }

    DeviceConfig cfg;
    Storage::loadDeviceConfig(cfg);
    String json = ProtocolFrame::buildMessage(cmd, cfg.device_id, dataJson, msgId);
    return sendRaw(json);
}

bool MeshComm::sendRaw(const String &json) {
    // ====== 链路独立性原则 ======
    // Mesh 与 UART0 是两条物理上独立并行的链路：
    //   - UART0：柜子直连 PC 的调试/单柜协议口（MODE_DEBUG 模式独占）
    //   - Mesh：组网通讯，柜子经根节点转发到上位机（Mesh 模式独占）
    // 不做任何"Mesh 失败回退 UART0"的降级——那样只会让数据流向混乱、
    // 业务消息假性丢失、调试日志误导排查方向。Mesh 失败就让业务层感知
    // 失败，由调用方决定是否重试，绝不偷偷切链路。

    // 柜子 Debug 模式：UART0 链路独占（不组 Mesh）
    if (!isRootNode && Storage::loadWorkMode() == MODE_DEBUG) {
        return uartHostSendRaw(json);
    }

    if (isRootNode) {
        // Root 上行由 MeshBridge 负责
        return false;
    }

    // ====== Mesh 链路（柜子 Mesh 模式） ======
    if (!meshStarted) {
        Debug::println(F("[MESH] send failed: Mesh not started (no fallback to UART0)"));
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

    mesh_data_t data;
    if (json.length() >= MESH_RX_BUFFER_SIZE) {
        Debug::printf("[MESH] payload too large for Mesh MTU: %u\n",
                      (unsigned)json.length());
        return false;
    }
    static uint8_t sendBuf[MESH_RX_BUFFER_SIZE];
    size_t copyLen = json.length();
    if (copyLen >= MESH_RX_BUFFER_SIZE) copyLen = MESH_RX_BUFFER_SIZE - 1;
    memcpy(sendBuf, json.c_str(), copyLen);
    sendBuf[copyLen] = 0;
    data.data = sendBuf;
    data.size = (int)copyLen;
    data.proto = MESH_PROTO_JSON;
    data.tos = MESH_TOS_P2P;

    // Official ESP-MESH upstream form: to=NULL, flag=0. NONBLOCK is added by
    // boundedMeshSend().  Never use P2P with a remembered Root MAC here.
    esp_err_t err = boundedMeshSend(NULL, &data, 0);
    if (err != ESP_OK) {
        Debug::printf("[MESH] esp_mesh_send failed: %s (no fallback to UART0)\n",
                      esp_err_to_name(err));
        return false;
    }
    return true;
}

// Binary app envelope → Mesh upstream / UART0 host (no outer A5 on mesh).
bool MeshComm::sendAppRaw(const uint8_t *appMsg, uint16_t len) {
    if (appMsg == nullptr || len == 0) return false;

    if (!isRootNode && Storage::loadWorkMode() == MODE_DEBUG) {
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
        size_t sent = Serial.write(frameBuf, frameLen);
        Serial.flush();
        return sent == (size_t)frameLen;
    }

    if (isRootNode) return false; // Root uplink uses MeshBridge
    if (!meshStarted) return false;
    if (len >= MESH_RX_BUFFER_SIZE) {
        Debug::printf("[MESH] app payload too large: %u\n", (unsigned)len);
        return false;
    }

    static uint8_t sendBuf[MESH_RX_BUFFER_SIZE];
    memcpy(sendBuf, appMsg, len);
    mesh_data_t data;
    data.data = sendBuf;
    data.size = (int)len;
#ifdef MESH_PROTO_BIN
    data.proto = MESH_PROTO_BIN;
#else
    data.proto = MESH_PROTO_JSON;
#endif
    data.tos = MESH_TOS_P2P;
    esp_err_t err = boundedMeshSend(NULL, &data, 0);
    if (err != ESP_OK) {
        Debug::printf("[MESH] sendAppRaw failed: %s\n", esp_err_to_name(err));
        return false;
    }
    return true;
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
#ifdef MESH_PROTO_BIN
    data.proto = MESH_PROTO_BIN;
#else
    data.proto = MESH_PROTO_JSON;
#endif
    data.tos = MESH_TOS_P2P;
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
    int n = appEncode(scratch, (int)scratchSize, cmdId, msgId, 0, flags,
                      did, selfMac.c_str(), payload, payloadLen, 0);
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
    data.tos = MESH_TOS_P2P;

    // Root-to-internal-node traffic must be marked FROMDS.  The bounded
    // non-blocking retry prevents a missing cabinet from freezing Root.
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

// ====== 状态查询 ======
bool MeshComm::isConnected() {
    if (!isRootNode && debugUartReady && debugHostSeen) {
        return true;
    }
    if (!isRootNode && Storage::loadWorkMode() == MODE_DEBUG) {
        return debugUartReady;
    }
    return meshConnected && (isRootNode || !rootResponseTimedOut);
}

bool MeshComm::isUartHostReady() {
    return debugUartReady;
}

WorkMode MeshComm::getMode() {
    return Storage::loadWorkMode();
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
        // May be called from the sys_evt task: state only, no logging or Mesh
        // API calls here.  Recovery is performed from update() on loopTask.
        lastReconnectTime = 0;
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

// ====== 柜子 UART0 主机协议口（与根节点 USB 上行同协议） ======
// 物理口：ESP32-S3 UART0 默认 U0TXD=GPIO43 / U0RXD=GPIO44
// 波特率：UPLINK_USB_BAUD（921600），帧：0xA5 0x5A + CRC16
// Mesh 模式下也常开，便于不经 Mesh 单柜联调上位机

static void uartHostHandlePlainTextProbe(uint8_t b) {
    static char line[16];
    static uint8_t pos = 0;
    if (b == '\r') return;
    if (b == '\n') {
        line[pos < sizeof(line) ? pos : (sizeof(line) - 1)] = 0;
        if (strcasecmp(line, "PING") == 0 || strcasecmp(line, "AT") == 0) {
            Serial.print("PONG\r\n");
            Serial.flush();
        } else if (strcasecmp(line, "HELP") == 0) {
            Serial.print("OK CABINET_UART0_FRAME=HEX baud=921600 same_as_root\r\n");
            Serial.flush();
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
                  "\"sd_ready\":false}";
    // 优先二进制信封
    if (sendApp(CMD_REGISTER, appNextMsgId(), 0,
                (const uint8_t *)data.c_str(), (uint16_t)data.length(), nullptr)) {
        lastDebugAnnounce = millis();
        return;
    }
    String json = ProtocolFrame::buildMessage("REGISTER", cfg.device_id, data);
    uartHostSendRaw(json);
    lastDebugAnnounce = millis();
}

bool MeshComm::initUartHost() {
    ProtocolFrame::resetDecoder();
    debugUartReady = true;
    debugHostSeen = false;
    lastDebugAnnounce = 0;

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
    // 上位机未回协议前周期性 REGISTER（同根节点 announce）
    if (!debugHostSeen &&
        (lastDebugAnnounce == 0 ||
         now - lastDebugAnnounce >= DEBUG_ANNOUNCE_INTERVAL_MS)) {
        uartHostAnnounceRegister();
    }

    // Debug-only 模式心跳；Mesh 模式心跳走 Mesh 路径
    if (Storage::loadWorkMode() == MODE_DEBUG &&
        now - lastHeartbeatTime >= MESH_HEARTBEAT_INTERVAL) {
        lastHeartbeatTime = now;
        MemPool::noteHeapSample();
        String hbData = "{\"free_heap\":" + String(MemPool::freeInternalHeap()) +
                        ",\"free_psram\":" + String(MemPool::freePsram()) +
                        ",\"min_free_heap\":" + String(MemPool::minFreeInternalHeap()) +
                        ",\"largest_free_block\":" + String(MemPool::largestFreeBlock()) +
                        ",\"mesh_layer\":0,\"mesh_send_failures\":0,"
                        "\"mesh_queue_full\":0,\"mesh_recoveries\":0}";
        sendMessage("HEARTBEAT", hbData);
    }
}

bool MeshComm::uartHostSendRaw(const String &raw) {
    if (!debugUartReady) {
        return false;
    }

    int frameCapacity = ProtocolFrame::getEncodedCapacity(raw);
    if (frameCapacity < 0) {
        Serial.println(F("[MESH] UART0 message exceeds frame reassembly limit"));
        return false;
    }
    // Phase 0: static/PSRAM TX pool for common path; rare multi-fragment
    // oversize falls back to one-shot malloc (pool is FRAME_TX_POOL_SIZE).
    uint8_t *frameBuf = MemPool::frameTxBuf();
    size_t poolSize = MemPool::frameTxBufSize();
    bool heapOwned = false;
    if (frameBuf == nullptr || (size_t)frameCapacity > poolSize) {
        frameBuf = (uint8_t *)malloc((size_t)frameCapacity);
        if (frameBuf == nullptr) {
            Serial.println(F("[MESH] UART0 frame buffer allocation failed"));
            return false;
        }
        heapOwned = true;
        poolSize = (size_t)frameCapacity;
    }
    int frameLen = ProtocolFrame::encode(raw, frameBuf, (int)poolSize);
    if (frameLen < 0) {
        Serial.println(F("[MESH] UART0 send: frame encode failed"));
        if (heapOwned) free(frameBuf);
        return false;
    }

    size_t sent = Serial.write(frameBuf, frameLen);
    Serial.flush();
    if (heapOwned) free(frameBuf);
    return (sent == (size_t)frameLen);
}

void MeshComm::uartHostProcessIncoming() {
    // decodeBytes avoids 0x00 truncation when host speaks binary app envelopes.
    static uint8_t payloadBuf[FRAGMENT_REASSEMBLY_BUF];
    while (Serial.available()) {
        uint8_t byte = Serial.read();
        uartHostHandlePlainTextProbe(byte);
        int outLen = 0;
        if (ProtocolFrame::decodeBytes(byte, payloadBuf, (int)sizeof(payloadBuf), outLen)) {
            debugHostSeen = true;
            if (msgCb && outLen > 0) {
                String raw((const char *)payloadBuf, (unsigned int)outLen);
                msgCb(raw);
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
        quiet = (binView.cmd_id == CMD_HEARTBEAT || binView.cmd_id == CMD_HEARTBEAT_ACK);
    } else {
        // Legacy JSON only — safe to scan as text
        quiet = json.indexOf("\"cmd\":\"HEARTBEAT\"") >= 0 ||
                json.indexOf("\"cmd\":\"HEARTBEAT_ACK\"") >= 0;
    }
    if (!quiet) {
        if (isBinary) {
            Debug::printf("[MESH] received app from %s: cmd=0x%04X (%s) len=%d\n",
                          macToString(fromMac).c_str(), binView.cmd_id,
                          appCmdName(binView.cmd_id) ? appCmdName(binView.cmd_id) : "?",
                          rawLen);
        } else {
            Debug::printf("[MESH] received message from %s: %s\n",
                          macToString(fromMac).c_str(), json.c_str());
        }
    }

    if (isRootNode) {
        // Root 收到子节点消息：转发到上行链路由 main.cpp 处理
        if (meshMsgCb) {
            meshMsgCb(fromMac, json);
        }
    } else {
        // 任意一条来自 Root 的 Mesh 消息都能证明双向应用层链路可达。
        unansweredHeartbeatSince = 0;
        rootResponseTimedOut = false;
        rootUnreachableSince = 0;
        lastRootRecoveryTime = 0;
        lastForcedReconnectTime = 0;
        // 子节点收到 Root 下发的命令：交给消息处理器（二进制或 JSON 字符串）
        if (msgCb) {
            msgCb(json);
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
