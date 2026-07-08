using System.Windows;
using System.Windows.Controls;

namespace FingerprintLockManager
{
    /// <summary>
    /// 权限管理页面
    /// 左侧用户列表，右侧4把锁（Lock0-3）权限勾选
    /// 注意：系统锁(Lock0)只有 admin 角色可勾选，其他角色禁用
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

        /// <summary>加载指定用户的权限并填充勾选框</summary>
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

            // 获取用户权限
            var permissions = App.PermissionService.GetUserPermissions(user.UserId);

            // 默认全 false，再按数据库记录填充
            bool[] access = new bool[4];
            foreach (var p in permissions)
            {
                if (p.LockId >= 0 && p.LockId < 4)
                {
                    access[p.LockId] = p.HasAccess;
                }
            }

            // 若数据库无记录，按角色默认权限
            if (permissions.Count == 0)
            {
                access = GetDefaultByRole(user.Role);
            }

            Lock0CheckBox.IsChecked = access[0] && isAdmin;
            Lock1CheckBox.IsChecked = access[1];
            Lock2CheckBox.IsChecked = access[2];
            Lock3CheckBox.IsChecked = access[3];

            // 非 admin 用户 Lock0 强制不勾选并禁用
            if (!isAdmin)
            {
                Lock0CheckBox.IsChecked = false;
            }
        }

        /// <summary>保存权限按钮</summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedUser == null)
            {
                MessageBox.Show("请先选择用户", "提示");
                return;
            }

            string userId = _selectedUser.UserId;
            bool isAdmin = _selectedUser.Role == "admin";

            // 构造权限字典
            var dict = new Dictionary<int, bool>
            {
                [0] = isAdmin && (Lock0CheckBox.IsChecked == true),
                [1] = Lock1CheckBox.IsChecked == true,
                [2] = Lock2CheckBox.IsChecked == true,
                [3] = Lock3CheckBox.IsChecked == true
            };

            if (App.PermissionService.SetPermissions(userId, dict))
            {
                MessageBox.Show("权限保存成功", "成功", MessageBoxButton.OK, MessageBoxImage.Information);

                // 同步权限到该用户所在的所有在线设备
                SyncPermissionsToDevice(userId);
            }
            else
            {
                MessageBox.Show("权限保存失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>同步权限到在线设备（按指纹查询权限后下发）</summary>
        private void SyncPermissionsToDevice(string userId)
        {
            try
            {
                var user = App.UserService.GetUser(userId);
                if (user == null || !user.FingerprintId.HasValue) return;

                bool[] permissions = App.PermissionService.GetPermissionsByFingerprint(user.FingerprintId.Value);

                var data = new Dictionary<string, object>
                {
                    ["fingerprint_id"] = user.FingerprintId.Value,
                    ["user_id"] = user.UserId,
                    ["permissions"] = permissions
                };

                // 广播同步权限到所有在线设备
                var msg = Message.Create(Protocol.CmdSyncPermissions, "", data);
                App.TcpServer.Broadcast(msg);
            }
            catch
            {
                // 同步失败时忽略，不影响本地保存
            }
        }

        /// <summary>根据角色获取默认权限（与 PermissionService 保持一致）</summary>
        private static bool[] GetDefaultByRole(string role)
        {
            bool[] result = new bool[4];
            switch (role)
            {
                case "admin":
                    result[0] = true; result[1] = true; result[2] = true; result[3] = true;
                    break;
                case "teacher":
                    result[0] = false; result[1] = true; result[2] = true; result[3] = true;
                    break;
                default:
                    result[0] = false; result[1] = false; result[2] = false; result[3] = false;
                    break;
            }
            return result;
        }
    }
}
