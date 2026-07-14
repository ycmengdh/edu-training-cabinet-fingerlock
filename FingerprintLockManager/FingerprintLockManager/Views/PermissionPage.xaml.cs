using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FingerprintLockManager
{
    /// <summary>
    /// 权限管理页面（双层权限模型 + 设备维度授权适配）
    ///
    /// 左侧用户列表，右侧 4 把锁（Lock0-3）权限勾选，每把锁显示权限来源标记（默认/覆盖）。
    /// 保存时写入个人覆盖项（SetUserPermission）；"重置为角色默认"按钮删除个人覆盖回退到角色默认。
    ///
    /// 适配需求 6/8：学生权限改为按设备维度下发（DeviceAuthorization）。
    /// 本页面只负责"角色默认 + 个人覆盖"全局策略，不再广播 SYNC_PERMISSIONS。
    /// 实际下发到柜子由「柜子分配」页面 + DeployService 完成。
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

        /// <summary>加载用户列表（老师按班级限制）</summary>
        private void LoadUsers()
        {
            var users = App.UserService.GetAllUsers();

            // 老师只能看自己班级的学生 + 自己
            if (App.CurrentUser?.Role == "teacher")
            {
                var myClassIds = App.ClassService.GetClassesByTeacher(App.CurrentUser.UserId)
                    .Select(c => c.ClassId)
                    .ToHashSet();
                users = users.Where(u =>
                    u.UserId == App.CurrentUser.UserId ||
                    (u.Role == "student" && myClassIds.Contains(u.ClassId ?? "")))
                    .ToList();
            }

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

            // 操作前自动备份（需求 11）
            App.BackupService.BackupBeforeAction($"修改用户权限 {userId}", App.CurrentUser?.UserId);

            if (App.PermissionService.SetUserPermissions(userId, dict))
            {
                // 重新加载以刷新来源标记
                LoadUserPermissions(_selectedUser);

                // 需求 6/8：学生权限按设备维度下发，不再广播 SYNC_PERMISSIONS
                // 提示用户去「柜子分配」页面重新下发
                if (_selectedUser.Role == "student")
                {
                    MessageBox.Show(
                        "权限保存成功（已写入个人覆盖项）。\n\n" +
                        "注意：学生权限按设备维度下发。如需让柜子生效，请到「柜子分配」页面重新下发。",
                        "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                else
                {
                    MessageBox.Show("权限保存成功（已写入个人覆盖项）", "成功",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                }
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

            // 操作前自动备份（需求 11）
            App.BackupService.BackupBeforeAction($"重置用户权限为默认 {_selectedUser.UserId}", App.CurrentUser?.UserId);

            if (App.PermissionService.DeleteAllUserPermissions(_selectedUser.UserId))
            {
                MessageBox.Show("已重置为角色默认权限", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadUserPermissions(_selectedUser);
            }
            else
            {
                MessageBox.Show("重置失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
