using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CabinetLock
{
    public partial class CabinetManagePage : Page
    {
        private static readonly TimeSpan ScrollRefreshDelay = TimeSpan.FromMilliseconds(700);
        private System.Windows.Threading.DispatcherTimer? _deferredApplyTimer;
        private List<Device> _allCabinets = new();
        private readonly ObservableCollection<Device> _visibleCabinets = new();
        private readonly HashSet<string> _metadataQueried =
            new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _metadataQueryCts;
        private bool _metadataQueryRunning;
        private bool _syncStateQueryRunning;
        private bool _syncStateQueryPending;
        private bool _loading;
        private bool _missingDeviceReloadPending;
        private DateTime _lastScrollInteractionUtc = DateTime.MinValue;
        private List<Device>? _pendingCabinetSnapshot;
        private IReadOnlyDictionary<string, CabinetExpectedSyncState>? _pendingSyncStates;
        private readonly Dictionary<string, Device> _pendingLiveUpdates =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly object _liveUpdateQueueLock = new();
        private readonly Dictionary<string, Device> _liveUpdateQueue =
            new(StringComparer.OrdinalIgnoreCase);
        private bool _liveUpdateDispatchScheduled;

        public CabinetManagePage()
        {
            InitializeComponent();
            CabinetDataGrid.ItemsSource = _visibleCabinets;
            Loaded += CabinetManagePage_Loaded;
            Unloaded += CabinetManagePage_Unloaded;
            bool isAdmin = string.Equals(App.CurrentUser?.Role, "admin",
                StringComparison.OrdinalIgnoreCase);
            MaintenancePasswordButton.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
            MaintenanceModeButton.Visibility = isAdmin ? Visibility.Visible : Visibility.Collapsed;
            MaintenanceSelectionColumn.Visibility = isAdmin
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void CabinetManagePage_Loaded(object sender, RoutedEventArgs e)
        {
            App.MeshBridge.DeviceConnected += OnDevicePresenceChanged;
            App.MeshBridge.DeviceDisconnected += OnDevicePresenceChanged;
            App.MeshBridge.MessageReceived += OnDeviceMessageReceived;
            App.CabinetSyncService.SyncStateChanged += OnCabinetSyncStateChanged;
            App.MaintenanceService.StateChanged += OnMaintenanceStateChanged;
            _metadataQueried.Clear();
            _metadataQueryCts?.Cancel();
            _metadataQueryCts?.Dispose();
            _metadataQueryCts = new CancellationTokenSource();
            await LoadCabinetsAsync();

            _deferredApplyTimer = new System.Windows.Threading.DispatcherTimer(
                System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = ScrollRefreshDelay
            };
            _deferredApplyTimer.Tick += DeferredApplyTimer_Tick;
        }

        private void CabinetManagePage_Unloaded(object sender, RoutedEventArgs e)
        {
            App.MeshBridge.DeviceConnected -= OnDevicePresenceChanged;
            App.MeshBridge.DeviceDisconnected -= OnDevicePresenceChanged;
            App.MeshBridge.MessageReceived -= OnDeviceMessageReceived;
            App.CabinetSyncService.SyncStateChanged -= OnCabinetSyncStateChanged;
            App.MaintenanceService.StateChanged -= OnMaintenanceStateChanged;
            _metadataQueryCts?.Cancel();
            _metadataQueryCts?.Dispose();
            _metadataQueryCts = null;
            _pendingCabinetSnapshot = null;
            _pendingSyncStates = null;
            _pendingLiveUpdates.Clear();
            lock (_liveUpdateQueueLock)
            {
                _liveUpdateQueue.Clear();
                _liveUpdateDispatchScheduled = false;
            }
            if (_deferredApplyTimer != null)
            {
                _deferredApplyTimer.Stop();
                _deferredApplyTimer.Tick -= DeferredApplyTimer_Tick;
                _deferredApplyTimer = null;
            }
        }

        private void OnMaintenanceStateChanged(string deviceId)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsLoaded) return;
                Device? device = _allCabinets.FirstOrDefault(candidate =>
                    string.Equals(candidate.DeviceId, deviceId,
                        StringComparison.OrdinalIgnoreCase));
                if (device == null) return;
                int filterHash = ComputeFilterHash(device);
                int overviewHash = ComputeOverviewHash(device);
                App.MaintenanceService.ApplyState(device);
                bool displayChanged = device.NotifyRuntimeDataChangedIfNeeded();
                if (filterHash != ComputeFilterHash(device)) ApplyFilter();
                if (displayChanged && overviewHash != ComputeOverviewHash(device))
                    UpdateOverview();
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnDevicePresenceChanged(DeviceClient device)
        {
            if (DeviceService.IsTrueRoot(device)) return;
            QueueLiveDeviceUpdate(CaptureLiveDevice(device));
        }

        private void OnDeviceMessageReceived(DeviceClient? device, Message message)
        {
            if (device == null || DeviceService.IsTrueRoot(device)) return;
            if (!IsDisplayUpdateMessage(message.Cmd)) return;
            QueueLiveDeviceUpdate(CaptureLiveDevice(device));
        }

        private static bool IsDisplayUpdateMessage(string command) =>
            string.Equals(command, Protocol.CmdStatusReport, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, Protocol.CmdStatusResponse, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, Protocol.CmdRegister, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command, Protocol.CmdConfigResponse, StringComparison.OrdinalIgnoreCase);

        private void OnCabinetSyncStateChanged(
            string deviceId, CabinetExpectedSyncState expected)
        {
            DeviceClient? live = App.MeshBridge.Devices.FirstOrDefault(candidate =>
                string.Equals(candidate.DeviceId, deviceId,
                    StringComparison.OrdinalIgnoreCase));
            if (live != null) QueueLiveDeviceUpdate(CaptureLiveDevice(live));

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsLoaded) return;
                var updates = _pendingSyncStates?.ToDictionary(
                    item => item.Key, item => item.Value,
                    StringComparer.OrdinalIgnoreCase) ??
                    new Dictionary<string, CabinetExpectedSyncState>(
                        StringComparer.OrdinalIgnoreCase);
                updates[deviceId] = expected;
                if (IsScrollRefreshDeferred())
                {
                    _pendingSyncStates = updates;
                    ScheduleDeferredApply();
                }
                else
                    ApplySyncStates(updates);
            }), System.Windows.Threading.DispatcherPriority.Background);
        }

        private void QueueLiveDeviceUpdate(Device snapshot)
        {
            string key = DeviceUpdateKey(snapshot);
            lock (_liveUpdateQueueLock)
            {
                _liveUpdateQueue[key] = snapshot;
                if (_liveUpdateDispatchScheduled) return;
                _liveUpdateDispatchScheduled = true;
            }
            Dispatcher.BeginInvoke(new Action(ProcessLiveDeviceUpdates),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ProcessLiveDeviceUpdates()
        {
            List<Device> updates;
            lock (_liveUpdateQueueLock)
            {
                updates = _liveUpdateQueue.Values.ToList();
                _liveUpdateQueue.Clear();
                _liveUpdateDispatchScheduled = false;
            }
            if (!IsLoaded) return;

            bool missingDevice = false;
            bool filterChanged = false;
            bool overviewChanged = false;
            bool sortChanged = false;
            foreach (Device update in updates)
            {
                Device? target = _allCabinets.FirstOrDefault(candidate =>
                    IsSameCabinet(candidate, update));
                if (target == null)
                {
                    missingDevice = true;
                    continue;
                }
                if (IsScrollRefreshDeferred())
                    _pendingLiveUpdates[DeviceUpdateKey(update)] = update;
                else
                {
                    LiveUpdateResult result = ApplyLiveDeviceData(target, update);
                    filterChanged |= result.FilterChanged;
                    overviewChanged |= result.OverviewChanged;
                    sortChanged |= result.SortChanged;
                }
            }

            CompleteLiveDeviceUpdates(filterChanged, overviewChanged, sortChanged);

            if (_pendingLiveUpdates.Count > 0) ScheduleDeferredApply();
            if (missingDevice) RequestMissingDeviceReload();
        }

        private void RequestMissingDeviceReload()
        {
            _missingDeviceReloadPending = true;
            if (_loading) return;
            _missingDeviceReloadPending = false;
            _ = LoadCabinetsAsync(quiet: true, deferForScrolling: true);
        }

        private LiveUpdateResult ApplyLiveDeviceData(Device target, Device source)
        {
            if (source.LastSeenUnix > 0 && target.LastSeenUnix > source.LastSeenUnix)
                return default;
            int filterHash = ComputeFilterHash(target);
            int overviewHash = ComputeOverviewHash(target);
            bool sortMayHaveChanged = target.IsOnline != source.IsOnline;

            target.IsOnline = source.IsOnline;
            target.LastOnlineTime = source.LastOnlineTime;
            target.LastSeenUnix = source.LastSeenUnix;
            target.OfflineTimeUnix = source.OfflineTimeUnix;
            if (!string.IsNullOrWhiteSpace(source.DeviceId)) target.DeviceId = source.DeviceId;
            if (!string.IsNullOrWhiteSpace(source.MeshMac)) target.MeshMac = source.MeshMac;
            if (!string.IsNullOrWhiteSpace(source.FirmwareVersion))
                target.FirmwareVersion = source.FirmwareVersion;
            if (!string.IsNullOrWhiteSpace(source.HardwareVersion))
                target.HardwareVersion = source.HardwareVersion;
            target.Status = source.Status;
            App.MaintenanceService.ApplyState(target);

            bool displayChanged = target.NotifyRuntimeDataChangedIfNeeded();
            bool filterChanged = filterHash != ComputeFilterHash(target);
            return new LiveUpdateResult(
                filterChanged,
                displayChanged && overviewHash != ComputeOverviewHash(target),
                sortMayHaveChanged);
        }

        private void CompleteLiveDeviceUpdates(
            bool filterChanged, bool overviewChanged, bool sortChanged)
        {
            if (sortChanged) _allCabinets = SortCabinets(_allCabinets);
            if (filterChanged || sortChanged) ApplyFilter();
            if (overviewChanged) UpdateOverview();
        }

        private static Device CaptureLiveDevice(DeviceClient source)
        {
            DateTime lastSeen = source.LastSeen == default ? source.ConnectTime : source.LastSeen;
            return new Device
            {
                DeviceId = source.DeviceId,
                DeviceName = source.DeviceName,
                IsOnline = source.IsOnline,
                LastOnlineTime = lastSeen == default ? null : lastSeen,
                LastSeenUnix = lastSeen == default
                    ? 0 : new DateTimeOffset(lastSeen).ToUnixTimeSeconds(),
                MeshMac = source.MeshMac,
                IsRoot = source.IsRoot,
                FirmwareVersion = source.FirmwareVersion,
                HardwareVersion = source.HardwareVersion,
                Status = source.Status ?? new DeviceRuntimeStatus()
            };
        }

        private static string DeviceUpdateKey(Device device) =>
            string.IsNullOrWhiteSpace(device.MeshMac)
                ? device.DeviceId.Trim()
                : device.MeshMac.Trim();

        private async Task LoadCabinetsAsync(
            bool quiet = false, bool deferForScrolling = false)
        {
            if (_loading) return;
            _loading = true;
            if (!quiet) SetBusy(true, "正在读取柜子列表");
            try
            {
                List<Device> cabinets = await Task.Run(() =>
                {
                    List<Device> loaded = App.DeviceService.GetAllDevices()
                        .Where(device => !DeviceService.IsTrueRoot(device))
                        .ToList();
                    foreach (Device cabinet in loaded)
                        App.MaintenanceService.ApplyState(cabinet);
                    return SortCabinets(loaded);
                });
                if (!IsLoaded) return;

                if (deferForScrolling && IsScrollRefreshDeferred())
                {
                    _pendingCabinetSnapshot = cabinets;
                    ScheduleDeferredApply();
                }
                else
                {
                    _pendingCabinetSnapshot = null;
                    ApplyCabinetSnapshot(cabinets);
                }
            }
            catch (Exception ex)
            {
                if (_allCabinets.Count == 0) ApplyFilter();
                PageStatusText.Text = $"柜子列表读取失败：{ex.Message}";
            }
            finally
            {
                if (!quiet) SetBusy(false);
                _loading = false;
                if (_missingDeviceReloadPending && IsLoaded)
                    RequestMissingDeviceReload();
            }
        }

        private void ApplyCabinetSnapshot(IReadOnlyList<Device> cabinets)
        {
            CabinetMergeResult result = MergeCabinetData(cabinets);
            if (result.VisibleSetMayHaveChanged) ApplyFilter();
            if (result.DisplayChanged) UpdateOverview();
            StartMissingMetadataQueries();
            StartSyncStateQuery();
        }

        private void StartSyncStateQuery()
        {
            if (!IsLoaded) return;
            if (_syncStateQueryRunning)
            {
                _syncStateQueryPending = true;
                return;
            }
            string[] deviceIds = _allCabinets
                .Select(device => device.DeviceId)
                .Where(deviceId => !string.IsNullOrWhiteSpace(deviceId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (deviceIds.Length == 0) return;

            _syncStateQueryRunning = true;
            _ = RefreshSyncStatesAsync(deviceIds);
        }

        private async Task RefreshSyncStatesAsync(string[] deviceIds)
        {
            try
            {
                IReadOnlyDictionary<string, CabinetExpectedSyncState> states =
                    await Task.Run(() => App.CabinetSyncService
                        .GetExpectedCabinetSyncStates(deviceIds));
                if (!IsLoaded) return;
                if (IsScrollRefreshDeferred())
                {
                    _pendingSyncStates = states;
                    ScheduleDeferredApply();
                }
                else
                    ApplySyncStates(states);
            }
            catch
            {
                if (IsLoaded)
                    PageStatusText.Text = $"共 {_allCabinets.Count} 台柜子，同步状态稍后重试";
            }
            finally
            {
                _syncStateQueryRunning = false;
                if (_syncStateQueryPending && IsLoaded)
                {
                    _syncStateQueryPending = false;
                    StartSyncStateQuery();
                }
            }
        }

        private void ApplySyncStates(
            IReadOnlyDictionary<string, CabinetExpectedSyncState> states)
        {
            bool displayChanged = false;
            bool visibleSetMayHaveChanged = false;
            foreach (Device device in _allCabinets)
            {
                if (!states.TryGetValue(device.DeviceId, out CabinetExpectedSyncState expected))
                    continue;
                int filterHash = ComputeFilterHash(device);
                App.CabinetSyncService.ApplyExpectedSyncState(device, expected);
                displayChanged |= device.NotifyRuntimeDataChangedIfNeeded();
                visibleSetMayHaveChanged |= filterHash != ComputeFilterHash(device);
            }
            if (visibleSetMayHaveChanged) ApplyFilter();
            if (displayChanged) UpdateOverview();
        }

        private void MaintenancePasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.Equals(App.CurrentUser?.Role, "admin", StringComparison.OrdinalIgnoreCase)) return;
            new MaintenancePasswordWindow { Owner = Window.GetWindow(this) }.ShowDialog();
        }

        private void MaintenanceModeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.Equals(App.CurrentUser?.Role, "admin", StringComparison.OrdinalIgnoreCase)) return;
            Device[] devices = _allCabinets
                .Where(device => device.IsSelected && device.IsOnline)
                .ToArray();
            if (devices.Length == 0)
            {
                MessageBox.Show("请先勾选至少一台在线柜机", "维护模式",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            new MaintenanceModeWindow(devices) { Owner = Window.GetWindow(this) }.ShowDialog();
        }

        private void SelectAllMaintenanceCheckBox_Click(object sender, RoutedEventArgs e)
        {
            Device[] selectable = _visibleCabinets.ToArray();
            bool selectAll = selectable.Any(device => !device.IsSelected);
            foreach (Device device in selectable)
            {
                device.IsSelected = selectAll;
                device.NotifySelectionChanged();
            }
            UpdateMaintenanceSelectionState();
        }

        private void CabinetSelectionCheckBox_Click(object sender, RoutedEventArgs e) =>
            UpdateMaintenanceSelectionState();

        private CabinetMergeResult MergeCabinetData(IReadOnlyList<Device> refreshed)
        {
            List<Device> previous = _allCabinets;
            var unmatched = new List<Device>(_allCabinets);
            var merged = new List<Device>(refreshed.Count);
            bool displayChanged = false;
            bool filterDataChanged = false;
            foreach (Device source in refreshed)
            {
                Device? target = unmatched.FirstOrDefault(candidate =>
                    IsSameCabinet(candidate, source));
                if (target == null)
                {
                    source.CaptureRuntimeDataSnapshot();
                    merged.Add(source);
                    displayChanged = true;
                    filterDataChanged = true;
                    continue;
                }

                unmatched.Remove(target);
                int filterHash = ComputeFilterHash(target);
                displayChanged |= CopyCabinetData(target, source);
                filterDataChanged |= filterHash != ComputeFilterHash(target);
                merged.Add(target);
            }
            bool orderChanged = previous.Count != merged.Count ||
                previous.Where((device, index) =>
                    index >= merged.Count || !ReferenceEquals(device, merged[index])).Any();
            _allCabinets = merged;
            return new CabinetMergeResult(
                displayChanged || unmatched.Count > 0,
                filterDataChanged || unmatched.Count > 0 || orderChanged);
        }

        private static bool IsSameCabinet(Device left, Device right)
        {
            if (!string.IsNullOrWhiteSpace(left.MeshMac) &&
                !string.IsNullOrWhiteSpace(right.MeshMac) &&
                string.Equals(left.MeshMac, right.MeshMac, StringComparison.OrdinalIgnoreCase))
                return true;
            return !string.IsNullOrWhiteSpace(left.DeviceId) &&
                   string.Equals(left.DeviceId, right.DeviceId,
                       StringComparison.OrdinalIgnoreCase);
        }

        private static bool CopyCabinetData(Device target, Device source)
        {
            target.DeviceId = source.DeviceId;
            target.DeviceName = source.DeviceName;
            target.DeviceNumber = source.DeviceNumber;
            target.IpAddress = source.IpAddress;
            target.IsOnline = source.IsOnline;
            target.RegisterTime = source.RegisterTime;
            target.LastOnlineTime = source.LastOnlineTime;
            target.LastSeenUnix = source.LastSeenUnix;
            target.OfflineTimeUnix = source.OfflineTimeUnix;
            target.MeshMac = source.MeshMac;
            target.IsRoot = source.IsRoot;
            target.FirmwareVersion = source.FirmwareVersion;
            target.HardwareVersion = source.HardwareVersion;
            target.Status = source.Status;
            target.MaintenanceActive = source.MaintenanceActive;
            target.MaintenanceLockMask = source.MaintenanceLockMask;
            target.MaintenanceSource = source.MaintenanceSource;
            return target.NotifyRuntimeDataChangedIfNeeded();
        }

        private static int ComputeFilterHash(Device device)
        {
            var hash = new HashCode();
            hash.Add(device.DeviceId, StringComparer.OrdinalIgnoreCase);
            hash.Add(device.DeviceName, StringComparer.OrdinalIgnoreCase);
            hash.Add(device.DeviceNumber, StringComparer.OrdinalIgnoreCase);
            hash.Add(device.MeshMac, StringComparer.OrdinalIgnoreCase);
            hash.Add(device.IsOnline);
            hash.Add(device.AttentionKind, StringComparer.Ordinal);
            hash.Add(device.MaintenanceActive);
            return hash.ToHashCode();
        }

        private static int ComputeOverviewHash(Device device)
        {
            var hash = new HashCode();
            hash.Add(device.IsOnline);
            hash.Add(device.DataSyncText, StringComparer.Ordinal);
            hash.Add(device.NeedsAttention);
            return hash.ToHashCode();
        }

        private static List<Device> SortCabinets(IEnumerable<Device> cabinets) => cabinets
            .OrderByDescending(device => device.IsOnline)
            .ThenBy(device => device.DeviceName)
            .ThenBy(device => device.DeviceId)
            .ToList();

        private void StartMissingMetadataQueries()
        {
            if (_metadataQueryRunning || _metadataQueryCts == null ||
                _metadataQueryCts.IsCancellationRequested)
                return;

            _metadataQueryRunning = true;
            _ = QueryMissingMetadataAsync(_metadataQueryCts.Token);
        }

        private async Task QueryMissingMetadataAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    Device? device = _allCabinets.FirstOrDefault(candidate =>
                        candidate.IsOnline &&
                        (!string.IsNullOrWhiteSpace(candidate.DeviceId)) &&
                        (string.IsNullOrWhiteSpace(candidate.FirmwareVersion) ||
                         string.IsNullOrWhiteSpace(candidate.HardwareVersion)) &&
                        !_metadataQueried.Contains(MetadataQueryKey(candidate)));
                    if (device == null) break;

                    string key = MetadataQueryKey(device);
                    _metadataQueried.Add(key);
                    App.MeshBridge.SendToDevice(device.DeviceId,
                        Message.Create(Protocol.CmdReadConfig, device.DeviceId));
                    await Task.Delay(200, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _metadataQueryRunning = false;
            }
        }

        private static string MetadataQueryKey(Device device) =>
            string.IsNullOrWhiteSpace(device.MeshMac)
                ? device.DeviceId.Trim()
                : device.MeshMac.Trim();

        private void UpdateOverview()
        {
            int online = _allCabinets.Count(device => device.IsOnline);
            int synced = _allCabinets.Count(device =>
                device.IsOnline && device.DataSyncText == "已同步");
            int attention = _allCabinets.Count(device =>
                device.NeedsAttention);

            TotalCabinetText.Text = _allCabinets.Count.ToString();
            OnlineCabinetText.Text = online.ToString();
            SyncedCabinetText.Text = synced.ToString();
            AttentionCabinetText.Text = attention.ToString();

            int lagging = _allCabinets.Count(device =>
                device.IsOnline && device.NeedsAttention);
            PageStatusText.Text = lagging > 0
                ? $"共 {_allCabinets.Count} 台柜子，在线 {online}，{lagging} 台待同步或核验"
                : $"共 {_allCabinets.Count} 台柜子，在线 {online}";
        }

        private void CabinetFilter_Changed(object sender, RoutedEventArgs e)
        {
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (CabinetDataGrid == null) return;

            string keyword = CabinetSearchBox?.Text?.Trim() ?? "";
            string onlineStatus = SelectedFilterTag(OnlineStatusFilter);
            string permissionSync = SelectedFilterTag(PermissionSyncFilter);
            string maintenanceStatus = SelectedFilterTag(MaintenanceStatusFilter);
            var visible = _allCabinets.Where(device =>
            {
                bool keywordMatched = string.IsNullOrWhiteSpace(keyword) ||
                    device.DeviceName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    device.DeviceNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    device.MeshMac.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    device.DeviceId.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                bool onlineMatched = onlineStatus switch
                {
                    "online" => device.IsOnline,
                    "offline" => !device.IsOnline,
                    _ => true
                };
                bool permissionMatched = permissionSync switch
                {
                    "synced" => device.DataSyncText == "已同步",
                    "lagging" => device.IsOnline && device.AttentionKind == "lagging",
                    "unknown" => device.IsOnline && device.AttentionKind == "unknown",
                    _ => true
                };
                bool maintenanceMatched = maintenanceStatus switch
                {
                    "normal" => !device.MaintenanceActive,
                    "maintenance" => device.MaintenanceActive,
                    _ => true
                };
                return keywordMatched && onlineMatched && permissionMatched && maintenanceMatched;
            }).ToList();

            UpdateVisibleCabinets(visible);
            VisibleCountText.Text = $"{visible.Count} 台";
            EmptyStatePanel.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Text = _allCabinets.Count == 0 ? "尚未发现柜子" : "没有符合条件的柜子";
            UpdateMaintenanceSelectionState();
        }

        private void UpdateMaintenanceSelectionState()
        {
            if (SelectAllMaintenanceCheckBox == null) return;
            int visibleSelected = _visibleCabinets.Count(device => device.IsSelected);
            SelectAllMaintenanceCheckBox.IsChecked = _visibleCabinets.Count == 0 || visibleSelected == 0
                ? false
                : visibleSelected == _visibleCabinets.Count ? true : null;

            int selectedCount = _allCabinets.Count(device => device.IsSelected);
            int selectedOnlineCount = _allCabinets.Count(device => device.IsSelected && device.IsOnline);
            MaintenanceModeButton.Content = selectedOnlineCount == 0
                ? "维护模式" : $"维护模式 ({selectedOnlineCount})";
            DeleteSelectedCabinetsButton.Visibility = selectedCount > 0
                ? Visibility.Visible : Visibility.Collapsed;
            DeleteSelectedCabinetsText.Text = $"删除选中 ({selectedCount})";
        }

        private static string SelectedFilterTag(ComboBox? filter) =>
            (filter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";

        private void UpdateVisibleCabinets(IReadOnlyList<Device> visible)
        {
            for (int index = 0; index < visible.Count; index++)
            {
                Device desired = visible[index];
                if (index < _visibleCabinets.Count &&
                    ReferenceEquals(_visibleCabinets[index], desired)) continue;

                int existingIndex = _visibleCabinets.IndexOf(desired);
                if (existingIndex >= 0)
                    _visibleCabinets.Move(existingIndex, index);
                else
                    _visibleCabinets.Insert(index, desired);
            }

            while (_visibleCabinets.Count > visible.Count)
                _visibleCabinets.RemoveAt(_visibleCabinets.Count - 1);
        }

        private void CabinetDataGrid_PreviewMouseWheel(object sender, MouseWheelEventArgs e) =>
            MarkScrollInteraction();

        private void CabinetDataGrid_ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (Math.Abs(e.VerticalChange) > double.Epsilon)
                MarkScrollInteraction();
        }

        private void MarkScrollInteraction()
        {
            _lastScrollInteractionUtc = DateTime.UtcNow;
            if (_pendingCabinetSnapshot != null || _pendingSyncStates != null)
                ScheduleDeferredApply();
        }

        private bool IsScrollRefreshDeferred() =>
            DateTime.UtcNow - _lastScrollInteractionUtc < ScrollRefreshDelay;

        private void ScheduleDeferredApply()
        {
            if (_deferredApplyTimer == null) return;
            _deferredApplyTimer.Stop();
            _deferredApplyTimer.Interval = ScrollRefreshDelay;
            _deferredApplyTimer.Start();
        }

        private void DeferredApplyTimer_Tick(object? sender, EventArgs e)
        {
            if (IsScrollRefreshDeferred()) return;
            _deferredApplyTimer?.Stop();

            List<Device>? cabinets = _pendingCabinetSnapshot;
            IReadOnlyDictionary<string, CabinetExpectedSyncState>? states = _pendingSyncStates;
            List<Device> liveUpdates = _pendingLiveUpdates.Values.ToList();
            _pendingCabinetSnapshot = null;
            _pendingSyncStates = null;
            _pendingLiveUpdates.Clear();

            if (cabinets != null) ApplyCabinetSnapshot(cabinets);
            if (states != null) ApplySyncStates(states);
            bool filterChanged = false;
            bool overviewChanged = false;
            bool sortChanged = false;
            foreach (Device update in liveUpdates)
            {
                Device? target = _allCabinets.FirstOrDefault(candidate =>
                    IsSameCabinet(candidate, update));
                if (target == null) continue;
                LiveUpdateResult result = ApplyLiveDeviceData(target, update);
                filterChanged |= result.FilterChanged;
                overviewChanged |= result.OverviewChanged;
                sortChanged |= result.SortChanged;
            }
            CompleteLiveDeviceUpdates(filterChanged, overviewChanged, sortChanged);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
            await LoadCabinetsAsync(quiet: true);

        private void FirmwareUpgradeButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var window = new CabinetOtaWindow { Owner = Window.GetWindow(this) };
                window.ShowDialog();
            }
            catch (Exception ex)
            {
                AppToast.Error("固件升级页面打开失败");
                MessageBox.Show($"固件升级页面加载失败：{ex.Message}",
                    "页面加载失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ResyncButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var window = new CabinetPermissionSyncWindow { Owner = Window.GetWindow(this) };
                window.ShowDialog();
                await LoadCabinetsAsync(quiet: true);
            }
            catch (Exception ex)
            {
                AppToast.Error("柜机权限同步页面打开失败");
                MessageBox.Show($"柜机权限同步页面加载失败：{ex.Message}",
                    "页面加载失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenDetailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: Device device }) OpenDetail(device);
        }

        private async void SyncOneButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Device device }) return;
            if (!device.IsOnline)
            {
                App.CabinetSyncQueueService.EnqueueCabinet(device.DeviceId, "手动同步（离线排队）");
                App.CabinetSyncQueueService.Trigger();
                AppToast.Info($"{device.DisplayIdentity} 离线，已加入待同步队列");
                return;
            }

            SetBusy(true, $"正在同步 {device.DisplayIdentity}…");
            try
            {
                var progress = new Progress<string>(stage =>
                    PageStatusText.Text = $"{device.DisplayIdentity}：{stage}");
                CabinetDataSyncResult result = await App.CabinetSyncService
                    .SyncCabinetDataAsync(device.DeviceId, progress);
                if (result.Success)
                    AppToast.Success($"{device.DisplayIdentity} 已同步");
                else
                {
                    AppToast.Warning($"{device.DisplayIdentity} 同步未完成");
                    MessageBox.Show(result.FormatForDisplay(), "同步未完成",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                await LoadCabinetsAsync(quiet: true);
            }
            catch (Exception ex)
            {
                AppToast.Error($"同步失败：{ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void DeleteSelectedCabinetsButton_Click(object sender, RoutedEventArgs e) =>
            await DeleteCabinetsAsync(_allCabinets.Where(device => device.IsSelected).ToArray());

        private async void DeleteDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Device device }) return;
            await DeleteCabinetsAsync(new[] { device });
        }

        private async Task DeleteCabinetsAsync(IReadOnlyList<Device> targets)
        {
            if (targets.Count == 0) return;

            var assignments = new Dictionary<Device, IReadOnlyList<User>>();
            SetBusy(true, targets.Count == 1
                ? $"正在检查 {targets[0].DisplayIdentity} 的学生绑定"
                : $"正在检查 {targets.Count} 台柜子的学生绑定");
            try
            {
                for (int index = 0; index < targets.Count; index++)
                {
                    Device device = targets[index];
                    UpdateOperationProgress(
                        $"正在检查学生绑定：{device.DisplayIdentity}（{index + 1}/{targets.Count}）",
                        index, targets.Count);
                    assignments[device] = await Task.Run(() =>
                        App.CabinetBindingService.GetAssignedStudents(device.DeviceId));
                }
                UpdateOperationProgress("学生绑定检查完成，等待确认", targets.Count, targets.Count);
            }
            catch (Exception ex)
            {
                AppToast.Error($"学生绑定读取失败：{ex.Message}");
                return;
            }
            finally
            {
                SetBusy(false);
            }

            int assignedStudentCount = assignments.Sum(pair => pair.Value.Count);
            string confirmMessage;
            if (targets.Count == 1)
            {
                Device device = targets[0];
                IReadOnlyList<User> assignedStudents = assignments[device];
                string bindingNote = assignedStudents.Count == 0
                    ? ""
                    : "\n\n已绑定以下学生，删除后将同时解除绑定：\n" +
                      string.Join("\n", assignedStudents.Select((student, index) =>
                    $"{index + 1}. {student.Name}（学号：{student.DisplayId}）"));
                confirmMessage = $"确认删除柜子「{device.DisplayIdentity}」？{bindingNote}";
            }
            else
            {
                string bindingNote = assignedStudentCount == 0
                    ? ""
                    : $"\n其中 {assignments.Count(pair => pair.Value.Count > 0)} 台柜子共绑定 " +
                      $"{assignedStudentCount} 名学生，删除后将同时解除绑定。";
                confirmMessage = $"确认批量删除选中的 {targets.Count} 台柜子？{bindingNote}";
            }
            if (MessageBox.Show(confirmMessage, "删除柜子", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            SetBusy(true, targets.Count == 1
                ? $"正在删除 {targets[0].DisplayIdentity}"
                : $"正在删除 {targets.Count} 台柜子");
            int successCount = 0;
            int removedStudentCount = 0;
            var failures = new List<string>();
            try
            {
                for (int index = 0; index < targets.Count; index++)
                {
                    Device device = targets[index];
                    UpdateOperationProgress(
                        $"正在删除并解绑：{device.DisplayIdentity}（{index + 1}/{targets.Count}）",
                        index, targets.Count);
                    try
                    {
                        (bool saved, int affectedStudents, string error) result = await Task.Run(() =>
                        {
                            bool saved = App.DeviceService.DeleteDevice(
                                device, out int affectedStudents, out string error);
                            return (saved, affectedStudents, error);
                        });
                        if (!result.saved)
                        {
                            failures.Add($"{device.DisplayIdentity}：" +
                                (string.IsNullOrWhiteSpace(result.error) ? "删除失败" : result.error));
                            continue;
                        }
                        device.IsSelected = false;
                        successCount++;
                        removedStudentCount += result.affectedStudents;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{device.DisplayIdentity}：{ex.Message}");
                    }
                    UpdateOperationProgress(
                        $"已处理 {index + 1}/{targets.Count} 台柜子",
                        index + 1, targets.Count);
                }

                UpdateOperationProgress("删除处理完成，正在刷新柜子列表", targets.Count, targets.Count);
                await LoadCabinetsAsync(quiet: true);
                if (failures.Count == 0)
                {
                    string resultMessage = removedStudentCount > 0
                        ? $"已删除 {successCount} 台柜子，并解除 {removedStudentCount} 名学生的绑定"
                        : $"已删除 {successCount} 台柜子";
                    PageStatusText.Text = resultMessage;
                    AppToast.Success(resultMessage);
                }
                else
                {
                    PageStatusText.Text = $"删除完成：成功 {successCount} 台，失败 {failures.Count} 台";
                    AppToast.Warning($"已删除 {successCount} 台，{failures.Count} 台失败");
                    MessageBox.Show(string.Join("\n", failures), "部分柜子删除失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void EditDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Device device }) return;
            var dialog = new Window
            {
                Title = "修改柜子信息",
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = FindResource("BackgroundBrush") as System.Windows.Media.Brush
            };
            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            panel.Children.Add(new TextBlock
            {
                Text = $"CAB MAC：{device.MeshMac}\n通讯 ID：{device.DeviceId}",
                Foreground = FindResource("SubTextBrush") as System.Windows.Media.Brush,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14)
            });
            panel.Children.Add(new TextBlock
            {
                Text = "柜子名称",
                Style = FindResource("LabelText") as Style,
                Margin = new Thickness(0, 0, 0, 6)
            });
            var nameInput = new TextBox
            {
                Text = device.DeviceName,
                Height = 36,
                MaxLength = 32
            };
            panel.Children.Add(nameInput);
            panel.Children.Add(new TextBlock
            {
                Text = "现场设备编号",
                Style = FindResource("LabelText") as Style,
                Margin = new Thickness(0, 14, 0, 6)
            });
            var numberInput = new TextBox
            {
                Text = device.DeviceNumber,
                Height = 36,
                MaxLength = 32
            };
            panel.Children.Add(numberInput);
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var cancel = new Button
            {
                Content = "取消",
                Style = FindResource("SecondaryButton") as Style
            };
            var save = new Button { Content = "保存", Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(cancel);
            buttons.Children.Add(save);
            panel.Children.Add(buttons);
            dialog.Content = panel;
            cancel.Click += (_, _) => dialog.Close();
            save.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(nameInput.Text))
                {
                    MessageBox.Show("柜子名称不能为空", "输入提示",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    nameInput.Focus();
                    return;
                }
                dialog.DialogResult = true;
            };
            dialog.ContentRendered += (_, _) =>
            {
                nameInput.Focus();
                Keyboard.Focus(nameInput);
                nameInput.SelectAll();
            };

            if (dialog.ShowDialog() != true) return;

            // WPF 控件只能由 UI 线程访问；后台保存仅使用复制后的普通字符串。
            string deviceName = nameInput.Text;
            string deviceNumber = numberInput.Text;
            SetBusy(true, "正在保存柜子信息");
            try
            {
                (bool saved, string error) result = await Task.Run(() =>
                {
                    bool saved = App.DeviceService.UpdateDeviceInfo(
                        device, deviceName, deviceNumber, out string error);
                    return (saved, error);
                });
                if (!result.saved)
                {
                    AppToast.Warning(string.IsNullOrWhiteSpace(result.error) ? "保存失败" : result.error);
                    return;
                }
                AppToast.Success("柜子信息已保存");
                await LoadCabinetsAsync(quiet: true);
            }
            catch (Exception ex)
            {
                AppToast.Error($"柜子信息保存失败：{ex.Message}");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void CabinetDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (CabinetDataGrid.SelectedItem is Device device) OpenDetail(device);
        }

        private void CabinetDataGrid_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || CabinetDataGrid.SelectedItem is not Device device) return;
            e.Handled = true;
            OpenDetail(device);
        }

        private void OpenDetail(Device device) =>
            NavigationService?.Navigate(new DevicePage(device));

        private void SetBusy(bool busy, string? status = null)
        {
            RefreshButton.IsEnabled = !busy;
            ResyncButton.IsEnabled = !busy;
            FirmwareUpgradeButton.IsEnabled = !busy;
            MaintenancePasswordButton.IsEnabled = !busy;
            MaintenanceModeButton.IsEnabled = !busy;
            DeleteSelectedCabinetsButton.IsEnabled = !busy;
            CabinetDataGrid.IsEnabled = !busy;
            OperationProgressPanel.Visibility = busy
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(status))
            {
                PageStatusText.Text = status;
                OperationProgressText.Text = status;
            }
            if (busy)
            {
                OperationProgressBar.IsIndeterminate = true;
                OperationProgressBar.Value = 0;
            }
        }

        private void UpdateOperationProgress(string status, int completed, int total)
        {
            PageStatusText.Text = status;
            OperationProgressText.Text = status;
            OperationProgressBar.IsIndeterminate = false;
            OperationProgressBar.Value = total <= 0
                ? 0
                : Math.Clamp(completed * 100d / total, 0d, 100d);
        }

        private readonly record struct CabinetMergeResult(
            bool DisplayChanged, bool VisibleSetMayHaveChanged);

        private readonly record struct LiveUpdateResult(
            bool FilterChanged, bool OverviewChanged, bool SortChanged);
    }
}
