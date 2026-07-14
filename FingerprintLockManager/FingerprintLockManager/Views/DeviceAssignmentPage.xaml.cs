using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FingerprintLockManager
{
    /// <summary>
    /// 柜子分配与权限下发页面（需求 6/8）
    ///
    /// 学生分配柜子+权限时才把指纹+权限+学生信息下发到该柜子（按需下发）。
    /// 老师只能管理自己班级的学生。
    /// 下发成功才更新根节点 DeviceAuthorization.FingerprintDeployed（事务性，需求 11）。
    /// </summary>
    public partial class DeviceAssignmentPage : Page
    {
        /// <summary>当前选中的学生</summary>
        private User? _selectedStudent;

        public DeviceAssignmentPage()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                LoadClassFilter();
                LoadDevices();
            };
        }

        /// <summary>加载班级筛选下拉框</summary>
        private void LoadClassFilter()
        {
            ClassFilterBox.Items.Clear();
            ClassFilterBox.Items.Add(new ComboBoxItem { Content = "全部班级", Tag = "" });

            List<ClassInfo> classes;
            if (App.CurrentUser?.Role == "teacher")
            {
                classes = App.ClassService.GetClassesByTeacher(App.CurrentUser.UserId);
            }
            else
            {
                classes = App.ClassService.GetClasses();
            }

            foreach (var c in classes)
            {
                ClassFilterBox.Items.Add(new ComboBoxItem { Content = $"{c.ClassName}（{c.ClassId}）", Tag = c.ClassId });
            }
            ClassFilterBox.SelectedIndex = 0;
        }

        /// <summary>加载设备下拉框（在线非根节点）</summary>
        private void LoadDevices()
        {
            var devices = App.DeviceService.GetOnlineDevices()
                .Where(d => !d.IsRoot)
                .ToList();
            DeviceCombo.ItemsSource = devices;
        }

        /// <summary>班级筛选变化：刷新学生列表</summary>
        private void ClassFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            LoadStudents();
        }

        /// <summary>加载学生列表（按班级筛选 + 角色限制）</summary>
        private void LoadStudents()
        {
            if (ClassFilterBox.SelectedItem is not ComboBoxItem item) return;

            string? classId = item.Tag?.ToString();
            var currentUser = App.CurrentUser;

            List<User> students;
            if (currentUser?.Role == "teacher")
            {
                // 老师只能看自己班级的学生
                var myClasses = App.ClassService.GetClassesByTeacher(currentUser.UserId);
                var myClassIds = myClasses.Select(c => c.ClassId).ToHashSet();
                students = App.UserService.GetUsersByRole("student")
                    .Where(u => myClassIds.Contains(u.ClassId))
                    .ToList();
            }
            else
            {
                students = App.UserService.GetUsersByRole("student");
            }

            if (!string.IsNullOrEmpty(classId))
            {
                students = students.Where(u => u.ClassId == classId).ToList();
            }

            StudentListBox.ItemsSource = students;
        }

        /// <summary>学生选择变化：加载该学生的已授权柜子和锁权限</summary>
        private void StudentListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (StudentListBox.SelectedItem is not User student)
            {
                _selectedStudent = null;
                return;
            }

            _selectedStudent = student;
            SelectedStudentName.Text = student.Name;
            SelectedStudentFp.Text = student.FingerprintId.HasValue
                ? $"指纹 ID：{student.FingerprintId.Value}"
                : "未录指纹";

            // 加载该学生已分配的柜子列表
            LoadAuthorizations(student.UserId);

            // 默认全不勾选
            Lock0CheckBox.IsChecked = false;
            Lock1CheckBox.IsChecked = false;
            Lock2CheckBox.IsChecked = false;
            Lock3CheckBox.IsChecked = false;
        }

        /// <summary>加载该学生已分配的柜子列表</summary>
        private void LoadAuthorizations(string userId)
        {
            var auths = App.PermissionService.GetDeviceAuthorizations(userId);
            var displayList = auths.Select(a => new AuthDisplay
            {
                DeviceId = a.DeviceId,
                Lock0 = a.Lock0,
                Lock1 = a.Lock1,
                Lock2 = a.Lock2,
                Lock3 = a.Lock3,
                LockSummary = BuildLockSummary(a),
                FingerprintDeployed = a.FingerprintDeployed ? "已下发" : "未下发",
                DeployTime = a.DeployTime
            }).ToList();
            AuthDataGrid.ItemsSource = displayList;
        }

        /// <summary>构造锁权限摘要字符串</summary>
        private static string BuildLockSummary(DeviceAuthorization a)
        {
            var parts = new List<string>();
            if (a.Lock0) parts.Add("L0");
            if (a.Lock1) parts.Add("L1");
            if (a.Lock2) parts.Add("L2");
            if (a.Lock3) parts.Add("L3");
            return parts.Count == 0 ? "（无权限）" : string.Join(" / ", parts);
        }

        /// <summary>下发到柜子</summary>
        private async void DeployButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedStudent == null)
            {
                MessageBox.Show("请先选择学生", "提示");
                return;
            }
            if (DeviceCombo.SelectedItem is not Device device)
            {
                MessageBox.Show("请选择要分配的柜子", "提示");
                return;
            }
            if (!_selectedStudent.FingerprintId.HasValue)
            {
                MessageBox.Show("该学生尚未录入指纹，请先在「指纹录入」页面录入", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 收集锁权限
            bool[] permissions = new bool[4];
            permissions[0] = Lock0CheckBox.IsChecked == true;
            permissions[1] = Lock1CheckBox.IsChecked == true;
            permissions[2] = Lock2CheckBox.IsChecked == true;
            permissions[3] = Lock3CheckBox.IsChecked == true;

            if (!permissions.Any(p => p))
            {
                MessageBox.Show("请至少勾选一把锁的权限", "提示");
                return;
            }

            // 操作前自动备份（需求 11）
            App.BackupService.BackupBeforeAction(
                $"分配柜子 {device.DeviceId} 给学生 {_selectedStudent.UserId}",
                App.CurrentUser?.UserId);

            // 先在根节点记录授权（事务性：下发成功才更新 FingerprintDeployed，由 DeployService.HandleAck 完成）
            App.PermissionService.SetDeviceAuthorization(_selectedStudent.UserId, device.DeviceId, permissions);

            // 下发到柜子
            long taskId = await App.DeployService.DeployStudentAsync(
                _selectedStudent.UserId, device.DeviceId, permissions, App.CurrentUser?.UserId);

            if (taskId > 0)
            {
                MessageBox.Show($"已下发到柜子「{device.DeviceName}」\n任务 ID：{taskId}\n\n请到「下发状态」页面查看每台柜子的接收状态。",
                    "已发起下发", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadAuthorizations(_selectedStudent.UserId);
            }
            else
            {
                MessageBox.Show("下发失败，请检查学生指纹是否已录入", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>移除该柜子授权</summary>
        private void RemoveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedStudent == null)
            {
                MessageBox.Show("请先选择学生", "提示");
                return;
            }
            if (AuthDataGrid.SelectedItem is not AuthDisplay auth) return;
            if (string.IsNullOrEmpty(auth.DeviceId)) return;

            var result = MessageBox.Show(
                $"确认移除学生「{_selectedStudent.Name}」在柜子「{auth.DeviceId}」的授权？\n该学生的指纹将从该柜子删除。",
                "确认移除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            // 操作前自动备份（需求 11）
            App.BackupService.BackupBeforeAction(
                $"移除柜子 {auth.DeviceId} 学生 {_selectedStudent.UserId}",
                App.CurrentUser?.UserId);

            // 从柜子删除用户
            if (_selectedStudent.FingerprintId.HasValue)
            {
                App.DeployService.RemoveUserFromDevice(
                    _selectedStudent.UserId, auth.DeviceId, _selectedStudent.FingerprintId.Value,
                    App.CurrentUser?.UserId);
            }

            // 删除根节点授权记录
            App.PermissionService.RemoveDeviceAuthorization(_selectedStudent.UserId, auth.DeviceId);

            LoadAuthorizations(_selectedStudent.UserId);
        }

        /// <summary>DataGrid 中显示用的授权包装类</summary>
        private class AuthDisplay
        {
            public string DeviceId { get; set; }
            public bool Lock0 { get; set; }
            public bool Lock1 { get; set; }
            public bool Lock2 { get; set; }
            public bool Lock3 { get; set; }
            public string LockSummary { get; set; }
            public string FingerprintDeployed { get; set; }
            public DateTime? DeployTime { get; set; }
        }
    }
}
