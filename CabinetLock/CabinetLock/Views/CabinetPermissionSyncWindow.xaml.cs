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
            Loaded += async (_, _) => await LoadAndSyncAsync();
        }

        private async Task LoadAndSyncAsync()
        {
            if (!await LoadRowsAsync() || _cts.IsCancellationRequested) return;

            List<CabinetPermissionSyncRow> pendingRows = _rows
                .Where(row => row.NeedsSync)
                .ToList();
            if (pendingRows.Count == 0)
            {
                int online = _rows.Count(row => row.IsOnline);
                PageStatusText.Text = online == 0
                    ? "没有在线柜机可同步"
                    : $"全部 {online} 台在线柜机权限已同步";
                ProgressText.Text = online == 0
                    ? "等待柜机上线后可重新读取"
                    : "权限版本均已一致，无需重复确认";
                return;
            }

            await SyncRowsAsync(pendingRows);
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

                uint expectedVersion = await Task.Run(
                    CabinetSyncService.GetExpectedPermissionVersion, _cts.Token);
                _rows.Clear();
                foreach (Device device in devices)
                    _rows.Add(new CabinetPermissionSyncRow(device, expectedVersion));

                SyncProgressBar.Value = 0;
                ProgressText.Text = "";
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

            SetBusy(true, "正在逐台同步柜机权限");
            int completed = 0;
            int success = 0;
            try
            {
                foreach (CabinetPermissionSyncRow row in rows)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    row.BeginSync();
                    UpdateProgress(completed, rows.Count, $"正在同步 {row.DisplayName}");

                    BroadcastCommandResult result;
                    try
                    {
                        result = await Task.Run(
                            () => App.CabinetSyncService.SyncCabinetPermissions(row.DeviceId),
                            _cts.Token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        result = BroadcastCommandResult.Failed(ex.Message, new[] { row.DeviceId });
                    }

                    if (result.Success)
                    {
                        row.MarkSuccess();
                        success++;
                    }
                    else
                    {
                        row.MarkFailed(string.IsNullOrWhiteSpace(result.ErrorMessage)
                            ? "柜机未确认权限同步"
                            : result.ErrorMessage);
                    }

                    completed++;
                    UpdateProgress(completed, rows.Count, $"已完成 {row.DisplayName}");
                    UpdateSummary();
                }

                int failed = completed - success;
                int online = _rows.Count(row => row.IsOnline);
                int synced = _rows.Count(row => row.Status == "已同步");
                PageStatusText.Text = failed == 0
                    ? $"全部 {synced}/{online} 台在线柜机权限已同步"
                    : $"已完成 {completed} 台，成功 {success} 台，失败 {failed} 台";
                if (failed == 0)
                    AppToast.Success("全部在线柜机权限已同步");
                else
                    AppToast.Warning($"{failed} 台柜机权限同步失败，可在窗口内重试");
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

        private async void SyncOneButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.Tag is CabinetPermissionSyncRow { IsOnline: true } row)
                await SyncRowsAsync(new[] { row });
        }

        private async void RetryFailedButton_Click(object sender, RoutedEventArgs e)
        {
            List<CabinetPermissionSyncRow> failedRows = _rows
                .Where(row => row.IsOnline && row.Status == "失败")
                .ToList();
            if (failedRows.Count == 0)
            {
                AppToast.Info("当前没有需要重试的柜机");
                return;
            }

            await SyncRowsAsync(failedRows);
        }

        private async void SyncAllButton_Click(object sender, RoutedEventArgs e) =>
            await SyncRowsAsync(_rows.Where(row => row.NeedsSync).ToList());

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
            int failed = _rows.Count(row => row.Status == "失败");
            CabinetCountText.Text = $"柜机 {_rows.Count}";
            OnlineCountText.Text = $"在线 {online}";
            SuccessCountText.Text = $"成功 {success}";
            FailedCountText.Text = $"失败 {failed}";
            RetryFailedButton.IsEnabled = !_busy && failed > 0;
            if (!keepStatus && !_busy)
                PageStatusText.Text = $"在线 {online} 台，待同步 {_rows.Count(row => row.NeedsSync)} 台";
        }

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            RefreshButton.IsEnabled = !busy;
            RetryFailedButton.IsEnabled = !busy && _rows.Any(row => row.Status == "失败");
            SyncAllButton.IsEnabled = !busy && _rows.Any(row => row.NeedsSync);
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

        public CabinetPermissionSyncRow(Device device, uint expectedVersion)
        {
            DeviceId = device.DeviceId;
            DeviceNumber = device.DeviceNumber;
            DeviceName = device.DeviceName;
            MeshMac = device.MeshMac;
            IsOnline = device.IsOnline;
            _currentVersion = device.Status.PermissionVersion;
            ExpectedVersion = expectedVersion;
            bool alreadySynced = device.IsOnline && device.Status.PermissionVersion != 0 &&
                expectedVersion != 0 && device.Status.PermissionVersion == expectedVersion;
            _status = !device.IsOnline ? "离线" : alreadySynced ? "已同步" : "待同步";
            _detail = !device.IsOnline
                ? "等待柜机上线"
                : alreadySynced
                    ? "当前权限版本已一致"
                    : device.Status.PermissionVersion == 0
                        ? "柜机权限版本未知，等待同步"
                        : "权限版本不一致，等待同步";
            _progress = alreadySynced ? 100 : 0;
        }

        public string DeviceId { get; }
        public string DeviceNumber { get; }
        public string DeviceName { get; }
        public string MeshMac { get; }
        public bool IsOnline { get; }
        public uint ExpectedVersion { get; }
        public string OnlineText => IsOnline ? "在线" : "离线";
        public string DisplayName => string.IsNullOrWhiteSpace(DeviceNumber)
            ? DeviceName
            : $"{DeviceNumber} · {DeviceName}";
        public string VersionText => $"{FormatVersion(_currentVersion)} / {FormatVersion(ExpectedVersion)}";
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
            Detail = "正在下发并提交柜机权限";
        }

        public void MarkSuccess()
        {
            _currentVersion = ExpectedVersion;
            OnPropertyChanged(nameof(VersionText));
            IsIndeterminate = false;
            Progress = 100;
            Status = "已同步";
            OnPropertyChanged(nameof(NeedsSync));
            Detail = $"权限版本 {FormatVersion(ExpectedVersion)} 已确认";
        }

        public void MarkFailed(string error)
        {
            IsIndeterminate = false;
            Progress = 100;
            Status = "失败";
            OnPropertyChanged(nameof(NeedsSync));
            Detail = error;
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
