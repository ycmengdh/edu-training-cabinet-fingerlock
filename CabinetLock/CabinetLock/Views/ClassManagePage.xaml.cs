using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CabinetLock
{
    public partial class ClassManagePage : Page
    {
        private readonly ListPager _pager = new(20);
        private List<ClassInfo> _classes = new();
        private bool _busy;

        public ClassManagePage()
        {
            InitializeComponent();
            if (string.Equals(App.CurrentUser?.Role, "teacher", StringComparison.OrdinalIgnoreCase))
            {
                AddButton.Visibility = Visibility.Collapsed;
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
                string keyword = ClassSearchBox?.Text?.Trim() ?? "";
                PagedResult<ClassInfo> result = await Task.Run(() =>
                    App.ClassService.QueryVisiblePage(
                        _pager.PageIndex, _pager.PageSize, keyword));
                _classes = result.Items.ToList();
                _pager.SetTotalCount(result.TotalCount);
                ApplyClassPage();
            }
            catch (RootDataUnavailableException ex)
            {
                ClassDataGrid.ItemsSource = null;
                _classes.Clear();
                _pager.BindChrome(Pager, "个班级");
                PageStatusText.Text = ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ApplyClassPage()
        {
            string keyword = ClassSearchBox?.Text?.Trim() ?? "";
            ClassDataGrid.ItemsSource = _classes;
            _pager.BindChrome(Pager, "个班级");
            PageStatusText.Text = string.IsNullOrWhiteSpace(keyword)
                ? _pager.StatusText(_classes.Count, "个班级")
                : $"找到 {_pager.TotalCount} 个班级 · {_pager.StatusText(_classes.Count, "个班级")}";
            UpdateDeleteButtonState();
        }

        private async void ClassSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!IsLoaded) return;
            _pager.Reset();
            await LoadAsync();
        }

        private async void Pager_PageRequested(object sender, Controls.PaginationRequestedEventArgs e)
        {
            _pager.ApplyRequest(e);
            await LoadAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

        private async void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ShowClassDialog(null, out string classId, out string name,
                    out IReadOnlyList<string> teacherIds, out _)) return;
            SetBusy(true, "正在保存班级");
            try
            {
                bool ok = await Task.Run(() => App.ClassService.Add(new ClassInfo
                {
                    ClassId = classId.Trim(),
                    Name = name.Trim(),
                    CreateTime = DateTime.Now
                }));
                if (ok)
                {
                    if (!await Task.Run(() => App.UserService.SetClassTeachers(classId, teacherIds)))
                        AppToast.Warning("班级已添加，但负责教师保存失败，可重新编辑班级重试");
                    AppToast.Success("班级已添加");
                    await LoadAsync();
                }
                else AppToast.Error("添加失败，班级 ID 可能已存在");
            }
            catch (RootDataUnavailableException ex)
            {
                AppToast.Error(ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task EditClassAsync(ClassInfo selected)
        {
            bool wasEnabled = selected.Enabled;
            if (!ShowClassDialog(selected, out _, out string name,
                    out IReadOnlyList<string> teacherIds,
                    out bool requestedEnabled)) return;
            selected.Name = name.Trim();
            SetBusy(true, "正在保存班级");
            try
            {
                bool ok = await Task.Run(() => App.ClassService.Update(selected));
                if (ok)
                {
                    if (!await Task.Run(() => App.UserService.SetClassTeachers(selected.ClassId, teacherIds)))
                        AppToast.Warning("班级信息已更新，但负责教师保存失败，可重试");
                    AppToast.Success("班级已更新");
                    if (requestedEnabled != wasEnabled)
                    {
                        ShowLifecycleWindow(new[] { selected }, requestedEnabled
                            ? ClassLifecycleAction.Enable
                            : ClassLifecycleAction.Disable);
                    }
                    await LoadAsync();
                }
                else AppToast.Error("更新失败");
            }
            catch (RootDataUnavailableException ex)
            {
                AppToast.Error(ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
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

        private async void EditRowButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ClassInfo selected)
                await EditClassAsync(selected);
        }

        private void ManageRowButton_Click(object sender, RoutedEventArgs e) =>
            OpenSelectedClass((sender as FrameworkElement)?.Tag as ClassInfo);

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

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            List<ClassInfo> selected = _classes.Where(item => item.IsSelected).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("请先勾选要删除的班级", "提示");
                return;
            }
            await DeleteClassesAsync(selected);
        }

        private async Task DeleteClassesAsync(IReadOnlyList<ClassInfo> selected)
        {
            int studentCount = selected.Sum(item => item.StudentCount);
            string message = selected.Count == 1
                ? $"确认删除班级「{selected[0].Name}」及其 {studentCount} 名学生？"
                : $"确认批量删除 {selected.Count} 个班级及其共 {studentCount} 名学生？";
            message += "\n系统会逐个学生先删除柜机权限与指纹，再删除本地学生数据；柜机离线或删除失败的学生会跳过。全部学生删除后才删除班级。";
            if (MessageBox.Show(message, "确认删除", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            ShowLifecycleWindow(selected, ClassLifecycleAction.Delete);
            await LoadAsync();
        }

        private void ShowLifecycleWindow(
            IReadOnlyList<ClassInfo> classes, ClassLifecycleAction action)
        {
            var window = new ClassLifecycleWindow(classes, action)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
        }

        private void SelectPageCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || sender is not CheckBox checkBox ||
                ClassDataGrid.ItemsSource is not IEnumerable<ClassInfo> page) return;
            foreach (ClassInfo item in page) item.IsSelected = checkBox.IsChecked == true;
            ClassDataGrid.Items.Refresh();
            UpdateDeleteButtonState();
        }

        private void ClassSelectionCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded || sender is not CheckBox checkBox ||
                checkBox.DataContext is not ClassInfo item) return;
            item.IsSelected = checkBox.IsChecked == true;
            UpdateDeleteButtonState();
        }

        private void UpdateDeleteButtonState()
        {
            int selectedCount = _classes.Count(item => item.IsSelected);
            DeleteButton.IsEnabled = !_busy && selectedCount > 0;
            DeleteButton.ToolTip = selectedCount == 0
                ? "请先勾选班级"
                : $"删除已勾选的 {selectedCount} 个班级";
        }

        private bool ShowClassDialog(ClassInfo? existing, out string classId, out string name,
            out IReadOnlyList<string> teacherIds, out bool enabled)
        {
            var editor = new ClassEditWindow(existing, App.UserService.GetUsersByRole("teacher"))
            {
                Owner = Window.GetWindow(this)
            };
            if (editor.ShowDialog() != true)
            {
                classId = existing?.ClassId ?? "";
                name = existing?.Name ?? "";
                teacherIds = Array.Empty<string>();
                enabled = existing?.Enabled ?? true;
                return false;
            }
            classId = editor.ClassId;
            name = editor.ClassName;
            teacherIds = editor.SelectedTeacherIds;
            enabled = editor.RequestedEnabled;
            return true;
        }

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            RefreshButton.IsEnabled = !busy;
            AddButton.IsEnabled = !busy;
            UpdateDeleteButtonState();
            if (!string.IsNullOrEmpty(status)) PageStatusText.Text = status;
        }
    }
}
