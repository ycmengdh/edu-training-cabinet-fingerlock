using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FingerprintLockManager
{
    public partial class StudentDetailWindow : BorderlessWindow
    {
        private User _user;
        private readonly string _className;
        private List<Device> _devices = new();
        private List<FingerprintTemplate> _templates = new();
        private List<CabinetAssignment> _assignments = new();
        private IReadOnlyList<CabinetSyncJob> _syncJobs = Array.Empty<CabinetSyncJob>();
        private HashSet<string> _assignedDeviceIds = new(StringComparer.OrdinalIgnoreCase);
        private bool _busy;

        public StudentDetailWindow(User user, string className)
        {
            ArgumentNullException.ThrowIfNull(user);
            InitializeComponent();
            _user = user;
            _className = className;
            StudentNameText.Text = string.IsNullOrWhiteSpace(user.Name) ? user.UserId : user.Name;
            StudentMetaText.Text = $"学号：{user.UserId}  ·  班级：{className}  ·  性别：{GenderText(user.Gender)}";
            StudentStatusText.Text = user.StatusText;
            Loaded += async (_, _) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            SetBusy(true, "正在读取柜子绑定、权限和指纹");
            try
            {
                _user = await Task.Run(() => App.UserService.GetUser(_user.UserId) ?? _user);
                StudentNameText.Text = string.IsNullOrWhiteSpace(_user.Name) ? _user.UserId : _user.Name;
                StudentMetaText.Text = $"学号：{_user.UserId}  ·  班级：{_className}  ·  性别：{GenderText(_user.Gender)}";
                StudentStatusText.Text = _user.StatusText;
                _devices = await Task.Run(() => App.DeviceService.GetAllDevices()
                    .Where(device => !DeviceService.IsTrueRoot(device) && !string.IsNullOrWhiteSpace(device.DeviceId))
                    .OrderBy(device => device.DeviceName)
                    .ToList());
                _templates = BusinessDatabase.ReadAllFpTemplateMetas()
                    .Where(meta => string.Equals(meta.UserId, _user.UserId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(meta => meta.FingerIndex).ThenBy(meta => meta.FingerprintId).ToList();
                _assignments = App.CabinetBindingService.GetAssignments(
                    _user, _devices.Select(device => device.DeviceId)).ToList();
                _syncJobs = App.CabinetSyncQueueService.GetAll().Where(job =>
                    (job.JobKind == "user" && string.Equals(job.UserId,
                        _user.UserId, StringComparison.OrdinalIgnoreCase)) ||
                    (job.JobKind == "cabinet" && _assignments.Any(assignment => string.Equals(
                        assignment.DeviceId, job.DeviceId, StringComparison.OrdinalIgnoreCase)))).ToList();
                _assignedDeviceIds = _assignments.Select(item => item.DeviceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                CabinetGrid.ItemsSource = _devices.Select(BuildCabinetRow).ToList();
                CabinetCombo.ItemsSource = _devices
                    .Where(device => !_assignedDeviceIds.Contains(device.DeviceId))
                    .Select(device => new CabinetOption(device))
                    .ToList();
                FingerprintGrid.ItemsSource = BuildFingerprintRows();
                LoadPermissions();
                StatusText.Text = $"已加载 {_assignedDeviceIds.Count} 个绑定柜子";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"详情读取失败：{ex.Message}";
            }
            finally
            {
                SetBusy(false);
            }
        }

        private StudentCabinetRow BuildCabinetRow(Device device)
        {
            CabinetAssignment? assignment = _assignments.FirstOrDefault(item => string.Equals(
                item.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase));
            bool bound = assignment != null;
            FingerprintTemplate? fingerprint = assignment?.ActiveFingerprintId.HasValue == true
                ? _templates.FirstOrDefault(item => item.Enabled &&
                    item.FingerprintId == assignment.ActiveFingerprintId.Value)
                : null;
            CabinetSyncJob? syncJob = _syncJobs.FirstOrDefault(job => string.Equals(
                job.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase));
            return new StudentCabinetRow
            {
                DeviceId = device.DeviceId,
                DeviceNumber = string.IsNullOrWhiteSpace(device.DeviceNumber) ? "未编号" : device.DeviceNumber,
                DeviceName = string.IsNullOrWhiteSpace(device.DeviceName) ? device.DeviceId : device.DeviceName,
                OnlineText = device.IsOnline ? "在线" : "离线",
                BindingText = bound ? "已绑定" : "未绑定",
                IsBound = bound,
                ActiveFingerprintId = assignment?.ActiveFingerprintId,
                ActiveFingerprintText = !bound ? "-" : fingerprint == null
                    ? "未选择" : $"{fingerprint.FingerDisplayName} #{fingerprint.FingerprintId}",
                SyncStatusText = !bound ? "-" : fingerprint == null
                    ? "待处理" : syncJob?.StatusText ?? "已配置"
            };
        }

        private List<StudentFingerprintRow> BuildFingerprintRows()
        {
            var metas = _templates.GroupBy(meta => meta.FingerprintId)
                .Select(group => group.First()).ToList();
            if (_user.FingerprintId.HasValue && metas.All(meta => meta.FingerprintId != _user.FingerprintId.Value))
            {
                metas.Add(new FingerprintTemplate
                {
                    FingerprintId = _user.FingerprintId.Value,
                    UserId = _user.UserId,
                    UserName = _user.Name,
                    FingerName = "旧数据指纹",
                    SourceDevice = "兼容记录",
                    BackupStatus = "sd"
                });
            }
            return metas.OrderBy(meta => meta.FingerIndex).ThenBy(meta => meta.FingerprintId)
                .Select(meta => new StudentFingerprintRow
                {
                    FingerprintId = meta.FingerprintId,
                    FingerIndex = meta.FingerIndex,
                    FingerName = meta.FingerDisplayName,
                    QualityText = meta.Quality > 0 ? meta.Quality.ToString() : "-",
                    SourceDevice = string.IsNullOrWhiteSpace(meta.SourceDevice) ? "本地模板库" : meta.SourceDevice,
                    BackupStatusText = meta.BackupStatusText,
                    UsedCabinetCount = _assignments.Count(item =>
                        item.ActiveFingerprintId == meta.FingerprintId)
                })
                .ToList();
        }

        private void LoadPermissions()
        {
            bool[] final = App.PermissionService.GetFinalPermissions(_user.UserId);
            bool isAdmin = string.Equals(_user.Role, "admin", StringComparison.OrdinalIgnoreCase);
            Lock0CheckBox.IsEnabled = isAdmin;
            Lock0CheckBox.IsChecked = isAdmin && final.ElementAtOrDefault(0);
            Lock1CheckBox.IsChecked = final.ElementAtOrDefault(1);
            Lock2CheckBox.IsChecked = final.ElementAtOrDefault(2);
            Lock3CheckBox.IsChecked = final.ElementAtOrDefault(3);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

        private async void BindCabinetButton_Click(object sender, RoutedEventArgs e)
        {
            if (CabinetCombo.SelectedItem is not CabinetOption option) return;
            SetBusy(true, $"正在绑定 {option.DisplayText}");
            try
            {
                bool saved = await Task.Run(() => App.CabinetBindingService.Assign(option.DeviceId, _user.UserId));
                if (saved)
                {
                    var sync = await Task.Run(() => App.CabinetSyncService.SyncCabinetPermissions(option.DeviceId));
                    StatusText.Text = sync.Success ? "柜子已绑定并完成权限同步" : "柜子已绑定，但权限同步待重试";
                    await LoadAsync();
                }
                else MessageBox.Show("柜子绑定保存失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "绑定柜子失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        private async void UnbindCabinetButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not StudentCabinetRow row || !row.IsBound) return;
            if (MessageBox.Show($"确认解除学生与柜子「{row.DeviceName}」的绑定？\n解除后将清理该柜子的权限下发数据。",
                    "解除绑定", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SetBusy(true, "正在解除柜子绑定并同步");
            try
            {
                await Task.Run(() => App.CabinetBindingService.Remove(row.DeviceId, _user.UserId));
                var sync = await Task.Run(() => App.CabinetSyncService.SyncCabinetPermissions(row.DeviceId));
                StatusText.Text = sync.Success ? "绑定已解除，柜子权限已清理" : "绑定已解除，但柜子未确认权限清理";
                await LoadAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "解除绑定失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        private async void SelectCabinetFingerprintButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not StudentCabinetRow row || !row.IsBound) return;
            List<StudentFingerprintRow> options = BuildFingerprintRows()
                .Where(item => item.FingerprintId > 0).ToList();
            if (options.Count == 0)
            {
                MessageBox.Show("请先为学生录入至少一枚指纹", "选择指纹",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            StudentFingerprintRow? selected = ShowFingerprintSelection(row, options);
            if (selected == null || selected.FingerprintId == row.ActiveFingerprintId) return;

            SetBusy(true, "正在保存柜机指纹选择");
            try
            {
                bool saved = await Task.Run(() => App.CabinetBindingService.SetActiveFingerprint(
                    _user.UserId, row.DeviceId, selected.FingerprintId));
                if (!saved)
                {
                    MessageBox.Show("柜机指纹选择保存失败", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                if (row.OnlineText == "在线")
                {
                    _user = App.UserService.GetUser(_user.UserId) ?? _user;
                    IReadOnlyList<UserCabinetSyncResult> result = await App.CabinetSyncService
                        .VerifyAndSyncUserAsync(_user, new[] { row.DeviceId });
                    StatusText.Text = result.FirstOrDefault()?.Success == true
                        ? "柜机当前指纹已更新并确认" : "选择已保存，柜机同步待重试";
                }
                else
                {
                    StatusText.Text = "选择已保存，柜机上线后同步";
                }
                await LoadAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "选择指纹失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        private void CabinetGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CabinetGrid.SelectedItem is StudentCabinetRow row)
                StatusText.Text = $"当前柜子：{row.DeviceName} · {row.OnlineText} · {row.BindingText}";
        }

        private async void SavePermissionsButton_Click(object sender, RoutedEventArgs e)
        {
            var permissions = new Dictionary<int, bool>
            {
                [0] = Lock0CheckBox.IsEnabled && Lock0CheckBox.IsChecked == true,
                [1] = Lock1CheckBox.IsChecked == true,
                [2] = Lock2CheckBox.IsChecked == true,
                [3] = Lock3CheckBox.IsChecked == true
            };
            SetBusy(true, "正在保存学生权限并同步在线柜子");
            try
            {
                bool saved = await Task.Run(() => App.PermissionService.SetUserPermissions(_user.UserId, permissions));
                if (!saved)
                {
                    MessageBox.Show("权限保存失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                await VerifyAndSyncStudentAsync("权限已保存");
                await LoadAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "保存权限失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        private async void ResetPermissionsButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确认按当前学生角色模板重置个人权限？\n这只影响该学生，不会修改其他已有用户。", "按模板重置",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            SetBusy(true, "正在按模板重置个人权限");
            try
            {
                await Task.Run(() => App.PermissionService.DeleteAllUserPermissions(_user.UserId));
                App.PermissionService.SetUserPermissions(_user.UserId,
                    new RolePermissionService().GetRolePermission(_user.Role).ToArray()
                        .Select((allowed, lockId) => new { lockId, allowed })
                        .ToDictionary(item => item.lockId, item => item.allowed));
                await VerifyAndSyncStudentAsync("个人权限已按模板重置");
                await LoadAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "恢复权限失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        private async void EnrollFingerprintButton_Click(object sender, RoutedEventArgs e)
        {
            string? target = (CabinetGrid.SelectedItem as StudentCabinetRow)?.DeviceId;
            if (string.IsNullOrWhiteSpace(target) || !_devices.Any(device =>
                    string.Equals(device.DeviceId, target, StringComparison.OrdinalIgnoreCase) && device.IsOnline))
            {
                target = _devices.FirstOrDefault(device => device.IsOnline)?.DeviceId;
            }
            if (string.IsNullOrWhiteSpace(target))
            {
                MessageBox.Show("请选择一台在线柜机作为采集设备", "无法录入", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var window = new EnrollFingerprintWindow(target, _user.UserId) { Owner = this };
            window.ShowDialog();
            if (window.EnrolledFingerprintId <= 0) return;
            await LoadAsync();
        }

        private void TestFingerprintButton_Click(object sender, RoutedEventArgs e)
        {
            if (FingerprintGrid.SelectedItem is not StudentFingerprintRow row) return;
            string? deviceId = (CabinetGrid.SelectedItem as StudentCabinetRow)?.DeviceId;
            if (string.IsNullOrWhiteSpace(deviceId) || !_devices.Any(device => device.IsOnline &&
                    string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)))
            {
                deviceId = _devices.FirstOrDefault(device => device.IsOnline)?.DeviceId;
            }
            if (string.IsNullOrWhiteSpace(deviceId))
            {
                MessageBox.Show("没有在线柜机可进入指纹测试模式", "指纹测试",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            new FingerprintTestWindow(_user.UserId, row.FingerprintId, deviceId)
            {
                Owner = this
            }.ShowDialog();
        }

        private async void VerifySyncButton_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(true, "正在校验指纹和权限");
            try
            {
                await VerifyAndSyncStudentAsync("校验完成");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "同步失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task VerifyAndSyncStudentAsync(string prefix)
        {
            if (_templates.Count == 0)
            {
                StatusText.Text = $"{prefix}，学生尚未录入用户指纹";
                return;
            }
            IReadOnlyList<UserCabinetSyncResult> result = await App.CabinetSyncService
                .VerifyAndSyncUserAsync(_user);
            int updated = result.Count(item => item.Success && item.Changed);
            int unchanged = result.Count(item => item.Success && !item.Changed);
            int failed = result.Count(item => !item.Success);
            StatusText.Text = $"{prefix}：更新 {updated}，无需更新 {unchanged}，失败 {failed}";
        }

        private async void DeleteFingerprintButton_Click(object sender, RoutedEventArgs e)
        {
            if (FingerprintGrid.SelectedItem is not StudentFingerprintRow row) return;
            if (row.UsedCabinetCount > 0)
            {
                MessageBox.Show($"该指纹仍被 {row.UsedCabinetCount} 台柜机使用。\n请先为这些柜机选择其他指纹。",
                    "不能删除", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (MessageBox.Show($"确认删除{row.FingerName} #{row.FingerprintId}？\n将从在线柜子和模板库清理该指纹。",
                    "删除指纹", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SetBusy(true, "正在删除柜子指纹和本地模板");
            try
            {
                var deleteResult = await App.CabinetSyncService
                    .DeleteFingerprintFromOnlineCabinetsAsync(row.FingerprintId);
                if (_user.FingerprintId == row.FingerprintId)
                {
                    await Task.Run(() => App.UserService.ClearFingerprint(_user.UserId, row.FingerprintId));
                    _user.FingerprintId = null;
                }
                try { await App.SdStorageService.DeleteFingerTemplateAsync(
                    _user.UserId, row.FingerIndex); } catch { }
                App.FingerprintTemplateService.DeleteTemplate(row.FingerprintId);
                FingerprintTemplate? replacement = _templates.FirstOrDefault(item =>
                    item.Enabled && item.FingerprintId != row.FingerprintId);
                if (!_user.FingerprintId.HasValue && replacement != null)
                    await Task.Run(() => App.UserService.AssignFingerprint(
                        _user.UserId, replacement.FingerprintId));
                await LoadAsync();
                if (!deleteResult.Success)
                    StatusText.Text = "本地指纹已删除，但部分在线柜子未确认删除，请重新同步或重试";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "删除指纹失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        private async void RestoreFingerprintButton_Click(object sender, RoutedEventArgs e)
        {
            if (FingerprintGrid.SelectedItem is not StudentFingerprintRow fp ||
                CabinetGrid.SelectedItem is not StudentCabinetRow cabinet) return;
            if (!cabinet.OnlineText.Equals("在线", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("请选择在线柜子", "无法下发", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            SetBusy(true, "正在读取模板并下发到柜子");
            try
            {
                byte[]? bytes = await App.FingerprintTemplateService.GetTemplateBytesAsync(fp.FingerprintId);
                if (bytes == null || bytes.Length == 0)
                {
                    MessageBox.Show("本地或 SD 卡没有可用模板", "无法下发", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var result = await App.CommandService.RestoreFingerprintAsync(
                    cabinet.DeviceId, _user.UserId, fp.FingerprintId, bytes);
                if (!result.Success)
                {
                    MessageBox.Show(result.ErrorMessage, "下发失败", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                var sync = await Task.Run(() => App.CabinetSyncService.SyncCabinetPermissions(cabinet.DeviceId));
                StatusText.Text = sync.Success ? "指纹已下发，柜子权限已同步" : "指纹已下发，但权限同步待重试";
                await LoadAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "下发指纹失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { SetBusy(false); }
        }

        private void FingerprintGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FingerprintGrid.SelectedItem is StudentFingerprintRow row)
                StatusText.Text = $"当前指纹：{row.FingerName} #{row.FingerprintId}";
        }

        private StudentFingerprintRow? ShowFingerprintSelection(
            StudentCabinetRow cabinet, IReadOnlyList<StudentFingerprintRow> options)
        {
            var dialog = new BorderlessWindow
            {
                Title = "选择柜机指纹",
                Width = 440,
                Height = 250,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = this,
                Background = FindResource("BackgroundBrush") as Brush
            };
            var panel = new StackPanel { Margin = new Thickness(24, 18, 24, 22) };
            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(cabinet.DeviceNumber)
                    ? cabinet.DeviceName : $"{cabinet.DeviceNumber} · {cabinet.DeviceName}",
                Style = FindResource("SectionTitleText") as Style
            });
            panel.Children.Add(new TextBlock
            {
                Text = "该柜机只启用所选的一枚指纹",
                Style = FindResource("PageStatusText") as Style,
                Margin = new Thickness(0, 4, 0, 12)
            });
            var combo = new ComboBox
            {
                ItemsSource = options,
                DisplayMemberPath = nameof(StudentFingerprintRow.DisplayText),
                SelectedValuePath = nameof(StudentFingerprintRow.FingerprintId),
                Height = 36
            };
            combo.SelectedValue = cabinet.ActiveFingerprintId;
            if (combo.SelectedIndex < 0) combo.SelectedIndex = 0;
            panel.Children.Add(combo);
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 18, 0, 0)
            };
            var cancel = new Button
            {
                Content = "取消",
                Style = FindResource("SecondaryButton") as Style,
                Width = 78
            };
            var confirm = new Button { Content = "保存并同步", Width = 104, Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(cancel);
            buttons.Children.Add(confirm);
            panel.Children.Add(buttons);
            dialog.Content = panel;
            StudentFingerprintRow? selected = null;
            cancel.Click += (_, _) => dialog.DialogResult = false;
            confirm.Click += (_, _) =>
            {
                selected = combo.SelectedItem as StudentFingerprintRow;
                dialog.DialogResult = selected != null;
            };
            dialog.ShowDialog();
            return selected;
        }

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            RefreshButton.IsEnabled = !busy;
            BindCabinetButton.IsEnabled = !busy;
            CabinetCombo.IsEnabled = !busy;
            CabinetGrid.IsEnabled = !busy;
            FingerprintGrid.IsEnabled = !busy;
            EnrollFingerprintButton.IsEnabled = !busy;
            RestoreFingerprintButton.IsEnabled = !busy;
            DeleteFingerprintButton.IsEnabled = !busy;
            TestFingerprintButton.IsEnabled = !busy;
            SavePermissionsButton.IsEnabled = !busy;
            ResetPermissionsButton.IsEnabled = !busy;
            VerifySyncButton.IsEnabled = !busy;
            if (!string.IsNullOrWhiteSpace(status)) StatusText.Text = status;
        }

        private static string GenderText(string? gender) => gender?.ToLowerInvariant() switch
        {
            "male" => "男",
            "female" => "女",
            "other" => "其他",
            _ => "未填写"
        };
    }

    public sealed class CabinetOption
    {
        public CabinetOption(Device device)
        {
            DeviceId = device.DeviceId;
            DisplayText = $"{(string.IsNullOrWhiteSpace(device.DeviceName) ? device.DeviceId : device.DeviceName)} ({device.DeviceId})";
        }

        public string DeviceId { get; }
        public string DisplayText { get; }
    }

    public sealed class StudentCabinetRow
    {
        public string DeviceId { get; init; } = "";
        public string DeviceNumber { get; init; } = "";
        public string DeviceName { get; init; } = "";
        public string OnlineText { get; init; } = "";
        public string BindingText { get; init; } = "";
        public bool IsBound { get; init; }
        public int? ActiveFingerprintId { get; init; }
        public string ActiveFingerprintText { get; init; } = "";
        public string SyncStatusText { get; init; } = "";
    }

    public sealed class StudentFingerprintRow
    {
        public int FingerprintId { get; init; }
        public int FingerIndex { get; init; }
        public string FingerName { get; init; } = "";
        public string QualityText { get; init; } = "-";
        public string SourceDevice { get; init; } = "";
        public string BackupStatusText { get; init; } = "";
        public int UsedCabinetCount { get; init; }
        public string UsedCabinetCountText => $"{UsedCabinetCount} 台";
        public string DisplayText => $"{FingerName} · #{FingerprintId} · {BackupStatusText}";
    }
}
