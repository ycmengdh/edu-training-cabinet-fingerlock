using System.Windows;
using System.Windows.Controls;

namespace FingerprintLockManager
{
    /// <summary>
    /// 下发状态监控页面（需求 7）
    ///
    /// 需求 7：上位机要显示发送老师权限的状态，确保录入后每台设备都能接收到。
    /// 未收到的柜子可手动重发。
    /// </summary>
    public partial class DeployStatusPage : Page
    {
        /// <summary>当前选中的任务</summary>
        private DeployTaskDisplay? _selectedTask;

        public DeployStatusPage()
        {
            InitializeComponent();
            Loaded += (s, e) => LoadTasks();
        }

        /// <summary>加载任务列表</summary>
        private void LoadTasks()
        {
            var tasks = App.DeployService.GetRecentDeployTasks(50);
            var list = tasks.Select(t => new DeployTaskDisplay
            {
                Id = t.Id,
                TaskType = t.TaskType,
                TaskTypeText = TaskTypeToText(t.TaskType),
                UserId = t.UserId ?? "",
                DeviceId = t.DeviceId ?? "",
                Status = t.Status,
                StatusText = StatusToText(t.Status),
                TotalDevices = t.TotalDevices,
                AckedDevices = t.AckedDevices,
                ProgressText = $"{t.AckedDevices} / {t.TotalDevices}",
                CreateTime = t.CreateTime,
                CompleteTime = t.CompleteTime
            }).ToList();
            TaskDataGrid.ItemsSource = list;
        }

        /// <summary>加载某任务的下发状态明细</summary>
        private void LoadStatuses(long taskId)
        {
            var statuses = App.DeployService.GetDeployStatuses(taskId);
            var list = statuses.Select(s => new DeployStatusDisplay
            {
                Id = s.Id,
                DeviceId = s.DeviceId,
                Status = s.Status,
                StatusText = StatusToText(s.Status),
                ErrorMessage = s.ErrorMessage ?? "",
                RetryCount = s.RetryCount,
                AckTime = s.AckTime
            }).ToList();
            StatusDataGrid.ItemsSource = list;
        }

        /// <summary>任务类型转中文</summary>
        private static string TaskTypeToText(string type)
        {
            return type switch
            {
                "teacher_broadcast" => "老师指纹广播",
                "student_assign" => "学生权限下发",
                "remove_user" => "删除柜子用户",
                "delete_class" => "按班级删除",
                _ => type
            };
        }

        /// <summary>状态转中文</summary>
        private static string StatusToText(string status)
        {
            return status switch
            {
                "pending" => "待接收",
                "running" => "进行中",
                "success" => "成功",
                "partial" => "部分成功",
                "failed" => "失败",
                _ => status
            };
        }

        /// <summary>刷新任务列表</summary>
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadTasks();
            if (_selectedTask != null)
            {
                LoadStatuses(_selectedTask.Id);
            }
        }

        /// <summary>任务选择变化：加载明细</summary>
        private void TaskDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TaskDataGrid.SelectedItem is not DeployTaskDisplay task) return;
            _selectedTask = task;
            SelectedTaskText.Text = $"- 任务 {task.Id}（{task.TaskTypeText}）";
            LoadStatuses(task.Id);
        }

        /// <summary>重发失败项</summary>
        private void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedTask == null)
            {
                MessageBox.Show("请先选择要重发的任务", "提示");
                return;
            }

            var result = MessageBox.Show(
                $"确认重发任务 {_selectedTask.Id}（{_selectedTask.TaskTypeText}）中所有未成功的项？",
                "确认重发", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                App.DeployService.RetryFailedDeploy(_selectedTask.Id);
                MessageBox.Show("已发起重发，请稍后刷新查看", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                LoadStatuses(_selectedTask.Id);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"重发失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>任务展示包装类</summary>
        private class DeployTaskDisplay
        {
            public long Id { get; set; }
            public string TaskType { get; set; }
            public string TaskTypeText { get; set; }
            public string UserId { get; set; }
            public string DeviceId { get; set; }
            public string Status { get; set; }
            public string StatusText { get; set; }
            public int TotalDevices { get; set; }
            public int AckedDevices { get; set; }
            public string ProgressText { get; set; }
            public DateTime CreateTime { get; set; }
            public DateTime? CompleteTime { get; set; }
        }

        /// <summary>状态明细展示包装类</summary>
        private class DeployStatusDisplay
        {
            public long Id { get; set; }
            public string DeviceId { get; set; }
            public string Status { get; set; }
            public string StatusText { get; set; }
            public string ErrorMessage { get; set; }
            public int RetryCount { get; set; }
            public DateTime? AckTime { get; set; }
        }
    }
}
