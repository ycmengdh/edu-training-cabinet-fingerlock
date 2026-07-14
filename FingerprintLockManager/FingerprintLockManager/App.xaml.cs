using System.Windows;

namespace FingerprintLockManager
{
    /// <summary>
    /// 应用程序入口
    /// 负责初始化数据库、启动 Mesh 桥接器（默认 USB 串口链路，可配置切换）、
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
        public static PermissionService PermissionService { get; } = new PermissionService();
        public static RolePermissionService RolePermissionService { get; } = new RolePermissionService();
        public static DeviceService DeviceService { get; } = new DeviceService();
        public static LogService LogService { get; } = new LogService();

        /// <summary>SD 卡集中存储服务（通过 Mesh 与根节点 SD 卡通信）</summary>
        public static SdStorageService SdStorageService { get; } = new SdStorageService();

        /// <summary>当前登录用户（登录成功后赋值）</summary>
        public static User? CurrentUser { get; set; }

        /// <summary>
        /// 应用启动：初始化数据库 -> 绑定消息事件 -> 启动 Mesh 桥接器 -> 显示登录窗口
        /// </summary>
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // 1. 数据源为根节点 SD 卡，无需本地数据库初始化
            //    DataStore 在根节点注册成功后自动从 SD 卡加载（见 OnRootDeviceRegistered）

            // 2. 绑定消息处理器业务事件
            WireUpMessageHandler();

            // 3. 启动 Mesh 桥接器（默认 USB 串口，可配置切换）
            try
            {
                MeshBridge.MessageReceived += OnMessageReceived;
                MeshBridge.DeviceConnected += OnDeviceConnected;
                MeshBridge.DeviceDisconnected += OnDeviceDisconnected;

                var transportConfig = ConfigHelper.Current.ToTransportConfig();
                MeshBridge.Start(transportConfig);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Mesh 链路启动失败：{ex.Message}\n请在主界面后检查链路配置。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // 4. 显示登录窗口
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
                // 退出前将内存中未刷盘的数据写回 SD 卡
                _ = DataStore.Current.FlushAllAsync();
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
            MessageHandler.OnFingerVerifyRequest += OnFingerVerifyRequest;
            MessageHandler.OnLogReport += OnLogReport;
            MessageHandler.OnAckReceived += OnAckReceived;
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
                    DeviceService.UpdateDeviceStatus(device.DeviceId, false);
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>收到消息回调：交给 MessageHandler 分发</summary>
        private void OnMessageReceived(DeviceClient device, Message msg)
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

        /// <summary>设备注册：写入或更新设备表</summary>
        private void OnDeviceRegistered(string deviceId, string deviceName)
        {
            try
            {
                if (!string.IsNullOrEmpty(deviceId))
                {
                    DeviceService.RegisterDevice(deviceId, deviceName, "");
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>根节点注册：记录根节点 ID，并触发从 SD 卡全量加载数据到内存</summary>
        private void OnRootDeviceRegistered(string rootDeviceId)
        {
            try
            {
                SdStorageService.RootDeviceId = rootDeviceId;
                System.Diagnostics.Debug.WriteLine($"[APP] 根节点已注册: {rootDeviceId}，开始从 SD 卡加载数据");
                // 根节点就绪后，从 SD 卡全量加载数据到 DataStore 内存
                _ = DataStore.Current.LoadFromSdCardAsync();
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 指纹验证请求：查询用户最终权限（双层合并）并回复 AUTH_OK / AUTH_FAIL
        /// </summary>
        private void OnFingerVerifyRequest(string deviceId, string fingerprintIdStr)
        {
            try
            {
                // 解析指纹 ID
                if (!int.TryParse(fingerprintIdStr, out int fpId))
                {
                    SendAuthFail(deviceId, "指纹ID无效");
                    return;
                }

                // 查询用户与最终权限（角色默认 + 个人覆盖合并）
                var (user, permissions) = PermissionService.VerifyByFingerprint(fpId);

                if (user == null)
                {
                    SendAuthFail(deviceId, "指纹未注册");
                    // 记录失败日志
                    LogService.AddLog(new LogEntry
                    {
                        DeviceId = deviceId,
                        UserId = "",
                        LockId = 0,
                        Action = "verify",
                        Result = "fail",
                        Reason = "指纹未注册",
                        CreateTime = DateTime.Now
                    });
                    return;
                }

                // 验证成功：回复 AUTH_OK 并携带最终权限数组
                var data = new Dictionary<string, object>
                {
                    ["user_id"] = user.UserId,
                    ["user_name"] = user.Name,
                    ["permissions"] = permissions
                };
                var okMsg = Message.Create(Protocol.CmdAuthOk, deviceId, data);
                MeshBridge.SendToDevice(deviceId, okMsg);

                // 记录成功日志
                LogService.AddLog(new LogEntry
                {
                    DeviceId = deviceId,
                    UserId = user.UserId,
                    LockId = 0,
                    Action = "verify",
                    Result = "success",
                    Reason = "",
                    CreateTime = DateTime.Now
                });
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>日志上报：保存到数据库</summary>
        private void OnLogReport(string deviceId, string logJson)
        {
            try
            {
                var log = JsonHelper.Deserialize<LogEntry>(logJson);
                if (log == null) return;

                if (string.IsNullOrEmpty(log.DeviceId)) log.DeviceId = deviceId;
                LogService.AddLog(log);
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>ACK 应答：当前仅记录日志，可用于命令确认匹配</summary>
        private void OnAckReceived(string msgId, string result)
        {
            // 可在此根据 msgId 匹配待确认命令并更新 UI；当前仅占位
            try
            {
                LogService.AddLog(new LogEntry
                {
                    DeviceId = "",
                    UserId = "",
                    LockId = 0,
                    Action = "ack",
                    Result = result == Protocol.ErrOk ? "success" : "fail",
                    Reason = $"msg_id={msgId}, result={result}",
                    CreateTime = DateTime.Now
                });
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>配置保存成功：记录日志（具体提示由 DeviceConfigWindow 自行处理）</summary>
        private void OnConfigSavedHandler(string deviceId)
        {
            // 占位：DeviceConfigWindow 已订阅 OnConfigSaved 显示提示
        }

        /// <summary>发送验证失败消息</summary>
        private void SendAuthFail(string deviceId, string reason)
        {
            var data = new Dictionary<string, string> { ["reason"] = reason };
            var msg = Message.Create(Protocol.CmdAuthFail, deviceId, data);
            MeshBridge.SendToDevice(deviceId, msg);
        }
    }
}
