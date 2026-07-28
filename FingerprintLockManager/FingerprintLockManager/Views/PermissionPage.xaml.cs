using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FingerprintLockManager
{
    /// <summary>
    /// 权限管理页面（双层权限模型）
    /// 左侧用户列表，右侧 4 把锁（Lock0-3）权限勾选，每把锁显示权限来源标记（默认/覆盖）。
    /// 保存时写入个人覆盖项（SetUserPermission）；"重置为角色默认"按钮删除个人覆盖回退到角色默认。
    /// </summary>
    public partial class PermissionPage : Page
    {
        /// <summary>当前选中的用户</summary>
        private User? _selectedUser;

        public PermissionPage()
        {
            InitializeComponent();
            Loaded += async (s, e) => await LoadUsersAsync();
        }

        /// <summary>加载用户列表</summary>
        private async Task LoadUsersAsync()
        {
            SetBusy(true, "正在读取根节点权限数据");
            try
            {
                // V2.7：使用 GetVisibleUsers 实现教师数据范围隔离
                var users = await Task.Run(App.UserService.GetVisibleUsers);
                UserListBox.ItemsSource = users;
                PageStatusText.Text = $"共 {users.Count} 个用户";
            }
            catch (RootDataUnavailableException ex)
            {
                UserListBox.ItemsSource = null;
                PageStatusText.Text = ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadUsersAsync();
        }

        /// <summary>用户列表选中变化：加载该用户权限</summary>
        private async void UserListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UserListBox.SelectedItem is not User user)
            {
                _selectedUser = null;
                return;
            }

            _selectedUser = user;
            await LoadUserPermissionsAsync(user);
        }

        /// <summary>
        /// 加载指定用户的权限并填充勾选框
        /// 合并角色默认权限 + 个人覆盖项，并标记每把锁的权限来源
        /// </summary>
        private async Task LoadUserPermissionsAsync(User user)
        {
            // 显示用户信息
            SelectedUserName.Text = user.Name;
            SelectedUserRole.Text = user.Role;
            SelectedUserFp.Text = user.FingerprintId.HasValue
                ? $"指纹ID：{user.FingerprintId.Value}"
                : "指纹ID：未分配";

            // 系统锁 Lock0 仅 admin 可勾选
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

            // 非 admin 用户 Lock0 强制不勾选并禁用
            if (!isAdmin)
            {
                Lock0CheckBox.IsChecked = false;
            }
            PageStatusText.Text = $"正在编辑 {user.Name} 的本地鉴权权限";
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

            // 构造个人覆盖字典（Lock0 仅 admin 可写）
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
                await LoadUserPermissionsAsync(_selectedUser);
                BroadcastCommandResult synced = await SyncPermissionsAsync();
                MessageBox.Show(
                    CabinetSyncService.FormatSyncResult(synced,
                        "权限已保存，所有在线柜子均已确认",
                        "权限已保存，但在线柜子未全部确认"),
                    synced.Success ? "保存完成" : "同步提示", MessageBoxButton.OK,
                    synced.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
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
                await LoadUserPermissionsAsync(_selectedUser);
                BroadcastCommandResult synced = await SyncPermissionsAsync();
                MessageBox.Show(
                    CabinetSyncService.FormatSyncResult(synced,
                        "个人权限已按模板重置，所有在线柜子均已确认",
                        "个人权限已按模板重置，但在线柜子未全部确认"),
                    "重置完成", MessageBoxButton.OK,
                    synced.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
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
            if (!string.IsNullOrEmpty(status)) PageStatusText.Text = status;
        }

        private async Task<BroadcastCommandResult> SyncPermissionsAsync()
        {
            try
            {
                return await Task.Run(App.CabinetSyncService.SyncAllPermissions);
            }
            catch (RootDataUnavailableException ex)
            {
                PageStatusText.Text = ex.Message;
                return BroadcastCommandResult.Failed(ex.Message);
            }
        }
    }
}
