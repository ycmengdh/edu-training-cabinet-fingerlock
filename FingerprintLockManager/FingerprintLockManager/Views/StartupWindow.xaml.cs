using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FingerprintLockManager
{
    /// <summary>
    /// 启动页：选择组网U盘或柜机串口直连，再选择对应串口。
    /// 同步前备份本地主库到带时间戳快照；同步写入临时库，成功才替换主库，失败不覆盖本地。
    /// 同步失败重试 3 次；3 次均失败后提示拔插设备并重选串口，再失败则显示
    /// 「使用本地历史数据继续」按钮，用最近一份历史备份恢复后进入登录。
    /// </summary>
    public partial class StartupWindow : BorderlessWindow
    {
        private bool _busy;
        private bool _navigating;
        private bool _autoStarted;
        private const int WaitRootTimeoutMs = 20000;
        private const int MaxSyncAttempts = 3;
        private const int WaitDirectTimeoutMs = 8000;

        private bool IsDirectUart => string.Equals(ConfigHelper.Current.LinkMode, "Uart",
            StringComparison.OrdinalIgnoreCase);

        public StartupWindow()
        {
            InitializeComponent();
            bool uart = string.Equals(ConfigHelper.Current.LinkMode, "Uart",
                StringComparison.OrdinalIgnoreCase);
            UartModeButton.IsChecked = uart;
            MeshModeButton.IsChecked = !uart;
            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            App.MeshBridge.ConnectionChanged += OnConnectionChanged;
            App.MessageHandler.OnRootDeviceRegistered += OnRootRegistered;
            App.SdStorageService.StatusChanged += OnStorageStatusChanged;

            LoadPortList(GetPreferredPort());
            UpdateLinkStatus();

            // 串口列表非空时自动开始连接并同步
            if (IsSelectedPortAvailable() && !_autoStarted)
            {
                _autoStarted = true;
                _ = RunStartupAsync();
            }
            else if (SerialPortBox.Items.Count > 0)
            {
                UpdateLinkStatus();
                SetProgress($"串口 {SerialPortBox.Text} 当前不可用，请重新连接设备或选择其他串口", 0);
            }
            else
            {
                SetProgress("未检测到串口。请插入设备后点击「刷新串口」", 0);
            }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            App.MeshBridge.ConnectionChanged -= OnConnectionChanged;
            App.MessageHandler.OnRootDeviceRegistered -= OnRootRegistered;
            App.SdStorageService.StatusChanged -= OnStorageStatusChanged;
        }

        // ===== 串口列表 =====

        private void LoadPortList(string? preferred)
        {
            string current = preferred ?? SerialPortBox.Text ?? "";
            SerialPortBox.Items.Clear();
            try
            {
                foreach (string p in SerialPortDiscovery.GetPortNames())
                    SerialPortBox.Items.Add(p);
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(current) && SerialPortBox.Items.Contains(current))
            {
                SerialPortBox.Text = current;
            }
            else if (!string.IsNullOrWhiteSpace(current))
            {
                SerialPortBox.Text = current;
            }
            else if (SerialPortBox.Items.Count > 0)
            {
                string? recommended = SerialPortDiscovery.GetPreferredPortName(IsDirectUart);
                if (!string.IsNullOrWhiteSpace(recommended) && SerialPortBox.Items.Contains(recommended))
                    SerialPortBox.Text = recommended;
                else
                    SerialPortBox.SelectedIndex = 0;
            }
            SerialPortBox.ToolTip = SerialPortDiscovery.GetPortDescription(SerialPortBox.Text ?? "");
        }

        private void RefreshPortsButton_Click(object sender, RoutedEventArgs e)
        {
            LoadPortList(SerialPortBox.Text);
            if (SerialPortBox.Items.Count == 0)
            {
                SetProgress("仍未检测到串口，请确认设备已插入", 0);
                return;
            }

            // 刷新后若链路未建立，自动开始连接并同步
            if (!_busy && !App.MeshBridge.IsConnected && !_navigating && IsSelectedPortAvailable())
            {
                _ = RunStartupAsync();
            }
        }

        private string GetPreferredPort()
        {
            var cfg = ConfigHelper.Current;
            string preferred = IsDirectUart ? cfg.UartSerialPortName : cfg.MeshSerialPortName;
            if (!string.IsNullOrWhiteSpace(preferred)) return preferred;
            return SerialPortDiscovery.GetPreferredPortName(IsDirectUart) ?? cfg.SerialPortName;
        }

        private bool IsSelectedPortAvailable() =>
            !string.IsNullOrWhiteSpace(SerialPortBox.Text) &&
            SerialPortBox.Items.Contains(SerialPortBox.Text.Trim());

        private void LinkMode_Checked(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || _busy) return;
            var cfg = ConfigHelper.Current;
            if (string.Equals(cfg.LinkMode, "Uart", StringComparison.OrdinalIgnoreCase))
                cfg.UartSerialPortName = SerialPortBox.Text?.Trim() ?? "";
            else
                cfg.MeshSerialPortName = SerialPortBox.Text?.Trim() ?? "";

            cfg.LinkMode = UartModeButton.IsChecked == true ? "Uart" : "Mesh";
            ConfigHelper.Save(cfg);
            LoadPortList(GetPreferredPort());
            UpdateLinkStatus();
            if (!_navigating) _ = RunStartupAsync();
        }

        private void OnConnectionChanged(bool connected) =>
            Dispatcher.BeginInvoke(new Action(UpdateLinkStatus));
        private void OnRootRegistered(string rootId, bool? storageReady) =>
            Dispatcher.BeginInvoke(new Action(UpdateLinkStatus));
        private void OnStorageStatusChanged() =>
            Dispatcher.BeginInvoke(new Action(UpdateLinkStatus));

        private void UpdateLinkStatus()
        {
            bool mesh = App.MeshBridge.IsConnected;
            bool physical = App.MeshBridge.IsPhysicalConnected;
            bool root = App.SdStorageService.IsRootConnected;
            bool? sd = App.SdStorageService.IsStorageReady;
            string transportError = App.MeshBridge.LastTransportError;
            bool selectedPortAvailable = IsSelectedPortAvailable();

            if (IsDirectUart)
            {
                List<DeviceClient> onlineDevices = App.MeshBridge.GetOnlineDevices();
                bool rootDetected = onlineDevices.Any(DeviceService.IsTrueRoot);
                var cabinet = rootDetected
                    ? null
                    : onlineDevices.FirstOrDefault(device => !DeviceService.IsTrueRoot(device));
                if (cabinet != null)
                {
                    StatusDot.Fill = (Brush)FindResource("SuccessBrush");
                    StatusText.Text = "柜机串口直连已就绪";
                    StatusDetail.Text = cabinet.DeviceId;
                }
                else if (rootDetected)
                {
                    StatusDot.Fill = (Brush)FindResource("WarningBrush");
                    StatusText.Text = "当前端口是组网U盘";
                    StatusDetail.Text = "请改选柜机的 UART 转 USB 串口";
                }
                else if (physical)
                {
                    StatusDot.Fill = (Brush)FindResource("WarningBrush");
                    StatusText.Text = "柜机串口已打开";
                    StatusDetail.Text = "物理链路正常，正在等待柜机协议响应…";
                }
                else if (!string.IsNullOrWhiteSpace(transportError))
                {
                    StatusDot.Fill = (Brush)FindResource("DangerBrush");
                    StatusText.Text = "柜机串口未连接";
                    StatusDetail.Text = transportError;
                }
                else if (!selectedPortAvailable)
                {
                    StatusDot.Fill = (Brush)FindResource("DangerBrush");
                    StatusText.Text = "未检测到柜机串口";
                    StatusDetail.Text = "请连接柜机 UART 转 USB 设备后刷新串口";
                }
                else
                {
                    StatusDot.Fill = (Brush)FindResource("DangerBrush");
                    StatusText.Text = "柜机串口尚未连接";
                    StatusDetail.Text = $"正在打开 {SerialPortBox.Text.Trim()}…";
                }
                return;
            }

            if (root && sd == true)
            {
                StatusDot.Fill = (Brush)FindResource("SuccessBrush");
                StatusText.Text = "根节点就绪";
                StatusDetail.Text = $"SD 可用 · {App.SdStorageService.RootDeviceId}";
            }
            else if (root)
            {
                StatusDot.Fill = (Brush)FindResource("WarningBrush");
                StatusText.Text = "根节点已连接";
                StatusDetail.Text = sd == false ? "SD 卡未就绪" : "等待 SD 状态…";
            }
            else if (mesh)
            {
                StatusDot.Fill = (Brush)FindResource("WarningBrush");
                StatusText.Text = "组网U盘协议已连接";
                StatusDetail.Text = "等待根节点注册…";
            }
            else if (physical)
            {
                StatusDot.Fill = (Brush)FindResource("WarningBrush");
                StatusText.Text = "组网U盘已连接";
                StatusDetail.Text = "物理链路正常，正在等待根节点协议响应…";
            }
            else if (!string.IsNullOrWhiteSpace(transportError))
            {
                StatusDot.Fill = (Brush)FindResource("DangerBrush");
                StatusText.Text = "组网U盘未连接";
                StatusDetail.Text = transportError;
            }
            else if (!selectedPortAvailable)
            {
                StatusDot.Fill = (Brush)FindResource("DangerBrush");
                StatusText.Text = "未检测到组网U盘";
                StatusDetail.Text = "请连接根节点 USB 设备后刷新串口";
            }
            else
            {
                StatusDot.Fill = (Brush)FindResource("DangerBrush");
                StatusText.Text = "组网U盘尚未连接";
                StatusDetail.Text = $"正在打开 {SerialPortBox.Text.Trim()}…";
            }
        }

        // ===== 主流程：连接 + 同步（带备份与重试） =====

        private async Task RunStartupAsync()
        {
            if (_busy) return;
            _busy = true;
            SetBusyUi(true);
            FailurePanel.Visibility = Visibility.Collapsed; // 同步期间隐藏失败按钮
            UseLocalButton.IsEnabled = false;
            RetryButton.IsEnabled = false;

            try
            {
                if (!SavePortConfig(out string err))
                {
                    SetProgress(err, 0);
                    return;
                }

                SetProgress(IsDirectUart ? "正在启动柜机串口直连…" : "正在连接组网U盘…", 10);
                try { App.MeshBridge.Stop(); } catch { }
                App.MeshBridge.Start(ConfigHelper.Current.ToTransportConfig());
                UpdateLinkStatus();

                if (IsDirectUart)
                {
                    SetProgress("等待柜机协议响应…", 35);
                    bool cabinetReady = await WaitForDirectCabinetAsync(WaitDirectTimeoutMs);
                    UpdateLinkStatus();
                    if (!cabinetReady)
                    {
                        SetProgress(GetDirectConnectionFailureMessage(), 0);
                        ShowRetryOrLocal();
                        return;
                    }

                    SetProgress("柜机串口直连已就绪", 100);
                    DirectMaintenanceStateService.BeginSession(
                        App.MeshBridge.GetOnlineDevices()
                            .FirstOrDefault(device => !DeviceService.IsTrueRoot(device))?.DeviceId ?? "");
                    await Task.Delay(200);
                    GoToLogin();
                    return;
                }

                SetProgress("等待根节点与 SD 就绪…", 25);
                bool ready = await WaitForSdReadyAsync(WaitRootTimeoutMs);
                UpdateLinkStatus();

                if (!ready)
                {
                    SetProgress(
                        string.IsNullOrWhiteSpace(App.SdStorageService.LastError)
                            ? "未能在超时内连接根节点 SD，请检查设备与串口。"
                            : App.SdStorageService.LastError, 0);
                    ShowRetryOrLocal();
                    return;
                }

                if (!await UploadPendingDirectChangesAsync())
                {
                    ShowRetryOrLocal();
                    return;
                }

                // 同步前：备份当前主库 + 切到临时库
                SetProgress("正在备份本地业务库…", 35);
                try { BusinessDatabaseBackupService.BeginSyncToTemp(); }
                catch (Exception ex)
                {
                    SetProgress($"本地库备份失败：{ex.Message}。已中止同步。", 0);
                    try { BusinessDatabaseBackupService.AbortTemp(); } catch { }
                    ShowRetryOrLocal();
                    return;
                }

                // 重试同步，最多 MaxSyncAttempts 次
                for (int attempt = 1; attempt <= MaxSyncAttempts; attempt++)
                {
                    SetProgress($"正在从 SD 同步业务库（第 {attempt}/{MaxSyncAttempts} 次）…", 45);
                    var progress = new Progress<string>(msg => SetProgress(msg, -1));
                    var result = await App.SdBusinessSyncService.PullBusinessFromSdAsync(progress);

                    if (result.Success)
                    {
                        // 同步成功：临时库替换为主库
                        SetProgress(result.Message + "，正在应用…", 90);
                        try { BusinessDatabaseBackupService.CommitTempAsMain(); }
                        catch (Exception ex)
                        {
                            SetProgress($"应用同步数据失败：{ex.Message}。本地库保持原样。", 0);
                            try { BusinessDatabaseBackupService.AbortTemp(); } catch { }
                            ShowRetryOrLocal();
                            return;
                        }
                        SetProgress(result.Message + "，即将进入登录…", 100);
                        await Task.Delay(400);
                        GoToLogin();
                        return;
                    }

                    // 部分成功也算成功（用户表已同步即可用）
                    if (result.PulledTables.Contains("users"))
                    {
                        SetProgress(result.Message + "，正在应用…", 90);
                        try { BusinessDatabaseBackupService.CommitTempAsMain(); }
                        catch (Exception ex)
                        {
                            SetProgress($"应用同步数据失败：{ex.Message}。", 0);
                            try { BusinessDatabaseBackupService.AbortTemp(); } catch { }
                            ShowRetryOrLocal();
                            return;
                        }
                        SetProgress(result.Message + "，即将进入登录…", 100);
                        await Task.Delay(400);
                        GoToLogin();
                        return;
                    }

                    SetProgress($"第 {attempt} 次同步失败：{result.Message}", 0);
                    if (attempt < MaxSyncAttempts)
                    {
                        await Task.Delay(800);
                        // 重新触发注册并等待
                        try { App.MeshBridge.Send("", Protocol.CmdRegister); } catch { }
                        await Task.Delay(600);
                    }
                }

                // 3 次均失败：丢弃临时库，主库保持原样
                try { BusinessDatabaseBackupService.AbortTemp(); } catch { }
                SetProgress(
                    "连续 " + MaxSyncAttempts + " 次同步失败。请拔插设备、选择正确的串口后点击「重试」；" +
                    "若设备暂不可用，可使用本地历史数据继续。", 0);
                ShowRetryOrLocal();
            }
            catch (Exception ex)
            {
                try { BusinessDatabaseBackupService.AbortTemp(); } catch { }
                SetProgress($"同步失败：{ex.Message}", 0);
                ShowRetryOrLocal();
            }
            finally
            {
                _busy = false;
                SetBusyUi(false);
            }
        }

        private async Task<bool> WaitForSdReadyAsync(int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            long nextRegisterAt = 1500;
            try { App.MeshBridge.Send("", Protocol.CmdRegister); } catch { }

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (App.SdStorageService.IsAvailable) return true;

                if (App.SdStorageService.IsRootConnected && App.SdStorageService.IsStorageReady == false)
                {
                    await Task.Delay(800);
                    if (App.SdStorageService.IsAvailable) return true;
                    if (sw.ElapsedMilliseconds > timeoutMs / 2) return false;
                }

                UpdateLinkStatus();
                if (sw.ElapsedMilliseconds >= nextRegisterAt && App.MeshBridge.IsPhysicalConnected)
                {
                    try { App.MeshBridge.Send("", Protocol.CmdRegister); } catch { }
                    nextRegisterAt = sw.ElapsedMilliseconds + 1500;
                }

                await Task.Delay(300);
            }

            return App.SdStorageService.IsAvailable;
        }

        private async Task<bool> WaitForDirectCabinetAsync(int timeoutMs)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            while (stopwatch.ElapsedMilliseconds < timeoutMs)
            {
                List<DeviceClient> onlineDevices = App.MeshBridge.GetOnlineDevices();
                if (onlineDevices.Any(DeviceService.IsTrueRoot))
                {
                    UpdateLinkStatus();
                    return false;
                }

                if (onlineDevices.Any(device => !DeviceService.IsTrueRoot(device)))
                    return true;

                UpdateLinkStatus();
                await Task.Delay(250);
            }
            List<DeviceClient> finalDevices = App.MeshBridge.GetOnlineDevices();
            return !finalDevices.Any(DeviceService.IsTrueRoot) &&
                finalDevices.Any(device => !DeviceService.IsTrueRoot(device));
        }

        private string GetDirectConnectionFailureMessage()
        {
            List<DeviceClient> onlineDevices = App.MeshBridge.GetOnlineDevices();
            if (onlineDevices.Any(DeviceService.IsTrueRoot))
                return "当前选择的是组网U盘串口，请改选柜机 UART 转 USB 串口。";

            if (!App.MeshBridge.IsPhysicalConnected)
            {
                if (!string.IsNullOrWhiteSpace(App.MeshBridge.LastTransportError))
                    return App.MeshBridge.LastTransportError;
                if (!IsSelectedPortAvailable())
                    return $"未检测到柜机串口 {SerialPortBox.Text.Trim()}，请检查 USB 连接后刷新串口。";
                return "柜机串口未能打开，请确认端口未被其他程序占用。";
            }

            return "柜机串口已打开，但未收到协议响应。请确认选择的是柜机端口，并检查供电及 TX/RX 接线。";
        }

        private async Task<bool> UploadPendingDirectChangesAsync()
        {
            if (!DirectMaintenanceStateService.TryGetPendingChanges(
                    out DirectMaintenanceStateService.SessionSnapshot? session,
                    out string pendingReason))
            {
                DirectMaintenanceStateService.CompleteSession();
                return true;
            }

            SetProgress($"检测到{pendingReason}，正在校验 SD 版本…", 30);
            SdVersionInfo? remote = await App.SdStorageService.QueryVersionAsync(8000);
            if (remote == null || session == null)
            {
                SetProgress("无法读取 SD 版本。为避免覆盖直连期间的本机变更，已停止启动同步。", 0);
                return false;
            }

            if (!session.MatchesRemote(remote, out string conflict))
            {
                SetProgress($"直连变更暂未回传：{conflict}。为避免数据互相覆盖，已保留本机数据。", 0);
                return false;
            }

            var progress = new Progress<string>(message =>
                SetProgress("正在回传直连期间的本机变更：" + message, -1));
            SdBusinessSyncService.SyncResult result =
                await App.SdBusinessSyncService.PushBusinessToSdAsync(progress, timeoutMs: 10000);
            if (!result.Success)
            {
                SetProgress("直连期间的本机变更回传失败：" + result.Message, 0);
                return false;
            }

            DirectMaintenanceStateService.CompleteSession();
            SetProgress("直连期间的本机变更已安全回传，继续读取 SD…", 40);
            return true;
        }

        private bool SavePortConfig(out string error)
        {
            error = "";
            try
            {
                var cfg = ConfigHelper.Current;
                cfg.TransportType = "UsbSerial"; // 启动页只支持 USB 串口
                cfg.SerialPortName = SerialPortBox.Text?.Trim() ?? "";
                cfg.SerialBaudRate = 921600; // 默认波特率，无需用户选择
                cfg.LinkMode = UartModeButton.IsChecked == true ? "Uart" : "Mesh";
                if (IsDirectUart) cfg.UartSerialPortName = cfg.SerialPortName;
                else cfg.MeshSerialPortName = cfg.SerialPortName;
                ConfigHelper.Save(cfg);
                return true;
            }
            catch (Exception ex)
            {
                error = "保存配置失败：" + ex.Message;
                return false;
            }
        }

        private void ShowRetryOrLocal()
        {
            FailurePanel.Visibility = Visibility.Visible; // 同步失败后才显示
            RetryButton.IsEnabled = true;
            // 历史备份或当前主库有数据时，允许使用本地历史数据继续
            UseLocalButton.IsEnabled = BusinessDatabaseBackupService.GetLatestBackup() != null
                || BusinessDatabase.HasAnyBusinessData();
        }

        // ===== 按钮回调 =====

        private void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            _ = RunStartupAsync();
        }

        private void UseLocalButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            UseLocalDataAndContinue();
        }

        private void UseLocalDataAndContinue()
        {
            // 优先用最近一份历史备份恢复为主库；没有备份但主库已有数据则直接用主库
            var latest = BusinessDatabaseBackupService.GetLatestBackup();
            if (latest != null)
            {
                try
                {
                    BusinessDatabaseBackupService.RestoreLatestBackup();
                    SetProgress($"已恢复历史数据（{latest.Time:yyyy-MM-dd HH:mm:ss}），即将进入登录…", 100);
                }
                catch (Exception ex)
                {
                    SetProgress($"恢复历史数据失败：{ex.Message}", 0);
                    return;
                }
            }
            else if (!BusinessDatabase.HasAnyBusinessData())
            {
                var r = MessageBox.Show(
                    "本机业务库为空且无历史备份。仍要进入登录吗？（仅内置管理员可能可用）",
                    "本地数据", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (r != MessageBoxResult.Yes) return;
            }

            // 确保链路按当前配置启动（便于登录后继续通讯）
            try
            {
                if (!App.MeshBridge.IsConnected)
                {
                    SavePortConfig(out _);
                    App.MeshBridge.Start(ConfigHelper.Current.ToTransportConfig());
                }
            }
            catch { }

            GoToLogin();
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            App.RequestShutdown(this);
        }

        private void GoToLogin()
        {
            if (_navigating) return;
            _navigating = true;
            try { App.CabinetBindingService.MigrateLegacyBindings(); } catch { }
            var login = new LoginWindow();
            login.Show();
            Close();
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_navigating && !App.ExitApproved)
            {
                e.Cancel = true;
                base.OnClosing(e);
                if (_busy)
                {
                    MessageBox.Show("业务数据同步正在进行，请稍候再退出。", "正在同步",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                Dispatcher.BeginInvoke(new Action(() => App.RequestShutdown(this)));
                return;
            }
            base.OnClosing(e);
        }

        private void SetBusyUi(bool busy)
        {
            SerialPortBox.IsEnabled = !busy;
            RefreshPortsButton.IsEnabled = !busy;
            MeshModeButton.IsEnabled = !busy;
            UartModeButton.IsEnabled = !busy;
            SyncProgress.IsIndeterminate = busy;
        }

        private void SetProgress(string text, double value)
        {
            ProgressText.Text = text;
            if (value >= 0)
            {
                SyncProgress.IsIndeterminate = false;
                SyncProgress.Value = Math.Max(0, Math.Min(100, value));
            }
            else if (_busy)
            {
                SyncProgress.IsIndeterminate = true;
            }
        }
    }
}
