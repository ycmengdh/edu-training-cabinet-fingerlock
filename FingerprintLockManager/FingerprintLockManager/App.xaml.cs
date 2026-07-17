using System.Windows;

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
        public static PermissionService PermissionService { get; } = new PermissionService();
        public static RolePermissionService RolePermissionService { get; } = new RolePermissionService();
        public static DeviceService DeviceService { get; } = new DeviceService();
        public static LogService LogService { get; } = new LogService();
        public static CabinetSyncService CabinetSyncService { get; } = new CabinetSyncService();
        public static CommandService CommandService { get; } = new CommandService();

        /// <summary>SD 卡集中存储服务（通过 Mesh 与根节点 SD 卡通信）</summary>
        public static SdStorageService SdStorageService { get; } = new SdStorageService();

        /// <summary>当前登录用户（登录成功后赋值）</summary>
        public static User? CurrentUser { get; set; }

        /// <summary>
        /// 应用启动：绑定消息事件 -> 启动 Mesh 桥接器 -> 请求根节点注册 -> 显示登录窗口
        /// </summary>
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // 1. 绑定消息处理器业务事件
            WireUpMessageHandler();

            // 2. 启动 Mesh 桥接器（默认 USB 串口，可配置切换）
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
                MessageBox.Show($"Mesh 链路启动失败：{ex.Message}\n请在主界面后检查链路配置。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // 3. 显示登录窗口
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

        /// <summary>根节点注册：记录根节点 ID，供 SD 卡集中存储服务定位</summary>
        private void OnRootDeviceRegistered(string rootDeviceId)
        {
            try
            {
                SdStorageService.RootDeviceId = rootDeviceId;
                System.Diagnostics.Debug.WriteLine($"[APP] 根节点已注册: {rootDeviceId}，SD 卡存储服务可用");
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>日志上报：根节点已先写入 SD，上位机不再落本地库。</summary>
        private void OnLogReport(string deviceId, string logJson)
        {
            System.Diagnostics.Debug.WriteLine($"[APP] root persisted log report from {deviceId}");
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

        /// <summary>配置保存成功：记录日志（具体提示由 DeviceConfigWindow 自行处理）</summary>
        private void OnConfigSavedHandler(string deviceId)
        {
            // 占位：DeviceConfigWindow 已订阅 OnConfigSaved 显示提示
        }

    }
}
