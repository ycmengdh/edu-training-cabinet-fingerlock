using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Collections.ObjectModel;

namespace CabinetLock
{
    /// <summary>
    /// 权限管理页面（双层权限模型）
    /// 左侧用户列表，右侧 4 把锁（界面 Lock1-4）权限勾选，每把锁显示权限来源标记（默认/覆盖）。
    /// 保存时写入个人覆盖项（SetUserPermission）；"重置为角色默认"按钮删除个人覆盖回退到角色默认。
    /// </summary>
    public partial class PermissionPage : Page
    {
        /// <summary>当前选中的用户</summary>
        private User? _selectedUser;
        private readonly ListPager _pager = new(40);
        private List<PermissionUserRow> _filteredUsers = new();
        private readonly ObservableCollection<string> _classOptions = new ObservableCollection<string>();

        public PermissionPage()
        {
            InitializeComponent();
            Loaded += async (s, e) =>
            {
                await LoadClassesAsync();
                await LoadUsersAsync();
            };
        }

        private async Task LoadClassesAsync()
        {
            _classOptions.Clear();
            _classOptions.Add("全部班级");
            var visibleClasses = (await Task.Run(App.ClassService.GetVisibleClassNames))
                .Values.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(name => name).ToList();
            foreach (var name in visibleClasses)
            {
                _classOptions.Add(name);
            }
            ClassFilterCombo.ItemsSource = _classOptions;
        }

        /// <summary>加载用户列表</summary>
        private async Task LoadUsersAsync(bool resetPage = true)
        {
            if (resetPage) _pager.Reset();
            string keyword = UserSearchBox?.Text?.Trim() ?? "";
            string selectedClass = ClassFilterCombo?.SelectedItem as string ?? "";
            string classFilter = selectedClass == "全部班级" ? "" : selectedClass;
            SetBusy(true, "正在读取根节点权限数据");
            try
            {
                (PagedResult<User> users, Dictionary<string, string> classNames) = await Task.Run(() =>
                {
                    PagedResult<User> page = App.UserService.QueryVisibleUsersPage(
                        _pager.PageIndex,
                        _pager.PageSize,
                        keyword: keyword,
                        className: classFilter,
                        sort: UserPageSort.RoleThenName);
                    return (page, App.ClassService.GetVisibleClassNames());
                });
                Dictionary<string, DateTime> permissionUpdates = await Task.Run(() =>
                    App.PermissionService.GetLatestUpdateTimes(
                        users.Items.Select(user => user.UserId).ToArray()));
                _filteredUsers = users.Items.Select(user => new PermissionUserRow(
                    user,
                    classNames,
                    permissionUpdates.TryGetValue(user.UserId, out DateTime updateTime) ? updateTime : null))
                    .ToList();
                _pager.SetTotalCount(users.TotalCount);
                ApplyUserPage();
            }
            catch (RootDataUnavailableException ex)
            {
                _filteredUsers.Clear();
                UserListBox.ItemsSource = null;
                PageStatusText.Text = ex.Message;
                _pager.BindChrome(Pager);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ApplyUserPage()
        {
            UserListBox.ItemsSource = _filteredUsers;
            UserListBox.SelectedIndex = _filteredUsers.Count > 0 ? 0 : -1;
            _pager.BindChrome(Pager);
            PageStatusText.Text = _pager.StatusText(_filteredUsers.Count);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadUsersAsync(resetPage: false);
        }

        private async void UserSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            await LoadUsersAsync(resetPage: true);
        }

        private async void ClassFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            await LoadUsersAsync(resetPage: true);
        }

        private async void Pager_PageRequested(object sender, Controls.PaginationRequestedEventArgs e)
        {
            _pager.ApplyRequest(e);
            await LoadUsersAsync(resetPage: false);
        }

        /// <summary>用户列表选中变化：加载该用户权限</summary>
        private async void UserListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UserListBox.SelectedItem is not PermissionUserRow row)
            {
                _selectedUser = null;
                PermissionEditor.IsEnabled = false;
                ShowStudentAuthorizationEditor(false);
                StudentAuthorizationGrid.ItemsSource = null;
                SelectedUserName.Text = "未选择用户";
                ApplyRoleBadge(null);
                SelectedUserFp.Text = "指纹 ID：-";
                return;
            }

            _selectedUser = row.User;
            PermissionEditor.IsEnabled = true;
            await LoadUserPermissionsAsync(row.User);
        }

        /// <summary>
        /// 加载指定用户的权限并填充勾选框
        /// 合并角色默认权限 + 个人覆盖项，并标记每把锁的权限来源
        /// </summary>
        private async Task LoadUserPermissionsAsync(User user)
        {
            // 显示用户信息
            SelectedUserName.Text = user.Name;
            ApplyRoleBadge(user.Role);
            bool isStudent = string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase);
            ShowStudentAuthorizationEditor(isStudent);
            if (isStudent)
            {
                SelectedUserFp.Text = "指纹模板：正在读取";
                await LoadStudentAuthorizationsAsync(user);
                return;
            }

            SelectedUserFp.Text = user.FingerprintId.HasValue
                ? $"指纹ID：{user.FingerprintId.Value}"
                : "指纹ID：未分配";

            // 系统锁（界面 Lock1，内部索引 0）仅 admin 可勾选
            bool isAdmin = user.Role == "admin";
            Lock0CheckBox.IsEnabled = isAdmin;

            // 第一层：角色默认权限
            RolePermission rolePerm;
            List<UserPermission> overrides;
            SetBusy(true, $"正在读取 {user.Name} 的权限");
            try
            {
                (rolePerm, overrides) = await Task.Run(() => (
                    App.RolePermissionService.GetRolePermission(user.Role),
                    App.PermissionService.GetUserPermissions(user.UserId)));
            }
            catch (RootDataUnavailableException ex)
            {
                PageStatusText.Text = ex.Message;
                return;
            }
            finally
            {
                SetBusy(false);
            }

            if (_selectedUser?.UserId != user.UserId) return;
            bool[] finalAccess = rolePerm.ToArray();
            bool[] hasOverride = new bool[4];
            foreach (var p in overrides)
            {
                if (p.LockId >= 0 && p.LockId < 4)
                {
                    finalAccess[p.LockId] = p.HasAccess;
                    hasOverride[p.LockId] = true;
                }
            }

            // 填充勾选框与来源标记
            SetLockState(Lock0CheckBox, Lock0Source, finalAccess[0] && isAdmin, hasOverride[0]);
            SetLockState(Lock1CheckBox, Lock1Source, finalAccess[1], hasOverride[1]);
            SetLockState(Lock2CheckBox, Lock2Source, finalAccess[2], hasOverride[2]);
            SetLockState(Lock3CheckBox, Lock3Source, finalAccess[3], hasOverride[3]);

            // 非 admin 用户的系统锁强制不勾选并禁用
            if (!isAdmin)
            {
                Lock0CheckBox.IsChecked = false;
            }
            PageStatusText.Text = $"正在编辑 {user.Name} 的本地鉴权权限";
        }

        private void ShowStudentAuthorizationEditor(bool showStudent)
        {
            StudentAuthorizationPanel.Visibility = showStudent ? Visibility.Visible : Visibility.Collapsed;
            StudentAuthorizationActions.Visibility = showStudent ? Visibility.Visible : Visibility.Collapsed;
            StandardPermissionPanel.Visibility = showStudent ? Visibility.Collapsed : Visibility.Visible;
            StandardPermissionActions.Visibility = showStudent ? Visibility.Collapsed : Visibility.Visible;
        }

        private async Task LoadStudentAuthorizationsAsync(User user)
        {
            SetBusy(true, $"正在读取 {user.Name} 的柜机授权");
            try
            {
                StudentAuthorizationSnapshot snapshot = await Task.Run(() =>
                {
                    List<Device> devices = App.DeviceService.GetAllDevices()
                        .Where(device => !DeviceService.IsTrueRoot(device) &&
                            !string.IsNullOrWhiteSpace(device.DeviceId))
                        .ToList();
                    List<FingerprintTemplate> templates = BusinessDatabase.ReadAllFpTemplateMetas()
                        .Where(item => string.Equals(item.UserId, user.UserId,
                            StringComparison.OrdinalIgnoreCase))
                        .GroupBy(item => item.FingerprintId)
                        .Select(group => group.First())
                        .ToList();
                    IReadOnlyList<CabinetAssignment> assignments = App.CabinetBindingService
                        .GetAssignments(user, devices.Select(device => device.DeviceId));
                    bool[] defaultPermissions = App.PermissionService.GetFinalPermissions(user.UserId);
                    IReadOnlyList<CabinetSyncJob> syncJobs = App.CabinetSyncQueueService.GetAll();
                    Dictionary<string, Device> devicesById = devices
                        .GroupBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(group => group.Key, group => group.First(),
                            StringComparer.OrdinalIgnoreCase);

                    List<StudentAuthorizationRow> rows = assignments.Select(assignment =>
                    {
                        devicesById.TryGetValue(assignment.DeviceId, out Device? device);
                        IReadOnlyList<int> selectedIds = App.CabinetBindingService
                            .GetSelectedFingerprintIds(user, assignment.DeviceId, templates);
                        string[] selectedFingerprints = templates
                            .Where(item => item.Enabled && selectedIds.Contains(item.FingerprintId))
                            .OrderBy(item => item.FingerIndex)
                            .ThenBy(item => item.FingerprintId)
                            .Select(item => $"{item.FingerDisplayName} #{item.FingerprintId}")
                            .ToArray();
                        bool[] lockPermissions = App.CabinetBindingService.GetLockPermissions(
                            user, assignment.DeviceId, defaultPermissions);
                        CabinetSyncJob? syncJob = syncJobs.FirstOrDefault(job =>
                            string.Equals(job.DeviceId, assignment.DeviceId,
                                StringComparison.OrdinalIgnoreCase) &&
                            ((job.JobKind == "user" && string.Equals(job.UserId, user.UserId,
                                StringComparison.OrdinalIgnoreCase)) || job.JobKind == "cabinet"));
                        bool complete = selectedFingerprints.Length > 0 &&
                            lockPermissions.Any(value => value);
                        return new StudentAuthorizationRow
                        {
                            DeviceText = device == null
                                ? assignment.DeviceId
                                : string.IsNullOrWhiteSpace(device.DeviceName)
                                    ? device.DeviceId : device.DeviceName,
                            FingerprintText = selectedFingerprints.Length == 0
                                ? "未选择" : string.Join("、", selectedFingerprints),
                            LockPermissionText = FormatStudentLockPermissions(lockPermissions),
                            AuthorizationStatusText = complete
                                ? syncJob?.StatusText ?? "未校验" : "待配置"
                        };
                    }).OrderBy(row => row.DeviceText, StringComparer.OrdinalIgnoreCase).ToList();

                    return new StudentAuthorizationSnapshot
                    {
                        FingerprintCount = templates.Count(item => item.Enabled),
                        Rows = rows
                    };
                });

                if (_selectedUser?.UserId != user.UserId) return;
                SelectedUserFp.Text = $"指纹模板：{snapshot.FingerprintCount} 枚";
                StudentAuthorizationGrid.ItemsSource = snapshot.Rows;
                bool hasAssignments = snapshot.Rows.Count > 0;
                StudentAuthorizationGrid.Visibility = hasAssignments
                    ? Visibility.Visible : Visibility.Collapsed;
                StudentAuthorizationEmpty.Visibility = hasAssignments
                    ? Visibility.Collapsed : Visibility.Visible;
                PageStatusText.Text = hasAssignments
                    ? $"{user.Name} 已授权 {snapshot.Rows.Count} 台实训柜"
                    : $"{user.Name} 尚未绑定实训柜";
            }
            catch (RootDataUnavailableException ex)
            {
                StudentAuthorizationGrid.ItemsSource = null;
                StudentAuthorizationGrid.Visibility = Visibility.Collapsed;
                StudentAuthorizationEmpty.Visibility = Visibility.Visible;
                PageStatusText.Text = ex.Message;
            }
            catch (Exception ex)
            {
                StudentAuthorizationGrid.ItemsSource = null;
                StudentAuthorizationGrid.Visibility = Visibility.Collapsed;
                StudentAuthorizationEmpty.Visibility = Visibility.Visible;
                PageStatusText.Text = $"柜机授权读取失败：{ex.Message}";
            }
            finally
            {
                SetBusy(false);
            }
        }

        private static string FormatStudentLockPermissions(IReadOnlyList<bool> permissions)
        {
            string[] names = { "系统锁", "柜门 1", "柜门 2", "柜门 3" };
            string[] selected = names.Where((_, index) => permissions.ElementAtOrDefault(index)).ToArray();
            return selected.Length == 0 ? "未选择" : string.Join("、", selected);
        }

        private void ApplyRoleBadge(string? role)
        {
            string normalizedRole = role?.Trim().ToLowerInvariant() ?? "";
            SelectedUserRole.Text = normalizedRole switch
            {
                "admin" => "管理员",
                "teacher" => "教师",
                "student" => "学生",
                _ => "-"
            };

            string backgroundKey;
            string borderKey;
            string foregroundKey;
            switch (normalizedRole)
            {
                case "admin":
                    backgroundKey = "DangerSurfaceBrush";
                    borderKey = "DangerBorderBrush";
                    foregroundKey = "DangerBrush";
                    break;
                case "teacher":
                    backgroundKey = "PrimaryLightBrush";
                    borderKey = "PrimaryBrush";
                    foregroundKey = "PrimaryDarkBrush";
                    break;
                case "student":
                    backgroundKey = "HintBrush";
                    borderKey = "HintBorderBrush";
                    foregroundKey = "SuccessBrush";
                    break;
                default:
                    backgroundKey = "SurfaceAltBrush";
                    borderKey = "BorderBrush";
                    foregroundKey = "SubTextBrush";
                    break;
            }

            RoleBadge.Background = FindResource(backgroundKey) as Brush;
            RoleBadge.BorderBrush = FindResource(borderKey) as Brush;
            SelectedUserRole.Foreground = FindResource(foregroundKey) as Brush;
        }

        /// <summary>设置单个锁的勾选状态与来源标记</summary>
        private void SetLockState(CheckBox cb, TextBlock sourceText, bool hasAccess, bool isOverride)
        {
            cb.IsChecked = hasAccess;
            if (isOverride)
            {
                sourceText.Text = "个人覆盖";
                sourceText.Foreground = FindResource("PrimaryBrush") as Brush;
            }
            else
            {
                sourceText.Text = "角色默认";
                sourceText.Foreground = FindResource("SubTextBrush") as Brush;
            }
        }

        /// <summary>保存权限按钮：写入个人覆盖项</summary>
        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedUser == null)
            {
                MessageBox.Show("请先选择用户", "提示");
                return;
            }

            string userId = _selectedUser.UserId;
            bool isAdmin = _selectedUser.Role == "admin";

            // 构造个人覆盖字典（系统锁内部索引 0 仅 admin 可写）
            var dict = new Dictionary<int, bool>
            {
                [0] = isAdmin && (Lock0CheckBox.IsChecked == true),
                [1] = Lock1CheckBox.IsChecked == true,
                [2] = Lock2CheckBox.IsChecked == true,
                [3] = Lock3CheckBox.IsChecked == true
            };

            SetBusy(true, "正在保存个人权限");
            bool saved;
            try
            {
                saved = await Task.Run(() => App.PermissionService.SetUserPermissions(userId, dict));
            }
            catch (RootDataUnavailableException ex)
            {
                MessageBox.Show(ex.Message, "根节点不可用", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                SetBusy(false);
            }

            if (saved)
            {
                UpdateSelectedPermissionTime(DateTime.Now);
                await LoadUserPermissionsAsync(_selectedUser);
                MessageBox.Show(
                    "权限已保存。在线柜子将立即增量更新，离线柜子已加入待同步队列。",
                    "保存完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("权限保存失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>重置为角色默认按钮：删除该用户所有个人覆盖项，回退到角色默认权限</summary>
        private async void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedUser == null)
            {
                MessageBox.Show("请先选择用户", "提示");
                return;
            }

            var result = MessageBox.Show($"确认按当前角色模板重置用户「{_selectedUser.Name}」的个人权限？\n这只修改该用户当前权限。",
                "确认重置", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            bool reset;
            SetBusy(true, "正在按模板重置个人权限");
            try
            {
                reset = await Task.Run(() => App.PermissionService.DeleteAllUserPermissions(_selectedUser.UserId));
            }
            catch (RootDataUnavailableException ex)
            {
                MessageBox.Show(ex.Message, "根节点不可用", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                SetBusy(false);
            }

            if (reset)
            {
                UpdateSelectedPermissionTime(null);
                await LoadUserPermissionsAsync(_selectedUser);
                MessageBox.Show(
                    "个人权限已按模板重置。在线柜子将立即增量更新，离线柜子已排队。",
                    "重置完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("重置失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void SetBusy(bool busy, string? status = null)
        {
            RefreshButton.IsEnabled = !busy;
            UserListBox.IsEnabled = !busy;
            SaveButton.IsEnabled = !busy && _selectedUser != null;
            ResetButton.IsEnabled = !busy && _selectedUser != null;
            ManageStudentAuthorizationButton.IsEnabled = !busy && _selectedUser != null;
            if (!string.IsNullOrEmpty(status)) PageStatusText.Text = status;
        }

        private async void ManageStudentAuthorizationButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedUser == null || !string.Equals(
                    _selectedUser.Role, "student", StringComparison.OrdinalIgnoreCase)) return;

            string className = UserListBox.SelectedItem is PermissionUserRow row
                ? row.ClassName : "未分配";
            var window = new StudentDetailWindow(_selectedUser, className)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
            _selectedUser = App.UserService.GetUser(_selectedUser.UserId) ?? _selectedUser;
            await LoadUserPermissionsAsync(_selectedUser);
        }

        private void UpdateSelectedPermissionTime(DateTime? updateTime)
        {
            if (UserListBox.SelectedItem is not PermissionUserRow row) return;
            row.SetPermissionUpdateTime(updateTime);
            UserListBox.Items.Refresh();
        }

        private sealed class PermissionUserRow
        {
            public PermissionUserRow(User user, IReadOnlyDictionary<string, string> classNames,
                DateTime? permissionUpdateTime)
            {
                User = user;
                RoleText = user.Role switch
                {
                    "admin" => "管理员",
                    "teacher" => "教师",
                    _ => "学生"
                };
                IdentityText = user.Role switch
                {
                    "teacher" => $"{user.DisplayId}",
                    "student" => $"{user.DisplayId}",
                    _ => $"{user.DisplayId}"
                };
                string[] assignedClasses = user.GetResponsibleClassIds().Select(classId =>
                    classNames.TryGetValue(classId, out string? className)
                        ? className : classId).ToArray();
                ClassName = assignedClasses.Length == 0
                    ? "未分配" : string.Join("、", assignedClasses);
                ClassText = assignedClasses.Length == 0
                    ? "-"
                    : string.Join("、", assignedClasses);
                SetPermissionUpdateTime(permissionUpdateTime);
            }

            public User User { get; }
            public string Name => User.Name;
            public string RoleText { get; }
            public string IdentityText { get; }
            public string ClassName { get; }
            public string ClassText { get; }
            public string PermissionUpdateText { get; private set; } = "";

            public bool Matches(string keyword) =>
                User.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                User.DisplayId.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                RoleText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                ClassText.Contains(keyword, StringComparison.OrdinalIgnoreCase);

            public void SetPermissionUpdateTime(DateTime? updateTime)
            {
                PermissionUpdateText = updateTime.HasValue
                    ? $" {updateTime.Value:yyyy-MM-dd HH:mm}"
                    : "尚无个人权限更新";
            }
        }

        private sealed class StudentAuthorizationSnapshot
        {
            public int FingerprintCount { get; init; }
            public List<StudentAuthorizationRow> Rows { get; init; } = new();
        }

        private sealed class StudentAuthorizationRow
        {
            public string DeviceText { get; init; } = "";
            public string FingerprintText { get; init; } = "";
            public string LockPermissionText { get; init; } = "";
            public string AuthorizationStatusText { get; init; } = "";
        }
    }
}
