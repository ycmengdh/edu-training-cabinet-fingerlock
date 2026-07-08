using System.Windows;

namespace FingerprintLockManager
{
    /// <summary>
    /// 应用程序入口
    /// 负责初始化数据库、启动 TCP 服务端（STA 模式监听）、绑定消息处理器业务事件，并显示登录窗口
    /// </summary>
    public partial class App : Application
    {
        /// <summary>TCP 服务端（STA 模式，上位机作为服务端监听 ESP32 连接）</summary>
        public static TcpServer TcpServer { get; } = new TcpServer();

        /// <summary>消息处理器（解析收到的消息并分发到业务事件）</summary>
        public static MessageHandler MessageHandler { get; } = new MessageHandler();

        // ===== 全局业务服务实例 =====
        public static AuthService AuthService { get; } = new AuthService();
        public static UserService UserService { get; } = new UserService();
        public static PermissionService PermissionService { get; } = new PermissionService();
        public static DeviceService DeviceService { get; } = new DeviceService();
        public static LogService LogService { get; } = new LogService();

        /// <summary>当前登录用户（登录成功后赋值）</summary>
        public static User? CurrentUser { get; set; }

        /// <summary>
        /// 应用启动：初始化数据库 -> 绑定消息事件 -> 启动 TCP 服务端 -> 显示登录窗口
        /// </summary>
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            // 1. 初始化数据库（自动建表并创建默认管理员 admin/admin123）
            try
            {
                new DatabaseService().Init(ConfigHelper.Current.DatabasePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"数据库初始化失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // 2. 绑定消息处理器业务事件
            WireUpMessageHandler();

            // 3. 启动 TCP 服务端（STA 模式监听）
            try
            {
                TcpServer.DeviceConnected += OnDeviceConnected;
                TcpServer.DeviceDisconnected += OnDeviceDisconnected;
                TcpServer.MessageReceived += OnMessageReceived;
                TcpServer.Start(ConfigHelper.Current.TcpPort);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"TCP 服务启动失败：{ex.Message}\n请在主界面后检查端口占用。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            // 4. 显示登录窗口
            var loginWindow = new LoginWindow();
            loginWindow.Show();
        }

        /// <summary>
        /// 应用退出：停止 TCP 服务端
        /// </summary>
        private void Application_Exit(object sender, ExitEventArgs e)
        {
            try
            {
                TcpServer.Stop();
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
            MessageHandler.OnFingerVerifyRequest += OnFingerVerifyRequest;
            MessageHandler.OnLogReport += OnLogReport;
        }

        /// <summary>设备连接回调（来自后台线程）</summary>
        private void OnDeviceConnected(DeviceClient device)
        {
            // 仅日志记录，UI 状态由 MainWindow 自行订阅 TcpServer 事件更新
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

        /// <summary>
        /// 指纹验证请求：查询用户权限并回复 AUTH_OK / AUTH_FAIL
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

                // 查询用户与权限
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

                // 验证成功：回复 AUTH_OK 并携带权限数组
                var data = new Dictionary<string, object>
                {
                    ["user_id"] = user.UserId,
                    ["user_name"] = user.Name,
                    ["permissions"] = permissions
                };
                var okMsg = Message.Create(Protocol.CmdAuthOk, deviceId, data);
                TcpServer.SendToDevice(deviceId, okMsg);

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

        /// <summary>发送验证失败消息</summary>
        private void SendAuthFail(string deviceId, string reason)
        {
            var data = new Dictionary<string, string> { ["reason"] = reason };
            var msg = Message.Create(Protocol.CmdAuthFail, deviceId, data);
            TcpServer.SendToDevice(deviceId, msg);
        }
    }
}
