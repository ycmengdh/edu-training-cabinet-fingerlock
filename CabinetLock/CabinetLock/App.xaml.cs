using System.Windows;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;

namespace CabinetLock
{
    /// <summary>
    /// 应用程序入口
    /// 启动页配置串口并同步 SD 业务库 → 登录；退出时统一释放通讯资源。
    /// </summary>
    public partial class App : Application
    {
        private const string SingleInstanceMutexName = @"Local\CabinetLock.SingleInstance";

        /// <summary>整条 Root/串口链路的业务事务协调器。</summary>
        public static CommunicationCoordinator CommunicationCoordinator { get; } = new();

        /// <summary>Mesh 桥接器（统一管理 USB 串口 / TCP 客户端 / TCP 服务端三种链路）</summary>
        public static MeshBridge MeshBridge { get; } = new MeshBridge();

        /// <summary>消息处理器（解析收到的消息并分发到业务事件）</summary>
        public static MessageHandler MessageHandler { get; } = new MessageHandler();

        // ===== 全局业务服务实例 =====
        public static AuthService AuthService { get; } = new AuthService();
        public static UserService UserService { get; } = new UserService();
        public static ClassService ClassService { get; } = new ClassService();
        public static ClassLifecycleService ClassLifecycleService { get; } = new ClassLifecycleService();
        public static PermissionService PermissionService { get; } = new PermissionService();
        public static RolePermissionService RolePermissionService { get; } = new RolePermissionService();
        public static DeviceService DeviceService { get; } = new DeviceService();
        public static LogService LogService { get; } = new LogService();
        public static OperationLogService OperationLogService { get; } = new OperationLogService();
        public static CabinetSyncService CabinetSyncService { get; } = new CabinetSyncService();
        public static CabinetBindingService CabinetBindingService { get; } = new CabinetBindingService();
        public static CabinetSyncQueueService CabinetSyncQueueService { get; } = new CabinetSyncQueueService();
        public static CommandService CommandService { get; } = new CommandService();
        public static MaintenanceService MaintenanceService { get; } = new MaintenanceService();
        public static SystemHealthService SystemHealthService { get; } = new SystemHealthService();

        /// <summary>SD 卡集中存储服务（通过 Mesh 与根节点 SD 卡通信）</summary>
        public static SdStorageService SdStorageService { get; } = new SdStorageService();

        public static CabinetOtaService CabinetOtaService { get; } = new CabinetOtaService();

        /// <summary>SD ↔ 本机业务库同步</summary>
        public static SdBusinessSyncService SdBusinessSyncService { get; } = new SdBusinessSyncService();

        /// <summary>指纹模板业务服务（采集-存储-分配解耦管理）</summary>
        public static FingerprintTemplateService FingerprintTemplateService { get; } = new FingerprintTemplateService();

        /// <summary>当前登录用户（登录成功后赋值）</summary>
        public static User? CurrentUser { get; set; }

        private readonly CancellationTokenSource _shutdownCts = new();
        private SingleInstanceGuard? _singleInstanceGuard;
        private int _exitStarted;
        private int _cabinetBackgroundServicesStarted;
        private int _devicePersistencePending;
        private int _devicePersistenceWorkerRunning;
        private readonly object _automaticPipelineLock = new();
        private Task? _automaticPipelineTask;
        private bool _automaticPipelinePending;
        private bool _automaticOtaCheckRequired;
        private readonly ConcurrentDictionary<string, byte> _registeredCabinetsThisSession =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, byte> _registeredRootsThisSession =
            new(StringComparer.OrdinalIgnoreCase);
        private static int _exitPromptOpen;

        public static bool ExitApproved { get; private set; }

        public static void RequestShutdown(Window? owner = null)
        {
            if (Current?.Dispatcher.HasShutdownStarted == true) return;
            if (ExitApproved)
            {
                Current?.Shutdown();
                return;
            }
            if (Interlocked.Exchange(ref _exitPromptOpen, 1) != 0) return;

            try
            {
                bool uploadRequired = BusinessUploadStateService.IsUploadRequired(out string reason);
                if (!uploadRequired)
                {
                    ExitApproved = true;
                    Current?.Shutdown();
                    return;
                }

                ExitBusinessSyncWindow dialog;
                try
                {
                    dialog = new ExitBusinessSyncWindow(reason);
                    if (owner?.IsVisible == true) dialog.Owner = owner;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"无法打开退出同步窗口：{ex.Message}\n\n程序将保持运行，请检查后重试。",
                        "退出同步窗口错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                bool confirmed = dialog.ShowDialog() == true && dialog.ExitAllowed;
                if (!confirmed) return;

                ExitApproved = true;
                Current?.Shutdown();
            }
            finally
            {
                Interlocked.Exchange(ref _exitPromptOpen, 0);
            }
        }

        /// <summary>
        /// 应用启动：初始化本机双库 → 绑定消息 → 显示启动页（串口 + SD 同步）
        /// </summary>
        private void Application_Startup(object sender, StartupEventArgs e)
        {
            _singleInstanceGuard = SingleInstanceGuard.Acquire(SingleInstanceMutexName);
            if (!_singleInstanceGuard.IsPrimaryInstance)
            {
                MessageBox.Show("程序已在运行，请勿重复启动。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown();
                return;
            }

            ThemeManager.Apply(ConfigHelper.Current.AppearanceTheme);

            // 1. 绑定消息处理器业务事件
            WireUpMessageHandler();

            // 2. 本地缓存目录 + 双 SQLite 库
            try { LocalCacheService.Initialize(); } catch { }
            try
            {
                BusinessDatabase.Initialize();
                LogDatabase.Initialize();
                BusinessDatabase.MigrateFromLocalCacheIfEmpty();
                BusinessDatabase.MigrateFingerprintsFromLocalCacheIfEmpty();
                LogDatabase.MigrateOperationLogsFromJsonIfEmpty();
                LogDatabase.MigrateUnlockLogsFromCacheIfEmpty();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"本地数据库初始化失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }

            // 3. 订阅 SD 降级 / 恢复事件（恢复后回传本机业务库）
            SdStorageService.StorageDegraded += OnStorageDegraded;
            SdStorageService.StorageRecovered += OnStorageRecovered;

            // 4. 绑定 Mesh 事件（链路由启动页按所选串口启动，此处不自动 Start）
            MeshBridge.MessageReceived += OnMessageReceived;
            MeshBridge.DeviceConnected += OnDeviceConnected;
            MeshBridge.DeviceDisconnected += OnDeviceDisconnected;
            MeshBridge.ConnectionChanged += OnConnectionChanged;

            // 5. 显示启动页（同步完成后进入登录）
            var startup = new StartupWindow();
            startup.Show();
        }

        /// <summary>
        /// 启动同步会用临时库原子替换主业务库，所有可能访问业务库的后台任务
        /// 必须等替换结束后再启动，避免 SQLite 连接池重新占用主库文件。
        /// </summary>
        internal void StartCabinetBackgroundServicesOnce()
        {
            if (Interlocked.Exchange(ref _cabinetBackgroundServicesStarted, 1) != 0) return;
            try { CabinetSyncQueueService.RemoveInvalidRootJobs(); } catch { }
            try { FingerprintTemplateService.EnsureGlobalStaffSyncQueued(); } catch { }
            try
            {
                foreach (DeviceClient device in MeshBridge.GetOnlineDevices())
                {
                    if (DeviceService.IsTrueRoot(device))
                    {
                        if (!string.IsNullOrWhiteSpace(device.DeviceId))
                            _registeredRootsThisSession.TryAdd(device.DeviceId, 0);
                        continue;
                    }
                    ReconcileMaintenanceJob(device);
                    if (!string.IsNullOrWhiteSpace(device.DeviceId))
                        _registeredCabinetsThisSession.TryAdd(device.DeviceId, 0);
                }
            }
            catch { }
            _ = CabinetSyncQueueService.RunAsync(_shutdownCts.Token);
            QueueAutomaticCommunicationPipeline(requireOtaCheck: true);
        }

        internal bool CabinetBackgroundServicesStarted =>
            Volatile.Read(ref _cabinetBackgroundServicesStarted) != 0;

        internal bool AutomaticCommunicationPipelineActive
        {
            get
            {
                lock (_automaticPipelineLock)
                    return _automaticPipelineTask != null &&
                        !_automaticPipelineTask.IsCompleted;
            }
        }

        /// <summary>
        /// 应用退出：业务数据上传校验已在主窗口 Closing 阶段完成，
        /// 此处只取消后台任务并停止 Mesh。
        /// </summary>
        private void Application_Exit(object sender, ExitEventArgs e)
        {
            if (Interlocked.Exchange(ref _exitStarted, 1) != 0) return;

            try { _shutdownCts.Cancel(); } catch { }

            try
            {
                MeshBridge.Stop();
            }
            catch
            {
                // 退出时忽略异常
            }
            finally
            {
                _singleInstanceGuard?.Dispose();
                _singleInstanceGuard = null;
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
            MessageHandler.OnConfigResponse += OnConfigResponse;
            MessageHandler.OnMaintenanceStatus += MaintenanceService.HandleReported;
        }

        private void OnDeviceConnected(DeviceClient device)
        {
            if (CabinetBackgroundServicesStarted)
            {
                QueueDevicePersistenceRefresh();
            }
        }

        private void OnDeviceDisconnected(DeviceClient device)
        {
            try
            {
                if (!string.IsNullOrEmpty(device.DeviceId))
                {
                    _registeredCabinetsThisSession.TryRemove(device.DeviceId, out _);
                    _registeredRootsThisSession.TryRemove(device.DeviceId, out _);
                    // 在线状态由根节点根据 Mesh 状态写入 devices.json。
                }
            }
            catch
            {
            }
        }

        private void OnMessageReceived(DeviceClient? device, Message msg)
        {
            try
            {
                MessageHandler.HandleMessage(device, msg);
            }
            catch
            {
            }
        }

        private void OnDeviceRegistered(string deviceId, string deviceName)
        {
            System.Diagnostics.Debug.WriteLine($"[APP] device registered: {deviceId} {deviceName}");
            if (CabinetBackgroundServicesStarted)
            {
                QueueDevicePersistenceRefresh();
                DeviceClient? registered = MeshBridge.GetOnlineDevices().FirstOrDefault(device =>
                    string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
                bool isRoot = registered != null && DeviceService.IsTrueRoot(registered);
                if (!isRoot && !string.IsNullOrWhiteSpace(deviceId))
                {
                    if (registered != null) ReconcileMaintenanceJob(registered);
                    if (_registeredCabinetsThisSession.TryAdd(deviceId, 0))
                        QueueAutomaticCommunicationPipeline();
                }
            }
        }

        private void ReconcileMaintenanceJob(DeviceClient device)
        {
            if (device == null || string.IsNullOrWhiteSpace(device.DeviceId) ||
                DeviceService.IsTrueRoot(device))
                return;

            if (MaintenanceService.NeedsConfigurationSync(
                    device.DeviceId, device.FirmwareVersion))
            {
                CabinetSyncQueueService.EnqueueMaintenance(
                    new[] { device.DeviceId }, "柜机维护配置版本落后");
            }
            else
            {
                CabinetSyncQueueService.RecordMaintenanceOutcome(
                    device.DeviceId, true);
            }
        }

        private void QueueDevicePersistenceRefresh()
        {
            Interlocked.Exchange(ref _devicePersistencePending, 1);
            if (Interlocked.CompareExchange(
                    ref _devicePersistenceWorkerRunning, 1, 0) != 0)
                return;

            _ = Task.Run(ProcessDevicePersistenceRefreshesAsync);
        }

        private async Task ProcessDevicePersistenceRefreshesAsync()
        {
            try
            {
                do
                {
                    await Task.Delay(400, _shutdownCts.Token).ConfigureAwait(false);
                    Interlocked.Exchange(ref _devicePersistencePending, 0);
                    try { DeviceService.GetAllDevices(); }
                    catch { }
                    try { FingerprintTemplateService.EnsureGlobalStaffSyncQueued(); }
                    catch { }
                    CabinetSyncQueueService.Trigger();
                }
                while (Volatile.Read(ref _devicePersistencePending) != 0 &&
                       !_shutdownCts.IsCancellationRequested);
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
            }
            finally
            {
                Interlocked.Exchange(ref _devicePersistenceWorkerRunning, 0);
                if (Volatile.Read(ref _devicePersistencePending) != 0 &&
                    !_shutdownCts.IsCancellationRequested)
                    QueueDevicePersistenceRefresh();
            }
        }

        private DateTime _lastTimeSyncAt = DateTime.MinValue;
        private string _lastTimeSyncRootId = "";

        private void OnRootDeviceRegistered(string rootDeviceId, bool? storageReady)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rootDeviceId)) return;
                try { LocalCacheService.Initialize(); } catch { }

                SdStorageService.RegisterRoot(rootDeviceId, storageReady);
                System.Diagnostics.Debug.WriteLine(
                    $"[APP] 根节点已注册: {rootDeviceId}，SD={storageReady?.ToString() ?? "unknown"}");
                if (CabinetBackgroundServicesStarted &&
                    _registeredRootsThisSession.TryAdd(rootDeviceId, 0))
                    QueueAutomaticCommunicationPipeline(requireOtaCheck: true);
            }
            catch
            {
            }
        }

        /// <summary>
        /// 日志上报：上位机在线时直接写入本机 logs.db；
        /// 根节点只暂存上位机离线期间的日志，并在下次启动时补传。
        /// </summary>
        private void OnLogReport(string deviceId, string logJson)
        {
            try
            {
                var log = ParseLogEntry(logJson, deviceId);
                if (log != null)
                {
                    LogDatabase.AppendUnlock(log);
                    System.Diagnostics.Debug.WriteLine($"[APP] unlock log cached from {deviceId}");
                }
            }
            catch
            {
            }
        }

        private void OnStorageDegraded()
        {
            System.Diagnostics.Debug.WriteLine("[APP] SD 进入降级模式");
        }

        private void OnStorageRecovered()
        {
            CancellationToken cancellationToken = _shutdownCts.Token;
            if (cancellationToken.IsCancellationRequested) return;
            if (CurrentUser == null)
            {
                System.Diagnostics.Debug.WriteLine("[APP] 启动/登录阶段 SD 恢复，跳过反向上传");
                return;
            }

            System.Diagnostics.Debug.WriteLine("[APP] SD 恢复，回传本机业务库");
            _ = Task.Run(async () =>
            {
                try
                {
                    await SdBusinessSyncService.PushBusinessToSdAsync(
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch
                {
                }
            }, cancellationToken);
        }

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

        private void OnAckReceived(string msgId, string result)
        {
            CommandService.HandleAck(msgId, result);
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

        private void OnConnectionChanged(bool connected)
        {
            SdStorageService.HandleConnectionChanged(connected);
            CommandService.HandleConnectionChanged(connected);
            if (connected)
            {
                MeshBridge.Send("", Protocol.CmdRegister);
                if (CabinetBackgroundServicesStarted)
                    QueueAutomaticCommunicationPipeline(requireOtaCheck: true);
            }
            else
            {
                _registeredCabinetsThisSession.Clear();
                _registeredRootsThisSession.Clear();
            }
        }

        internal Task SyncRootTimeIfDueAsync(
            CancellationToken cancellationToken = default) =>
            CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.SdSync,
                "同步根节点时间",
                SdStorageService.RootDeviceId,
                _ =>
                {
                    TrySendRootTimeSyncIfDue(SdStorageService.RootDeviceId);
                    return Task.CompletedTask;
                },
                cancellationToken);

        private void TrySendRootTimeSyncIfDue(string rootDeviceId)
        {
            if (string.IsNullOrWhiteSpace(rootDeviceId)) return;
            DateTime now = DateTime.UtcNow;
            bool sameRoot = string.Equals(
                _lastTimeSyncRootId, rootDeviceId, StringComparison.OrdinalIgnoreCase);
            if (sameRoot && (now - _lastTimeSyncAt).TotalSeconds < 10) return;

            if (MeshBridge.Send(rootDeviceId, Protocol.CmdTimeSync, new
                {
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                }))
            {
                _lastTimeSyncAt = now;
                _lastTimeSyncRootId = rootDeviceId;
            }
        }

        internal Task QueueAutomaticCommunicationPipelineAsync(
            bool requireOtaCheck = false)
        {
            if (_shutdownCts.IsCancellationRequested || !CabinetBackgroundServicesStarted)
                return Task.CompletedTask;

            lock (_automaticPipelineLock)
            {
                _automaticPipelinePending = true;
                if (requireOtaCheck && !string.Equals(
                        ConfigHelper.Current.LinkMode, "Uart",
                        StringComparison.OrdinalIgnoreCase))
                    _automaticOtaCheckRequired = true;

                if (_automaticPipelineTask == null || _automaticPipelineTask.IsCompleted)
                    _automaticPipelineTask = ProcessAutomaticCommunicationPipelineAsync();
                return _automaticPipelineTask;
            }
        }

        internal void QueueAutomaticCommunicationPipeline(
            bool requireOtaCheck = false) =>
            _ = QueueAutomaticCommunicationPipelineAsync(requireOtaCheck);

        private async Task ProcessAutomaticCommunicationPipelineAsync()
        {
            try
            {
                while (!_shutdownCts.IsCancellationRequested)
                {
                    lock (_automaticPipelineLock)
                        _automaticPipelinePending = false;

                    // 合并同一批柜机启动时密集到达的 REGISTER。
                    await Task.Delay(500, _shutdownCts.Token).ConfigureAwait(false);

                    bool requireOtaCheck;
                    lock (_automaticPipelineLock)
                    {
                        requireOtaCheck = _automaticOtaCheckRequired;
                        _automaticOtaCheckRequired = false;
                    }

                    if (requireOtaCheck &&
                        !await ConfirmOtaStateAndWaitForClearAsync()
                            .ConfigureAwait(false))
                    {
                        lock (_automaticPipelineLock)
                            _automaticOtaCheckRequired = true;
                        return;
                    }

                    while (!_shutdownCts.IsCancellationRequested)
                    {
                        int processed = await CabinetSyncQueueService
                            .ProcessPendingAsync(_shutdownCts.Token)
                            .ConfigureAwait(false);
                        if (processed == 0) break;

                        lock (_automaticPipelineLock)
                        {
                            if (_automaticOtaCheckRequired)
                            {
                                _automaticPipelinePending = true;
                                break;
                            }
                        }
                    }

                    lock (_automaticPipelineLock)
                    {
                        if (!_automaticPipelinePending && !_automaticOtaCheckRequired)
                            return;
                    }
                }
            }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[APP] 自动通讯流水线失败: {ex.Message}");
            }
            finally
            {
                bool restart;
                lock (_automaticPipelineLock)
                {
                    _automaticPipelineTask = null;
                    restart = (_automaticPipelinePending || _automaticOtaCheckRequired) &&
                        !_shutdownCts.IsCancellationRequested &&
                        (!_automaticOtaCheckRequired || SdStorageService.IsRootConnected);
                }
                if (restart) QueueAutomaticCommunicationPipeline();
            }
        }

        private async Task<bool> ConfirmOtaStateAndWaitForClearAsync()
        {
            if (string.Equals(ConfigHelper.Current.LinkMode, "Uart",
                    StringComparison.OrdinalIgnoreCase))
                return true;
            if (!SdStorageService.IsRootConnected) return false;

            bool confirmed = false;
            await CommunicationCoordinator.RunExclusiveAsync(
                CommunicationOperationKind.Ota,
                "自动流程 1/3 · 检查并恢复 OTA",
                SdStorageService.RootDeviceId,
                async token =>
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                    int failures = 0;
                    while (SdStorageService.IsRootConnected &&
                           !token.IsCancellationRequested && failures < 3)
                    {
                        try
                        {
                            await CabinetOtaService.ResumeActiveDistributionAsync(
                                cancellationToken: token).ConfigureAwait(false);
                            confirmed = true;
                            break;
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            failures++;
                            System.Diagnostics.Debug.WriteLine(
                                $"[APP] 等待 OTA 状态恢复: {ex.Message}");
                            if (failures < 3)
                                await Task.Delay(3000, token).ConfigureAwait(false);
                        }
                    }
                },
                _shutdownCts.Token).ConfigureAwait(false);

            if (confirmed)
                await SyncRootTimeIfDueAsync(_shutdownCts.Token).ConfigureAwait(false);
            return confirmed;
        }

        private void OnConfigSavedHandler(string deviceId)
        {
        }

        private void OnConfigResponse(string deviceId, string configJson)
        {
            if (!CabinetBackgroundServicesStarted) return;
            QueueDevicePersistenceRefresh();
        }
    }
}
