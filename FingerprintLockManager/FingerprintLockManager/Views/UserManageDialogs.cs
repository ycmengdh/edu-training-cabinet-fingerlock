using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FingerprintLockManager
{
    /// <summary>
    /// 用户管理相关的共用输入对话框（从 UserManagePage 抽出，避免列表页堆砌 UI 构建代码）。
    /// </summary>
    internal static class UserManageDialogs
    {
        public static string? SelectCabinet(Window? owner, List<Device> cabinets, string title = "选择柜子")
        {
            if (cabinets.Count == 0) return null;
            if (cabinets.Count == 1) return cabinets[0].DeviceId;

            var dialog = new Window
            {
                Title = title,
                Width = 400,
                MinHeight = 200,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                Background = Brush("BackgroundBrush")
            };
            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            panel.Children.Add(new TextBlock
            {
                Text = "请选择目标柜子",
                Margin = new Thickness(0, 0, 0, 8)
            });
            var combo = new ComboBox
            {
                ItemsSource = cabinets,
                DisplayMemberPath = "DeviceName",
                Height = 34,
                SelectedIndex = 0
            };
            panel.Children.Add(combo);
            var ok = new Button
            {
                Content = "确定",
                Width = 80,
                Height = 34,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0)
            };
            panel.Children.Add(ok);
            dialog.Content = panel;

            string? selected = null;
            ok.Click += (_, _) =>
            {
                selected = (combo.SelectedItem as Device)?.DeviceId;
                dialog.Close();
            };
            dialog.ShowDialog();
            return selected;
        }

        public static List<string>? SelectCabinets(Window? owner, List<Device> cabinets)
        {
            var dialog = new Window
            {
                Title = "选择目标柜子",
                Width = 420,
                Height = 440,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                Background = Brush("BackgroundBrush")
            };
            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            panel.Children.Add(new TextBlock
            {
                Text = "勾选需要开通权限的在线柜子：",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("SubTextBrush"),
                Margin = new Thickness(0, 0, 0, 12)
            });
            var list = new ListBox { Height = 260, SelectionMode = SelectionMode.Multiple };
            foreach (var cabinet in cabinets)
            {
                list.Items.Add(new ListBoxItem
                {
                    Content = $"{cabinet.DeviceName}  ·  {cabinet.DeviceId}",
                    Tag = cabinet.DeviceId,
                    Padding = new Thickness(10, 8, 10, 8)
                });
            }
            panel.Children.Add(list);
            var buttons = ButtonRow(out Button ok, out Button cancel, okText: "继续");
            panel.Children.Add(buttons);
            dialog.Content = panel;

            List<string>? selected = null;
            ok.Click += (_, _) =>
            {
                selected = list.SelectedItems.OfType<ListBoxItem>()
                    .Select(item => item.Tag?.ToString())
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Cast<string>()
                    .ToList();
                dialog.Close();
            };
            cancel.Click += (_, _) => dialog.Close();
            dialog.ShowDialog();
            return selected;
        }

        public static bool ShowPermissionDialog(Window? owner, out bool[] permissions)
        {
            permissions = new[] { false, true, true, false };
            var dialog = new Window
            {
                Title = "设置柜子权限",
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                Background = Brush("BackgroundBrush")
            };
            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            panel.Children.Add(new TextBlock
            {
                Text = "选择本次分配的锁权限。系统锁只允许管理员使用。",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("SubTextBrush"),
                Margin = new Thickness(0, 0, 0, 14)
            });
            var checks = new[]
            {
                new CheckBox
                {
                    Content = "Lock 0  系统锁",
                    IsChecked = permissions[0],
                    IsEnabled = App.CurrentUser?.Role == "admin",
                    Margin = new Thickness(0, 0, 0, 10)
                },
                new CheckBox { Content = "Lock 1  实训柜 1", IsChecked = permissions[1], Margin = new Thickness(0, 0, 0, 10) },
                new CheckBox { Content = "Lock 2  实训柜 2", IsChecked = permissions[2], Margin = new Thickness(0, 0, 0, 10) },
                new CheckBox { Content = "Lock 3  实训柜 3", IsChecked = permissions[3], Margin = new Thickness(0, 0, 0, 16) }
            };
            foreach (var check in checks) panel.Children.Add(check);
            var buttons = ButtonRow(out Button ok, out Button cancel, okText: "保存并同步");
            panel.Children.Add(buttons);
            dialog.Content = panel;

            bool confirmed = false;
            bool[] localPermissions = permissions;
            ok.Click += (_, _) =>
            {
                localPermissions = checks.Select(check => check.IsChecked == true).ToArray();
                confirmed = true;
                dialog.Close();
            };
            cancel.Click += (_, _) => dialog.Close();
            dialog.ShowDialog();
            if (confirmed) permissions = localPermissions;
            return confirmed;
        }

        public static bool ShowAddUserDialog(
            Window? owner,
            out string name,
            out string role,
            out string password,
            out string? classId,
            string? forcedClassId = null,
            bool forceStudent = false)
        {
            name = "";
            role = "student";
            password = "";
            classId = null;

            var dlg = FormWindow(owner, "添加用户", 420, 420);
            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };

            panel.Children.Add(Label("姓名"));
            var nameBox = new TextBox { Height = 34, Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(nameBox);

            panel.Children.Add(Label("角色"));
            var roleCombo = BuildRoleCombo(forceStudent, selectedRole: "student");
            panel.Children.Add(roleCombo);

            panel.Children.Add(Label("班级（可选）"));
            var classCombo = BuildClassCombo(forcedClassId);
            classCombo.Height = 34;
            classCombo.Margin = new Thickness(0, 0, 0, 14);
            classCombo.IsEnabled = forcedClassId == null;
            panel.Children.Add(classCombo);

            var passwordLabel = Label("密码");
            var passwordBox = new PasswordBox { Height = 34, Margin = new Thickness(0, 0, 0, 8) };
            var passwordHint = new TextBlock
            {
                Text = PasswordHelper.PasswordRequirement,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brush("SubTextBrush"),
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 16)
            };
            if (!forceStudent)
            {
                panel.Children.Add(passwordLabel);
                panel.Children.Add(passwordBox);
                panel.Children.Add(passwordHint);
            }

            void UpdatePasswordVisibility()
            {
                bool visible = !forceStudent &&
                    (roleCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() != "student";
                passwordLabel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                passwordBox.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
                passwordHint.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            }
            roleCombo.SelectionChanged += (_, _) => UpdatePasswordVisibility();
            UpdatePasswordVisibility();

            var btnPanel = ButtonRow(out Button okBtn, out Button cancelBtn);
            panel.Children.Add(btnPanel);
            dlg.Content = Scroll(panel);

            bool confirmed = false;
            string localName = "";
            string localRole = "student";
            string localPassword = "";
            string? localClassId = null;
            okBtn.Click += (_, _) =>
            {
                localName = nameBox.Text;
                if (roleCombo.SelectedItem is ComboBoxItem item)
                    localRole = item.Tag?.ToString() ?? "student";
                localPassword = passwordBox.Password;
                localClassId = (classCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                if (forcedClassId != null) localClassId = forcedClassId;
                if (string.IsNullOrWhiteSpace(localClassId)) localClassId = null;
                confirmed = true;
                dlg.Close();
            };
            cancelBtn.Click += (_, _) => dlg.Close();

            dlg.ShowDialog();
            if (!confirmed) return false;
            name = localName;
            role = localRole;
            password = localPassword;
            classId = localClassId;
            return true;
        }

        public static bool ShowEditUserDialog(
            Window? owner,
            User user,
            out string name,
            out string role,
            out string? classId,
            string? forcedClassId = null,
            bool forceStudent = false)
        {
            name = user.Name;
            role = user.Role;
            classId = user.ClassId;

            var dlg = FormWindow(owner, "编辑用户", 420, 360);
            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            panel.Children.Add(new TextBlock
            {
                Text = $"用户 ID：{user.UserId}",
                Margin = new Thickness(0, 0, 0, 14),
                Foreground = Brush("SubTextBrush")
            });
            panel.Children.Add(Label("姓名"));
            var nameBox = new TextBox { Text = user.Name, Height = 34, Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(nameBox);

            panel.Children.Add(Label("角色"));
            var roleCombo = BuildRoleCombo(forceStudent, user.Role);
            panel.Children.Add(roleCombo);

            panel.Children.Add(Label("班级（可选）"));
            var classCombo = BuildClassCombo(forcedClassId ?? user.ClassId);
            classCombo.Height = 34;
            classCombo.Margin = new Thickness(0, 0, 0, 18);
            classCombo.IsEnabled = forcedClassId == null;
            panel.Children.Add(classCombo);

            var btnPanel = ButtonRow(out Button okBtn, out Button cancelBtn);
            panel.Children.Add(btnPanel);
            dlg.Content = Scroll(panel);

            bool confirmed = false;
            string localName = user.Name;
            string localRole = user.Role;
            string? localClassId = user.ClassId;
            okBtn.Click += (_, _) =>
            {
                localName = nameBox.Text;
                if (roleCombo.SelectedItem is ComboBoxItem item)
                    localRole = item.Tag?.ToString() ?? user.Role;
                localClassId = (classCombo.SelectedItem as ComboBoxItem)?.Tag as string;
                if (forcedClassId != null) localClassId = forcedClassId;
                if (string.IsNullOrWhiteSpace(localClassId)) localClassId = null;
                confirmed = true;
                dlg.Close();
            };
            cancelBtn.Click += (_, _) => dlg.Close();
            dlg.ShowDialog();
            if (!confirmed) return false;
            name = localName;
            role = localRole;
            classId = localClassId;
            return true;
        }

        public static bool ShowResetPasswordDialog(Window? owner, out string password)
        {
            password = "";
            var dlg = FormWindow(owner, "重置密码", 420, 280);
            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            panel.Children.Add(new TextBlock
            {
                Text = PasswordHelper.PasswordRequirement,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 14),
                Foreground = Brush("SubTextBrush"),
                FontSize = 11
            });
            panel.Children.Add(Label("新密码"));
            var passwordBox = new PasswordBox { Height = 34, Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(passwordBox);
            panel.Children.Add(Label("确认密码"));
            var confirmBox = new PasswordBox { Height = 34, Margin = new Thickness(0, 0, 0, 18) };
            panel.Children.Add(confirmBox);

            var btnPanel = ButtonRow(out Button okBtn, out Button cancelBtn);
            panel.Children.Add(btnPanel);
            dlg.Content = panel;

            bool confirmed = false;
            string localPassword = "";
            okBtn.Click += (_, _) =>
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
            cancelBtn.Click += (_, _) => dlg.Close();
            dlg.ShowDialog();
            if (!confirmed) return false;
            password = localPassword;
            return true;
        }

        public static ComboBox BuildClassCombo(string? selectedClassId)
        {
            var combo = new ComboBox();
            combo.Items.Add(new ComboBoxItem { Content = "（无）", Tag = "" });
            try
            {
                foreach (var c in App.ClassService.GetVisible().Where(x => x.Enabled))
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

        private static ComboBox BuildRoleCombo(bool forceStudent, string selectedRole)
        {
            var roleCombo = new ComboBox { Height = 34, Margin = new Thickness(0, 0, 0, 14) };
            if (forceStudent)
            {
                roleCombo.Items.Add(new ComboBoxItem { Content = "学生（无登录密码）", Tag = "student" });
                roleCombo.SelectedIndex = 0;
                roleCombo.IsEnabled = false;
                return roleCombo;
            }

            roleCombo.Items.Add(new ComboBoxItem { Content = "老师 (teacher)", Tag = "teacher" });
            roleCombo.Items.Add(new ComboBoxItem { Content = "学生（无登录密码）", Tag = "student" });
            roleCombo.Items.Add(new ComboBoxItem { Content = "管理员 (admin)", Tag = "admin" });
            roleCombo.SelectedIndex = selectedRole switch
            {
                "admin" => 2,
                "teacher" => 0,
                _ => 1
            };
            return roleCombo;
        }

        private static Window FormWindow(Window? owner, string title, double width, double minHeight) =>
            new()
            {
                Title = title,
                Width = width,
                MinHeight = minHeight,
                SizeToContent = SizeToContent.Height,
                MaxHeight = SystemParameters.WorkArea.Height * 0.9,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = owner,
                ResizeMode = ResizeMode.NoResize,
                Background = Brush("BackgroundBrush")
            };

        private static ScrollViewer Scroll(UIElement content) =>
            new()
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = content
            };

        private static TextBlock Label(string text) =>
            new()
            {
                Text = text,
                Style = Application.Current.TryFindResource("LabelText") as Style,
                Margin = new Thickness(0, 0, 0, 6)
            };

        private static StackPanel ButtonRow(out Button ok, out Button cancel, string okText = "确定")
        {
            var btnPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0)
            };
            ok = new Button { Content = okText, Width = okText.Length > 2 ? 100 : 88, Height = 34, Margin = new Thickness(0, 0, 10, 0) };
            cancel = new Button
            {
                Content = "取消",
                Width = 88,
                Height = 34,
                Style = Application.Current.TryFindResource("SecondaryButton") as Style
            };
            btnPanel.Children.Add(ok);
            btnPanel.Children.Add(cancel);
            return btnPanel;
        }

        private static Brush? Brush(string key) =>
            Application.Current.TryFindResource(key) as Brush;
    }
}
