using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.Win32;

namespace CabinetLock
{
    internal sealed class CabinetOtaNodeRow : INotifyPropertyChanged
    {
        private CabinetOtaNodeStatus _node;

        public CabinetOtaNodeRow(CabinetOtaNodeStatus node) => _node = node;

        public string DeviceId => _node.DeviceId;
        public string LayerText => _node.MeshLayer > 0 ? $"L{_node.MeshLayer}" : "--";
        public string ParentText => string.IsNullOrWhiteSpace(_node.ParentDeviceId)
            ? "未上报" : _node.ParentDeviceId;
        public string VersionText => string.IsNullOrWhiteSpace(_node.Version)
            ? "--" : _node.Version;
        public string StatusText => PhaseText(_node.Phase);
        public string PhaseKind => GetPhaseKind(_node.Phase);
        public int Progress => PhaseKind == "complete" ? 100 : _node.Progress;
        public string ProgressText => $"{Progress}%";
        public string RetryText => _node.RetryCount == 0 ? "--" : _node.RetryCount.ToString();
        public string UpdatedText => FormatAge(_node.UpdatedAgoSeconds);
        public string ErrorText
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(_node.Error)) return _node.Error;
                return PhaseKind switch
                {
                    "offline" => "设备当前不在线",
                    "incompatible" => "硬件版本不匹配",
                    "waiting" when _node.Phase == "waiting_parent" =>
                        $"等待 {ParentText} 完成升级",
                    _ => "--"
                };
            }
        }
        public string? ErrorToolTip => ErrorText == "--" ? null : ErrorText;
        public int SortRank => PhaseKind switch
        {
            "failed" => 0,
            "active" => 1,
            "waiting" => 2,
            "incompatible" => 3,
            "complete" => 4,
            "offline" => 5,
            _ => 6
        };

        public void Update(CabinetOtaNodeStatus node)
        {
            _node = node;
            OnPropertyChanged(string.Empty);
        }

        private static string GetPhaseKind(string phase) => phase switch
        {
            "completed" or "complete" => "complete",
            "notified" or "starting" or "downloading" or "verifying" or
                "rebooting" or "validating" => "active",
            "pending" or "waiting_parent" => "waiting",
            "failed" => "failed",
            "offline" => "offline",
            "incompatible" => "incompatible",
            _ => "idle"
        };

        private static string PhaseText(string phase) => phase switch
        {
            "completed" or "complete" => "已完成",
            "notified" => "已通知",
            "starting" => "准备中",
            "downloading" => "下载中",
            "verifying" => "校验中",
            "rebooting" => "重启中",
            "validating" => "启动确认",
            "pending" => "等待调度",
            "waiting_parent" => "等待父节点",
            "failed" => "失败重试",
            "offline" => "离线",
            "incompatible" => "不兼容",
            "idle" => "未发布",
            _ => string.IsNullOrWhiteSpace(phase) ? "未知" : phase
        };

        private static string FormatAge(uint seconds)
        {
            if (seconds < 5) return "刚刚";
            if (seconds < 60) return $"{seconds} 秒前";
            if (seconds < 3600) return $"{seconds / 60} 分钟前";
            if (seconds < 86400) return $"{seconds / 3600} 小时前";
            return $"{seconds / 86400} 天前";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    public partial class CabinetOtaWindow : BorderlessWindow
    {
        private readonly ObservableCollection<CabinetOtaNodeRow> _nodeRows = new();
        private CabinetFirmwareInfo? _firmware;
        private CancellationTokenSource? _cancellation;
        private CancellationTokenSource? _pollingCancellation;
        private int _onlineCabinetCount;
        private bool _running;
        private bool _refreshing;
        private string _lastRefreshError = "";
        private string _lastLoggedStage = "";
        private int _lastLoggedPercent = -5;
        private DateTime _lastProgressLogAt = DateTime.MinValue;

        public CabinetOtaWindow()
        {
            InitializeComponent();
            NodeDataGrid.ItemsSource = _nodeRows;
            Closing += CabinetOtaWindow_Closing;
            Loaded += CabinetOtaWindow_Loaded;
            RefreshEnvironment();
        }

        private async void CabinetOtaWindow_Loaded(object sender, RoutedEventArgs e)
        {
            if (_pollingCancellation != null) return;
            _pollingCancellation = new CancellationTokenSource();
            await RefreshSnapshotAsync(false, _pollingCancellation.Token);
            _ = PollSnapshotsAsync(_pollingCancellation.Token);
        }

        private async Task PollSnapshotsAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    if (AutoRefreshCheckBox.IsChecked == true && !_running)
                        await RefreshSnapshotAsync(false, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private void RefreshEnvironment()
        {
            try
            {
                _onlineCabinetCount = App.DeviceService.GetOnlineDevices()
                    .Count(device => !DeviceService.IsTrueRoot(device));
            }
            catch
            {
                _onlineCabinetCount = App.MeshBridge.GetOnlineDevices()
                    .Count(device => !DeviceService.IsTrueRoot(device));
            }

            bool rootReady = App.MeshBridge.IsConnected &&
                !string.IsNullOrWhiteSpace(App.SdStorageService.RootDeviceId) &&
                App.SdStorageService.IsStorageReady != false;
            ConnectionText.Text = rootReady
                ? $"根节点已连接，当前在线柜机 {_onlineCabinetCount} 台"
                : "根节点或 SD 卡未就绪";
            StartButton.IsEnabled = !_running && rootReady && _firmware != null;
            QueryStatusButton.IsEnabled = !_running && !_refreshing && rootReady;
            RestrictHardwareCheckBox.IsEnabled = !_running &&
                !string.IsNullOrWhiteSpace(_firmware?.HardwareVersion);
        }

        private void ChooseFileButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择柜机 ESP-IDF 固件",
                Filter = "ESP-IDF 固件 (*.bin)|*.bin|所有文件 (*.*)|*.*",
                CheckFileExists = true
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                _firmware = App.CabinetOtaService.InspectFirmware(dialog.FileName);
                FileNameText.Text = Path.GetFileName(_firmware.FilePath);
                FileNameText.ToolTip = _firmware.FilePath;
                VersionText.Text = _firmware.Version;
                HardwareVersionText.Text = _firmware.HardwareVersion;
                ImageSizeText.Text = $"{_firmware.ImageSize / 1024.0:N1} KB";
                ShaText.Text = _firmware.Sha256;
                ShaText.ToolTip = _firmware.Sha256;
                var onlineCabinets = App.MeshBridge.GetOnlineDevices()
                    .Where(device => !DeviceService.IsTrueRoot(device)).ToList();
                RestrictHardwareCheckBox.IsChecked = onlineCabinets.Count > 0 &&
                    onlineCabinets.All(device => string.Equals(
                        device.HardwareVersion, _firmware.HardwareVersion,
                        StringComparison.OrdinalIgnoreCase));
                AppendLog($"镜像校验通过：{_firmware.Version}");
            }
            catch (Exception ex)
            {
                _firmware = null;
                FileNameText.Text = "未选择";
                VersionText.Text = "--";
                HardwareVersionText.Text = "--";
                ImageSizeText.Text = "--";
                ShaText.Text = "--";
                MessageBox.Show(ex.Message, "固件无效",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            RefreshEnvironment();
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_firmware == null) return;
            RefreshEnvironment();
            bool restrictHardware = RestrictHardwareCheckBox.IsChecked == true;
            string scope = restrictHardware
                ? $"仅限硬件 {_firmware.HardwareVersion}"
                : "不限制已上报的硬件版本";
            if (MessageBox.Show(
                    $"确认将 {_firmware.Version} 发布为柜机目标版本？\n\n范围：{scope}\n当前在线：{_onlineCabinetCount} 台",
                    "发布柜机固件", MessageBoxButton.YesNo,
                    MessageBoxImage.Question) != MessageBoxResult.Yes) return;

            SetRunning(true);
            _lastLoggedStage = "";
            _lastLoggedPercent = -5;
            _lastProgressLogAt = DateTime.MinValue;
            _cancellation = new CancellationTokenSource();
            try
            {
                var progress = new Progress<CabinetOtaProgress>(UpdateProgress);
                AppendLog("开始将目标固件发布到根节点");
                CabinetOtaStatus result = await App.CabinetOtaService.DeployAsync(
                    _firmware.FilePath, restrictHardware, progress,
                    _cancellation.Token);
                ApplyStatus(result, true);
                AppendLog($"发布成功：目标版本 {result.Version}，根节点已开始拓扑分发");
                AppToast.Success("柜机目标固件已发布，正在升级");
            }
            catch (OperationCanceledException)
            {
                StageText.Text = "发布已取消";
                AppendLog("操作已取消；已经提交的 Mesh 分发不会被强制中断");
            }
            catch (Exception ex)
            {
                StageText.Text = "发布未完成";
                AppendLog($"失败：{ex.Message}");
                MessageBox.Show(ex.Message, "柜机固件发布失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _cancellation?.Dispose();
                _cancellation = null;
                SetRunning(false);
                RefreshEnvironment();
                if (_pollingCancellation != null)
                    await RefreshSnapshotAsync(false, _pollingCancellation.Token);
            }
        }

        private async void QueryStatusButton_Click(object sender, RoutedEventArgs e)
        {
            await RefreshSnapshotAsync(true,
                _pollingCancellation?.Token ?? CancellationToken.None);
        }

        private async Task RefreshSnapshotAsync(
            bool showError, CancellationToken cancellationToken)
        {
            if (_refreshing || _running) return;
            _refreshing = true;
            QueryStatusButton.IsEnabled = false;
            LastRefreshText.Text = "正在读取根节点状态";
            try
            {
                CabinetOtaSnapshot snapshot =
                    await App.CabinetOtaService.QuerySnapshotAsync(cancellationToken);
                ApplyStatus(snapshot.Status, true);
                ApplyNodes(snapshot.Nodes);
                LastRefreshText.Text =
                    $"上次刷新 {DateTime.Now:HH:mm:ss} · 已登记 {snapshot.Nodes.Count} 台";
                if (showError)
                {
                    AppendLog($"刷新完成：{snapshot.Status.CompletedNodes} / " +
                        $"{snapshot.Status.CompatibleNodes} 台完成，" +
                        $"{snapshot.Status.PendingNodes} 台待升级");
                }
                _lastRefreshError = "";
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LastRefreshText.Text = $"刷新失败 · {ex.Message}";
                if (!string.Equals(_lastRefreshError, ex.Message, StringComparison.Ordinal))
                {
                    AppendLog($"状态刷新失败：{ex.Message}");
                    _lastRefreshError = ex.Message;
                }
                if (showError)
                {
                    MessageBox.Show(ex.Message, "查询失败",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            finally
            {
                _refreshing = false;
                RefreshEnvironment();
            }
        }

        private void ApplyNodes(IReadOnlyList<CabinetOtaNodeStatus> nodes)
        {
            string selectedId = (NodeDataGrid.SelectedItem as CabinetOtaNodeRow)?.DeviceId ?? "";
            var incomingIds = nodes.Select(node => node.DeviceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            for (int index = _nodeRows.Count - 1; index >= 0; --index)
            {
                if (!incomingIds.Contains(_nodeRows[index].DeviceId))
                    _nodeRows.RemoveAt(index);
            }

            var existing = _nodeRows.ToDictionary(
                row => row.DeviceId, StringComparer.OrdinalIgnoreCase);
            foreach (CabinetOtaNodeStatus node in nodes)
            {
                if (existing.TryGetValue(node.DeviceId, out CabinetOtaNodeRow? row))
                    row.Update(node);
                else
                    _nodeRows.Add(new CabinetOtaNodeRow(node));
            }

            var ordered = _nodeRows.OrderBy(row => row.SortRank)
                .ThenBy(row => row.LayerText, StringComparer.Ordinal)
                .ThenBy(row => row.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (int target = 0; target < ordered.Count; ++target)
            {
                int current = _nodeRows.IndexOf(ordered[target]);
                if (current != target) _nodeRows.Move(current, target);
            }

            CompletedCountText.Text = _nodeRows.Count(row => row.PhaseKind == "complete").ToString();
            ActiveCountText.Text = _nodeRows.Count(row => row.PhaseKind == "active").ToString();
            WaitingCountText.Text = _nodeRows.Count(row => row.PhaseKind == "waiting").ToString();
            FailedCountText.Text = _nodeRows.Count(row => row.PhaseKind == "failed").ToString();
            OfflineCountText.Text = _nodeRows.Count(row => row.PhaseKind == "offline").ToString();

            if (!string.IsNullOrWhiteSpace(selectedId))
            {
                NodeDataGrid.SelectedItem = _nodeRows.FirstOrDefault(row =>
                    string.Equals(row.DeviceId, selectedId,
                        StringComparison.OrdinalIgnoreCase));
            }
        }

        private void UpdateProgress(CabinetOtaProgress progress)
        {
            StageText.Text = progress.Stage;
            ProgressDetailText.Text = progress.Detail;
            OtaProgressBar.Value = Math.Clamp(progress.Percent, 0, 100);
            if (progress.ExpectedNodes > 0)
                NodeCountText.Text = $"{progress.CompletedNodes} / {progress.ExpectedNodes} 台完成";

            bool stageChanged = !string.Equals(_lastLoggedStage, progress.Stage,
                StringComparison.Ordinal);
            bool percentDue = progress.Percent >= _lastLoggedPercent + 5;
            bool timeDue = DateTime.Now - _lastProgressLogAt >= TimeSpan.FromSeconds(10);
            if (progress.IsImportant || stageChanged || percentDue || timeDue)
            {
                AppendLog($"{progress.Stage}：{progress.Detail}");
                _lastLoggedStage = progress.Stage;
                _lastLoggedPercent = progress.Percent;
                _lastProgressLogAt = DateTime.Now;
            }
        }

        private void ApplyStatus(CabinetOtaStatus status, bool updateProgress)
        {
            StageText.Text = PhaseText(status.Phase);
            uint compatible = status.CompatibleNodes > 0
                ? status.CompatibleNodes : status.ExpectedNodes;
            uint pending = status.PendingNodes > 0
                ? status.PendingNodes
                : compatible > status.CompletedNodes
                    ? compatible - status.CompletedNodes : 0;
            NodeCountText.Text = $"{status.CompletedNodes} / {compatible} 台完成";
            string hardware = string.IsNullOrWhiteSpace(status.HardwareVersion)
                ? "全部硬件" : status.HardwareVersion;
            string errorDetail = string.IsNullOrWhiteSpace(status.Error)
                ? "" : $" · {status.Error}";
            bool activePolicy = status.Active ||
                string.Equals(status.Phase, "published", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status.Phase, "distributing", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status.Phase, "complete", StringComparison.OrdinalIgnoreCase);
            ProgressDetailText.Text = activePolicy
                ? $"目标 {status.Version} · {hardware} · 在线兼容 {compatible} · 待升级 {pending} · 不兼容 {status.IncompatibleNodes} · 未知硬件 {status.UnknownHardwareNodes}{errorDetail}"
                : $"根节点尚未保存柜机目标版本{errorDetail}";
            if (updateProgress)
                OtaProgressBar.Value = activePolicy
                    ? Math.Clamp(status.MeshProgress, 0, 100) : 0;
        }

        private void AppendLog(string text)
        {
            string line = $"{DateTime.Now:HH:mm:ss}  {text}";
            LogTextBox.AppendText((LogTextBox.Text.Length == 0 ? "" : Environment.NewLine) + line);
            LogTextBox.ScrollToEnd();
        }

        private void SetRunning(bool running)
        {
            _running = running;
            ChooseFileButton.IsEnabled = !running;
            RestrictHardwareCheckBox.IsEnabled = !running && _firmware != null;
            StartButton.IsEnabled = false;
            QueryStatusButton.IsEnabled = !running && !_refreshing;
            CancelButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        }

        private static string PhaseText(string phase) => phase switch
        {
            "uploading" => "上传到根节点",
            "ready" => "镜像已就绪",
            "distributing" => "正在拓扑分发",
            "published" => "目标版本已发布",
            "complete" => "升级完成",
            "idle" => "尚未发布",
            "unavailable" => "状态不可用",
            _ => string.IsNullOrWhiteSpace(phase) ? "未知状态" : phase
        };

        private void CancelButton_Click(object sender, RoutedEventArgs e) =>
            _cancellation?.Cancel();

        private void HardwareRestriction_Changed(object sender, RoutedEventArgs e)
        {
            if (RestrictHardwareCheckBox != null)
                RefreshEnvironment();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void CabinetOtaWindow_Closing(object? sender, CancelEventArgs e)
        {
            if (_running && MessageBox.Show(
                    "固件仍在发布，确认取消本次操作并关闭窗口？",
                    "关闭发布窗口", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _cancellation?.Cancel();
            _pollingCancellation?.Cancel();
        }
    }
}
