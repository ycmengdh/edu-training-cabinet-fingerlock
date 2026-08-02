using System.Windows;
using System.Windows.Controls;

namespace CabinetLock
{
    /// <summary>
    /// 用户列表壳：展示/筛选 + 基础账号维护。
    /// 指纹录入/恢复、批量分配权限、CSV 导入等场景入口打开独立窗口，
    /// 避免本页继续膨胀为「上帝页面」。
    /// </summary>
    public partial class UserManagePage : Page
    {
        private readonly string? _classId;
        private readonly string? _className;
        private readonly ListPager _pager = new(50);
        private List<User> _filteredUsers = new();

        public UserManagePage()
            : this(null, null)
        {
        }

        public UserManagePage(string? classId, string? className)
        {
            _classId = string.IsNullOrWhiteSpace(classId) ? null : classId;
            _className = className;
            InitializeComponent();
            if (_classId != null)
            {
                PageTitleText.Text = $"班级工作台 · {_className}";
                PageStatusText.Text = "维护本班学生、录入指纹并分配柜子权限";
                RoleFilterPanel.Visibility = Visibility.Collapsed;
                ImportCsvButton.Visibility = Visibility.Collapsed;
                ResetPasswordButton.Visibility = Visibility.Collapsed;
            }
            Loaded += async (s, e) =>
            {
                if (_classId == null) RoleFilterBox.SelectedIndex = 0;
                await LoadUsersAsync();
            };
        }

        private Window? OwnerWindow => Window.GetWindow(this);

        /// <summary>加载用户列表（按筛选条件 + 分页）</summary>
        private async Task LoadUsersAsync(bool resetPage = true)
        {
            if (resetPage) _pager.Reset();
            string? role = GetSelectedRole();
            string keyword = UserSearchBox?.Text?.Trim() ?? "";
            SetBusy(true, "正在读取根节点用户数据");
            try
            {
                _filteredUsers = await Task.Run(() =>
                {
                    var visible = App.UserService.GetVisibleUsers();
                    IEnumerable<User> query = visible;
                    if (_classId != null)
                    {
                        query = query.Where(u => u.Role == "student" &&
                            string.Equals(u.ClassId, _classId, StringComparison.OrdinalIgnoreCase));
                    }
                    else if (!string.IsNullOrEmpty(role))
                    {
                        query = query.Where(u => u.Role == role);
                    }
                    if (!string.IsNullOrWhiteSpace(keyword))
                    {
                        query = query.Where(u =>
                            (u.Name?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false) ||
                            u.DisplayId.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                            (u.ClassId?.Contains(keyword, StringComparison.OrdinalIgnoreCase) ?? false));
                    }
                    return query.OrderBy(u => u.Role).ThenBy(u => u.DisplayId).ToList();
                });
                ApplyUserPage();
            }
            catch (RootDataUnavailableException ex)
            {
                _filteredUsers.Clear();
                UserDataGrid.ItemsSource = null;
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
            var page = _pager.Slice(_filteredUsers);
            UserDataGrid.ItemsSource = page;
            _pager.BindChrome(Pager);
            PageStatusText.Text = _pager.StatusText(page.Count);
        }

        private string? GetSelectedRole()
        {
            if (RoleFilterBox.SelectedItem is ComboBoxItem item)
                return item.Tag?.ToString();
            return null;
        }

        private async void RoleFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            await LoadUsersAsync(resetPage: true);
        }

        private async void UserSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            await LoadUsersAsync(resetPage: true);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
            await LoadUsersAsync(resetPage: false);

        private void Pager_PageRequested(object sender, Controls.PaginationRequestedEventArgs e)
        {
            _pager.ApplyRequest(e);
            ApplyUserPage();
        }

        private void SelectPageCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || sender is not CheckBox checkBox ||
                UserDataGrid.ItemsSource is not IEnumerable<User> page) return;
            bool selected = checkBox.IsChecked == true;
            foreach (User item in page) item.IsSelected = selected;
            UserDataGrid.Items.Refresh();
        }

        // ===== 基础账号维护（留在列表页） =====

        private async void AddUserButton_Click(object sender, RoutedEventArgs e)
        {
            if (!UserManageDialogs.ShowAddUserDialog(OwnerWindow, out string userCode,
                    out string name, out string role,
                    out string password, out string? classId, _classId, _classId != null))
                return;

            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("请输入姓名", "提示");
                return;
            }

            if (role != "student" && !PasswordHelper.IsPasswordAcceptable(password))
            {
                MessageBox.Show(PasswordHelper.PasswordRequirement, "密码不符合要求");
                return;
            }

            var user = new User
            {
                UserCode = userCode,
                Name = name.Trim(),
                Role = role,
                ClassId = classId,
                AssignedDeviceIds = string.Equals(role, "student", StringComparison.OrdinalIgnoreCase)
                    ? new List<string>()
                    : null,
                FingerprintId = null,
                CreateTime = DateTime.Now
            };
            if (string.Equals(role, "teacher", StringComparison.OrdinalIgnoreCase))
                user.SetResponsibleClassIds(string.IsNullOrWhiteSpace(classId)
                    ? Array.Empty<string>() : new[] { classId });

            SetBusy(true, "正在保存用户");
            bool added;
            try
            {
                added = await Task.Run(() => App.UserService.AddUser(user, password));
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

            if (added)
            {
                MessageBox.Show($"用户添加成功！\n{user.IdentityLabel}：{user.DisplayId}", "成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadUsersAsync();
            }
            else
            {
                MessageBox.Show("用户添加失败，可能用户ID已存在", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private List<User> GetCheckedOrSelectedUsers()
        {
            var checkedUsers = _filteredUsers.Where(u => u.IsSelected).ToList();
            if (checkedUsers.Count > 0) return checkedUsers;

            // 兼容：未勾选时回退到 DataGrid 当前选中行
            return UserDataGrid.SelectedItems.OfType<User>().ToList();
        }

        private async void DeleteUserButton_Click(object sender, RoutedEventArgs e)
        {
            List<User> targets = GetCheckedOrSelectedUsers();
            if (targets.Count == 0)
            {
                MessageBox.Show("请先勾选要删除的用户", "提示");
                return;
            }

            var adminTargets = targets.Where(u => u.Role == "admin").ToList();
            if (adminTargets.Count > 0)
            {
                List<User> admins;
                try
                {
                    admins = await Task.Run(() => App.UserService.GetUsersByRole("admin"));
                }
                catch (RootDataUnavailableException ex)
                {
                    MessageBox.Show(ex.Message, "根节点不可用", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                if (admins.Count - adminTargets.Count < 1)
                {
                    MessageBox.Show("不允许删除最后一个管理员账号", "提示",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            string confirm = targets.Count == 1
                ? $"确认删除用户「{targets[0].Name}（{targets[0].DisplayId}）」？\n该用户的权限记录将一并删除。"
                : $"确认批量删除已勾选的 {targets.Count} 名用户？\n其权限记录将一并删除。";
            if (MessageBox.Show(confirm, "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;

            SetBusy(true, targets.Count == 1 ? "正在删除用户" : $"正在删除 {targets.Count} 名用户");
            int success = 0;
            int fail = 0;
            try
            {
                foreach (User selected in targets)
                {
                    bool deleted;
                    try
                    {
                        deleted = await Task.Run(() => App.UserService.DeleteUser(selected.UserId));
                    }
                    catch (RootDataUnavailableException ex)
                    {
                        MessageBox.Show(ex.Message, "根节点不可用", MessageBoxButton.OK, MessageBoxImage.Error);
                        break;
                    }

                    if (!deleted)
                    {
                        fail++;
                        continue;
                    }

                    success++;
                    if (selected.FingerprintId.HasValue)
                        App.CabinetSyncService.DeleteFingerprintFromAll(selected.FingerprintId.Value);
                    App.CabinetBindingService.RemoveFromAll(selected.UserId);
                    try
                    {
                        await App.SdStorageService.DeleteTemplateAsync(selected.UserId);
                    }
                    catch
                    {
                        // 模板清理失败不影响用户删除结果
                    }
                }
            }
            finally
            {
                SetBusy(false);
            }

            if (success > 0 && fail == 0)
            {
                MessageBox.Show(success == 1 ? "删除成功" : $"已成功删除 {success} 名用户", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (success > 0)
            {
                MessageBox.Show($"成功 {success} 名，失败 {fail} 名", "部分完成",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show("删除失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            if (success > 0) await LoadUsersAsync();
        }

        private async void ToggleUserButton_Click(object sender, RoutedEventArgs e)
        {
            if (UserDataGrid.SelectedItem is not User selected)
            {
                MessageBox.Show("请先选择用户", "提示");
                return;
            }
            if (selected.UserId == App.CurrentUser?.UserId)
            {
                MessageBox.Show("不能停用当前登录账号", "操作受限",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool targetEnabled = !selected.Enabled;
            if (!targetEnabled && selected.Role == "admin")
            {
                List<User> enabledAdmins;
                try
                {
                    enabledAdmins = await Task.Run(() => App.UserService
                        .GetUsersByRole("admin").Where(u => u.Enabled).ToList());
                }
                catch (RootDataUnavailableException ex)
                {
                    MessageBox.Show(ex.Message, "根节点不可用",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                if (enabledAdmins.Count <= 1)
                {
                    MessageBox.Show("至少需要保留一个启用的管理员账号", "操作受限",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            string action = targetEnabled ? "启用" : "停用";
            if (MessageBox.Show($"确认{action}用户「{selected.Name}」？",
                    $"确认{action}", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes) return;

            SetBusy(true, $"正在{action}用户");
            try
            {
                bool saved = await Task.Run(() =>
                    App.UserService.SetEnabled(selected.UserId, targetEnabled));
                if (!saved)
                {
                    MessageBox.Show($"用户{action}失败", "错误",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                BroadcastCommandResult synced = await Task.Run(App.CabinetSyncService.SyncAllPermissions);
                MessageBox.Show(
                    CabinetSyncService.FormatSyncResult(synced,
                        $"用户已{action}，所有在线柜子均已确认权限更新",
                        $"用户已{action}，但在线柜子未全部确认权限更新"),
                    action + "完成", MessageBoxButton.OK,
                    synced.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
                await LoadUsersAsync();
            }
            catch (RootDataUnavailableException ex)
            {
                MessageBox.Show(ex.Message, "根节点不可用",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void EditUserButton_Click(object sender, RoutedEventArgs e)
        {
            if (UserDataGrid.SelectedItem is not User selected)
            {
                MessageBox.Show("请先选择用户", "提示");
                return;
            }

            if (!UserManageDialogs.ShowEditUserDialog(OwnerWindow, selected, out string userCode,
                    out string name,
                    out string role, out string? classId, _classId, _classId != null))
                return;

            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("请输入姓名", "提示");
                return;
            }

            bool roleChanged = !string.Equals(selected.Role, role, StringComparison.Ordinal);
            if (roleChanged && selected.Role == "admin" && role != "admin")
            {
                List<User> enabledAdmins;
                try
                {
                    enabledAdmins = await Task.Run(() => App.UserService
                        .GetUsersByRole("admin").Where(u => u.Enabled).ToList());
                }
                catch (RootDataUnavailableException ex)
                {
                    MessageBox.Show(ex.Message, "根节点不可用", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                if (enabledAdmins.Count <= 1)
                {
                    MessageBox.Show("至少需要保留一个管理员账号", "操作受限",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            IReadOnlyList<string> teacherClassIds = string.Equals(role, "teacher", StringComparison.OrdinalIgnoreCase)
                ? string.Equals(selected.Role, "teacher", StringComparison.OrdinalIgnoreCase)
                    ? selected.GetResponsibleClassIds()
                    : string.IsNullOrWhiteSpace(classId) ? Array.Empty<string>() : new[] { classId }
                : Array.Empty<string>();
            var updated = new User
            {
                UserId = selected.UserId,
                UserCode = userCode,
                Name = name.Trim(),
                Gender = selected.Gender,
                Role = role,
                ClassId = classId,
                FingerprintId = selected.FingerprintId,
                PasswordSalt = selected.PasswordSalt,
                PasswordHash = selected.PasswordHash,
                Enabled = selected.Enabled,
                CreateTime = selected.CreateTime,
                UpdateTime = DateTime.Now
            };
            if (string.Equals(role, "teacher", StringComparison.OrdinalIgnoreCase))
                updated.SetResponsibleClassIds(teacherClassIds);

            SetBusy(true, "正在保存用户信息");
            try
            {
                bool saved = await Task.Run(() => App.UserService.UpdateUser(updated));
                if (!saved)
                {
                    MessageBox.Show("用户信息保存失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (roleChanged && updated.FingerprintId.HasValue && updated.Enabled)
                {
                    BroadcastCommandResult synced = await Task.Run(App.CabinetSyncService.SyncAllPermissions);
                    MessageBox.Show(
                        CabinetSyncService.FormatSyncResult(synced,
                            "用户信息已更新，权限已同步到在线柜子",
                            "用户信息已更新，但权限未全部同步"),
                        "编辑完成", MessageBoxButton.OK,
                        synced.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
                }
                else
                {
                    MessageBox.Show("用户信息已更新", "编辑完成",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
                await LoadUsersAsync();
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

        private async void ResetPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (UserDataGrid.SelectedItem is not User selected)
            {
                MessageBox.Show("请先选择用户", "提示");
                return;
            }

            if (!UserManageDialogs.ShowResetPasswordDialog(OwnerWindow, out string password))
                return;

            if (!PasswordHelper.IsPasswordAcceptable(password))
            {
                MessageBox.Show(PasswordHelper.PasswordRequirement, "密码不符合要求");
                return;
            }

            SetBusy(true, "正在重置密码");
            try
            {
                bool ok = await Task.Run(() =>
                    App.UserService.ResetPassword(selected.UserId, password));
                MessageBox.Show(ok ? "密码已重置" : "密码重置失败",
                    ok ? "完成" : "错误", MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Error);
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

        // ===== 场景入口：指纹 / 权限 / 导入 =====

        /// <summary>打开统一录入窗口录入指纹，成功后写入用户并同步权限。</summary>
        private async void AssignFingerprintButton_Click(object sender, RoutedEventArgs e)
        {
            if (UserDataGrid.SelectedItem is not User selected)
            {
                MessageBox.Show("请先选择要分配指纹的用户", "提示");
                return;
            }

            var window = new EnrollFingerprintWindow(presetDeviceId: null, presetUserId: selected.UserId)
            {
                Owner = OwnerWindow
            };
            window.ShowDialog();

            if (window.EnrolledFingerprintId <= 0)
                return;

            selected.FingerprintId = window.EnrolledFingerprintId;
            await LoadUsersAsync();
        }

        private async void RestoreFingerprintButton_Click(object sender, RoutedEventArgs e)
        {
            if (UserDataGrid.SelectedItem is not User selected)
            {
                MessageBox.Show("请先选择用户", "提示");
                return;
            }
            if (!selected.FingerprintId.HasValue)
            {
                MessageBox.Show("该用户尚未录入指纹，无法恢复", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var cabinets = App.MeshBridge.GetOnlineDevices()
                .Where(d => !d.IsRoot)
                .Select(d => new Device
                {
                    DeviceId = d.DeviceId,
                    DeviceName = string.IsNullOrWhiteSpace(d.DeviceName) ? d.DeviceId : d.DeviceName,
                    IsOnline = true,
                    MeshMac = d.MeshMac,
                    IpAddress = ""
                }).ToList();
            string? targetDevice = UserManageDialogs.SelectCabinet(OwnerWindow, cabinets, "选择恢复目标柜子");
            if (string.IsNullOrEmpty(targetDevice))
            {
                MessageBox.Show("当前没有可执行恢复的在线柜子", "无法恢复",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SetBusy(true, "正在从根节点下载模板并写入目标柜子");
            try
            {
                byte[]? template = await App.SdStorageService.DownloadTemplateAsync(selected.UserId, 1);
                if (template == null || template.Length == 0)
                {
                    MessageBox.Show("根节点没有该用户的指纹模板备份", "无法恢复",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                CommandResult restore = await App.CommandService.RestoreFingerprintAsync(
                    targetDevice, selected.UserId, selected.FingerprintId.Value, template);
                if (!restore.Success)
                {
                    MessageBox.Show(
                        string.IsNullOrWhiteSpace(restore.ErrorMessage)
                            ? "柜子未能写入指纹模板"
                            : restore.ErrorMessage,
                        "恢复失败", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                BroadcastCommandResult synced = await Task.Run(App.CabinetSyncService.SyncAllPermissions);
                MessageBox.Show(
                    "模板已写入目标柜子。\n" +
                    CabinetSyncService.FormatSyncResult(synced,
                        "权限同步完成。",
                        "权限同步未全部完成。"),
                    "恢复完成", MessageBoxButton.OK,
                    synced.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
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

        private async void BatchAssignPermButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedUsers = GetCheckedOrSelectedUsers();
            if (selectedUsers.Count == 0)
            {
                MessageBox.Show("请先勾选一个或多个用户", "提示");
                return;
            }

            var window = new BatchAssignPermissionWindow(selectedUsers) { Owner = OwnerWindow };
            if (window.ShowDialog() == true)
                await LoadUsersAsync();
        }

        private async void ImportCsvButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new ImportUsersWindow { Owner = OwnerWindow };
            if (window.ShowDialog() == true || window.AnyImported)
                await LoadUsersAsync();
        }

        private void SetBusy(bool busy, string? status = null)
        {
            RefreshButton.IsEnabled = !busy;
            AddUserButton.IsEnabled = !busy;
            ImportCsvButton.IsEnabled = !busy;
            AssignFingerprintButton.IsEnabled = !busy;
            RestoreFingerprintButton.IsEnabled = !busy;
            BatchAssignPermButton.IsEnabled = !busy;
            EditUserButton.IsEnabled = !busy;
            ResetPasswordButton.IsEnabled = !busy;
            ToggleUserButton.IsEnabled = !busy;
            DeleteUserButton.IsEnabled = !busy;
            RoleFilterBox.IsEnabled = !busy;
            if (!string.IsNullOrEmpty(status)) PageStatusText.Text = status;
        }
    }
}
