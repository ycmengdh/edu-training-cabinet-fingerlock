using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FingerprintLockManager
{
    /// <summary>
    /// 设备管理页面（左设备列表 + 右操作面板布局）
    /// 左侧：精简设备列表（在线绿点/离线红点）
    /// 右侧：操作面板（远程开锁、重新同步权限、读取设备状态、设备指纹清单、录入新指纹）
    /// 选中设备变化时自动加载该设备的指纹清单。
    /// </summary>
    public partial class DevicePage : Page
    {
        private Device? _selectedDevice;
        private System.Windows.Threading.DispatcherTimer? _refreshTimer;
        private bool _loading;
        private int _fpListLoadVersion;
        private string? _lastFpListDeviceId;

        public DevicePage()
        {
            InitializeComponent();
            Loaded += DevicePage_Loaded;
            Unloaded += DevicePage_Unloaded;
        }

        private async void DevicePage_Loaded(object sender, RoutedEventArgs e)
        {
            App.MeshBridge.DeviceConnected += OnDevicePresenceChanged;
            App.MeshBridge.DeviceDisconnected += OnDevicePresenceChanged;
            if (LockSelectBox.Items.Count > 0)
            {
                LockSelectBox.SelectedIndex = 1; // 默认 Lock1
            }
            await LoadDevicesAsync();

            // 心跳约 10s：定时合并 Mesh 在线状态，避免“通讯有柜子但列表不刷新”
            _refreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _refreshTimer.Tick += async (_, __) =>
            {
                if (IsLoaded && !_loading) await LoadDevicesAsync(quiet: true);
            };
            _refreshTimer.Start();
        }

        private void DevicePage_Unloaded(object sender, RoutedEventArgs e)
        {
            App.MeshBridge.DeviceConnected -= OnDevicePresenceChanged;
            App.MeshBridge.DeviceDisconnected -= OnDevicePresenceChanged;
            if (_refreshTimer != null)
            {
                _refreshTimer.Stop();
                _refreshTimer = null;
            }
        }

        /// <summary>柜子上线/心跳超时后立即刷新当前页，无需用户手动点击刷新。</summary>
        private void OnDevicePresenceChanged(DeviceClient device)
        {
            // 根节点也触发一次刷新：Mesh 上线状态可能刚建立
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (IsLoaded && !_loading) await LoadDevicesAsync(quiet: true);
            }));
        }

        /// <summary>加载设备列表</summary>
        private async Task LoadDevicesAsync(bool quiet = false)
        {
            if (_loading) return;
            _loading = true;
            if (!quiet) SetBusy(true, "正在读取根节点数据");
            try
            {
                // 设备页：见过的柜子全部保留（含离线），在线状态实时刷新
                var live = await Task.Run(App.DeviceService.GetLiveDevices);
                List<Device> devices = new List<Device>(live);
                try
                {
                    var fromSd = await Task.Run(App.DeviceService.GetAllDevices);
                    foreach (var d in fromSd)
                    {
                        if (DeviceService.IsTrueRoot(d)) continue;
                        var hit = devices.FirstOrDefault(x =>
                            (!string.IsNullOrWhiteSpace(d.MeshMac) &&
                             string.Equals(x.MeshMac, d.MeshMac, StringComparison.OrdinalIgnoreCase)) ||
                            string.Equals(x.DeviceId, d.DeviceId, StringComparison.OrdinalIgnoreCase));
                        if (hit == null) devices.Add(d);
                        else
                        {
                            // 保留 Mesh 实时在线状态，补充 SD 侧名称等
                            if (string.IsNullOrWhiteSpace(hit.DeviceName) &&
                                !string.IsNullOrWhiteSpace(d.DeviceName))
                                hit.DeviceName = d.DeviceName;
                        }
                    }
                }
                catch { /* SD 可选 */ }

                uint globalVersion = 0;
                try
                {
                    var version = await Task.Run(() => App.SdStorageService.QueryVersion());
                    if (version != null) globalVersion = version.GlobalVersion;
                }
                catch { /* 忽略版本查询瞬时失败 */ }

                // 过滤真正根节点；若过滤后为空但 Mesh 有已知节点，则降级显示全部已知节点
                // （避免“状态栏有数、列表全空”的体验）
                var list = devices.Where(d => !DeviceService.IsTrueRoot(d)).ToList();
                int known = App.MeshBridge.KnownDeviceCount;
                int recv = (int)App.MeshBridge.ReceivedCount;
                if (list.Count == 0 && known > 0)
                {
                    // 直接从 MeshBridge 拉全部已知节点，不再二次过滤
                    list = App.MeshBridge.GetKnownDevices()
                        .Select(c => new Device
                        {
                            DeviceId = string.IsNullOrWhiteSpace(c.DeviceId) ? (c.MeshMac ?? "UNKNOWN") : c.DeviceId,
                            DeviceName = string.IsNullOrWhiteSpace(c.DeviceName)
                                ? (string.IsNullOrWhiteSpace(c.DeviceId) ? c.MeshMac : c.DeviceId)
                                : c.DeviceName,
                            IsOnline = c.IsOnline,
                            IsRoot = c.IsRoot,
                            MeshMac = c.MeshMac ?? "",
                            RegisterTime = c.ConnectTime == default ? DateTime.Now : c.ConnectTime,
                            LastOnlineTime = c.LastSeen == default ? DateTime.Now : c.LastSeen,
                            LastSeenUnix = c.LastSeen == default
                                ? DateTimeOffset.Now.ToUnixTimeSeconds()
                                : new DateTimeOffset(c.LastSeen).ToUnixTimeSeconds(),
                        })
                        .Where(d => !string.IsNullOrWhiteSpace(d.DeviceId))
                        .ToList();
                }
                foreach (var device in list)
                {
                    // 列表里展示的节点默认按柜子处理（根节点会在名称上可区分）
                    if (!device.DeviceId.Contains("ROOT", StringComparison.OrdinalIgnoreCase))
                        device.IsRoot = false;
                    device.RootPermissionVersion = globalVersion;
                }

                string selectedId = _selectedDevice?.DeviceId ?? "";
                // 安静刷新时尽量原地更新，避免反复触发 SelectionChanged → 指纹清单重载卡 UI
                if (quiet && DeviceDataGrid.ItemsSource is IList<Device> existing &&
                    existing.Count == list.Count)
                {
                    bool sameOrder = true;
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (!string.Equals(existing[i].DeviceId, list[i].DeviceId,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            sameOrder = false;
                            break;
                        }
                    }
                    if (sameOrder)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            var dst = existing[i];
                            var src = list[i];
                            dst.DeviceName = src.DeviceName;
                            dst.IsOnline = src.IsOnline;
                            dst.IsRoot = src.IsRoot;
                            dst.MeshMac = src.MeshMac;
                            dst.LastOnlineTime = src.LastOnlineTime;
                            dst.LastSeenUnix = src.LastSeenUnix;
                            dst.RootPermissionVersion = src.RootPermissionVersion;
                            if (src.Status != null)
                            {
                                dst.Status ??= new DeviceRuntimeStatus();
                                dst.Status.PermissionVersion = src.Status.PermissionVersion;
                            }
                        }
                        DeviceDataGrid.Items.Refresh();
                    }
                    else
                    {
                        DeviceDataGrid.ItemsSource = list;
                    }
                }
                else
                {
                    DeviceDataGrid.ItemsSource = list;
                }

                int online = list.Count(d => d.IsOnline);
                int meshOnline = App.MeshBridge.GetOnlineDevices().Count;
                int lagging = list.Count(d => d.IsOnline && d.PermissionSyncText == "落后");
                PageStatusText.Text = lagging > 0
                    ? $"共 {list.Count} 个节点，在线 {online}（Mesh在线{meshOnline}），{lagging} 台权限落后 · 已知{known}/收包{recv}"
                    : $"共 {list.Count} 个节点，在线 {online}（Mesh在线{meshOnline}）· 已知{known}/收包{recv}";

                // 自动选中第一个在线设备（仅在无选中或选中项已消失时切换）
                if (string.IsNullOrEmpty(selectedId) ||
                    !list.Any(d => string.Equals(d.DeviceId, selectedId, StringComparison.OrdinalIgnoreCase)))
                {
                    var first = list.FirstOrDefault(d => d.IsOnline) ?? list.FirstOrDefault();
                    if (first != null)
                    {
                        if (!string.Equals(_selectedDevice?.DeviceId, first.DeviceId,
                                StringComparison.OrdinalIgnoreCase))
                            DeviceDataGrid.SelectedItem = first;
                    }
                    else
                    {
                        _selectedDevice = null;
                        UpdatePanelTitle(null);
                    }
                }
                else if (DeviceDataGrid.SelectedItem is not Device cur ||
                         !string.Equals(cur.DeviceId, selectedId, StringComparison.OrdinalIgnoreCase))
                {
                    // 保留当前选中状态；若绑定对象变了才重设 SelectedItem
                    DeviceDataGrid.SelectedItem = list.FirstOrDefault(d =>
                        string.Equals(d.DeviceId, selectedId, StringComparison.OrdinalIgnoreCase));
                }
            }
            catch (Exception ex)
            {
                PageStatusText.Text = "设备列表刷新失败: " + ex.Message;
            }
            finally
            {
                if (!quiet) SetBusy(false);
                _loading = false;
            }
        }

        /// <summary>选中设备变化：更新面板标题并加载指纹清单</summary>
        private async void DeviceDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            if (DeviceDataGrid.SelectedItem is not Device selected)
            {
                // 定时刷新重绑过程中会出现短暂的 null 选中，不要清掉当前上下文
                if (DeviceDataGrid.ItemsSource != null && DeviceDataGrid.Items.Count > 0)
                    return;
                _selectedDevice = null;
                _lastFpListDeviceId = null;
                UpdatePanelTitle(null);
                return;
            }

            bool sameDevice = _selectedDevice != null &&
                string.Equals(_selectedDevice.DeviceId, selected.DeviceId, StringComparison.OrdinalIgnoreCase);
            _selectedDevice = selected;
            UpdatePanelTitle(selected);

            // 同一设备重复选中（例如 3 秒刷新重绑）不再重复拉清单，避免界面卡住
            if (sameDevice &&
                string.Equals(_lastFpListDeviceId, selected.DeviceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await LoadDeviceFpListAsync(selected.DeviceId);
        }

        /// <summary>更新右侧面板标题</summary>
        private void UpdatePanelTitle(Device? device)
        {
            PanelTitle.Text = device == null
                ? "设备操作面板 - （未选择设备）"
                : $"设备操作面板 - {device.DeviceName}（{device.DeviceId}）";
        }

        /// <summary>加载指定设备的指纹清单</summary>
        private async Task LoadDeviceFpListAsync(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                DeviceFpListGrid.ItemsSource = null;
                FpListStatusText.Text = "未选择设备";
                _lastFpListDeviceId = null;
                return;
            }

            int version = Interlocked.Increment(ref _fpListLoadVersion);
            _lastFpListDeviceId = deviceId;
            FpListStatusText.Text = "正在加载设备指纹清单...";
            try
            {
                var list = await App.FingerprintTemplateService
                    .GetDeviceFingerprintListAsync(deviceId)
                    .ConfigureAwait(true);

                // 过期请求（用户已点到别的柜子）直接丢弃
                if (version != _fpListLoadVersion) return;

                DeviceFpListGrid.ItemsSource = list;
                int? deviceCount = list.FirstOrDefault()?.DeviceReportedCount;
                FpListStatusText.Text = deviceCount.HasValue
                    ? $"清单共 {list.Count} 条记录，设备实际报告 {deviceCount.Value} 个指纹"
                    : $"清单共 {list.Count} 条记录（设备未响应状态查询）";
            }
            catch (Exception ex)
            {
                if (version != _fpListLoadVersion) return;
                DeviceFpListGrid.ItemsSource = null;
                FpListStatusText.Text = $"加载失败：{ex.Message}";
            }
        }

        /// <summary>刷新按钮</summary>
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadDevicesAsync();
        }

        /// <summary>重新同步权限</summary>
        private async void ResyncButton_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(true, "正在向在线柜子同步权限");
            try
            {
                BroadcastCommandResult result = await Task.Run(App.CabinetSyncService.SyncAllPermissions);
                MessageBox.Show(
                    CabinetSyncService.FormatSyncResult(result,
                        "所有在线柜子均已确认权限同步",
                        "权限同步未全部完成"),
                    result.Success ? "同步完成" : "同步提示",
                    MessageBoxButton.OK,
                    result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
                await LoadDevicesAsync();
            }
            catch (RootDataUnavailableException ex)
            {
                MessageBox.Show(ex.Message, "根节点不可用", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>读取设备状态（调 READ_STATUS）</summary>
        private async void ReadStatusButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null)
            {
                MessageBox.Show("请先在左侧选择一个设备", "提示");
                return;
            }

            if (!IsDeviceMeshOnline(_selectedDevice))
            {
                MessageBox.Show($"设备「{_selectedDevice.DeviceName}」当前未连接", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetBusy(true, "正在读取设备状态");
            try
            {
                // READ_STATUS 的响应是 STATUS_RESPONSE，不走 ACK 通道；
                // 这里复用 FingerprintTemplateService 的事件订阅机制（5 秒超时）
                var list = await App.FingerprintTemplateService.GetDeviceFingerprintListAsync(_selectedDevice.DeviceId);
                DeviceFpListGrid.ItemsSource = list;
                int? deviceCount = list.FirstOrDefault()?.DeviceReportedCount;
                FpListStatusText.Text = deviceCount.HasValue
                    ? $"已读取状态：设备报告 {deviceCount.Value} 个指纹，清单共 {list.Count} 条"
                    : "设备未响应 READ_STATUS，请检查设备是否在线";
                MessageBox.Show("设备状态已刷新", "读取完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"读取状态失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>远程开锁：向选中设备发送 CONTROL_LOCK 命令</summary>
        private async void RemoteUnlockButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null)
            {
                MessageBox.Show("请先在左侧选择要开锁的设备", "提示");
                return;
            }

            // 获取锁号
            int lockId = 1;
            if (LockSelectBox.SelectedItem is ComboBoxItem item && item.Tag != null)
            {
                int.TryParse(item.Tag.ToString(), out lockId);
            }

            if (!IsDeviceMeshOnline(_selectedDevice))
            {
                MessageBox.Show($"设备「{_selectedDevice.DeviceName}」当前未连接，无法远程开锁", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 权限检查：非管理员不允许操作系统锁
            if (lockId == 0 && App.CurrentUser?.Role != "admin")
            {
                MessageBox.Show("系统锁(Lock0)仅管理员可远程开启", "权限不足",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 构造并发送控制命令（经 Mesh 桥接器转发到目标设备）
            var data = new Dictionary<string, object>
            {
                ["lock_id"] = lockId,
                ["action"] = "open",
                ["operator"] = App.CurrentUser?.UserId ?? "system"
            };
            var msg = Message.Create(Protocol.CmdControlLock, _selectedDevice.DeviceId, data);
            RemoteUnlockButton.IsEnabled = false;
            var result = await App.CommandService.SendAsync(_selectedDevice.DeviceId, msg);
            RemoteUnlockButton.IsEnabled = !IsBusy();
            if (!result.Success)
            {
                MessageBox.Show(result.ErrorMessage, "开锁失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // 开锁日志由柜子记录并上报根节点，上位机不重复写日志表。
            MessageBox.Show($"设备「{_selectedDevice.DeviceName}」已确认 Lock {lockId} 开锁", "开锁完成",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        /// <summary>录入新指纹：在选中柜子上采集，模板暂存到本地指纹模板库</summary>
        private async void EnrollFingerprintButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null)
            {
                MessageBox.Show("请先在左侧选择要录入指纹的柜子", "提示");
                return;
            }

            if (!IsDeviceMeshOnline(_selectedDevice))
            {
                MessageBox.Show($"设备「{_selectedDevice.DeviceName}」当前未连接，无法录入指纹", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 建议下一个可用指纹 ID（不依赖 SD）
            int suggestId;
            try
            {
                suggestId = await Task.Run(App.UserService.GetNextFingerprintIdLocal);
            }
            catch
            {
                suggestId = 1;
            }

            // 加载用户列表用于可选关联
            List<UserBrief> users;
            try
            {
                users = await Task.Run(App.UserService.GetAllUsersBrief);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"加载用户列表失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (!ShowEnrollFingerprintDialog(suggestId, users,
                out int fingerprintId, out int fingerIndex, out UserBrief? selectedUser))
            {
                return;
            }

            if (fingerprintId <= 0)
            {
                MessageBox.Show("指纹ID必须为正整数", "提示");
                return;
            }

            SetBusy(true, "准备录入：请按提示在柜子指纹头操作（共4次按压+2次验证）");
            try
            {
                string userId = selectedUser?.UserId ?? $"fp_{fingerprintId}";
                FingerprintEnrollmentResult enrollment =
                    await App.CommandService.EnrollFingerprintAsync(
                        _selectedDevice.DeviceId, userId, fingerprintId, false,
                        180_000,
                        (phase, step, total, hint) =>
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                PageStatusText.Text = string.IsNullOrWhiteSpace(hint)
                                    ? $"录入进度 {step}/{total}（{phase}）"
                                    : $"[{step}/{total}] {hint}";
                            }));
                        });
                // 允许无 template_hex 备份仍算成功（传感器已写入）
                if (!enrollment.Success)
                {
                    MessageBox.Show(
                        string.IsNullOrWhiteSpace(enrollment.ErrorMessage)
                            ? "柜子未能完成指纹录入"
                            : enrollment.ErrorMessage,
                        "录入失败", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                if (enrollment.TemplateBytes == null || enrollment.TemplateBytes.Length == 0)
                {
                    MessageBox.Show(
                        "传感器录入成功，但未能导出模板备份。\n可在本柜使用，跨柜恢复需重新录入。",
                        "部分成功", MessageBoxButton.OK, MessageBoxImage.Warning);
                    // 仍尝试绑定用户
                    if (selectedUser != null)
                    {
                        await Task.Run(() =>
                            App.FingerprintTemplateService.BindToUser(fingerprintId, selectedUser.UserId));
                    }
                    await LoadDeviceFpListAsync(_selectedDevice.DeviceId);
                    return;
                }

                // 保存模板到本地指纹模板库（采集-存储-分配解耦：只采集和存储，不立即下发）
                bool saved = await Task.Run(() =>
                    App.FingerprintTemplateService.SaveEnrolledTemplate(
                        fingerprintId, enrollment.TemplateBytes!,
                        _selectedDevice.DeviceId,
                        selectedUser?.UserId));

                // 如果选了用户，绑定到用户
                bool bound = false;
                if (selectedUser != null)
                {
                    bound = await Task.Run(() =>
                        App.FingerprintTemplateService.BindToUser(fingerprintId, selectedUser.UserId));
                }

                // 上传到 SD（带 fallback：SD 不可用时仅保留本地）
                bool uploaded = false;
                if (App.SdStorageService.IsAvailable && selectedUser != null)
                {
                    try
                    {
                        uploaded = await App.FingerprintTemplateService.UploadToSdAsync(fingerprintId);
                    }
                    catch
                    {
                        // 上传失败不影响主流程
                    }
                }

                string summary = "指纹采集完成。\n模板已暂存到本地指纹模板库，请前往「指纹模板库」进行分配。";
                if (selectedUser != null)
                {
                    summary += bound
                        ? $"\n已关联用户：{selectedUser.Name}（{selectedUser.UserId}）"
                        : "\n用户关联失败，可在「指纹模板库」中手动关联。";
                }
                summary += uploaded
                    ? "\n模板已备份到 SD 卡。"
                    : (App.SdStorageService.IsAvailable
                        ? "\n模板上传 SD 失败，仅保存在本地。"
                        : "\nSD 不可用，模板仅保存在本地，待 SD 恢复后可手动上传。");

                MessageBox.Show(summary, "采集完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);

                // 刷新设备指纹清单
                await LoadDeviceFpListAsync(_selectedDevice.DeviceId);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"录入失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>刷新指纹清单按钮</summary>
        private async void RefreshFpListButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null)
            {
                MessageBox.Show("请先在左侧选择一个设备", "提示");
                return;
            }
            await LoadDeviceFpListAsync(_selectedDevice.DeviceId);
        }

        // ===== V2.7 副指纹操作 =====

        /// <summary>录入本机副指纹：打开副指纹录入窗口</summary>
        private void EnrollBackupFpButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new BackupFingerprintWindow
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
            // 录入完成后刷新指纹清单
            if (_selectedDevice != null)
            {
                _ = LoadDeviceFpListAsync(_selectedDevice.DeviceId);
            }
        }

        /// <summary>删除选中柜子上指定用户的本机副指纹</summary>
        private async void DeleteBackupFpButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedDevice == null)
            {
                MessageBox.Show("请先在左侧选择一个设备", "提示");
                return;
            }
            // 弹出输入框让用户输入要删除副指纹的用户 ID
            string? userId = PromptDialog.Show(
                "请输入要删除副指纹的用户 ID：",
                "删除本机副指纹",
                "");
            if (string.IsNullOrWhiteSpace(userId)) return;

            var result = await App.CommandService.DeleteBackupFingerprintAsync(_selectedDevice.DeviceId, userId.Trim());
            string msg = result.Success
                ? $"已删除用户 {userId} 在 {_selectedDevice.DeviceId} 上的本机副指纹"
                : $"删除失败：{result.ErrorMessage}";
            MessageBox.Show(msg, result.Success ? "成功" : "失败",
                MessageBoxButton.OK, result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);

            await LoadDeviceFpListAsync(_selectedDevice.DeviceId);
        }

        // ===== 辅助 =====

        /// <summary>判断指定设备在 Mesh 上是否在线（按 device_id 或 MAC）</summary>
        private static bool IsDeviceMeshOnline(Device device)
        {
            foreach (var dc in App.MeshBridge.GetOnlineDevices())
            {
                if (!dc.IsOnline) continue;
                if (string.Equals(dc.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase))
                    return true;
                if (!string.IsNullOrWhiteSpace(device.MeshMac) &&
                    string.Equals(dc.MeshMac, device.MeshMac, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        private bool _busy;

        private bool IsBusy() => _busy;

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            RefreshButton.IsEnabled = !busy;
            ResyncButton.IsEnabled = !busy;
            RemoteUnlockButton.IsEnabled = !busy;
            ReadStatusButton.IsEnabled = !busy;
            EnrollFingerprintButton.IsEnabled = !busy;
            RefreshFpListButton.IsEnabled = !busy;
            if (!string.IsNullOrEmpty(status)) PageStatusText.Text = status;
        }

        /// <summary>显示录入指纹对话框：输入指纹 ID + 选手指索引 + 可选关联用户</summary>
        private bool ShowEnrollFingerprintDialog(int suggestId, List<UserBrief> users,
            out int fingerprintId, out int fingerIndex, out UserBrief? selectedUser)
        {
            fingerprintId = 0;
            fingerIndex = 1;
            selectedUser = null;

            var dlg = new Window
            {
                Title = "录入新指纹",
                Width = 380,
                Height = 380,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = FindResource("BackgroundBrush") as Brush
            };

            var panel = new StackPanel { Margin = new Thickness(20) };

            panel.Children.Add(new TextBlock
            {
                Text = "流程：按提示按压/抬起共 4 次，再验证 2 次。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = FindResource("SubTextBrush") as Brush,
                Margin = new Thickness(0, 0, 0, 12),
                FontSize = 11
            });

            panel.Children.Add(new TextBlock
            {
                Text = "指纹 ID（正整数）",
                Margin = new Thickness(0, 0, 0, 6)
            });
            var idBox = new TextBox { Text = suggestId.ToString(), Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(idBox);

            panel.Children.Add(new TextBlock
            {
                Text = "手指索引",
                Margin = new Thickness(0, 0, 0, 6)
            });
            var fingerCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 12) };
            fingerCombo.Items.Add(new ComboBoxItem { Content = "1 - 食指（默认）", Tag = "1" });
            fingerCombo.Items.Add(new ComboBoxItem { Content = "2 - 中指", Tag = "2" });
            fingerCombo.SelectedIndex = 0;
            panel.Children.Add(fingerCombo);

            panel.Children.Add(new TextBlock
            {
                Text = "关联用户（可选，可不绑）",
                Margin = new Thickness(0, 0, 0, 6)
            });
            var userCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 16) };
            userCombo.Items.Add(new ComboBoxItem { Content = "（暂不关联）", Tag = null });
            foreach (var u in users)
            {
                userCombo.Items.Add(new ComboBoxItem { Content = u.ToString(), Tag = u });
            }
            userCombo.SelectedIndex = 0;
            panel.Children.Add(userCombo);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 70,
                Style = FindResource("SecondaryButton") as Style };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);

            dlg.Content = panel;

            bool confirmed = false;
            int localId = 0;
            int localFinger = 1;
            UserBrief? localUser = null;
            okBtn.Click += (s, e) =>
            {
                if (!int.TryParse(idBox.Text?.Trim(), out int id))
                {
                    MessageBox.Show("请输入有效的指纹 ID 数字", "提示");
                    return;
                }
                if (id <= 0)
                {
                    MessageBox.Show("指纹 ID 必须为正整数", "提示");
                    return;
                }
                localId = id;
                if (fingerCombo.SelectedItem is ComboBoxItem fi && fi.Tag != null)
                {
                    int.TryParse(fi.Tag.ToString(), out localFinger);
                }
                if (userCombo.SelectedItem is ComboBoxItem ui && ui.Tag is UserBrief u)
                {
                    localUser = u;
                }
                confirmed = true;
                dlg.Close();
            };
            cancelBtn.Click += (s, e) => dlg.Close();
            dlg.ShowDialog();

            if (confirmed)
            {
                fingerprintId = localId;
                fingerIndex = localFinger;
                selectedUser = localUser;
            }
            return confirmed;
        }
    }
}
