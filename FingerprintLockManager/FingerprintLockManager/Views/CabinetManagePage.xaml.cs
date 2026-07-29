using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FingerprintLockManager
{
    public partial class CabinetManagePage : Page
    {
        private System.Windows.Threading.DispatcherTimer? _refreshTimer;
        private List<Device> _allCabinets = new();
        private bool _loading;

        public CabinetManagePage()
        {
            InitializeComponent();
            Loaded += CabinetManagePage_Loaded;
            Unloaded += CabinetManagePage_Unloaded;
        }

        private async void CabinetManagePage_Loaded(object sender, RoutedEventArgs e)
        {
            App.MeshBridge.DeviceConnected += OnDevicePresenceChanged;
            App.MeshBridge.DeviceDisconnected += OnDevicePresenceChanged;
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

                _allCabinets = cabinets;
                ApplyFilter();
                UpdateOverview();
            }
            catch (Exception ex)
            {
                _allCabinets.Clear();
                ApplyFilter();
                PageStatusText.Text = $"柜子列表读取失败：{ex.Message}";
            }
            finally
            {
                if (!quiet) SetBusy(false);
                _loading = false;
            }
        }

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

        private void CabinetFilter_Changed(object sender, RoutedEventArgs e) => ApplyFilter();

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

            CabinetDataGrid.ItemsSource = visible;
            VisibleCountText.Text = $"{visible.Count} 台";
            EmptyStatePanel.Visibility = visible.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyStateText.Text = _allCabinets.Count == 0 ? "尚未发现柜子" : "没有符合条件的柜子";
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
            await LoadCabinetsAsync();

        private async void ResyncButton_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(true, "正在同步全部在线柜子的权限");
            try
            {
                var result = await Task.Run(App.CabinetSyncService.SyncAllPermissions);
                string summary = CabinetSyncService.FormatSyncResult(result,
                    "所有在线柜子均已确认权限同步",
                    "权限同步未全部完成");
                if (result.Success) AppToast.Success("全部在线柜权限已同步");
                else AppToast.Warning("部分柜子未确认，详见提示");
                if (!result.Success)
                    MessageBox.Show(summary, "同步提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                await LoadCabinetsAsync(quiet: true);
            }
            catch (RootDataUnavailableException ex)
            {
                AppToast.Error(ex.Message);
            }
            finally
            {
                SetBusy(false);
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
                CabinetDataSyncResult result = await App.CabinetSyncService
                    .SyncCabinetDataAsync(device.DeviceId);
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
            nameInput.SelectAll();
            nameInput.Focus();

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
            if (!string.IsNullOrWhiteSpace(status)) PageStatusText.Text = status;
        }
    }
}
