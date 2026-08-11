using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace CabinetLock
{
    public partial class CabinetFingerprintBindingWindow : BorderlessWindow
    {
        private readonly Device _device;
        private List<UserOption> _users = new();
        private List<TemplateOption> _templates = new();
        private bool _loading;
        private bool _busy;

        public CabinetFingerprintBindingWindow(Device device)
        {
            InitializeComponent();
            ArgumentNullException.ThrowIfNull(device);
            _device = device;
            CabinetMetaText.Text = $"{device.DisplayIdentity} · {device.DeviceId}";
            LoadClasses();
        }

        public bool BindingCompleted { get; private set; }

        private void LoadClasses()
        {
            _loading = true;
            try
            {
                List<ClassOption> classes = App.ClassService.GetVisible()
                    .Where(item => item.Enabled)
                    .OrderBy(item => item.Name)
                    .ThenBy(item => item.ClassId)
                    .Select(item => ClassOption.ForClass(item.ClassId, item.Name))
                    .ToList();
                if (DataScopeContext.Instance.IsAdmin)
                    classes.Add(ClassOption.Management());
                ClassCombo.ItemsSource = classes;
                ClassCombo.SelectedIndex = -1;
                ResetUserSelection(classes.Count == 0
                    ? "没有可选择的班级"
                    : "请先选择班级，再选择学生");
            }
            catch (Exception ex)
            {
                ClassCombo.ItemsSource = null;
                ResetUserSelection($"班级读取失败：{ex.Message}");
            }
            finally
            {
                _loading = false;
                SetActionState();
            }
        }

        private void ClassCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading) LoadUsers();
        }

        private void LoadUsers(string? selectedUserId = null)
        {
            _loading = true;
            try
            {
                if (ClassCombo.SelectedItem is not ClassOption selectedClass)
                {
                    ResetUserSelection("请先选择班级，再选择学生");
                    return;
                }

                IEnumerable<User> users = selectedClass.IsManagement
                    ? ReadManagementUsers()
                    : ReadUsers("student", selectedClass.ClassId);
                _users = users
                    .Where(user => user.Enabled && DataScopeContext.Instance.CanModify(user))
                    .OrderBy(user => user.Name)
                    .ThenBy(user => user.DisplayId)
                    .Select(user => new UserOption(user))
                    .ToList();
                UserCombo.ItemsSource = _users;
                UserCombo.IsEnabled = _users.Count > 0;
                UserCombo.SelectedIndex = -1;
                if (!string.IsNullOrWhiteSpace(selectedUserId))
                    UserCombo.SelectedValue = selectedUserId;
                ClearSelectedUser(_users.Count == 0
                    ? "当前班级没有可绑定的学生"
                    : "请选择学生");
            }
            catch (Exception ex)
            {
                ResetUserSelection($"学生读取失败：{ex.Message}");
            }
            finally
            {
                _loading = false;
                if (UserCombo.SelectedItem is UserOption)
                    RefreshSelectedUserSafely();
                else
                    SetActionState();
            }
        }

        private static IEnumerable<User> ReadManagementUsers()
        {
            return ReadUsers("teacher").Concat(ReadUsers("admin"));
        }

        private static IReadOnlyList<User> ReadUsers(string role, string? classId = null)
        {
            var users = new List<User>();
            int pageIndex = 1;
            while (true)
            {
                PagedResult<User> page = App.UserService.QueryVisibleUsersPage(
                    pageIndex, 500, role: role, classId: classId,
                    sort: UserPageSort.RoleThenName);
                users.AddRange(page.Items);
                if (users.Count >= page.TotalCount || page.Items.Count == 0) return users;
                pageIndex++;
            }
        }

        private void UserCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loading) RefreshSelectedUserSafely();
        }

        private void RefreshSelectedUserSafely()
        {
            try
            {
                RefreshSelectedUser();
            }
            catch (Exception ex)
            {
                ClearSelectedUser($"用户绑定数据读取失败：{ex.Message}");
            }
        }

        private void RefreshSelectedUser()
        {
            if (UserCombo.SelectedItem is not UserOption option)
            {
                _templates.Clear();
                TemplateList.ItemsSource = null;
                TemplateCountText.Text = "0 枚";
                TemplateStatusText.Text = _users.Count == 0 ? "没有可绑定的用户" : "请先选择用户";
                NoTemplatePanel.Visibility = Visibility.Visible;
                SetActionState();
                return;
            }

            User user = option.User;
            List<FingerprintTemplate> allTemplates = App.FingerprintTemplateService.GetAllTemplates();
            HashSet<int> selectedIds = App.CabinetBindingService
                .GetSelectedFingerprintIds(user, _device.DeviceId, allTemplates).ToHashSet();
            _templates = allTemplates
                .Where(template => template.Enabled && template.FingerprintId > 0 &&
                    string.Equals(template.UserId, user.UserId, StringComparison.OrdinalIgnoreCase))
                .GroupBy(template => template.FingerprintId)
                .Select(group => group.Last())
                .OrderBy(template => template.FingerIndex)
                .ThenBy(template => template.FingerprintId)
                .Select(template => new TemplateOption(template, selectedIds.Contains(template.FingerprintId)))
                .ToList();
            if (_templates.Count > 0 && _templates.All(template => !template.IsSelected))
                _templates[0].IsSelected = true;
            TemplateList.ItemsSource = _templates;
            TemplateCountText.Text = $"{_templates.Count} 枚";
            TemplateStatusText.Text = _templates.Count == 0
                ? "需要先采集用户模板，录入不会自动占用正式槽位"
                : "选择要写入当前柜机正式槽位的模板";
            NoTemplatePanel.Visibility = _templates.Count == 0
                ? Visibility.Visible : Visibility.Collapsed;

            bool[] fallback = App.PermissionService.GetFinalPermissions(user.UserId);
            bool[] permissions = App.CabinetBindingService.GetLockPermissions(
                user, _device.DeviceId, fallback);
            Lock0CheckBox.IsChecked = permissions.ElementAtOrDefault(0);
            Lock1CheckBox.IsChecked = permissions.ElementAtOrDefault(1);
            Lock2CheckBox.IsChecked = permissions.ElementAtOrDefault(2);
            Lock3CheckBox.IsChecked = permissions.ElementAtOrDefault(3);
            SetPermissionControlState(user, enabled: true);
            Lock0CheckBox.ToolTip = Lock0CheckBox.IsEnabled
                ? "允许使用管理员系统锁" : "系统锁仅管理员可用";
            StatusText.Text = _templates.Count == 0
                ? "请先录入。采集结束后 0 号临时槽会被清空。"
                : "点击“绑定并下发”后，才会写入柜机正式槽位。";
            SetActionState();
        }

        private void TemplateSelection_Changed(object sender, RoutedEventArgs e) => SetActionState();

        private void EnrollButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || UserCombo.SelectedItem is not UserOption option) return;
            var window = new EnrollFingerprintWindow(
                _device.DeviceId, option.User.UserId, fixedUserMode: true)
            {
                Owner = this
            };
            window.ShowDialog();
            if (window.EnrolledFingerprintId > 0)
                LoadUsers(option.User.UserId);
        }

        private async void BindButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || UserCombo.SelectedItem is not UserOption option) return;
            int[] fingerprintIds = _templates.Where(template => template.IsSelected)
                .Select(template => template.FingerprintId).ToArray();
            if (fingerprintIds.Length == 0)
            {
                AppToast.Info("请至少选择一枚用户指纹");
                return;
            }
            int[] lockIds = new[]
            {
                Lock0CheckBox.IsChecked == true ? 0 : -1,
                Lock1CheckBox.IsChecked == true ? 1 : -1,
                Lock2CheckBox.IsChecked == true ? 2 : -1,
                Lock3CheckBox.IsChecked == true ? 3 : -1
            }.Where(id => id >= 0).ToArray();
            if (lockIds.Length == 0)
            {
                AppToast.Info("请至少选择一个柜门权限");
                return;
            }

            SetBusy(true, "正在保存当前柜机绑定配置");
            try
            {
                bool saved = await Task.Run(() => App.CabinetBindingService
                    .SetAssignmentConfiguration(option.User.UserId, _device.DeviceId,
                        fingerprintIds, lockIds, enqueueSync: false));
                if (!saved)
                {
                    StatusText.Text = "绑定配置保存失败，请检查用户、指纹模板和权限选择。";
                    AppToast.Error("绑定配置保存失败");
                    return;
                }

                User? current = await Task.Run(() => App.UserService.GetUser(option.User.UserId));
                if (current == null)
                {
                    StatusText.Text = "绑定已保存，但重新读取用户失败。";
                    return;
                }
                var progress = new Progress<UserCabinetSyncProgress>(item =>
                    StatusText.Text = $"正在下发：{item.Status}");
                IReadOnlyList<UserCabinetSyncResult> results = await App.CabinetSyncService
                    .VerifyAndSyncUserAsync(current, new[] { _device.DeviceId }, progress);
                UserCabinetSyncResult? result = results.FirstOrDefault();
                if (result?.Success != true)
                {
                    string reason = result?.ErrorMessage ?? "柜机未返回同步结果";
                    StatusText.Text = $"绑定已保存，但下发未完成：{reason}";
                    AppToast.Warning("绑定已保存，柜机下发未完成");
                    return;
                }

                BindingCompleted = true;
                StatusText.Text = result.Changed
                    ? "模板与权限已写入柜机。" : "柜机中的模板与权限已经一致。";
                AppToast.Success("用户指纹已绑定并下发");
                DialogResult = true;
            }
            catch (Exception ex)
            {
                StatusText.Text = $"绑定失败：{ex.Message}";
                AppToast.Error("绑定用户失败");
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            UserCombo.IsEnabled = !busy;
            TemplateList.IsEnabled = !busy;
            EnrollButton.IsEnabled = !busy && UserCombo.SelectedItem is UserOption;
            CancelButton.IsEnabled = !busy;
            SetPermissionControlState(
                (UserCombo.SelectedItem as UserOption)?.User, enabled: !busy);
            SyncProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (!string.IsNullOrWhiteSpace(status)) StatusText.Text = status;
            SetActionState();
        }

        private void SetActionState()
        {
            EnrollButton.IsEnabled = !_busy && UserCombo.SelectedItem is UserOption;
            BindButton.IsEnabled = !_busy && UserCombo.SelectedItem is UserOption &&
                _templates.Any(template => template.IsSelected);
        }

        private void SetPermissionControlState(User? user, bool enabled)
        {
            CheckBox[] controls =
            {
                Lock0CheckBox, Lock1CheckBox, Lock2CheckBox, Lock3CheckBox
            };
            for (int lockId = 0; lockId < controls.Length; lockId++)
            {
                bool canGrant = user != null && PermissionPolicy.CanGrant(user.Role, lockId);
                controls[lockId].IsEnabled = enabled && canGrant;
                if (!canGrant && user != null) controls[lockId].IsChecked = false;
            }
        }

        private void ResetUserSelection(string status)
        {
            _users = new List<UserOption>();
            UserCombo.ItemsSource = null;
            UserCombo.SelectedIndex = -1;
            UserCombo.IsEnabled = false;
            ClearSelectedUser(status);
        }

        private void ClearSelectedUser(string status)
        {
            _templates = new List<TemplateOption>();
            TemplateList.ItemsSource = null;
            TemplateCountText.Text = "0 枚";
            TemplateStatusText.Text = status;
            NoTemplatePanel.Visibility = Visibility.Visible;
            Lock0CheckBox.IsChecked = false;
            Lock1CheckBox.IsChecked = false;
            Lock2CheckBox.IsChecked = false;
            Lock3CheckBox.IsChecked = false;
            Lock0CheckBox.IsEnabled = false;
            Lock1CheckBox.IsEnabled = false;
            Lock2CheckBox.IsEnabled = false;
            Lock3CheckBox.IsEnabled = false;
            StatusText.Text = status;
            SetActionState();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

        private sealed class UserOption
        {
            public UserOption(User user) => User = user;
            public User User { get; }
            public string UserId => User.UserId;
            public string DisplayText => $"{User.Name} ({User.DisplayId}) · " +
                FingerprintSelectionData.RoleText(User.Role);
        }

        private sealed class ClassOption
        {
            private const string ManagementKey = "__management__";

            private ClassOption(string key, string classId, string displayText, bool management)
            {
                Key = key;
                ClassId = classId;
                DisplayText = displayText;
                IsManagement = management;
            }

            public string Key { get; }
            public string ClassId { get; }
            public string DisplayText { get; }
            public bool IsManagement { get; }

            public static ClassOption ForClass(string classId, string name) => new(
                classId, classId, $"{name} ({classId})", false);

            public static ClassOption Management() => new(
                ManagementKey, "", "管理员与教师", true);
        }

        private sealed class TemplateOption : INotifyPropertyChanged
        {
            private bool _isSelected;

            public TemplateOption(FingerprintTemplate template, bool selected)
            {
                FingerprintId = template.FingerprintId;
                FingerName = template.FingerDisplayName;
                BackupStatusText = template.BackupStatusText;
                _isSelected = selected;
            }

            public int FingerprintId { get; }
            public string FingerName { get; }
            public string BackupStatusText { get; }
            public bool IsSelected
            {
                get => _isSelected;
                set
                {
                    if (_isSelected == value) return;
                    _isSelected = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
                }
            }

            public event PropertyChangedEventHandler? PropertyChanged;
        }
    }
}
