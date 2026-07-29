using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FingerprintLockManager
{
    public partial class TeacherManagePage : Page
    {
        private readonly ListPager _pager = new(30);
        private List<User> _teachers = new();

        public TeacherManagePage()
        {
            InitializeComponent();
            Loaded += async (_, _) => await LoadTeachersAsync();
        }

        private async Task LoadTeachersAsync(bool resetPage = true)
        {
            if (resetPage) _pager.Reset();
            SetBusy(true, "正在读取老师账号");
            try
            {
                _teachers = await Task.Run(() => App.UserService.GetUsersByRole("teacher"));
                ApplyTeacherPage();
            }
            catch (RootDataUnavailableException ex)
            {
                _teachers.Clear();
                TeacherDataGrid.ItemsSource = null;
                PageStatusText.Text = ex.Message;
                _pager.BindChrome(PrevPageButton, NextPageButton, PageInfoText);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ApplyTeacherPage()
        {
            var page = _pager.Slice(_teachers);
            TeacherDataGrid.ItemsSource = page;
            _pager.BindChrome(PrevPageButton, NextPageButton, PageInfoText);
            PageStatusText.Text = _pager.StatusText(page.Count) + " · 负责班级可在此调整";
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
            await LoadTeachersAsync(resetPage: false);

        private void PrevPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pager.Prev()) ApplyTeacherPage();
        }

        private void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pager.Next()) ApplyTeacherPage();
        }

        private void FingerprintSyncButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new TeacherFingerprintSyncWindow { Owner = Window.GetWindow(this) };
            window.ShowDialog();
        }

        private async void EnrollTeacherFingerprintButton_Click(object sender, RoutedEventArgs e)
        {
            if (TeacherDataGrid.SelectedItem is not User teacher)
            {
                MessageBox.Show("请先选择老师", "提示");
                return;
            }
            var window = new EnrollFingerprintWindow(null, teacher.UserId)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
            if (window.EnrolledFingerprintId > 0) await LoadTeachersAsync();
        }

        private async void AddTeacherButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ShowTeacherDialog(null, out string userId, out string name,
                    out string? classId, out string password)) return;
            if (!PasswordHelper.IsPasswordAcceptable(password))
            {
                MessageBox.Show(PasswordHelper.PasswordRequirement, "密码不符合要求");
                return;
            }

            var teacher = new User
            {
                UserId = userId,
                Name = name,
                Role = "teacher",
                ClassId = classId,
                CreateTime = DateTime.Now
            };
            SetBusy(true, "正在创建老师账号");
            try
            {
                bool added = await Task.Run(() => App.UserService.AddUser(teacher, password));
                MessageBox.Show(added ? "老师账号已创建" : "创建失败，账号 ID 可能已存在",
                    added ? "完成" : "错误", MessageBoxButton.OK,
                    added ? MessageBoxImage.Information : MessageBoxImage.Error);
                if (added) await LoadTeachersAsync();
            }
            catch (RootDataUnavailableException ex)
            {
                MessageBox.Show(ex.Message, "根节点不可用", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void EditTeacherButton_Click(object sender, RoutedEventArgs e)
        {
            if (TeacherDataGrid.SelectedItem is not User selected)
            {
                MessageBox.Show("请先选择老师", "提示");
                return;
            }
            if (!ShowTeacherDialog(selected, out string userId, out string name,
                    out string? classId, out _)) return;

            var updated = new User
            {
                UserId = userId,
                Name = name,
                Role = "teacher",
                ClassId = classId,
                FingerprintId = selected.FingerprintId,
                PasswordSalt = selected.PasswordSalt,
                PasswordHash = selected.PasswordHash,
                Enabled = selected.Enabled,
                CreateTime = selected.CreateTime,
                UpdateTime = DateTime.Now
            };
            SetBusy(true, "正在保存老师信息");
            try
            {
                bool saved = await Task.Run(() => App.UserService.UpdateUser(updated));
                MessageBox.Show(saved ? "老师信息已更新" : "保存失败", saved ? "完成" : "错误",
                    MessageBoxButton.OK, saved ? MessageBoxImage.Information : MessageBoxImage.Error);
                if (saved) await LoadTeachersAsync();
            }
            catch (RootDataUnavailableException ex)
            {
                MessageBox.Show(ex.Message, "根节点不可用", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void ResetPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (TeacherDataGrid.SelectedItem is not User selected)
            {
                MessageBox.Show("请先选择老师", "提示");
                return;
            }
            if (!ShowPasswordDialog(out string password) ||
                !PasswordHelper.IsPasswordAcceptable(password))
            {
                if (!string.IsNullOrEmpty(password))
                    MessageBox.Show(PasswordHelper.PasswordRequirement, "密码不符合要求");
                return;
            }

            SetBusy(true, "正在重置老师密码");
            try
            {
                bool ok = await Task.Run(() => App.UserService.ResetPassword(selected.UserId, password));
                MessageBox.Show(ok ? "密码已重置" : "密码重置失败", ok ? "完成" : "错误",
                    MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
            }
            catch (RootDataUnavailableException ex)
            {
                MessageBox.Show(ex.Message, "根节点不可用", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void ToggleTeacherButton_Click(object sender, RoutedEventArgs e)
        {
            if (TeacherDataGrid.SelectedItem is not User selected)
            {
                MessageBox.Show("请先选择老师", "提示");
                return;
            }

            SetBusy(true, selected.Enabled ? "正在停用老师账号" : "正在启用老师账号");
            try
            {
                bool ok = await Task.Run(() => App.UserService.SetEnabled(selected.UserId, !selected.Enabled));
                if (ok) await LoadTeachersAsync();
                else MessageBox.Show("操作失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (RootDataUnavailableException ex)
            {
                MessageBox.Show(ex.Message, "根节点不可用", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void DeleteTeacherButton_Click(object sender, RoutedEventArgs e)
        {
            if (TeacherDataGrid.SelectedItem is not User selected)
            {
                MessageBox.Show("请先选择老师", "提示");
                return;
            }
            if (MessageBox.Show($"确认删除老师「{selected.Name}」？", "确认删除",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            SetBusy(true, "正在删除老师账号");
            try
            {
                bool ok = await Task.Run(() => App.UserService.DeleteUser(selected.UserId));
                MessageBox.Show(ok ? "老师账号已删除" : "删除失败", ok ? "完成" : "错误",
                    MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
                if (ok) await LoadTeachersAsync();
            }
            catch (RootDataUnavailableException ex)
            {
                MessageBox.Show(ex.Message, "根节点不可用", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private bool ShowTeacherDialog(User? existing, out string userId, out string name,
            out string? classId, out string password)
        {
            userId = existing?.UserId ?? "";
            name = existing?.Name ?? "";
            classId = existing?.ClassId;
            password = "";

            var dialog = new Window
            {
                Title = existing == null ? "添加老师" : "编辑老师",
                Width = 420,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = FindResource("BackgroundBrush") as Brush
            };
            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            panel.Children.Add(new TextBlock { Text = "老师 ID", Style = FindResource("LabelText") as Style, Margin = new Thickness(0, 0, 0, 6) });
            var idBox = new TextBox { Text = userId, IsEnabled = existing == null, Height = 34, Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(idBox);
            panel.Children.Add(new TextBlock { Text = "姓名", Style = FindResource("LabelText") as Style, Margin = new Thickness(0, 0, 0, 6) });
            var nameBox = new TextBox { Text = name, Height = 34, Margin = new Thickness(0, 0, 0, 14) };
            panel.Children.Add(nameBox);
            panel.Children.Add(new TextBlock { Text = "负责班级", Style = FindResource("LabelText") as Style, Margin = new Thickness(0, 0, 0, 6) });
            var classBox = BuildClassCombo(classId);
            classBox.Height = 34;
            classBox.Margin = new Thickness(0, 0, 0, 14);
            panel.Children.Add(classBox);

            PasswordBox? passwordBox = null;
            if (existing == null)
            {
                panel.Children.Add(new TextBlock { Text = "初始密码", Style = FindResource("LabelText") as Style, Margin = new Thickness(0, 0, 0, 6) });
                passwordBox = new PasswordBox { Height = 34, Margin = new Thickness(0, 0, 0, 6) };
                panel.Children.Add(passwordBox);
                panel.Children.Add(new TextBlock { Text = PasswordHelper.PasswordRequirement, Foreground = FindResource("SubTextBrush") as Brush, FontSize = 11, Margin = new Thickness(0, 0, 0, 16) });
            }

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "确定", Width = 88, Height = 34, Margin = new Thickness(0, 0, 10, 0) };
            var cancel = new Button { Content = "取消", Width = 88, Height = 34, Style = FindResource("SecondaryButton") as Style };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            dialog.Content = panel;

            bool confirmed = false;
            string localUserId = userId;
            string localName = name;
            string? localClassId = classId;
            string localPassword = "";
            ok.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(idBox.Text) || string.IsNullOrWhiteSpace(nameBox.Text))
                {
                    MessageBox.Show("老师 ID 和姓名不能为空", "提示");
                    return;
                }
                localUserId = idBox.Text.Trim();
                localName = nameBox.Text.Trim();
                localClassId = (classBox.SelectedItem as ComboBoxItem)?.Tag as string;
                localPassword = passwordBox?.Password ?? "";
                confirmed = true;
                dialog.Close();
            };
            cancel.Click += (_, _) => dialog.Close();
            dialog.ShowDialog();
            if (confirmed)
            {
                userId = localUserId;
                name = localName;
                classId = localClassId;
                password = localPassword;
            }
            return confirmed;
        }

        private ComboBox BuildClassCombo(string? selectedClassId)
        {
            var combo = new ComboBox();
            combo.Items.Add(new ComboBoxItem { Content = "（未分配班级）", Tag = null });
            foreach (var item in App.ClassService.GetAll())
            {
                combo.Items.Add(new ComboBoxItem { Content = $"{item.Name} ({item.ClassId})", Tag = item.ClassId });
            }
            combo.SelectedIndex = Math.Max(0, combo.Items.OfType<ComboBoxItem>()
                .ToList().FindIndex(i => string.Equals(i.Tag as string, selectedClassId, StringComparison.OrdinalIgnoreCase)));
            return combo;
        }

        private bool ShowPasswordDialog(out string password)
        {
            password = "";
            var dialog = new Window
            {
                Title = "重置老师密码",
                Width = 380,
                SizeToContent = SizeToContent.Height,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = FindResource("BackgroundBrush") as Brush
            };
            var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
            panel.Children.Add(new TextBlock { Text = PasswordHelper.PasswordRequirement, Foreground = FindResource("SubTextBrush") as Brush, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
            var box = new PasswordBox { Height = 34, Margin = new Thickness(0, 0, 0, 18) };
            panel.Children.Add(box);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "确定", Width = 88, Height = 34, Margin = new Thickness(0, 0, 10, 0) };
            var cancel = new Button { Content = "取消", Width = 88, Height = 34, Style = FindResource("SecondaryButton") as Style };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            dialog.Content = panel;
            bool confirmed = false;
            string localPassword = "";
            ok.Click += (_, _) => { localPassword = box.Password; confirmed = true; dialog.Close(); };
            cancel.Click += (_, _) => dialog.Close();
            dialog.ShowDialog();
            if (confirmed) password = localPassword;
            return confirmed;
        }

        private void SetBusy(bool busy, string? status = null)
        {
            AddTeacherButton.IsEnabled = !busy;
            EditTeacherButton.IsEnabled = !busy;
            ResetPasswordButton.IsEnabled = !busy;
            ToggleTeacherButton.IsEnabled = !busy;
            DeleteTeacherButton.IsEnabled = !busy;
            RefreshButton.IsEnabled = !busy;
            FingerprintSyncButton.IsEnabled = !busy;
            EnrollTeacherFingerprintButton.IsEnabled = !busy;
            if (!string.IsNullOrWhiteSpace(status)) PageStatusText.Text = status;
        }
    }
}
