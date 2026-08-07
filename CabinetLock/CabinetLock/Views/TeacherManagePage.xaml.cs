using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace CabinetLock
{
    public partial class TeacherManagePage : Page
    {
        private readonly ListPager _pager = new(30);
        private List<User> _teachers = new();
        private bool _busy;

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
                PagedResult<User> result = await Task.Run(() =>
                    App.UserService.QueryVisibleUsersPage(
                        _pager.PageIndex, _pager.PageSize, role: "teacher"));
                _teachers = result.Items.ToList();
                _pager.SetTotalCount(result.TotalCount);
                Dictionary<string, string> classNames = await Task.Run(
                    App.ClassService.GetVisibleClassNames);
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
            TeacherDataGrid.ItemsSource = _teachers;
            _pager.BindChrome(Pager);
            PageStatusText.Text = _pager.StatusText(_teachers.Count) + " · 负责班级可在此调整";
            UpdateSelectionActions();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
            await LoadTeachersAsync(resetPage: false);

        private async void Pager_PageRequested(object sender, Controls.PaginationRequestedEventArgs e)
        {
            _pager.ApplyRequest(e);
            await LoadTeachersAsync(resetPage: false);
        }

        private void SelectPageCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || sender is not CheckBox checkBox ||
                TeacherDataGrid.ItemsSource is not IEnumerable<User> page) return;
            bool selected = checkBox.IsChecked == true;
            foreach (User teacher in page) teacher.IsSelected = selected;
            TeacherDataGrid.Items.Refresh();
            UpdateSelectionActions();
        }

        private void TeacherSelectionCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded) return;
            UpdateSelectionActions();
        }

        private List<User> GetCheckedTeachers() =>
            _teachers.Where(teacher => teacher.IsSelected).ToList();

        private void UpdateSelectionActions()
        {
            int selectedCount = GetCheckedTeachers().Count;
            DeleteSelectedTeachersButton.IsEnabled = !_busy && selectedCount > 0;
            DeleteSelectedTeachersButton.ToolTip = selectedCount == 0
                ? "请先勾选教师"
                : $"删除已勾选的 {selectedCount} 名教师";
        }

        private void FingerprintSyncButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new TeacherFingerprintSyncWindow { Owner = Window.GetWindow(this) };
            window.ShowDialog();
        }

        private async void ImportTeachersButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new ImportUsersWindow(null, "teacher")
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
            if (window.AnyImported) await LoadTeachersAsync();
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

        private void EnrollFingerprintRowButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not User teacher) return;
            TeacherDataGrid.SelectedItem = teacher;
            EnrollTeacherFingerprintButton_Click(sender, e);
        }

        private void MoreRowButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: User teacher } button) return;
            TeacherDataGrid.SelectedItem = teacher;

            bool isCurrentUser = string.Equals(
                teacher.UserId, App.CurrentUser?.UserId, StringComparison.OrdinalIgnoreCase);
            var menu = new ContextMenu
            {
                PlacementTarget = button,
                Placement = PlacementMode.Bottom,
                HorizontalOffset = -128,
                VerticalOffset = 2,
                Background = FindResource("CardBrush") as Brush,
                BorderBrush = FindResource("BorderBrush") as Brush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(4),
                MinWidth = 160
            };
            menu.Items.Add(CreateRowMenuItem("\uE72E", "重置密码", () =>
            {
                TeacherDataGrid.SelectedItem = teacher;
                ResetPasswordButton_Click(button, new RoutedEventArgs());
            }));
            menu.Items.Add(CreateRowMenuItem(teacher.Enabled ? "\uE711" : "\uE73E",
                teacher.Enabled ? "停用教师" : "启用教师", () =>
                {
                    TeacherDataGrid.SelectedItem = teacher;
                    ToggleTeacherButton_Click(button, new RoutedEventArgs());
                }, !isCurrentUser));
            menu.Items.Add(new Separator { Margin = new Thickness(4, 3, 4, 3) });
            menu.Items.Add(CreateRowMenuItem("\uE74D", "删除教师",
                async () => await DeleteTeachersAsync(new List<User> { teacher }),
                !isCurrentUser, danger: true));

            button.ContextMenu = menu;
            menu.IsOpen = true;
        }

        private MenuItem CreateRowMenuItem(
            string glyph,
            string text,
            Action action,
            bool enabled = true,
            bool danger = false)
        {
            Brush? foreground = danger
                ? FindResource("DangerBrush") as Brush
                : FindResource("TextBrush") as Brush;
            var header = new StackPanel { Orientation = Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = FindResource("AppIconFont") as FontFamily,
                Foreground = foreground,
                Width = 22,
                VerticalAlignment = VerticalAlignment.Center
            });
            header.Children.Add(new TextBlock
            {
                Text = text,
                Foreground = foreground,
                VerticalAlignment = VerticalAlignment.Center
            });
            var item = new MenuItem
            {
                Header = header,
                IsEnabled = enabled,
                Padding = new Thickness(10, 7, 10, 7),
                Background = Brushes.Transparent
            };
            item.Click += (_, _) => action();
            return item;
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
            if (string.Equals(selected.UserId, App.CurrentUser?.UserId,
                    StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("不能停用当前登录账号", "操作受限",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string action = selected.Enabled ? "停用" : "启用";
            if (MessageBox.Show($"确认{action}教师「{selected.Name}」？", $"确认{action}",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

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

        private async void DeleteSelectedTeachersButton_Click(object sender, RoutedEventArgs e)
        {
            await DeleteTeachersAsync(GetCheckedTeachers());
        }

        private async Task DeleteTeachersAsync(IReadOnlyList<User> targets)
        {
            if (targets.Count == 0)
            {
                MessageBox.Show("请先勾选要删除的教师", "提示");
                return;
            }

            if (targets.Any(teacher => string.Equals(
                    teacher.UserId, App.CurrentUser?.UserId, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("不能删除当前登录账号", "操作受限",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string confirm = targets.Count == 1
                ? $"确认删除教师「{targets[0].Name}（{targets[0].DisplayId}）」？\n该教师的权限记录将一并删除。"
                : $"确认批量删除已勾选的 {targets.Count} 名教师？\n其权限记录将一并删除。";
            if (MessageBox.Show(confirm, "确认删除", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

            SetBusy(true, targets.Count == 1
                ? "正在删除教师账号"
                : $"正在删除 {targets.Count} 名教师");
            int success = 0;
            int fail = 0;
            try
            {
                foreach (User teacher in targets)
                {
                    bool deleted;
                    try
                    {
                        deleted = await Task.Run(() => App.UserService.DeleteUser(teacher.UserId));
                    }
                    catch (RootDataUnavailableException ex)
                    {
                        MessageBox.Show(ex.Message, "根节点不可用",
                            MessageBoxButton.OK, MessageBoxImage.Error);
                        fail = targets.Count - success;
                        break;
                    }

                    if (!deleted)
                    {
                        fail++;
                        continue;
                    }

                    success++;
                    if (teacher.FingerprintId.HasValue)
                        App.CabinetSyncService.DeleteFingerprintFromAll(teacher.FingerprintId.Value);
                    App.CabinetBindingService.RemoveFromAll(teacher.UserId);
                    try
                    {
                        await App.SdStorageService.DeleteTemplateAsync(teacher.UserId);
                    }
                    catch
                    {
                    }
                }
            }
            finally
            {
                SetBusy(false);
            }

            if (success > 0 && fail == 0)
            {
                MessageBox.Show(success == 1 ? "教师账号已删除" : $"已成功删除 {success} 名教师",
                    "完成", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (success > 0)
            {
                MessageBox.Show($"成功 {success} 名，失败 {fail} 名", "部分完成",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else
            {
                MessageBox.Show("删除失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            if (success > 0) await LoadTeachersAsync();
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
            _busy = busy;
            AddTeacherButton.IsEnabled = !busy;
            ImportTeachersButton.IsEnabled = !busy;
            RefreshButton.IsEnabled = !busy;
            FingerprintSyncButton.IsEnabled = !busy;
            TeacherDataGrid.IsEnabled = !busy;
            UpdateSelectionActions();
            if (!string.IsNullOrWhiteSpace(status)) PageStatusText.Text = status;
        }
    }
}
