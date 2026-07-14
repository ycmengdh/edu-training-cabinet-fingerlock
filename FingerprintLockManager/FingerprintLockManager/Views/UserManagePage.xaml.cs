using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FingerprintLockManager
{
    /// <summary>
    /// 用户管理页面（需求 3/4 适配）
    ///
    /// 适配新模型：
    /// - 新增 ClassId 字段（学生必填，老师/管理员为空）
    /// - 添加学生时必须选择班级；老师由管理员录入；管理员无需班级
    /// - 老师只能看自己班级的学生（按 ClassId 过滤）
    /// - 学生不能登录上位机后台（由 AuthService 拦截登录）
    /// - 指纹录入流程移到「指纹录入」页面（4+2 录入，需求 5）
    /// </summary>
    public partial class UserManagePage : Page
    {
        public UserManagePage()
        {
            InitializeComponent();
            Loaded += (s, e) =>
            {
                RoleFilterBox.SelectedIndex = 0;
                LoadClassFilter();
                LoadUsers();
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
                ClassFilterBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{c.ClassName}（{c.ClassId}）",
                    Tag = c.ClassId
                });
            }
            ClassFilterBox.SelectedIndex = 0;
        }

        /// <summary>加载用户列表（按角色+班级筛选，老师按自己班级限制）</summary>
        private void LoadUsers()
        {
            string? role = GetSelectedRole();
            string? classId = GetSelectedClass();

            List<User> users = App.UserService.GetAllUsers();

            // 老师只能看自己班级的学生 + 自己
            if (App.CurrentUser?.Role == "teacher")
            {
                var myClassIds = App.ClassService.GetClassesByTeacher(App.CurrentUser.UserId)
                    .Select(c => c.ClassId)
                    .ToHashSet();
                users = users.Where(u =>
                    u.UserId == App.CurrentUser.UserId ||  // 自己
                    (u.Role == "student" && myClassIds.Contains(u.ClassId ?? "")))
                    .ToList();
            }

            // 角色筛选
            if (!string.IsNullOrEmpty(role))
            {
                users = users.Where(u => u.Role == role).ToList();
            }

            // 班级筛选
            if (!string.IsNullOrEmpty(classId))
            {
                users = users.Where(u => u.ClassId == classId).ToList();
            }

            UserDataGrid.ItemsSource = users;
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

        /// <summary>获取下拉框选中的班级</summary>
        private string? GetSelectedClass()
        {
            if (ClassFilterBox.SelectedItem is ComboBoxItem item)
            {
                return item.Tag?.ToString();
            }
            return null;
        }

        /// <summary>角色筛选变化</summary>
        private void RoleFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            LoadUsers();
        }

        /// <summary>班级筛选变化</summary>
        private void ClassFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded) return;
            LoadUsers();
        }

        /// <summary>刷新按钮</summary>
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadClassFilter();
            LoadUsers();
        }

        /// <summary>添加用户</summary>
        private void AddUserButton_Click(object sender, RoutedEventArgs e)
        {
            // 弹出对话框输入姓名、角色、班级（学生必选）、密码
            if (!ShowAddUserDialog(out string name, out string role, out string classId, out string password))
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                MessageBox.Show("请输入姓名", "提示");
                return;
            }

            if (string.IsNullOrEmpty(password))
            {
                MessageBox.Show("请输入密码", "提示");
                return;
            }

            if (role == "student" && string.IsNullOrEmpty(classId))
            {
                MessageBox.Show("学生必须选择所属班级", "提示");
                return;
            }

            // 自动生成用户ID（角色前缀 + 时间戳，避免重复）
            string userId = $"{role}_{DateTime.Now:yyyyMMddHHmmss}";

            var user = new User
            {
                UserId = userId,
                Name = name.Trim(),
                Role = role,
                ClassId = role == "student" ? classId : "",
                FingerprintId = null,
                CreateTime = DateTime.Now
            };

            // 操作前自动备份（需求 11）
            App.BackupService.BackupBeforeAction($"添加用户 {userId}", App.CurrentUser?.UserId);

            if (App.UserService.AddUser(user, password))
            {
                // 学生添加后更新班级人数
                if (role == "student" && !string.IsNullOrEmpty(classId))
                {
                    App.ClassService.RefreshStudentCount(classId);
                }
                MessageBox.Show($"用户添加成功！\n用户ID：{userId}\n\n" +
                    "如需录入指纹，请到「指纹录入」页面进行 4+2 录入。",
                    "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadClassFilter();
                LoadUsers();
            }
            else
            {
                MessageBox.Show("用户添加失败，可能用户ID已存在", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>删除用户</summary>
        private void DeleteUserButton_Click(object sender, RoutedEventArgs e)
        {
            if (UserDataGrid.SelectedItem is not User selected)
            {
                MessageBox.Show("请先选择要删除的用户", "提示");
                return;
            }

            // 老师只能删除自己班级的学生
            if (App.CurrentUser?.Role == "teacher" && selected.UserId != App.CurrentUser.UserId)
            {
                if (selected.Role != "student" || !App.ClassService.CanTeacherManageClass(App.CurrentUser.UserId, selected.ClassId ?? ""))
                {
                    MessageBox.Show("您只能删除自己班级的学生", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // 不允许删除最后一个管理员
            if (selected.Role == "admin")
            {
                var admins = App.UserService.GetUsersByRole("admin");
                if (admins.Count <= 1)
                {
                    MessageBox.Show("不允许删除最后一个管理员账号", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            var result = MessageBox.Show($"确认删除用户「{selected.Name}（{selected.UserId}」？\n" +
                "该用户的权限记录、设备授权记录将一并删除。\n" +
                "如该用户已下发到柜子，需到「下发状态」页面手动清理柜子数据。",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            // 操作前自动备份（需求 11）
            App.BackupService.BackupBeforeAction($"删除用户 {selected.UserId}", App.CurrentUser?.UserId);

            // 删除根节点的设备授权记录
            var auths = App.PermissionService.GetDeviceAuthorizations(selected.UserId);
            foreach (var auth in auths)
            {
                App.PermissionService.RemoveDeviceAuthorization(selected.UserId, auth.DeviceId);
            }

            // 删除个人权限覆盖
            App.PermissionService.DeleteAllUserPermissions(selected.UserId);

            if (App.UserService.DeleteUser(selected.UserId))
            {
                // 更新班级人数
                if (selected.Role == "student" && !string.IsNullOrEmpty(selected.ClassId))
                {
                    App.ClassService.RefreshStudentCount(selected.ClassId);
                }
                MessageBox.Show("删除成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadClassFilter();
                LoadUsers();
            }
            else
            {
                MessageBox.Show("删除失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ===== 代码构建的对话框 =====

        /// <summary>显示添加用户对话框</summary>
        private bool ShowAddUserDialog(out string name, out string role, out string classId, out string password)
        {
            name = "";
            role = "student";
            classId = "";
            password = "";

            var dlg = new Window
            {
                Title = "添加用户",
                Width = 340,
                Height = 380,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = FindResource("BackgroundBrush") as Brush
            };

            var panel = new StackPanel { Margin = new Thickness(20) };

            panel.Children.Add(new TextBlock { Text = "姓名", Margin = new Thickness(0, 0, 0, 6) });
            var nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(nameBox);

            panel.Children.Add(new TextBlock { Text = "角色", Margin = new Thickness(0, 0, 0, 6) });
            var roleCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 16) };
            // 老师只能添加学生
            if (App.CurrentUser?.Role == "teacher")
            {
                roleCombo.Items.Add(new ComboBoxItem { Content = "学生 (student)", Tag = "student" });
                roleCombo.SelectedIndex = 0;
                roleCombo.IsEnabled = false;
            }
            else
            {
                roleCombo.Items.Add(new ComboBoxItem { Content = "老师 (teacher)", Tag = "teacher" });
                roleCombo.Items.Add(new ComboBoxItem { Content = "学生 (student)", Tag = "student" });
                roleCombo.Items.Add(new ComboBoxItem { Content = "管理员 (admin)", Tag = "admin" });
                roleCombo.SelectedIndex = 1;
            }
            panel.Children.Add(roleCombo);

            // 班级选择（学生必填）
            panel.Children.Add(new TextBlock { Text = "所属班级（学生必选）", Margin = new Thickness(0, 0, 0, 6) });
            var classCombo = new ComboBox { Margin = new Thickness(0, 0, 0, 16) };
            classCombo.Items.Add(new ComboBoxItem { Content = "（无班级）", Tag = "" });
            // 老师只能选自己班级
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
                classCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{c.ClassName}（{c.ClassId}）",
                    Tag = c.ClassId
                });
            }
            classCombo.SelectedIndex = 0;
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
            string localClassId = "";
            string localPassword = "";
            okBtn.Click += (s, e) =>
            {
                localName = nameBox.Text;
                if (roleCombo.SelectedItem is ComboBoxItem item)
                {
                    localRole = item.Tag?.ToString() ?? "student";
                }
                if (classCombo.SelectedItem is ComboBoxItem citem)
                {
                    localClassId = citem.Tag?.ToString() ?? "";
                }
                localPassword = passwordBox.Password;
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
                password = localPassword;
            }
            return confirmed;
        }
    }
}
