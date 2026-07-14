using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace FingerprintLockManager
{
    /// <summary>
    /// 设备容量监控页面（需求 10）
    ///
    /// ESP32-S3 N16R8 Flash 存本地老师/学生/指纹，最多 200 个。
    /// 到 190 预警提示清理。可按班级删除归还空间。
    ///
    /// 容量数据来源：
    /// - 上位机授权记录数（GetAuthorizedUserCount，立即可见）
    /// - 柜子返回的 READ_CAPACITY（按"查询所有柜子容量"按钮主动查询）
    /// </summary>
    public partial class DeviceCapacityPage : Page
    {
        public DeviceCapacityPage()
        {
            InitializeComponent();
            Loaded += (s, e) => LoadCapacities();
        }

        /// <summary>加载柜子容量列表</summary>
        private void LoadCapacities()
        {
            var devices = App.DeviceService.GetAllDevices().Where(d => !d.IsRoot).ToList();
            var list = new List<CapacityDisplay>();

            foreach (var device in devices)
            {
                int used = App.CapacityService.GetAuthorizedUserCount(device.DeviceId);
                string level = App.CapacityService.GetCapacityLevel(used);
                int percent = App.CapacityService.GetUsagePercent(used);

                list.Add(new CapacityDisplay
                {
                    DeviceId = device.DeviceId,
                    DeviceName = device.DeviceName,
                    IsOnline = device.IsOnline,
                    OnlineText = device.IsOnline ? "在线" : "离线",
                    UsedCount = used,
                    MaxCount = Protocol.DeviceMaxUsers,
                    UsageText = $"{used} / {Protocol.DeviceMaxUsers}",
                    UsagePercent = percent,
                    Level = level,
                    LevelText = LevelToText(level)
                });
            }

            CapacityDataGrid.ItemsSource = list;

            // 显示预警横幅
            int warnCount = list.Count(c => c.Level != "normal");
            if (warnCount > 0)
            {
                WarningBanner.Visibility = Visibility.Visible;
                WarningText.Text = $"⚠ 有 {warnCount} 台柜子达到预警阈值（≥{Protocol.CapacityWarnThreshold}），" +
                    $"建议在「按班级清理」中删除已毕业班级的学生以释放空间。";
            }
            else
            {
                WarningBanner.Visibility = Visibility.Collapsed;
            }
        }

        /// <summary>预警级别转中文</summary>
        private static string LevelToText(string level)
        {
            return level switch
            {
                "full" => "已满",
                "warning" => "预警",
                _ => "正常"
            };
        }

        /// <summary>查询所有在线柜子容量</summary>
        private void QueryAllButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                App.CapacityService.QueryAllCapacities();
                MessageBox.Show("已向所有在线柜子发送容量查询，请稍后刷新查看。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"查询失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>按班级清理（学生毕业全班删）</summary>
        private void ClearByClassButton_Click(object sender, RoutedEventArgs e)
        {
            // 弹出班级选择对话框
            if (!ShowClassSelectDialog(out string classId, out string className))
            {
                return;
            }

            int studentCount = App.ClassService.GetStudentsByClass(classId).Count;
            if (studentCount == 0)
            {
                MessageBox.Show("该班级下没有学生", "提示");
                return;
            }

            var result = MessageBox.Show(
                $"确认删除班级「{className}（{classId}」在所有柜子上的 {studentCount} 名学生？\n" +
                "此操作将向所有在线柜子下发 DELETE_CLASS_USERS 命令，并删除根节点上的授权记录。\n" +
                "操作前会自动备份。",
                "确认按班级清理", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            // 操作前自动备份（需求 11）
            App.BackupService.BackupBeforeAction($"按班级清理 {classId}", App.CurrentUser?.UserId);

            try
            {
                App.CapacityService.DeleteClassFromAllDevices(classId, App.CurrentUser?.UserId);
                MessageBox.Show("已发起清理任务，请到「下发状态」页面查看执行情况。", "已发起",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                LoadCapacities();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"清理失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>刷新列表</summary>
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadCapacities();
        }

        /// <summary>显示班级选择对话框</summary>
        private bool ShowClassSelectDialog(out string classId, out string className)
        {
            classId = "";
            className = "";

            var classes = App.CurrentUser?.Role == "teacher"
                ? App.ClassService.GetClassesByTeacher(App.CurrentUser.UserId)
                : App.ClassService.GetClasses();

            if (classes.Count == 0)
            {
                MessageBox.Show("当前没有班级可清理", "提示");
                return false;
            }

            var dlg = new Window
            {
                Title = "选择要清理的班级",
                Width = 360,
                Height = 200,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = FindResource("BackgroundBrush") as Brush
            };

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(new TextBlock { Text = "班级", Margin = new Thickness(0, 0, 0, 6) });
            var combo = new ComboBox { Margin = new Thickness(0, 0, 0, 16) };
            foreach (var c in classes)
            {
                combo.Items.Add(new ComboBoxItem
                {
                    Content = $"{c.ClassName}（{c.ClassId}，{c.StudentCount}人）",
                    Tag = $"{c.ClassId}|{c.ClassName}"
                });
            }
            combo.SelectedIndex = 0;
            panel.Children.Add(combo);

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
            string localClassId = "";
            string localClassName = "";
            okBtn.Click += (s, e) =>
            {
                if (combo.SelectedItem is ComboBoxItem item)
                {
                    string tag = item.Tag?.ToString() ?? "";
                    var parts = tag.Split('|', 2);
                    if (parts.Length == 2)
                    {
                        localClassId = parts[0];
                        localClassName = parts[1];
                        confirmed = true;
                    }
                }
                dlg.Close();
            };
            cancelBtn.Click += (s, e) => dlg.Close();

            dlg.ShowDialog();
            if (confirmed)
            {
                classId = localClassId;
                className = localClassName;
            }
            return confirmed;
        }

        /// <summary>容量展示包装类</summary>
        private class CapacityDisplay
        {
            public string DeviceId { get; set; }
            public string DeviceName { get; set; }
            public bool IsOnline { get; set; }
            public string OnlineText { get; set; }
            public int UsedCount { get; set; }
            public int MaxCount { get; set; }
            public string UsageText { get; set; }
            public int UsagePercent { get; set; }
            public string Level { get; set; }
            public string LevelText { get; set; }
        }
    }
}
