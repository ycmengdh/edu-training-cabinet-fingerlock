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
            Loaded += (s, e) =>
            {
                RoleFilterBox.SelectedIndex = 0;
                LoadUsers();
            };
        }

        /// <summary>加载用户列表（按筛选条件）</summary>
        private void LoadUsers()
        {
            string? role = GetSelectedRole();
            List<User> users;
            if (string.IsNullOrEmpty(role))
            {
                users = App.UserService.GetAllUsers();
            }
            else
            {
                users = App.UserService.GetUsersByRole(role);
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

        /// <summary>角色筛选变化</summary>
        private void RoleFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // 加载完成前 SelectedIndex=0 会触发，此时控件可能未就绪
            if (!IsLoaded) return;
            LoadUsers();
        }

        /// <summary>刷新按钮</summary>
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadUsers();
        }

        /// <summary>添加用户</summary>
        private void AddUserButton_Click(object sender, RoutedEventArgs e)
        {
            // 弹出对话框输入姓名、角色与密码
            if (!ShowAddUserDialog(out string name, out string role, out string password))
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

            // 自动生成用户ID（角色前缀 + 时间戳，避免重复）
            string userId = $"{role}_{DateTime.Now:yyyyMMddHHmmss}";

            var user = new User
            {
                UserId = userId,
                Name = name.Trim(),
                Role = role,
                FingerprintId = null,
                CreateTime = DateTime.Now
            };

            // 双层权限模型：无需初始化个人权限，用户默认继承角色权限模板
            if (App.UserService.AddUser(user, password))
            {
                MessageBox.Show($"用户添加成功！\n用户ID：{userId}", "成功", MessageBoxButton.OK, MessageBoxImage.Information);
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

            var result = MessageBox.Show($"确认删除用户「{selected.Name}（{selected.UserId}」？\n该用户的权限记录将一并删除。",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            if (App.UserService.DeleteUser(selected.UserId))
            {
                MessageBox.Show("删除成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadUsers();
            }
            else
            {
                MessageBox.Show("删除失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>分配指纹ID</summary>
        private void AssignFingerprintButton_Click(object sender, RoutedEventArgs e)
        {
            if (UserDataGrid.SelectedItem is not User selected)
            {
                MessageBox.Show("请先选择要分配指纹的用户", "提示");
                return;
            }

            // 默认建议下一个可用指纹ID
            int suggestId = App.UserService.GetNextFingerprintId();
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
            var existUser = App.UserService.GetUserByFingerprint(fingerprintId);
            if (existUser != null && existUser.UserId != selected.UserId)
            {
                MessageBox.Show($"指纹ID {fingerprintId} 已被用户「{existUser.Name}」占用", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (App.UserService.AssignFingerprint(selected.UserId, fingerprintId))
            {
                MessageBox.Show("指纹分配成功", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadUsers();
            }
            else
            {
                MessageBox.Show("指纹分配失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ===== 代码构建的对话框（避免额外文件） =====

        /// <summary>显示添加用户对话框，返回姓名、角色与密码</summary>
        private bool ShowAddUserDialog(out string name, out string role, out string password)
        {
            name = "";
            role = "student";
            password = "";

            var dlg = new Window
            {
                Title = "添加用户",
                Width = 320,
                Height = 300,
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
            roleCombo.Items.Add(new ComboBoxItem { Content = "老师 (teacher)", Tag = "teacher" });
            roleCombo.Items.Add(new ComboBoxItem { Content = "学生 (student)", Tag = "student" });
            roleCombo.Items.Add(new ComboBoxItem { Content = "管理员 (admin)", Tag = "admin" });
            roleCombo.SelectedIndex = 1;
            panel.Children.Add(roleCombo);

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
            // 使用局部变量在 lambda 中暂存（out 参数不能在 lambda 中赋值）
            string localName = "";
            string localRole = "student";
            string localPassword = "";
            okBtn.Click += (s, e) =>
            {
                localName = nameBox.Text;
                if (roleCombo.SelectedItem is ComboBoxItem item)
                {
                    localRole = item.Tag?.ToString() ?? "student";
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
                password = localPassword;
            }
            return confirmed;
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
    }
}
