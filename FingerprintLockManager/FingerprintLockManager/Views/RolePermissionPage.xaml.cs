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
            Loaded += async (s, e) => await LoadRolePermissionsAsync();
        }

        /// <summary>加载角色默认权限到矩阵</summary>
        private async Task LoadRolePermissionsAsync()
        {
            SetBusy(true, "正在读取角色权限");
            List<RolePermission> roles;
            try
            {
                roles = await Task.Run(App.RolePermissionService.GetAll);
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

            var admin = roles.FirstOrDefault(r => r.Role == "admin") ?? DefaultRole("admin");
            var teacher = roles.FirstOrDefault(r => r.Role == "teacher") ?? DefaultRole("teacher");
            var student = roles.FirstOrDefault(r => r.Role == "student") ?? DefaultRole("student");

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
            PageStatusText.Text = "角色权限已从根节点加载";
        }

        /// <summary>保存按钮：保存 3 个角色默认权限，并批量同步到设备</summary>
        private async void SaveButton_Click(object sender, RoutedEventArgs e)
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
            SetBusy(true, "正在保存角色权限");
            bool ok;
            try
            {
                ok = await Task.Run(() => App.RolePermissionService.SetAll(
                    new[] { admin, teacher, student }));
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

            if (!ok)
            {
                MessageBox.Show("角色权限保存失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            BroadcastCommandResult sent;
            try
            {
                sent = await Task.Run(App.CabinetSyncService.SyncAllPermissions);
            }
            catch (RootDataUnavailableException ex)
            {
                PageStatusText.Text = ex.Message;
                sent = BroadcastCommandResult.Failed(ex.Message);
            }
            string text = CabinetSyncService.FormatSyncResult(sent,
                "角色权限已保存，所有在线柜子均已确认",
                "角色权限已保存，但在线柜子未全部确认");
            PageStatusText.Text = sent.Success ? "角色权限已保存，在线柜子均已确认" : "角色权限已保存，在线柜子未全部确认";
            MessageBox.Show(text,
                sent.Success ? "保存完成" : "同步提示", MessageBoxButton.OK,
                sent.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        /// <summary>重新加载按钮</summary>
        private async void ReloadButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadRolePermissionsAsync();
        }

        /// <summary>
        /// 批量重新计算所有用户最终权限并同步到在线设备
        /// 对每个有指纹的用户，计算最终权限（角色默认 + 个人覆盖合并）后下发 SYNC_PERMISSIONS。
        /// </summary>
        private static RolePermission DefaultRole(string role)
        {
            return role switch
            {
                "admin" => new RolePermission { Role = role, Lock0 = true, Lock1 = true, Lock2 = true, Lock3 = true },
                "teacher" => new RolePermission { Role = role, Lock1 = true, Lock2 = true, Lock3 = true },
                _ => new RolePermission { Role = role }
            };
        }

        private void SetBusy(bool busy, string? status = null)
        {
            SaveButton.IsEnabled = !busy;
            ReloadButton.IsEnabled = !busy;
            if (!string.IsNullOrEmpty(status)) PageStatusText.Text = status;
        }
    }
}
