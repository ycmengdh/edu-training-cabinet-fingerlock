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
            Loaded += async (_, _) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            SetBusy(true, "正在读取班级数据");
            try
            {
                List<ClassInfo> classes = await Task.Run(App.ClassService.GetAll);
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
            ToggleButton.IsEnabled = !busy;
            DeleteButton.IsEnabled = !busy;
            if (!string.IsNullOrEmpty(status)) PageStatusText.Text = status;
        }
    }
}
