using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FingerprintLockManager
{
    public partial class ClassManagePage : Page
    {
        public ClassManagePage()
        {
            InitializeComponent();
            if (string.Equals(App.CurrentUser?.Role, "teacher", StringComparison.OrdinalIgnoreCase))
            {
                AddButton.Visibility = Visibility.Collapsed;
                EditButton.Visibility = Visibility.Collapsed;
                ToggleButton.Visibility = Visibility.Collapsed;
                DeleteButton.Visibility = Visibility.Collapsed;
                PageStatusText.Text = "打开负责的班级，维护学生、指纹和柜子权限";
            }
            Loaded += async (_, _) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            SetBusy(true, "正在读取班级数据");
            try
            {
                List<ClassInfo> classes = await Task.Run(() =>
                {
                    var items = App.ClassService.GetVisible();
                    var users = App.UserService.GetAllUsers();
                    foreach (var item in items)
                    {
                        var teachers = users
                            .Where(user => string.Equals(user.Role, "teacher", StringComparison.OrdinalIgnoreCase)
                                && string.Equals(user.ClassId, item.ClassId, StringComparison.OrdinalIgnoreCase))
                            .Select(user => string.IsNullOrWhiteSpace(user.Name) ? user.UserId : user.Name)
                            .Where(name => !string.IsNullOrWhiteSpace(name))
                            .ToList();
                        item.TeacherText = teachers.Count == 0 ? "未分配" : string.Join("、", teachers);
                        item.StudentCount = users.Count(user =>
                            string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(user.ClassId, item.ClassId, StringComparison.OrdinalIgnoreCase));
                    }
                    return items;
                });
                ClassDataGrid.ItemsSource = classes;
                PageStatusText.Text = $"共 {classes.Count} 个班级";
            }
            catch (RootDataUnavailableException ex)
            {
                ClassDataGrid.ItemsSource = null;
                PageStatusText.Text = ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ShowClassDialog(null, out string classId, out string name)) return;
            SetBusy(true, "正在保存班级");
            try
            {
                bool ok = await Task.Run(() => App.ClassService.Add(new ClassInfo
                {
                    ClassId = classId.Trim(),
                    Name = name.Trim(),
                    CreateTime = DateTime.Now
                }));
                MessageBox.Show(ok ? "班级已添加" : "添加失败，班级 ID 可能已存在",
                    ok ? "完成" : "错误", MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Error);
                if (ok) await LoadAsync();
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

        private async void EditButton_Click(object sender, RoutedEventArgs e)
        {
            if (ClassDataGrid.SelectedItem is not ClassInfo selected)
            {
                MessageBox.Show("请先选择班级", "提示");
                return;
            }
            if (!ShowClassDialog(selected, out _, out string name)) return;
            selected.Name = name.Trim();
            SetBusy(true, "正在保存班级");
            try
            {
                bool ok = await Task.Run(() => App.ClassService.Update(selected));
                MessageBox.Show(ok ? "班级已更新" : "更新失败", ok ? "完成" : "错误",
                    MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
                if (ok) await LoadAsync();
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

        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            OpenSelectedClass(ClassDataGrid.SelectedItem as ClassInfo);
        }

        private void ClassDataGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source &&
                FindVisualParent<DataGridRow>(source) == null) return;
            OpenSelectedClass(ClassDataGrid.SelectedItem as ClassInfo);
        }

        private void OpenRowButton_Click(object sender, RoutedEventArgs e)
        {
            OpenSelectedClass((sender as FrameworkElement)?.Tag as ClassInfo);
        }

        private void CabinetSyncButton_Click(object sender, RoutedEventArgs e) =>
            OpenCabinetSync(ClassDataGrid.SelectedItem as ClassInfo);

        private void CabinetSyncRowButton_Click(object sender, RoutedEventArgs e) =>
            OpenCabinetSync((sender as FrameworkElement)?.Tag as ClassInfo);

        private void OpenCabinetSync(ClassInfo? selected)
        {
            if (selected == null)
            {
                MessageBox.Show("请先选择班级", "提示");
                return;
            }
            var window = new ClassCabinetSyncWindow(selected.ClassId, selected.Name)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
            _ = LoadAsync();
        }

        private void OpenSelectedClass(ClassInfo? selected)
        {
            if (selected == null)
            {
                MessageBox.Show("请先选择班级", "提示");
                return;
            }
            NavigationService?.Navigate(new ClassStudentsPage(selected.ClassId, selected.Name));
        }

        private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T match) return match;
                child = System.Windows.Media.VisualTreeHelper.GetParent(child);
            }
            return null;
        }

        private async void DeleteRowButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ClassInfo selected) return;
            ClassDataGrid.SelectedItem = selected;
            if (MessageBox.Show($"确认删除班级「{selected.Name}」？\n若仍有用户绑定该班级将无法删除。",
                    "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            await DeleteClassAsync(selected);
        }

        private async void ToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (ClassDataGrid.SelectedItem is not ClassInfo selected)
            {
                MessageBox.Show("请先选择班级", "提示");
                return;
            }
            bool target = !selected.Enabled;
            SetBusy(true, target ? "正在启用班级" : "正在停用班级");
            try
            {
                bool ok = await Task.Run(() => App.ClassService.SetEnabled(selected.ClassId, target));
                if (ok) await LoadAsync();
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

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (ClassDataGrid.SelectedItem is not ClassInfo selected)
            {
                MessageBox.Show("请先选择班级", "提示");
                return;
            }
            if (MessageBox.Show($"确认删除班级「{selected.Name}」？\n若仍有用户绑定该班级将无法删除。",
                    "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            await DeleteClassAsync(selected);
        }

        private async Task DeleteClassAsync(ClassInfo selected)
        {
            SetBusy(true, "正在删除班级");
            try
            {
                bool ok = await Task.Run(() => App.ClassService.Delete(selected.ClassId));
                MessageBox.Show(ok ? "班级已删除" : "删除失败，可能仍有用户绑定该班级",
                    ok ? "完成" : "错误", MessageBoxButton.OK,
                    ok ? MessageBoxImage.Information : MessageBoxImage.Error);
                if (ok) await LoadAsync();
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

        private bool ShowClassDialog(ClassInfo? existing, out string classId, out string name)
        {
            classId = existing?.ClassId ?? "";
            name = existing?.Name ?? "";
            bool isEdit = existing != null;

            var dlg = new Window
            {
                Title = isEdit ? "编辑班级" : "添加班级",
                Width = 340,
                Height = 240,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = FindResource("BackgroundBrush") as Brush
            };
            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "班级 ID", Margin = new Thickness(0, 0, 0, 6) });
            var idBox = new TextBox
            {
                Text = classId,
                IsEnabled = !isEdit,
                Margin = new Thickness(0, 0, 0, 12)
            };
            panel.Children.Add(idBox);
            panel.Children.Add(new TextBlock { Text = "班级名称", Margin = new Thickness(0, 0, 0, 6) });
            var nameBox = new TextBox { Text = name, Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(nameBox);
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
            string localId = classId;
            string localName = name;
            okBtn.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(idBox.Text) || string.IsNullOrWhiteSpace(nameBox.Text))
                {
                    MessageBox.Show("班级 ID 和名称不能为空", "提示");
                    return;
                }
                localId = idBox.Text.Trim();
                localName = nameBox.Text.Trim();
                confirmed = true;
                dlg.Close();
            };
            cancelBtn.Click += (_, _) => dlg.Close();
            dlg.ShowDialog();
            if (confirmed)
            {
                classId = localId;
                name = localName;
            }
            return confirmed;
        }

        private void SetBusy(bool busy, string? status = null)
        {
            RefreshButton.IsEnabled = !busy;
            AddButton.IsEnabled = !busy;
            EditButton.IsEnabled = !busy;
            OpenButton.IsEnabled = !busy;
            CabinetSyncButton.IsEnabled = !busy;
            ToggleButton.IsEnabled = !busy;
            DeleteButton.IsEnabled = !busy;
            if (!string.IsNullOrEmpty(status)) PageStatusText.Text = status;
        }
    }
}
