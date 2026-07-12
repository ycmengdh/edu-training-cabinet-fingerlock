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
            Loaded += (s, e) => LoadUsers();
        }

        /// <summary>加载用户列表</summary>
        private void LoadUsers()
        {
            var users = App.UserService.GetAllUsers();
            UserListBox.ItemsSource = users;
        }

        /// <summary>用户列表选中变化：加载该用户权限</summary>
        private void UserListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UserListBox.SelectedItem is not User user)
            {
                _selectedUser = null;
                return;
            }

            _selectedUser = user;
            LoadUserPermissions(user);
        }

        /// <summary>
        /// 加载指定用户的权限并填充勾选框
        /// 合并角色默认权限 + 个人覆盖项，并标记每把锁的权限来源
        /// </summary>
        private void LoadUserPermissions(User user)
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
            var rolePerm = App.RolePermissionService.GetRolePermission(user.Role);
            bool[] finalAccess = rolePerm.ToArray();

            // 第二层：个人覆盖项（存在覆盖则替换对应锁，并标记来源）
            bool[] hasOverride = new bool[4];
            var overrides = App.PermissionService.GetUserPermissions(user.UserId);
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
        }

        /// <summary>设置单个锁的勾选状态与来源标记</summary>
        private void SetLockState(CheckBox cb, TextBlock sourceText, bool hasAccess, bool isOverride)
        {
            cb.IsChecked = hasAccess;
            if (isOverride)
            {
                sourceText.Text = "[覆盖]";
                sourceText.Foreground = FindResource("PrimaryBrush") as Brush;
            }
            else
            {
                sourceText.Text = "[默认]";
                sourceText.Foreground = FindResource("SubTextBrush") as Brush;
            }
        }

        /// <summary>保存权限按钮：写入个人覆盖项</summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
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

            if (App.PermissionService.SetUserPermissions(userId, dict))
            {
                MessageBox.Show("权限保存成功（已写入个人覆盖项）", "成功", MessageBoxButton.OK, MessageBoxImage.Information);

                // 重新加载以刷新来源标记
                LoadUserPermissions(_selectedUser);

                // 同步权限到该用户所在的所有在线设备
                SyncPermissionsToDevice(userId);
            }
            else
            {
                MessageBox.Show("权限保存失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>重置为角色默认按钮：删除该用户所有个人覆盖项，回退到角色默认权限</summary>
        private void ResetButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedUser == null)
            {
                MessageBox.Show("请先选择用户", "提示");
                return;
            }

            var result = MessageBox.Show($"确认将用户「{_selectedUser.Name}」的权限重置为角色默认？\n所有个人覆盖项将被删除。",
                "确认重置", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            if (App.PermissionService.DeleteAllUserPermissions(_selectedUser.UserId))
            {
                MessageBox.Show("已重置为角色默认权限", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadUserPermissions(_selectedUser);
                SyncPermissionsToDevice(_selectedUser.UserId);
            }
            else
            {
                MessageBox.Show("重置失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>同步权限到在线设备（按指纹查询最终权限后广播下发）</summary>
        private void SyncPermissionsToDevice(string userId)
        {
            try
            {
                var user = App.UserService.GetUser(userId);
                if (user == null || !user.FingerprintId.HasValue) return;

                bool[] permissions = App.PermissionService.GetFinalPermissions(user.UserId);

                var data = new Dictionary<string, object>
                {
                    ["fingerprint_id"] = user.FingerprintId.Value,
                    ["user_id"] = user.UserId,
                    ["permissions"] = permissions
                };

                // 广播同步权限到所有在线设备（经 Root 转发）
                var msg = Message.Create(Protocol.CmdSyncPermissions, "", data);
                App.MeshBridge.Broadcast(msg);
            }
            catch
            {
                // 同步失败时忽略，不影响本地保存
            }
        }
    }
}
