using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FingerprintLockManager
{
    /// <summary>
    /// 用户管理页面
    /// 用户列表展示、添加/删除用户、分配指纹ID、按角色筛选
    /// </summary>
    public partial class UserManagePage : Page
    {
        public UserManagePage()
        {
            InitializeComponent();
            Loaded += async (s, e) =>
            {
                RoleFilterBox.SelectedIndex = 0;
                await LoadUsersAsync();
            };
        }

        /// <summary>加载用户列表（按筛选条件）</summary>
        private async Task LoadUsersAsync()
        {
            string? role = GetSelectedRole();
            SetBusy(true, "正在读取根节点用户数据");
            try
            {
                // V2.7：使用 GetVisibleUsers 实现教师数据范围隔离
                List<User> users = await Task.Run(() =>
                {
                    var visible = App.UserService.GetVisibleUsers();
                    return string.IsNullOrEmpty(role)
                        ? visible
                        : visible.Where(u => u.Role == role).ToList();
                });
                UserDataGrid.ItemsSource = users;
                PageStatusText.Text = $"共 {users.Count} 个用户";
            }
            catch (RootDataUnavailableException ex)
            {
                UserDataGrid.ItemsSource = null;
                PageStatusText.Text = ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>获取下拉框选中的角色</summary>
        private string? GetSelectedRole()
        {
            if (RoleFilterBox.SelectedItem is ComboBoxItem item)
            {
                return item.Tag?.ToString();
            }
            return null;
        }

        /// <summary>角色筛选变化</summary>
        private async void RoleFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 加载完成前 SelectedIndex=0 会触发，此时控件可能未就绪
            if (!IsLoaded) return;
            await LoadUsersAsync();
        }

        /// <summary>刷新按钮</summary>
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadUsersAsync();
        }

        /// <summary>添加用户</summary>
        private async void AddUserButton_Click(object sender, RoutedEventArgs e)
        {
            // 弹出对话框输入姓名、角色与密码
            if (!ShowAddUserDialog(out string name, out string role, out string password, out string? classId))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("请输入姓名", "提示");
                return;
            }

            if (!PasswordHelper.IsPasswordAcceptable(password))
            {
                MessageBox.Show(PasswordHelper.PasswordRequirement, "密码不符合要求");
                return;
            }

            // 自动生成用户ID（角色前缀 + 时间戳，避免重复）
            string userId = $"{role}_{DateTime.Now:yyyyMMddHHmmss}";

            var user = new User
            {
                UserId = userId,
                Name = name.Trim(),
                Role = role,
                ClassId = classId,
                FingerprintId = null,
                CreateTime = DateTime.Now
            };

            // 双层权限模型：无需初始化个人权限，用户默认继承角色权限模板
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
                MessageBox.Show($"用户添加成功！\n用户ID：{userId}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadUsersAsync();
            }
            else
            {
                MessageBox.Show("用户添加失败，可能用户ID已存在", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>删除用户</summary>
        private async void DeleteUserButton_Click(object sender, RoutedEventArgs e)
        {
            if (UserDataGrid.SelectedItem is not User selected)
            {
                MessageBox.Show("请先选择要删除的用户", "提示");
                return;
            }

            // 不允许删除最后一个管理员
            if (selected.Role == "admin")
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
                if (admins.Count <= 1)
                {
                    MessageBox.Show("不允许删除最后一个管理员账号", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var result = MessageBox.Show($"确认删除用户「{selected.Name}（{selected.UserId}」？\n该用户的权限记录将一并删除。",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            SetBusy(true, "正在删除用户");
            bool deleted;
            try
            {
                deleted = await Task.Run(() => App.UserService.DeleteUser(selected.UserId));
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

            if (deleted)
            {
                if (selected.FingerprintId.HasValue)
                {
                    App.CabinetSyncService.DeleteFingerprintFromAll(selected.FingerprintId.Value);
                }
                try
                {
                    await App.SdStorageService.DeleteTemplateAsync(selected.UserId);
                }
                catch
                {
                    // 模板清理失败不影响用户删除结果
                }
                MessageBox.Show("删除成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadUsersAsync();
            }
            else
            {
                MessageBox.Show("删除失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>在指定柜子录入指纹，成功后再写入根节点并同步权限。</summary>
        private async void AssignFingerprintButton_Click(object sender, RoutedEventArgs e)
        {
            if (UserDataGrid.SelectedItem is not User selected)
            {
                MessageBox.Show("请先选择要分配指纹的用户", "提示");
                return;
            }

            bool replacing = selected.FingerprintId.HasValue;

            // 已有指纹时固定使用原编号，避免改号后在其他柜子留下孤立模板。
            int suggestId;
            try
            {
                suggestId = selected.FingerprintId ??
                    await Task.Run(App.UserService.GetNextFingerprintId);
            }
            catch (RootDataUnavailableException ex)
            {
                MessageBox.Show(ex.Message, "根节点不可用", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (!ShowAssignFingerprintDialog(suggestId, out int fingerprintId))
            {
                return;
            }

            if (fingerprintId <= 0)
            {
                MessageBox.Show("指纹ID必须为正整数", "提示");
                return;
            }

            // 检查指纹ID是否已被占用
            User? existUser;
            try
            {
                existUser = await Task.Run(() => App.UserService.GetUserByFingerprint(fingerprintId));
            }
            catch (RootDataUnavailableException ex)
            {
                MessageBox.Show(ex.Message, "根节点不可用", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (replacing && fingerprintId != selected.FingerprintId)
            {
                MessageBox.Show($"重新录入必须沿用当前指纹 ID {selected.FingerprintId}",
                    "指纹编号不可变", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (existUser != null && existUser.UserId != selected.UserId)
            {
                MessageBox.Show($"指纹ID {fingerprintId} 已被用户「{existUser.Name}」占用", "提示",
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
            string? targetDevice = SelectCabinet(cabinets);
            if (string.IsNullOrEmpty(targetDevice))
            {
                MessageBox.Show("当前没有可执行录入的在线柜子", "无法录入",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (replacing)
            {
                var confirm = MessageBox.Show(
                    $"确认在目标柜子重新录入指纹 ID {fingerprintId}？\n原模板将被新录入结果覆盖。",
                    "确认重新录入", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes) return;
            }

            SetBusy(true, "请在目标柜子的指纹模块上完成两次采集");
            try
            {
                FingerprintEnrollmentResult enrollment =
                    await App.CommandService.EnrollFingerprintAsync(
                        targetDevice, selected.UserId, fingerprintId, replacing);
                if (!enrollment.Success)
                {
                    MessageBox.Show(
                        string.IsNullOrWhiteSpace(enrollment.ErrorMessage)
                            ? "柜子未能完成指纹录入"
                            : enrollment.ErrorMessage,
                        "录入失败", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                if (!replacing)
                {
                    bool assigned = await Task.Run(() =>
                        App.UserService.AssignFingerprint(selected.UserId, fingerprintId));
                    if (!assigned)
                    {
                        await App.CommandService.SendAsync(targetDevice, Message.Create(
                            Protocol.CmdDeleteFingerprint, targetDevice,
                            new { fingerprint_id = fingerprintId }));
                        MessageBox.Show("指纹已采集，但根节点未能保存编号；已请求柜子回滚模板。",
                            "保存失败", MessageBoxButton.OK, MessageBoxImage.Error);
                        return;
                    }
                    selected.FingerprintId = fingerprintId;
                }

                // 采集-存储-分配解耦：先把模板存到本地指纹模板库
                bool savedLocal = false;
                if (enrollment.TemplateBytes is { Length: > 0 })
                {
                    savedLocal = await Task.Run(() =>
                        App.FingerprintTemplateService.SaveEnrolledTemplate(
                            fingerprintId, enrollment.TemplateBytes!,
                            targetDevice, selected.UserId));
                }

                // 模板上传改为调 FingerprintTemplateService.UploadToSd（带 fallback）
                bool templateBackedUp = false;
                if (savedLocal && enrollment.TemplateBytes is { Length: > 0 })
                {
                    try
                    {
                        templateBackedUp = await App.FingerprintTemplateService.UploadToSdAsync(fingerprintId);
                    }
                    catch
                    {
                        templateBackedUp = false;
                    }
                }

                BroadcastCommandResult permissionsSynced = await Task.Run(
                    App.CabinetSyncService.SyncAllPermissions);
                string summary = "指纹录入已完成。";
                if (savedLocal)
                {
                    summary += "\n模板已暂存到本地指纹模板库。";
                }
                summary += templateBackedUp
                    ? "\n模板已备份到根节点。"
                    : (App.SdStorageService.IsAvailable
                        ? "\n模板尚未备份到根节点。"
                        : "\nSD 不可用，模板仅保存在本地，待 SD 恢复后可在「指纹模板库」中手动上传。");
                summary += "\n" + CabinetSyncService.FormatSyncResult(permissionsSynced,
                    "所有在线柜子均已确认权限更新。",
                    "在线柜子未全部确认权限更新，未确认设备仍使用原有缓存。");

                MessageBox.Show(summary,
                    permissionsSynced.Success ? "录入完成" : "录入完成，待同步",
                    MessageBoxButton.OK,
                    permissionsSynced.Success && savedLocal
                        ? MessageBoxImage.Information
                        : MessageBoxImage.Warning);
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

        /// <summary>
        /// V2.7：批量分配柜子权限。
        /// 对 DataGrid 选中的多个学生，弹出柜子多选对话框，为每个学生写入 4 锁权限覆盖并同步。
        /// </summary>
        private async void BatchAssignPermButton_Click(object sender, RoutedEventArgs e)
        {
            var selectedUsers = UserDataGrid.SelectedItems.OfType<User>().ToList();
            if (selectedUsers.Count == 0)
            {
                MessageBox.Show("请先在列表中选择一个或多个用户（按住 Ctrl 多选）", "提示");
                return;
            }

            // 弹出柜子多选对话框（复用简单的多行输入：每行一个 deviceId，或 * 表示全部在线柜子）
            string? input = PromptDialog.Show(
                "请输入要分配权限的柜子 ID（每行一个），或输入 * 表示全部在线柜子：",
                "批量分配柜子权限",
                "*");
            if (string.IsNullOrWhiteSpace(input)) return;

            // 解析目标柜子列表
            List<string> targetDevices;
            if (input.Trim() == "*")
            {
                targetDevices = App.MeshBridge.GetOnlineDevices()
                    .Where(d => d.IsOnline && !d.IsRoot && !string.IsNullOrWhiteSpace(d.DeviceId))
                    .Select(d => d.DeviceId).Distinct().ToList();
            }
            else
            {
                targetDevices = input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList();
            }
            if (targetDevices.Count == 0)
            {
                MessageBox.Show("没有可用的目标柜子", "提示");
                return;
            }

            // 弹出权限选择（4 锁，简单的多行输入 lock_0..lock_3 的 true/false）
            string? permInput = PromptDialog.Show(
                "请输入 4 把锁的权限（true/false，逗号分隔，对应 Lock0-3）：\n例如：false,true,true,false",
                "权限配置",
                "false,true,true,false");
            if (string.IsNullOrWhiteSpace(permInput)) return;

            var parts = permInput.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 4)
            {
                MessageBox.Show("权限格式错误：需要 4 个 true/false 值", "错误");
                return;
            }
            bool[] perms = new bool[4];
            for (int i = 0; i < 4; i++)
            {
                if (!bool.TryParse(parts[i], out perms[i]))
                {
                    MessageBox.Show($"权限值 '{parts[i]}' 无效", "错误");
                    return;
                }
            }

            SetBusy(true, $"正在为 {selectedUsers.Count} 个用户批量分配权限...");
            try
            {
                int success = 0, fail = 0;
                foreach (var user in selectedUsers)
                {
                    try
                    {
                        var dict = new Dictionary<int, bool>
                        {
                            { 0, perms[0] }, { 1, perms[1] }, { 2, perms[2] }, { 3, perms[3] }
                        };
                        bool ok = await Task.Run(() => App.PermissionService.SetUserPermissions(user.UserId, dict));
                        if (ok) success++;
                        else fail++;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        fail++;
                    }
                }

                // 同步到柜子
                var syncResult = App.CabinetSyncService.SyncAllPermissions();

                MessageBox.Show(
                    $"批量分配完成：成功 {success}，失败 {fail}\n柜子同步：{syncResult}",
                    "完成", MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadUsersAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show("批量分配异常：" + ex.Message, "错误");
            }
            finally
            {
                SetBusy(false);
            }
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

            if (!ShowEditUserDialog(selected, out string name, out string role, out string? classId))
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

            var updated = new User
            {
                UserId = selected.UserId,
                Name = name.Trim(),
                Role = role,
                ClassId = classId,
                FingerprintId = selected.FingerprintId,
                PasswordSalt = selected.PasswordSalt,
                PasswordHash = selected.PasswordHash,
                Enabled = selected.Enabled,
                CreateTime = selected.CreateTime,
                UpdateTime = DateTime.Now
            };

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

            if (!ShowResetPasswordDialog(out string password))
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
            string? targetDevice = SelectCabinet(cabinets);
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

        private void SetBusy(bool busy, string? status = null)
        {
            RefreshButton.IsEnabled = !busy;
            AddUserButton.IsEnabled = !busy;
            ImportCsvButton.IsEnabled = !busy;
            AssignFingerprintButton.IsEnabled = !busy;
            RestoreFingerprintButton.IsEnabled = !busy;
            EditUserButton.IsEnabled = !busy;
            ResetPasswordButton.IsEnabled = !busy;
            ToggleUserButton.IsEnabled = !busy;
            DeleteUserButton.IsEnabled = !busy;
            RoleFilterBox.IsEnabled = !busy;
            if (!string.IsNullOrEmpty(status)) PageStatusText.Text = status;
        }

        private string? SelectCabinet(List<Device> cabinets)
        {
            if (cabinets.Count == 0) return null;
            if (cabinets.Count == 1) return cabinets[0].DeviceId;

            var dialog = new Window
            {
                Title = "选择录入柜子",
                Width = 360,
                Height = 180,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = FindResource("BackgroundBrush") as Brush
            };
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "请选择执行指纹录入的柜子", Margin = new Thickness(0, 0, 0, 8) });
            var combo = new ComboBox { ItemsSource = cabinets, DisplayMemberPath = "DeviceName" };
            combo.SelectedIndex = 0;
            panel.Children.Add(combo);
            var ok = new Button { Content = "确定", Width = 70, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
            panel.Children.Add(ok);
            dialog.Content = panel;

            string? selected = null;
            ok.Click += (s, e) =>
            {
                selected = (combo.SelectedItem as Device)?.DeviceId;
                dialog.Close();
            };
            dialog.ShowDialog();
            return selected;
        }

        // ===== 代码构建的对话框（避免额外文件） =====

        /// <summary>显示添加用户对话框，返回姓名、角色、密码与班级</summary>
        private bool ShowAddUserDialog(out string name, out string role, out string password, out string? classId)
        {
            name = "";
            role = "student";
            password = "";
            classId = null;

            var dlg = new Window
            {
                Title = "添加用户",
                Width = 340,
                Height = 360,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = FindResource("BackgroundBrush") as Brush
            };

            var panel = new StackPanel { Margin = new Thickness(20) };

            panel.Children.Add(new TextBlock { Text = "姓名", Margin = new Thickness(0, 0, 0, 6) });
            var nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(nameBox);

            panel.Children.Add(new TextBlock { Text = "角色", Margin = new Thickness(0, 0, 0, 6) });
            var roleCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 12) };
            roleCombo.Items.Add(new ComboBoxItem { Content = "老师 (teacher)", Tag = "teacher" });
            roleCombo.Items.Add(new ComboBoxItem { Content = "学生 (student)", Tag = "student" });
            roleCombo.Items.Add(new ComboBoxItem { Content = "管理员 (admin)", Tag = "admin" });
            roleCombo.SelectedIndex = 1;
            panel.Children.Add(roleCombo);

            panel.Children.Add(new TextBlock { Text = "班级（可选）", Margin = new Thickness(0, 0, 0, 6) });
            var classCombo = BuildClassCombo(null);
            classCombo.Margin = new Thickness(0, 0, 0, 12);
            panel.Children.Add(classCombo);

            panel.Children.Add(new TextBlock { Text = "密码", Margin = new Thickness(0, 0, 0, 6) });
            var passwordBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(passwordBox);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 8, 0, 0)
            };
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 70, Style = FindResource("SecondaryButton") as Style };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);

            dlg.Content = panel;

            bool confirmed = false;
            string localName = "";
            string localRole = "student";
            string localPassword = "";
            string? localClassId = null;
            okBtn.Click += (s, e) =>
            {
                localName = nameBox.Text;
                if (roleCombo.SelectedItem is ComboBoxItem item)
                    localRole = item.Tag?.ToString() ?? "student";
                localPassword = passwordBox.Password;
                localClassId = (classCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                if (string.IsNullOrWhiteSpace(localClassId)) localClassId = null;
                confirmed = true;
                dlg.Close();
            };
            cancelBtn.Click += (s, e) => dlg.Close();

            dlg.ShowDialog();
            if (confirmed)
            {
                name = localName;
                role = localRole;
                password = localPassword;
                classId = localClassId;
            }
            return confirmed;
        }

        private async void ImportCsvButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;

            SetBusy(true, "正在导入 CSV");
            try
            {
                string[] lines = await Task.Run(() => File.ReadAllLines(dialog.FileName, Encoding.UTF8));
                int success = 0, fail = 0;
                var errors = new List<string>();
                HashSet<string> classIds = new(StringComparer.OrdinalIgnoreCase);
                try
                {
                    classIds = (await Task.Run(App.ClassService.GetAll))
                        .Select(c => c.ClassId).ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                catch (RootDataUnavailableException)
                {
                    // 无班级表时仅允许空 class_id
                }

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (i == 0 && line.Contains("user_id", StringComparison.OrdinalIgnoreCase)) continue;

                    string[] parts = SplitCsvLine(line);
                    if (parts.Length < 4)
                    {
                        fail++;
                        errors.Add($"第 {i + 1} 行字段不足");
                        continue;
                    }

                    string userId = parts[0].Trim();
                    string name = parts[1].Trim();
                    string role = parts[2].Trim().ToLowerInvariant();
                    string password = parts[3];
                    string? classId = parts.Length > 4 ? parts[4].Trim() : null;
                    if (string.IsNullOrWhiteSpace(classId)) classId = null;

                    if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(name) ||
                        role is not ("admin" or "teacher" or "student") ||
                        !PasswordHelper.IsPasswordAcceptable(password))
                    {
                        fail++;
                        errors.Add($"第 {i + 1} 行校验失败");
                        continue;
                    }
                    if (classId != null && classIds.Count > 0 && !classIds.Contains(classId))
                    {
                        fail++;
                        errors.Add($"第 {i + 1} 行班级不存在: {classId}");
                        continue;
                    }

                    bool added = await Task.Run(() => App.UserService.AddUser(new User
                    {
                        UserId = userId,
                        Name = name,
                        Role = role,
                        ClassId = classId,
                        CreateTime = DateTime.Now
                    }, password));
                    if (added) success++;
                    else
                    {
                        fail++;
                        errors.Add($"第 {i + 1} 行写入失败（可能重复）");
                    }
                }

                BroadcastCommandResult? synced = null;
                if (success > 0)
                    synced = await Task.Run(App.CabinetSyncService.SyncAllPermissions);

                string msg = $"导入完成：成功 {success}，失败 {fail}";
                if (errors.Count > 0)
                    msg += "\n" + string.Join("\n", errors.Take(8));
                if (synced != null)
                    msg += "\n" + CabinetSyncService.FormatSyncResult(synced,
                        "权限已同步。", "权限未全部同步。");
                MessageBox.Show(msg, "导入结果", MessageBoxButton.OK,
                    fail == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                await LoadUsersAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private static string[] SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else sb.Append(c);
            }
            fields.Add(sb.ToString());
            return fields.ToArray();
        }

        private ComboBox BuildClassCombo(string? selectedClassId)
        {
            var combo = new ComboBox();
            combo.Items.Add(new ComboBoxItem { Content = "（无）", Tag = "" });
            try
            {
                foreach (var c in App.ClassService.GetAll().Where(x => x.Enabled))
                {
                    var item = new ComboBoxItem { Content = $"{c.Name} ({c.ClassId})", Tag = c.ClassId };
                    combo.Items.Add(item);
                    if (selectedClassId == c.ClassId) combo.SelectedItem = item;
                }
            }
            catch (RootDataUnavailableException)
            {
                // ignore
            }
            if (combo.SelectedItem == null) combo.SelectedIndex = 0;
            return combo;
        }

        /// <summary>显示分配指纹对话框，返回输入的指纹ID</summary>
        private bool ShowAssignFingerprintDialog(int suggestId, out int fingerprintId)
        {
            fingerprintId = 0;

            var dlg = new Window
            {
                Title = "分配指纹ID",
                Width = 320,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = FindResource("BackgroundBrush") as Brush
            };

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "指纹ID（正整数）", Margin = new Thickness(0, 0, 0, 6) });
            var idBox = new TextBox { Text = suggestId.ToString(), Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(idBox);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 70, Style = FindResource("SecondaryButton") as Style };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);

            dlg.Content = panel;

            bool confirmed = false;
            // 使用局部变量在 lambda 中暂存（out 参数不能在 lambda 中赋值）
            int localId = 0;
            okBtn.Click += (s, e) =>
            {
                if (int.TryParse(idBox.Text?.Trim(), out int id))
                {
                    localId = id;
                    confirmed = true;
                    dlg.Close();
                }
                else
                {
                    MessageBox.Show("请输入有效的数字", "提示");
                }
            };
            cancelBtn.Click += (s, e) => dlg.Close();

            dlg.ShowDialog();
            if (confirmed)
            {
                fingerprintId = localId;
            }
            return confirmed;
        }

        private bool ShowEditUserDialog(User user, out string name, out string role, out string? classId)
        {
            name = user.Name;
            role = user.Role;
            classId = user.ClassId;

            var dlg = new Window
            {
                Title = "编辑用户",
                Width = 340,
                Height = 300,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = FindResource("BackgroundBrush") as Brush
            };

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock
            {
                Text = $"用户 ID：{user.UserId}",
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = FindResource("SubTextBrush") as Brush
            });
            panel.Children.Add(new TextBlock { Text = "姓名", Margin = new Thickness(0, 0, 0, 6) });
            var nameBox = new TextBox { Text = user.Name, Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(nameBox);

            panel.Children.Add(new TextBlock { Text = "角色", Margin = new Thickness(0, 0, 0, 6) });
            var roleCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 12) };
            roleCombo.Items.Add(new ComboBoxItem { Content = "老师 (teacher)", Tag = "teacher" });
            roleCombo.Items.Add(new ComboBoxItem { Content = "学生 (student)", Tag = "student" });
            roleCombo.Items.Add(new ComboBoxItem { Content = "管理员 (admin)", Tag = "admin" });
            roleCombo.SelectedIndex = user.Role switch
            {
                "admin" => 2,
                "teacher" => 0,
                _ => 1
            };
            panel.Children.Add(roleCombo);

            panel.Children.Add(new TextBlock { Text = "班级（可选）", Margin = new Thickness(0, 0, 0, 6) });
            var classCombo = BuildClassCombo(user.ClassId);
            classCombo.Margin = new Thickness(0, 0, 0, 16);
            panel.Children.Add(classCombo);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 70, Style = FindResource("SecondaryButton") as Style };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);
            dlg.Content = panel;

            bool confirmed = false;
            string localName = user.Name;
            string localRole = user.Role;
            string? localClassId = user.ClassId;
            okBtn.Click += (s, e) =>
            {
                localName = nameBox.Text;
                if (roleCombo.SelectedItem is ComboBoxItem item)
                    localRole = item.Tag?.ToString() ?? user.Role;
                localClassId = (classCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                if (string.IsNullOrWhiteSpace(localClassId)) localClassId = null;
                confirmed = true;
                dlg.Close();
            };
            cancelBtn.Click += (s, e) => dlg.Close();
            dlg.ShowDialog();
            if (confirmed)
            {
                name = localName;
                role = localRole;
                classId = localClassId;
            }
            return confirmed;
        }

        private bool ShowResetPasswordDialog(out string password)
        {
            password = "";
            var dlg = new Window
            {
                Title = "重置密码",
                Width = 340,
                Height = 240,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = FindResource("BackgroundBrush") as Brush
            };
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock
            {
                Text = PasswordHelper.PasswordRequirement,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                Foreground = FindResource("SubTextBrush") as Brush,
                FontSize = 11
            });
            panel.Children.Add(new TextBlock { Text = "新密码", Margin = new Thickness(0, 0, 0, 6) });
            var passwordBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(passwordBox);
            panel.Children.Add(new TextBlock { Text = "确认密码", Margin = new Thickness(0, 0, 0, 6) });
            var confirmBox = new PasswordBox { Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(confirmBox);

            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var okBtn = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 10, 0) };
            var cancelBtn = new Button { Content = "取消", Width = 70, Style = FindResource("SecondaryButton") as Style };
            btnPanel.Children.Add(okBtn);
            btnPanel.Children.Add(cancelBtn);
            panel.Children.Add(btnPanel);
            dlg.Content = panel;

            bool confirmed = false;
            string localPassword = "";
            okBtn.Click += (s, e) =>
            {
                if (passwordBox.Password != confirmBox.Password)
                {
                    MessageBox.Show("两次输入的密码不一致", "提示");
                    return;
                }
                localPassword = passwordBox.Password;
                confirmed = true;
                dlg.Close();
            };
            cancelBtn.Click += (s, e) => dlg.Close();
            dlg.ShowDialog();
            if (confirmed) password = localPassword;
            return confirmed;
        }
    }
}
