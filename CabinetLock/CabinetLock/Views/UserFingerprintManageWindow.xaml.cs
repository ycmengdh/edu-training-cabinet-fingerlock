using System.Windows;
using System.Windows.Controls;

namespace CabinetLock
{
    public partial class UserFingerprintManageWindow : BorderlessWindow
    {
        private User _user;
        private List<FingerprintTemplate> _templates = new();
        private List<CabinetAssignment> _assignments = new();
        private int? _effectiveDefaultFingerprintId;
        private readonly bool _canModify;
        private bool _busy;

        public UserFingerprintManageWindow(User user)
        {
            ArgumentNullException.ThrowIfNull(user);
            InitializeComponent();
            _user = user;
            _canModify = DataScopeContext.Instance.CanModify(user);
            PolicyText.Visibility = string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase)
                ? Visibility.Collapsed : Visibility.Visible;
            Loaded += async (_, _) => await LoadAsync();
        }

        public bool Changed { get; private set; }

        private async Task LoadAsync()
        {
            SetBusy(true, "正在读取用户指纹模板");
            try
            {
                (_user, _templates, _assignments) = await Task.Run(() =>
                {
                    User current = App.UserService.GetUser(_user.UserId) ?? _user;
                    List<FingerprintTemplate> templates = App.FingerprintTemplateService
                        .GetTemplatesForUser(current.UserId)
                        .Where(item => item.Enabled && item.FingerprintId > 0)
                        .GroupBy(item => item.FingerprintId)
                        .Select(group => group.Last())
                        .OrderBy(item => item.FingerIndex)
                        .ThenBy(item => item.FingerprintId)
                        .ToList();
                    string[] deviceIds = App.DeviceService.GetAllDevices()
                        .Where(device => !DeviceService.IsTrueRoot(device))
                        .Select(device => device.DeviceId).ToArray();
                    List<CabinetAssignment> assignments = App.CabinetBindingService
                        .GetAssignments(current, deviceIds).ToList();
                    return (current, templates, assignments);
                });
                _effectiveDefaultFingerprintId = App.CabinetBindingService
                    .ResolveDefaultFingerprintId(_user, _templates);

                int allCabinetCount = await Task.Run(() => App.DeviceService.GetAllDevices()
                    .Count(device => !DeviceService.IsTrueRoot(device)));

                UserNameText.Text = string.IsNullOrWhiteSpace(_user.Name) ? _user.DisplayId : _user.Name;
                UserMetaText.Text = $"{RoleText(_user.Role)} · {_user.DisplayId}";
                List<UserFingerprintRow> rows = _templates.Select(template => new UserFingerprintRow
                {
                    FingerprintId = template.FingerprintId,
                    FingerIndex = template.FingerIndex,
                    FingerName = template.FingerDisplayName,
                    IsDefault = _effectiveDefaultFingerprintId == template.FingerprintId,
                    IsPersistedDefault = _user.FingerprintId == template.FingerprintId,
                    SourceDevice = string.IsNullOrWhiteSpace(template.SourceDevice)
                        ? "本地模板库" : template.SourceDevice,
                    BackupStatusText = template.BackupStatusText,
                    ExplicitCabinetCount = _assignments.Count(assignment =>
                        assignment.FingerprintIds.Contains(template.FingerprintId)),
                    UsedCabinetCount = ResolveUsedCabinetCount(
                        _user, template.FingerprintId, _effectiveDefaultFingerprintId,
                        _assignments, allCabinetCount),
                    EnrollTime = template.EnrollTime
                }).ToList();
                FingerprintGrid.ItemsSource = rows;
                FingerprintGrid.SelectedIndex = rows.Count > 0 ? 0 : -1;
                EmptyPanel.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                TemplateCountText.Text = $"指纹 {rows.Count} 枚";
                DefaultFingerprintText.Text = _effectiveDefaultFingerprintId.HasValue
                    ? _user.FingerprintId == _effectiveDefaultFingerprintId
                        ? $"默认指纹：#{_effectiveDefaultFingerprintId.Value}"
                        : $"默认指纹：#{_effectiveDefaultFingerprintId.Value}（自动）"
                    : "默认指纹：未设置";
                StatusText.Text = rows.Count == 0
                    ? "尚未录入用户指纹"
                    : $"已加载 {rows.Count} 枚指纹模板";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"指纹读取失败：{ex.Message}";
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

        private async void EnrollButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new EnrollFingerprintWindow(null, _user.UserId, fixedUserMode: true)
            {
                Owner = this
            };
            window.ShowDialog();
            if (window.EnrolledFingerprintId <= 0) return;
            Changed = true;
            await LoadAsync();
        }

        private void TestButton_Click(object sender, RoutedEventArgs e)
        {
            if (FingerprintGrid.SelectedItem is not UserFingerprintRow row) return;
            new FingerprintTestWindow(_user.UserId, row.FingerprintId)
            {
                Owner = this
            }.ShowDialog();
        }

        private async void SetDefaultButton_Click(object sender, RoutedEventArgs e)
        {
            if (FingerprintGrid.SelectedItem is not UserFingerprintRow row || row.IsPersistedDefault) return;
            SetBusy(true, "正在设置默认指纹");
            try
            {
                bool saved = await Task.Run(() =>
                    App.UserService.AssignFingerprint(_user.UserId, row.FingerprintId));
                if (!saved)
                {
                    AppToast.Error("默认指纹设置失败");
                    return;
                }
                Changed = true;
                await LoadAsync();
                AppToast.Success("默认指纹已更新");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "设置默认指纹失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void SyncButton_Click(object sender, RoutedEventArgs e)
        {
            if (_templates.Count == 0) return;
            SetBusy(true, "正在校验并同步用户指纹");
            try
            {
                IReadOnlyList<UserCabinetSyncResult> results = await App.CabinetSyncService
                    .VerifyAndSyncUserAsync(_user);
                int updated = results.Count(item => item.Success && item.Changed);
                int unchanged = results.Count(item => item.Success && !item.Changed);
                int failed = results.Count(item => !item.Success);
                StatusText.Text = results.Count == 0
                    ? "没有符合当前用户规则的在线柜机"
                    : $"同步完成：更新 {updated}，无需更新 {unchanged}，失败 {failed}";
                if (failed > 0) AppToast.Warning("部分柜机同步失败");
                else AppToast.Success("指纹与权限已校验同步");
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "指纹同步失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (FingerprintGrid.SelectedItem is not UserFingerprintRow row) return;
            if (row.ExplicitCabinetCount > 0)
            {
                MessageBox.Show($"该指纹仍被 {row.ExplicitCabinetCount} 台柜机明确选择。\n请先在柜机绑定中改用其他指纹。",
                    "不能删除", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            FingerprintTemplate? replacement = _templates.FirstOrDefault(item =>
                item.FingerprintId != row.FingerprintId && item.Enabled);
            string defaultImpact = row.IsDefault
                ? replacement == null
                    ? "\n删除后该用户将没有默认指纹，柜机开锁会暂停到重新录入。"
                    : $"\n删除后将自动把 {replacement.FingerDisplayName} #{replacement.FingerprintId} 设为默认。"
                : "";
            if (MessageBox.Show($"确认删除{row.FingerName} #{row.FingerprintId}？\n将从在线柜机、本机模板库和 SD 备份中清理。{defaultImpact}",
                    "删除指纹", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            SetBusy(true, "正在删除用户指纹");
            try
            {
                BroadcastCommandResult cabinetResult = await App.CabinetSyncService
                    .DeleteFingerprintFromOnlineCabinetsAsync(row.FingerprintId);
                if (_user.FingerprintId == row.FingerprintId)
                {
                    await Task.Run(() => App.UserService.ClearFingerprint(
                        _user.UserId, row.FingerprintId));
                    _user.FingerprintId = null;
                }
                try
                {
                    await App.SdStorageService.DeleteFingerTemplateAsync(
                        _user.UserId, row.FingerIndex);
                }
                catch
                {
                }
                App.FingerprintTemplateService.DeleteTemplate(row.FingerprintId);

                if (!_user.FingerprintId.HasValue && replacement != null)
                    await Task.Run(() => App.UserService.AssignFingerprint(
                        _user.UserId, replacement.FingerprintId));

                string[] deviceIds = await Task.Run(() => App.DeviceService.GetAllDevices()
                    .Where(device => !DeviceService.IsTrueRoot(device))
                    .Select(device => device.DeviceId).ToArray());
                foreach (string deviceId in deviceIds)
                    App.CabinetSyncQueueService.EnqueueCabinet(
                        deviceId, "删除用户指纹并清理柜机槽位");
                App.CabinetSyncQueueService.Trigger();

                Changed = true;
                await LoadAsync();
                StatusText.Text = cabinetResult.Success
                    ? "指纹已删除"
                    : "本地指纹已删除，部分在线柜机未确认清理，请重新同步";
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "删除指纹失败",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void FingerprintGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            UpdateActions();

        private void UpdateActions()
        {
            bool selected = FingerprintGrid.SelectedItem is UserFingerprintRow;
            bool isPersistedDefault = FingerprintGrid.SelectedItem is UserFingerprintRow
                { IsPersistedDefault: true };
            TestButton.IsEnabled = !_busy && _canModify && selected;
            SetDefaultButton.IsEnabled = !_busy && _canModify && selected && !isPersistedDefault;
            SyncButton.IsEnabled = !_busy && _canModify && _templates.Count > 0;
            DeleteButton.IsEnabled = !_busy && _canModify && selected;
        }

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            RefreshButton.IsEnabled = !busy;
            EnrollButton.IsEnabled = !busy && _canModify;
            FingerprintGrid.IsEnabled = !busy;
            if (!string.IsNullOrWhiteSpace(status)) StatusText.Text = status;
            UpdateActions();
        }

        private static string RoleText(string role) => role switch
        {
            "admin" => "管理员",
            "teacher" => "教师",
            _ => "学生"
        };

        private static int ResolveUsedCabinetCount(
            User user, int fingerprintId, int? effectiveDefaultFingerprintId,
            IReadOnlyCollection<CabinetAssignment> assignments, int allCabinetCount)
        {
            int explicitCount = assignments.Count(assignment =>
                assignment.FingerprintIds.Contains(fingerprintId));
            if (string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase))
                return explicitCount;
            if (effectiveDefaultFingerprintId != fingerprintId) return explicitCount;
            int overriddenWithoutDefault = assignments.Count(assignment =>
                !assignment.FingerprintIds.Contains(fingerprintId));
            return Math.Max(0, allCabinetCount - overriddenWithoutDefault);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }

    public sealed class UserFingerprintRow
    {
        public int FingerprintId { get; init; }
        public int FingerIndex { get; init; }
        public string FingerName { get; init; } = "";
        public bool IsDefault { get; init; }
        public bool IsPersistedDefault { get; init; }
        public string DefaultText => IsPersistedDefault ? "默认" : IsDefault ? "自动" : "-";
        public string SourceDevice { get; init; } = "";
        public string BackupStatusText { get; init; } = "";
        public int ExplicitCabinetCount { get; init; }
        public int UsedCabinetCount { get; init; }
        public string UsedCabinetText => UsedCabinetCount == 0 ? "未指定" : $"{UsedCabinetCount} 台";
        public DateTime EnrollTime { get; init; }
    }
}
