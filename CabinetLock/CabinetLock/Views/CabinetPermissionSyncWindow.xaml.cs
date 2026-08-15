using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace CabinetLock
{
    public partial class CabinetPermissionSyncWindow : BorderlessWindow
    {
        private readonly ObservableCollection<CabinetPermissionSyncRow> _rows = new();
        private readonly CancellationTokenSource _cts = new();
        private bool _busy;

        public CabinetPermissionSyncWindow()
        {
            InitializeComponent();
            CabinetGrid.ItemsSource = _rows;
            Loaded += async (_, _) => await LoadRowsAsync();
        }

        private async Task<bool> LoadRowsAsync()
        {
            SetBusy(true, "正在读取柜机状态");
            bool loaded = false;
            try
            {
                List<Device> devices = (await Task.Run(App.DeviceService.GetAllDevices, _cts.Token))
                    .Where(device => !DeviceService.IsTrueRoot(device))
                    .OrderByDescending(device => device.IsOnline)
                    .ThenBy(device => device.DeviceNumber)
                    .ThenBy(device => device.DeviceName)
                    .ToList();

                string[] deviceIds = devices.Select(device => device.DeviceId).ToArray();
                IReadOnlyDictionary<string, CabinetExpectedSyncState> expectedStates =
                    await Task.Run(() => App.CabinetSyncService
                        .GetExpectedCabinetSyncStates(deviceIds), _cts.Token);
                _rows.Clear();
                foreach (Device device in devices)
                {
                    if (expectedStates.TryGetValue(device.DeviceId, out CabinetExpectedSyncState expected))
                        App.CabinetSyncService.ApplyExpectedSyncState(device, expected);
                    _rows.Add(new CabinetPermissionSyncRow(device));
                }

                SyncProgressBar.Value = 0;
                ProgressText.Text = "选择单台同步，或手动同步全部在线柜机";
                loaded = true;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                PageStatusText.Text = $"柜机状态读取失败：{ex.Message}";
                ProgressText.Text = "请检查业务数据和设备连接后重试";
            }
            finally
            {
                SetBusy(false);
                if (loaded) UpdateSummary();
            }
            return loaded;
        }

        private async Task SyncRowsAsync(IReadOnlyList<CabinetPermissionSyncRow> rows)
        {
            if (_busy || rows.Count == 0) return;

            SetBusy(true, "正在逐台分析差异并增量同步柜机指纹与权限");
            int completed = 0;
            int success = 0;
            try
            {
                await App.CommunicationCoordinator.RunExclusiveAsync(
                    CommunicationOperationKind.CabinetSync,
                    rows.Count == 1
                        ? $"手动同步柜机 {rows[0].DeviceId}"
                        : $"手动批量同步 {rows.Count} 台柜机",
                    rows.Count == 1 ? rows[0].DeviceId : "",
                    async token =>
                    {
                        foreach (CabinetPermissionSyncRow row in rows)
                        {
                            token.ThrowIfCancellationRequested();
                            row.BeginSync();
                            UpdateProgress(completed, rows.Count,
                                $"正在同步 {row.DisplayName}");

                            CabinetDataSyncResult result = await SyncRowOnceAsync(
                                row, completed, rows.Count, token);
                            if (!result.Success && ShouldRetry(result))
                            {
                                row.UpdateStage("链路未确认，稳定后自动重试一次");
                                UpdateProgress(completed, rows.Count,
                                    $"{row.DisplayName}：正在重试本柜机");
                                await Task.Delay(1000, token);
                                result = await SyncRowOnceAsync(
                                    row, completed, rows.Count, token);
                            }

                            if (result.Success)
                            {
                                row.MarkSuccess(result);
                                App.CabinetSyncQueueService
                                    .CompletePermissionJobsForDevice(row.DeviceId);
                                success++;
                            }
                            else
                            {
                                row.MarkFailed(result);
                            }

                            completed++;
                            UpdateProgress(completed, rows.Count,
                                $"已完成 {row.DisplayName}");
                            UpdateSummary();
                            await Task.Delay(result.Success ? 250 : 750, token);
                        }
                    },
                    _cts.Token);

                int failed = completed - success;
                int online = _rows.Count(row => row.IsOnline);
                int synced = _rows.Count(row => row.Status == "已同步");
                PageStatusText.Text = failed == 0
                    ? $"全部 {synced}/{online} 台在线柜机指纹与权限已同步"
                    : $"已完成 {completed} 台，成功 {success} 台，未完成 {failed} 台";
                if (failed == 0)
                    AppToast.Success("全部在线柜机指纹与权限已同步");
                else
                    AppToast.Warning($"{failed} 台柜机未完成，可在窗口内重试");
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                SetBusy(false);
                UpdateSummary(keepStatus: true);
            }
        }

        private async Task<CabinetDataSyncResult> SyncRowOnceAsync(
            CabinetPermissionSyncRow row, int completed, int total,
            CancellationToken cancellationToken)
        {
            try
            {
                var progress = new Progress<string>(stage =>
                {
                    row.UpdateStage(stage);
                    UpdateProgress(completed, total,
                        $"{row.DisplayName}：{stage}");
                });
                return await App.CabinetSyncService.SyncCabinetDataAsync(
                    row.DeviceId, progress, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return CabinetDataSyncResult.Failed(row.DeviceId, ex.Message);
            }
        }

        private static bool ShouldRetry(CabinetDataSyncResult result)
        {
            if (result.Success) return false;
            string detail = result.PermissionResult.ErrorMessage + " " +
                string.Join(" ", result.FingerprintFailures);
            return detail.Contains("超时", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("未确认", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("无法读取", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("未读取到", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("链路", StringComparison.OrdinalIgnoreCase) ||
                detail.Contains("路由", StringComparison.OrdinalIgnoreCase);
        }

        private async void SyncOneButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is CabinetPermissionSyncRow { IsOnline: true } row)
                await SyncRowsAsync(new[] { row });
        }

        private async void RetryFailedButton_Click(object sender, RoutedEventArgs e)
        {
            List<CabinetPermissionSyncRow> failedRows = _rows
                .Where(row => row.IsOnline && row.Status == "未完成")
                .ToList();
            if (failedRows.Count == 0)
            {
                AppToast.Info("当前没有需要重试的柜机");
                return;
            }

            await SyncRowsAsync(failedRows);
        }

        private async void SyncAllButton_Click(object sender, RoutedEventArgs e)
        {
            List<CabinetPermissionSyncRow> rows = _rows
                .Where(row => row.IsOnline).ToList();
            if (rows.Count == 0)
            {
                AppToast.Info("当前没有在线柜机");
                return;
            }
            if (MessageBox.Show(
                    $"确认增量同步全部 {rows.Count} 台在线柜机？\n\n" +
                    "已有且一致的数据会跳过；仅新增或变更项会下发。" +
                    "同步期间通讯通道将专用于本次操作。",
                    "同步全部在线柜机", MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;
            await SyncRowsAsync(rows);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
            await LoadRowsAsync();

        private void UpdateProgress(int completed, int total, string text)
        {
            SyncProgressBar.Value = total <= 0 ? 0 : completed * 100.0 / total;
            ProgressText.Text = total <= 0 ? text : $"{text} · {completed}/{total}";
        }

        private void UpdateSummary(bool keepStatus = false)
        {
            int online = _rows.Count(row => row.IsOnline);
            int success = _rows.Count(row => row.Status == "已同步");
            int failed = _rows.Count(row => row.Status == "未完成");
            CabinetCountText.Text = $"柜机 {_rows.Count}";
            OnlineCountText.Text = $"在线 {online}";
            SuccessCountText.Text = $"成功 {success}";
            FailedCountText.Text = $"未完成 {failed}";
            RetryFailedButton.IsEnabled = !_busy && failed > 0;
            if (!keepStatus && !_busy)
                PageStatusText.Text = $"在线 {online} 台，完整已同步 {success} 台";
        }

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            RefreshButton.IsEnabled = !busy;
            RetryFailedButton.IsEnabled = !busy && _rows.Any(row => row.Status == "未完成");
            SyncAllButton.IsEnabled = !busy && _rows.Any(row => row.IsOnline);
            CabinetGrid.IsEnabled = !busy;
            CloseButton.IsEnabled = !busy;
            if (!string.IsNullOrWhiteSpace(status)) PageStatusText.Text = status;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosed(EventArgs e)
        {
            _cts.Cancel();
            base.OnClosed(e);
        }
    }

    public sealed class CabinetPermissionSyncRow : INotifyPropertyChanged
    {
        private uint _currentVersion;
        private double _progress;
        private bool _isIndeterminate;
        private string _status;
        private string _detail;

        public CabinetPermissionSyncRow(Device device, uint? expectedVersion = null)
        {
            DeviceId = device.DeviceId;
            DeviceNumber = device.DeviceNumber;
            DeviceName = device.DeviceName;
            MeshMac = device.MeshMac;
            IsOnline = device.IsOnline;
            _currentVersion = device.Status.PermissionVersion;
            if (expectedVersion.HasValue)
                device.RootPermissionVersion = expectedVersion.Value;
            ExpectedVersion = device.RootPermissionVersion;
            ExpectedCount = device.ExpectedFingerprintCount;
            _fingerprintCount = device.Status.FingerprintCount;
            _permissionCount = device.Status.PermissionCount;
            bool alreadySynced = device.DataSyncText == "已同步";
            _status = !device.IsOnline ? "离线" : alreadySynced ? "已同步" : "待同步";
            _detail = !device.IsOnline
                ? "等待柜机上线"
                : alreadySynced
                    ? "指纹内容、权限版本和权限条数均已确认"
                    : device.DataSyncText switch
                    {
                        "指纹缺失" => "柜机指纹数量不足，需要补写",
                        "权限落后" => "权限版本不一致，需要同步",
                        "权限不完整" => "柜机权限记录不完整，需要同步",
                        "待核验" => "权限一致，指纹内容尚未逐枚核验",
                        _ => "柜机状态不完整，需要重新校验"
                    };
            _progress = alreadySynced ? 100 : 0;
        }

        public string DeviceId { get; }
        public string DeviceNumber { get; }
        public string DeviceName { get; }
        public string MeshMac { get; }
        public bool IsOnline { get; }
        public uint ExpectedVersion { get; }
        public int ExpectedCount { get; }
        private int _fingerprintCount;
        private int _permissionCount;
        public string OnlineText => IsOnline ? "在线" : "离线";
        public string DisplayName => string.IsNullOrWhiteSpace(DeviceNumber)
            ? DeviceName
            : $"{DeviceNumber} · {DeviceName}";
        public string VersionText => $"{FormatVersion(_currentVersion)} / {FormatVersion(ExpectedVersion)}";
        public string CountText => $"{_fingerprintCount} / {_permissionCount}";
        public double Progress
        {
            get => _progress;
            private set
            {
                if (Set(ref _progress, value)) OnPropertyChanged(nameof(ProgressText));
            }
        }
        public bool IsIndeterminate
        {
            get => _isIndeterminate;
            private set
            {
                if (Set(ref _isIndeterminate, value)) OnPropertyChanged(nameof(ProgressText));
            }
        }
        public string ProgressText => IsIndeterminate ? "同步中" : $"{Progress:0}%";
        public string Status
        {
            get => _status;
            private set
            {
                if (Set(ref _status, value)) OnPropertyChanged(nameof(CanSync));
            }
        }
        public string Detail { get => _detail; private set => Set(ref _detail, value); }
        public bool NeedsSync => IsOnline && Status != "已同步" && Status != "同步中";
        public bool CanSync => NeedsSync;

        public void BeginSync()
        {
            Progress = 0;
            IsIndeterminate = true;
            Status = "同步中";
            OnPropertyChanged(nameof(NeedsSync));
            Detail = "正在读取槽位并分析增量差异";
        }

        public void UpdateStage(string stage) => Detail = stage;

        public void MarkSuccess(CabinetDataSyncResult result)
        {
            _currentVersion = ExpectedVersion;
            _fingerprintCount = result.ConfirmedFingerprintCount;
            _permissionCount = result.PermissionRecordCount;
            OnPropertyChanged(nameof(VersionText));
            OnPropertyChanged(nameof(CountText));
            IsIndeterminate = false;
            Progress = 100;
            Status = "已同步";
            OnPropertyChanged(nameof(NeedsSync));
            Detail = result.UsedFullPermissionSync
                ? $"指纹 {result.ConfirmedFingerprintCount} 枚，权限 {result.PermissionRecordCount} 条完整确认"
                : $"指纹 {result.ConfirmedFingerprintCount} 枚（新增 {result.RestoredFingerprintCount}），" +
                  $"权限 {result.PermissionRecordCount} 条（本次更新 {result.PermissionUpdatedCount}）";
        }

        public void MarkFailed(CabinetDataSyncResult result)
        {
            if (result.PermissionResult.Success)
            {
                _currentVersion = ExpectedVersion;
                _permissionCount = result.PermissionRecordCount;
                OnPropertyChanged(nameof(VersionText));
                OnPropertyChanged(nameof(CountText));
            }
            IsIndeterminate = false;
            Progress = 100;
            Status = "未完成";
            OnPropertyChanged(nameof(NeedsSync));
            Detail = result.FormatForDisplay().Replace(Environment.NewLine, "；");
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        private static string FormatVersion(uint version) => version == 0 ? "未上报" : version.ToString();
    }
}
