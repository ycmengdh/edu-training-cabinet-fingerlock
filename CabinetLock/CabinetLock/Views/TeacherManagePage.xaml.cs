using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CabinetLock
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
            SetBusy(true, "正在读取教师账号");
            try
            {
                _teachers = await Task.Run(() => App.UserService.GetUsersByRole("teacher"));
                Dictionary<string, string> classNames = (await Task.Run(() => App.ClassService.GetAll()))
                    .ToDictionary(item => item.ClassId, item => item.Name, StringComparer.OrdinalIgnoreCase);
                foreach (User teacher in _teachers)
                {
                    string[] names = teacher.GetResponsibleClassIds().Select(id =>
                        classNames.TryGetValue(id, out string? name) ? $"{name} ({id})" : id).ToArray();
                    teacher.ResponsibleClassText = names.Length == 0 ? "未分配" : string.Join("、", names);
                }
                ApplyTeacherPage();
            }
            catch (RootDataUnavailableException ex)
            {
                _teachers.Clear();
                TeacherDataGrid.ItemsSource = null;
                PageStatusText.Text = ex.Message;
                _pager.BindChrome(Pager);
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
            _pager.BindChrome(Pager);
            PageStatusText.Text = _pager.StatusText(page.Count) + " · 负责班级可在此调整";
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
            await LoadTeachersAsync(resetPage: false);

        private void Pager_PageRequested(object sender, Controls.PaginationRequestedEventArgs e)
        {
            _pager.ApplyRequest(e);
            ApplyTeacherPage();
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
                MessageBox.Show("请先选择教师", "提示");
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
            var editor = new TeacherEditWindow(null, App.ClassService.GetAll())
            {
                Owner = Window.GetWindow(this)
            };
            if (editor.ShowDialog() != true) return;
            if (!PasswordHelper.IsPasswordAcceptable(editor.Password))
            {
                MessageBox.Show(PasswordHelper.PasswordRequirement, "密码不符合要求");
                return;
            }

            var teacher = new User
            {
                UserCode = editor.TeacherCode,
                Name = editor.TeacherName,
                Role = "teacher",
                CreateTime = DateTime.Now
            };
            teacher.SetResponsibleClassIds(editor.SelectedClassIds);
            SetBusy(true, "正在创建教师账号");
            try
            {
                bool added = await Task.Run(() => App.UserService.AddUser(teacher, editor.Password));
                MessageBox.Show(added ? "教师账号已创建" : "创建失败，账号 ID 可能已存在",
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
                MessageBox.Show("请先选择教师", "提示");
                return;
            }
            var editor = new TeacherEditWindow(selected, App.ClassService.GetAll())
            {
                Owner = Window.GetWindow(this)
            };
            if (editor.ShowDialog() != true) return;

            var updated = new User
            {
                UserId = selected.UserId,
                UserCode = editor.TeacherCode,
                Name = editor.TeacherName,
                Role = "teacher",
                FingerprintId = selected.FingerprintId,
                PasswordSalt = selected.PasswordSalt,
                PasswordHash = selected.PasswordHash,
                Enabled = selected.Enabled,
                CreateTime = selected.CreateTime,
                UpdateTime = DateTime.Now
            };
            updated.SetResponsibleClassIds(editor.SelectedClassIds);
            SetBusy(true, "正在保存教师信息");
            try
            {
                bool saved = await Task.Run(() => App.UserService.UpdateUser(updated));
                MessageBox.Show(saved ? "教师信息已更新" : "保存失败", saved ? "完成" : "错误",
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

        private void EditRowButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not User teacher) return;
            TeacherDataGrid.SelectedItem = teacher;
            EditTeacherButton_Click(sender, e);
        }

        private void ResetPasswordRowButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not User teacher) return;
            TeacherDataGrid.SelectedItem = teacher;
            ResetPasswordButton_Click(sender, e);
        }

        private void DeleteRowButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not User teacher) return;
            TeacherDataGrid.SelectedItem = teacher;
            DeleteTeacherButton_Click(sender, e);
        }

        private async void ResetPasswordButton_Click(object sender, RoutedEventArgs e)
        {
            if (TeacherDataGrid.SelectedItem is not User selected)
            {
                MessageBox.Show("请先选择教师", "提示");
                return;
            }
            if (!ShowPasswordDialog(out string password) ||
                !PasswordHelper.IsPasswordAcceptable(password))
            {
                if (!string.IsNullOrEmpty(password))
                    MessageBox.Show(PasswordHelper.PasswordRequirement, "密码不符合要求");
                return;
            }

            SetBusy(true, "正在重置教师密码");
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
                MessageBox.Show("请先选择教师", "提示");
                return;
            }

            SetBusy(true, selected.Enabled ? "正在停用教师账号" : "正在启用教师账号");
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
                MessageBox.Show("请先选择教师", "提示");
                return;
            }
            if (MessageBox.Show($"确认删除教师「{selected.Name}」？", "确认删除",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            SetBusy(true, "正在删除教师账号");
            try
            {
                bool ok = await Task.Run(() => App.UserService.DeleteUser(selected.UserId));
                MessageBox.Show(ok ? "教师账号已删除" : "删除失败", ok ? "完成" : "错误",
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

        private bool ShowPasswordDialog(out string password)
        {
            password = "";
            var dialog = new Window
            {
                Title = "重置教师密码",
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
