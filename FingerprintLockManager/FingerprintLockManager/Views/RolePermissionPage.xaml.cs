using System.Windows;
using System.Windows.Controls;

namespace FingerprintLockManager
{
    /// <summary>
    /// 角色默认权限配置页（仅 admin 可访问）
    /// 3 行（admin/teacher/student）× 4 列（Lock0-3）的权限矩阵。
    /// 保存调用 RolePermissionService.SetRolePermission，并批量重新计算所有用户最终权限同步到设备。
    /// </summary>
    public partial class RolePermissionPage : Page
    {
        public RolePermissionPage()
        {
            InitializeComponent();
            Loaded += (s, e) => LoadRolePermissions();
        }

        /// <summary>加载角色默认权限到矩阵</summary>
        private void LoadRolePermissions()
        {
            var admin = App.RolePermissionService.GetRolePermission("admin");
            var teacher = App.RolePermissionService.GetRolePermission("teacher");
            var student = App.RolePermissionService.GetRolePermission("student");

            AdminLock0.IsChecked = admin.Lock0;
            AdminLock1.IsChecked = admin.Lock1;
            AdminLock2.IsChecked = admin.Lock2;
            AdminLock3.IsChecked = admin.Lock3;

            TeacherLock0.IsChecked = teacher.Lock0;
            TeacherLock1.IsChecked = teacher.Lock1;
            TeacherLock2.IsChecked = teacher.Lock2;
            TeacherLock3.IsChecked = teacher.Lock3;

            StudentLock0.IsChecked = student.Lock0;
            StudentLock1.IsChecked = student.Lock1;
            StudentLock2.IsChecked = student.Lock2;
            StudentLock3.IsChecked = student.Lock3;
        }

        /// <summary>保存按钮：保存 3 个角色默认权限，并批量同步到设备</summary>
        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            // 构造 3 个角色权限对象
            var now = DateTime.Now;
            var admin = new RolePermission
            {
                Role = "admin",
                Lock0 = AdminLock0.IsChecked == true,
                Lock1 = AdminLock1.IsChecked == true,
                Lock2 = AdminLock2.IsChecked == true,
                Lock3 = AdminLock3.IsChecked == true,
                UpdateTime = now
            };
            var teacher = new RolePermission
            {
                Role = "teacher",
                Lock0 = TeacherLock0.IsChecked == true,
                Lock1 = TeacherLock1.IsChecked == true,
                Lock2 = TeacherLock2.IsChecked == true,
                Lock3 = TeacherLock3.IsChecked == true,
                UpdateTime = now
            };
            var student = new RolePermission
            {
                Role = "student",
                Lock0 = StudentLock0.IsChecked == true,
                Lock1 = StudentLock1.IsChecked == true,
                Lock2 = StudentLock2.IsChecked == true,
                Lock3 = StudentLock3.IsChecked == true,
                UpdateTime = now
            };

            // 保存
            bool ok = App.RolePermissionService.SetRolePermission(admin)
                      && App.RolePermissionService.SetRolePermission(teacher)
                      && App.RolePermissionService.SetRolePermission(student);

            if (!ok)
            {
                MessageBox.Show("角色权限保存失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            MessageBox.Show("角色默认权限保存成功，即将批量同步到在线设备...", "成功",
                MessageBoxButton.OK, MessageBoxImage.Information);

            // 批量重新计算所有用户最终权限并同步到设备
            SyncAllUsersToDevice();
        }

        /// <summary>重新加载按钮</summary>
        private void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            LoadRolePermissions();
        }

        /// <summary>
        /// 批量重新计算所有用户最终权限并同步到在线设备
        /// 对每个有指纹的用户，计算最终权限（角色默认 + 个人覆盖合并）后下发 SYNC_PERMISSIONS。
        /// </summary>
        private void SyncAllUsersToDevice()
        {
            try
            {
                var users = App.UserService.GetAllUsers();
                int syncCount = 0;
                foreach (var user in users)
                {
                    if (!user.FingerprintId.HasValue) continue;

                    bool[] permissions = App.RolePermissionService.GetFinalPermissions(user.UserId);

                    var data = new Dictionary<string, object>
                    {
                        ["fingerprint_id"] = user.FingerprintId.Value,
                        ["user_id"] = user.UserId,
                        ["permissions"] = permissions
                    };
                    var msg = Message.Create(Protocol.CmdSyncPermissions, "", data);
                    App.MeshBridge.Broadcast(msg);
                    syncCount++;
                }

                MessageBox.Show($"已向在线设备广播 {syncCount} 个用户的最终权限。", "同步完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch
            {
                // 同步失败忽略，不影响本地保存
            }
        }
    }
}
