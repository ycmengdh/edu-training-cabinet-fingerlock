using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CabinetLock
{
    public partial class CabinetManagePage : Page
    {
        private System.Windows.Threading.DispatcherTimer? _refreshTimer;
        private List<Device> _allCabinets = new();
        private readonly ObservableCollection<Device> _visibleCabinets = new();
        private readonly ListPager _pager = new(20);
        private readonly HashSet<string> _metadataQueried =
            new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _metadataQueryCts;
        private bool _metadataQueryRunning;
        private bool _loading;

        public CabinetManagePage()
        {
            InitializeComponent();
            CabinetDataGrid.ItemsSource = _visibleCabinets;
            Loaded += CabinetManagePage_Loaded;
            Unloaded += CabinetManagePage_Unloaded;
        }

        private async void CabinetManagePage_Loaded(object sender, RoutedEventArgs e)
        {
            App.MeshBridge.DeviceConnected += OnDevicePresenceChanged;
            App.MeshBridge.DeviceDisconnected += OnDevicePresenceChanged;
            _metadataQueried.Clear();
            _metadataQueryCts?.Cancel();
            _metadataQueryCts?.Dispose();
            _metadataQueryCts = new CancellationTokenSource();
            await LoadCabinetsAsync();

            _refreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _refreshTimer.Tick += RefreshTimer_Tick;
            _refreshTimer.Start();
        }

        private void CabinetManagePage_Unloaded(object sender, RoutedEventArgs e)
        {
            App.MeshBridge.DeviceConnected -= OnDevicePresenceChanged;
            App.MeshBridge.DeviceDisconnected -= OnDevicePresenceChanged;
            _metadataQueryCts?.Cancel();
            _metadataQueryCts?.Dispose();
            _metadataQueryCts = null;
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer.Tick -= RefreshTimer_Tick;
                _refreshTimer = null;
            }
        }

        private async void RefreshTimer_Tick(object? sender, EventArgs e)
        {
            if (IsLoaded && !_loading) await LoadCabinetsAsync(quiet: true);
        }

        private void OnDevicePresenceChanged(DeviceClient device)
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (IsLoaded && !_loading) await LoadCabinetsAsync(quiet: true);
            }));
        }

        private async Task LoadCabinetsAsync(bool quiet = false)
        {
            if (_loading) return;
            _loading = true;
            if (!quiet) SetBusy(true, "正在读取柜子列表");
            try
            {
                var cabinets = await Task.Run(App.DeviceService.GetAllDevices);
                cabinets = cabinets
                    .Where(device => !DeviceService.IsTrueRoot(device))
                    .OrderByDescending(device => device.IsOnline)
                    .ThenBy(device => device.DeviceName)
                    .ThenBy(device => device.DeviceId)
                    .ToList();

                uint globalVersion = 0;
                try
                {
                    globalVersion = await Task.Run(CabinetSyncService.GetExpectedPermissionVersion);
                }
                catch
                {
                }

                foreach (var cabinet in cabinets)
                    cabinet.RootPermissionVersion = globalVersion;

                MergeCabinetData(cabinets);
                ApplyFilter();
                UpdateOverview();
                StartMissingMetadataQueries();
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
            }
        }

        private void MergeCabinetData(IReadOnlyList<Device> refreshed)
        {
            var unmatched = new List<Device>(_allCabinets);
            var merged = new List<Device>(refreshed.Count);
            foreach (Device source in refreshed)
            {
                Device? target = unmatched.FirstOrDefault(candidate =>
                    IsSameCabinet(candidate, source));
                if (target == null)
                {
                    merged.Add(source);
                    continue;
                }

                unmatched.Remove(target);
                CopyCabinetData(target, source);
                merged.Add(target);
            }
            _allCabinets = merged;
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

        private static void CopyCabinetData(Device target, Device source)
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
            target.RootPermissionVersion = source.RootPermissionVersion;
        }

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
                device.IsOnline && device.PermissionSyncText == "已同步");
            int attention = _allCabinets.Count(device =>
                !device.IsOnline || device.PermissionSyncText == "落后");

            TotalCabinetText.Text = _allCabinets.Count.ToString();
            OnlineCabinetText.Text = online.ToString();
            SyncedCabinetText.Text = synced.ToString();
            AttentionCabinetText.Text = attention.ToString();

            int lagging = _allCabinets.Count(device =>
                device.IsOnline && device.PermissionSyncText == "落后");
            PageStatusText.Text = lagging > 0
                ? $"共 {_allCabinets.Count} 台柜子，在线 {online}，{lagging} 台权限待同步"
                : $"共 {_allCabinets.Count} 台柜子，在线 {online}";
        }

        private void CabinetFilter_Changed(object sender, RoutedEventArgs e)
        {
            _pager.Reset();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            if (CabinetDataGrid == null) return;

            string keyword = CabinetSearchBox?.Text?.Trim() ?? "";
            string status = (CabinetStatusFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "all";
            var visible = _allCabinets.Where(device =>
            {
                bool keywordMatched = string.IsNullOrWhiteSpace(keyword) ||
                    device.DeviceName.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    device.DeviceNumber.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    device.MeshMac.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                    device.DeviceId.Contains(keyword, StringComparison.OrdinalIgnoreCase);
                bool statusMatched = status switch
                {
                    "online" => device.IsOnline,
                    "offline" => !device.IsOnline,
                    "lagging" => device.IsOnline && device.PermissionSyncText == "落后",
                    "attention" => device.NeedsAttention,
                    _ => true
                };
                return keywordMatched && statusMatched;
            }).ToList();

            IReadOnlyList<Device> page = _pager.Slice(visible);
            UpdateVisibleCabinets(page);
            _pager.BindChrome(Pager, "台柜子");
            VisibleCountText.Text = $"{visible.Count} 台";
            EmptyStatePanel.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Text = _allCabinets.Count == 0 ? "尚未发现柜子" : "没有符合条件的柜子";
        }

        private void UpdateVisibleCabinets(IReadOnlyList<Device> page)
        {
            for (int index = 0; index < page.Count; index++)
            {
                Device desired = page[index];
                if (index < _visibleCabinets.Count &&
                    ReferenceEquals(_visibleCabinets[index], desired)) continue;

                int existingIndex = _visibleCabinets.IndexOf(desired);
                if (existingIndex >= 0)
                    _visibleCabinets.Move(existingIndex, index);
                else
                    _visibleCabinets.Insert(index, desired);
            }

            while (_visibleCabinets.Count > page.Count)
                _visibleCabinets.RemoveAt(_visibleCabinets.Count - 1);

            CabinetDataGrid.Items.Refresh();
        }

        private void Pager_PageRequested(object sender, Controls.PaginationRequestedEventArgs e)
        {
            _pager.ApplyRequest(e);
            ApplyFilter();
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

        private async void DeleteDeviceButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: Device device }) return;

            IReadOnlyList<User> assignedStudents;
            SetBusy(true, $"正在检查 {device.DisplayIdentity} 的学生绑定");
            try
            {
                assignedStudents = await Task.Run(() =>
                    App.CabinetBindingService.GetAssignedStudents(device.DeviceId));
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

            if (assignedStudents.Count > 0)
            {
                string studentList = string.Join("\n", assignedStudents.Select((student, index) =>
                    $"{index + 1}. {student.Name}（学号：{student.DisplayId}）"));
                string message =
                    $"柜子「{device.DisplayIdentity}」已绑定以下 {assignedStudents.Count} 名学生：\n\n" +
                    $"{studentList}\n\n删除柜子会同时解除以上学生与该柜子的绑定。确认继续删除？";
                if (MessageBox.Show(message, "删除柜子", MessageBoxButton.YesNo,
                        MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                    return;
            }

            SetBusy(true, $"正在删除 {device.DisplayIdentity}");
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
                    AppToast.Error(string.IsNullOrWhiteSpace(result.error)
                        ? "柜机删除失败" : result.error);
                    return;
                }

                AppToast.Success(result.affectedStudents > 0
                    ? $"柜机已删除，并解除 {result.affectedStudents} 名学生的绑定"
                    : "柜机已删除");
                await LoadCabinetsAsync(quiet: true);
            }
            catch (Exception ex)
            {
                AppToast.Error($"柜机删除失败：{ex.Message}");
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
            CabinetDataGrid.IsEnabled = !busy;
            OperationProgressPanel.Visibility = busy
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(status)) PageStatusText.Text = status;
        }
    }
}
