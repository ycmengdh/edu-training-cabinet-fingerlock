using System.Windows;
using Newtonsoft.Json.Linq;

namespace FingerprintLockManager
{
    /// <summary>
    /// 应用程序入口
    /// 负责启动 Mesh 桥接器（默认 USB 串口链路，可配置切换）、
    /// 绑定消息处理器业务事件（含 ACK），并显示登录窗口。
    /// </summary>
    public partial class App : Application
    {
        /// <summary>Mesh 桥接器（统一管理 USB 串口 / TCP 客户端 / TCP 服务端三种链路）</summary>
        public static MeshBridge MeshBridge { get; } = new MeshBridge();

        /// <summary>消息处理器（解析收到的消息并分发到业务事件）</summary>
        public static MessageHandler MessageHandler { get; } = new MessageHandler();

        // ===== 全局业务服务实例 =====
        public static AuthService AuthService { get; } = new AuthService();
        public static UserService UserService { get; } = new UserService();
        public static ClassService ClassService { get; } = new ClassService();
        public static PermissionService PermissionService { get; } = new PermissionService();
        public static RolePermissionService RolePermissionService { get; } = new RolePermissionService();
        public static DeviceService DeviceService { get; } = new DeviceService();
        public static LogService LogService { get; } = new LogService();
        public static OperationLogService OperationLogService { get; } = new OperationLogService();
        public static CabinetSyncService CabinetSyncService { get; } = new CabinetSyncService();
        public static CommandService CommandService { get; } = new CommandService();
        public static SystemHealthService SystemHealthService { get; } = new SystemHealthService();

        /// <summary>SD 卡集中存储服务（通过 Mesh 与根节点 SD 卡通信）</summary>
        public static SdStorageService SdStorageService { get; } = new SdStorageService();

        /// <summary>指纹模板业务服务（采集-存储-分配解耦管理）</summary>
        public static FingerprintTemplateService FingerprintTemplateService { get; } = new FingerprintTemplateService();

        /// <summary>当前登录用户（登录成功后赋值）</summary>
        public static User? CurrentUser { get; set; }

        /// <summary>
        /// 应用启动：绑定消息事件 -> 启动 Mesh 桥接器 -> 请求根节点注册 -> 显示登录窗口
        /// </summary>
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // 1. 绑定消息处理器业务事件
            WireUpMessageHandler();

            // 2. 初始化本地缓存目录（即使 SD 可用，也用于写入镜像）
            try { LocalCacheService.Initialize(); } catch { }

            // 3. 订阅 SD 降级 / 恢复事件（SD 恢复后自动回传本地缓存）
            SdStorageService.StorageDegraded += OnStorageDegraded;
            SdStorageService.StorageRecovered += OnStorageRecovered;

            // 4. 启动 Mesh 桥接器（默认 USB 串口，可配置切换）
            try
            {
                MeshBridge.MessageReceived += OnMessageReceived;
                MeshBridge.DeviceConnected += OnDeviceConnected;
                MeshBridge.DeviceDisconnected += OnDeviceDisconnected;
                MeshBridge.ConnectionChanged += OnConnectionChanged;

                var transportConfig = ConfigHelper.Current.ToTransportConfig();
                MeshBridge.Start(transportConfig);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Mesh 链路启动失败：{ex.Message}\n请在登录页面打开“连接设置”检查链路配置。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // 5. 显示登录窗口
            var loginWindow = new LoginWindow();
            loginWindow.Show();
        }

        /// <summary>
        /// 应用退出：停止 Mesh 桥接器
        /// </summary>
        private void Application_Exit(object sender, ExitEventArgs e)
        {
            try
            {
                MeshBridge.Stop();
            }
            catch
            {
                // 退出时忽略异常
            }
        }

        /// <summary>
        /// 绑定消息处理器的业务事件
        /// </summary>
        private void WireUpMessageHandler()
        {
            MessageHandler.OnDeviceRegistered += OnDeviceRegistered;
            MessageHandler.OnRootDeviceRegistered += OnRootDeviceRegistered;
            MessageHandler.OnLogReport += OnLogReport;
            MessageHandler.OnAckReceived += OnAckReceived;
            MessageHandler.OnErrorReceived += OnErrorReceived;
            MessageHandler.OnFingerprintEnrollmentResult += OnFingerprintEnrollmentResult;
            MessageHandler.OnPermissionSyncResult += OnPermissionSyncResult;
            MessageHandler.OnConfigSaved += OnConfigSavedHandler;
        }

        /// <summary>设备连接回调（来自后台线程）</summary>
        private void OnDeviceConnected(DeviceClient device)
        {
            // 仅日志记录，UI 状态由 MainWindow 自行订阅 MeshBridge 事件更新
        }

        /// <summary>设备断开回调（来自后台线程）</summary>
        private void OnDeviceDisconnected(DeviceClient device)
        {
            try
            {
                if (!string.IsNullOrEmpty(device.DeviceId))
                {
                    // 在线状态由根节点根据 Mesh 状态写入 devices.json。
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>收到消息回调：交给 MessageHandler 分发</summary>
        private void OnMessageReceived(DeviceClient? device, Message msg)
        {
            try
            {
                MessageHandler.HandleMessage(device, msg);
            }
            catch
            {
                // 消息处理异常时忽略，避免影响接收循环
            }
        }

        /// <summary>设备注册：根节点已写入设备表，上位机只接收通知。</summary>
        private void OnDeviceRegistered(string deviceId, string deviceName)
        {
            System.Diagnostics.Debug.WriteLine($"[APP] device registered: {deviceId} {deviceName}");
        }

        /// <summary>根节点注册：记录根节点 ID 并初始化本地缓存目录（SD 不可用时由 RegisterRoot 触发 StorageDegraded）</summary>
        private void OnRootDeviceRegistered(string rootDeviceId, bool? storageReady)
        {
            try
            {
                // 始终确保本地缓存目录就绪；SD 不可用时即进入降级模式
                try { LocalCacheService.Initialize(); } catch { }

                SdStorageService.RegisterRoot(rootDeviceId, storageReady);
                MeshBridge.Send(rootDeviceId, Protocol.CmdTimeSync, new
                {
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                });
                System.Diagnostics.Debug.WriteLine(
                    $"[APP] 根节点已注册: {rootDeviceId}，SD={storageReady?.ToString() ?? "unknown"}");
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>日志上报：SD 可用时由根节点落 SD；SD 不可用时上位机写入本地缓存等待回传。</summary>
        private void OnLogReport(string deviceId, string logJson)
        {
            try
            {
                if (App.SdStorageService.IsAvailable)
                {
                    System.Diagnostics.Debug.WriteLine($"[APP] root persisted log report from {deviceId}");
                    return;
                }

                // SD 不可用：解析日志并写入本地缓存
                var log = ParseLogEntry(logJson, deviceId);
                if (log != null)
                {
                    LocalCacheService.AppendLog(log);
                    System.Diagnostics.Debug.WriteLine($"[APP] cached log from {deviceId}");
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>SD 进入降级模式：仅记录日志（UI 通过 MainWindow 定时刷新感知）</summary>
        private void OnStorageDegraded()
        {
            System.Diagnostics.Debug.WriteLine("[APP] SD 进入降级模式，启用本地缓存");
        }

        /// <summary>SD 恢复：异步回传本地缓存到 SD 卡（失败不影响主流程）</summary>
        private void OnStorageRecovered()
        {
            System.Diagnostics.Debug.WriteLine("[APP] SD 恢复，开始回传本地缓存");
            _ = Task.Run(UploadLocalCacheToSdAsync);
        }

        /// <summary>将本地缓存的业务表 / 日志 / 指纹模板回传到 SD 卡</summary>
        private static async Task UploadLocalCacheToSdAsync()
        {
            try
            {
                // 业务表：devices / users / classes / permissions / role_permissions
                string[] tables = { "devices", "users", "classes", "permissions", "role_permissions" };
                foreach (string table in tables)
                {
                    var arr = LocalCacheService.ReadTable(table);
                    if (arr == null) continue;
                    uint v = LocalCacheService.ReadTableVersion(table);
                    // 用本地版本号 - 1 作为 base_version（与 RootDataService.SaveArray 写入的 baseVersion + 1 对齐）
                    uint baseVersion = v > 0 ? v - 1 : 0;
                    await App.SdStorageService.SaveTableWithFallbackAsync(
                        table, arr.ToString(Newtonsoft.Json.Formatting.None), baseVersion);
                }

                // 日志：追加到 SD logs 表，完成后清空本地缓存
                var logs = LocalCacheService.ReadLogs();
                if (logs.Count > 0)
                {
                    try
                    {
                        App.LogService.AddLogs(logs);
                        LocalCacheService.ClearLogs();
                    }
                    catch
                    {
                        // 追加日志失败时保留本地缓存，等待下次回传
                    }
                }

                // 指纹模板：逐个上传
                foreach (var (userId, fingerIndex) in LocalCacheService.ListFpTemplates())
                {
                    var template = LocalCacheService.ReadFpTemplate(userId, fingerIndex);
                    if (template == null || template.Length == 0) continue;
                    bool ok = await App.SdStorageService.UploadFpTemplateWithFallbackAsync(
                        userId, fingerIndex, template);
                    if (ok)
                    {
                        // SD 上传成功后删除本地缓存（保留降级期间再次生成的模板）
                        try { LocalCacheService.DeleteFpTemplate(userId); } catch { }
                    }
                }
            }
            catch
            {
                // 回传失败不影响主流程
            }
        }

        /// <summary>从日志上报 JSON 解析 LogEntry（兼容单条/数组/字段命名差异）</summary>
        private static LogEntry? ParseLogEntry(string logJson, string deviceId)
        {
            if (string.IsNullOrWhiteSpace(logJson)) return null;
            try
            {
                JToken token = JToken.Parse(logJson);
                JObject? obj = token as JObject;
                if (obj == null && token is JArray arr && arr.Count > 0)
                    obj = arr.First as JObject;
                if (obj == null) return null;

                var log = new LogEntry
                {
                    Id = obj.Value<long?>("id") ?? obj.Value<long?>("log_seq") ?? 0,
                    DeviceId = obj.Value<string>("device_id") ?? deviceId ?? "",
                    UserId = obj.Value<string>("user_id") ?? "",
                    LockId = obj.Value<int?>("lock_id") ?? 0,
                    Action = obj.Value<string>("action") ?? "",
                    Result = obj.Value<string>("result") ?? "",
                    Reason = obj.Value<string>("reason") ?? ""
                };

                // 时间字段兼容 create_time / time / timestamp（unix 秒）
                string? timeStr = obj.Value<string>("create_time");
                if (!string.IsNullOrWhiteSpace(timeStr) && DateTime.TryParse(timeStr, out var dt))
                {
                    log.CreateTime = dt;
                }
                else
                {
                    long unix = obj.Value<long?>("time") ?? obj.Value<long?>("timestamp") ?? 0;
                    log.CreateTime = unix > 0
                        ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime
                        : DateTime.Now;
                }
                return log;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>ACK 应答：当前仅记录日志，可用于命令确认匹配</summary>
        private void OnAckReceived(string msgId, string result)
        {
            CommandService.HandleAck(msgId, result);
            // UI command state may consume this event; never perform a
            // synchronous root query from the transport receive thread.
            System.Diagnostics.Debug.WriteLine($"[APP] ACK {msgId}: {result}");
        }

        private void OnErrorReceived(string msgId, string errorCode, string message)
        {
            CommandService.HandleError(msgId, errorCode, message);
        }

        private void OnFingerprintEnrollmentResult(
            string msgId, FingerprintEnrollmentResult result)
        {
            CommandService.HandleFingerprintEnrollmentResult(msgId, result);
        }

        private void OnPermissionSyncResult(string deviceId, string msgId, string result)
        {
            CommandService.HandlePermissionSyncResult(deviceId, msgId, result);
        }

        /// <summary>链路建立后重新发现根节点；断线时立即结束所有 SD 请求。</summary>
        private void OnConnectionChanged(bool connected)
        {
            SdStorageService.HandleConnectionChanged(connected);
            CommandService.HandleConnectionChanged(connected);
            if (connected)
            {
                MeshBridge.Send("", Protocol.CmdRegister);
            }
        }

        /// <summary>配置保存成功：占位实现（V2.7 之前由独立 AP 配置窗口展示提示，现已去除该窗口）</summary>
        private void OnConfigSavedHandler(string deviceId)
        {
            // 占位：AP 配置功能已移除，无 UI 订阅此事件。保留以兼容协议层事件总线。
        }

    }
}
