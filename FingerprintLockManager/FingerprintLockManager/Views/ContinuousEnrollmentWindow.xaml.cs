using System.Windows;

namespace FingerprintLockManager
{
    public partial class ContinuousEnrollmentWindow : BorderlessWindow
    {
        private readonly List<ContinuousEnrollmentItem> _items;

        public ContinuousEnrollmentWindow(string className, IEnumerable<User> users)
        {
            InitializeComponent();
            _items = users.Where(user => user != null)
                .DistinctBy(user => user.UserId, StringComparer.OrdinalIgnoreCase)
                .Select((user, index) => new ContinuousEnrollmentItem
                {
                    Sequence = index + 1,
                    User = user,
                    ExistingFingerprintCount = BusinessDatabase.ReadAllFpTemplateMetas().Count(item =>
                        string.Equals(item.UserId, user.UserId, StringComparison.OrdinalIgnoreCase))
                }).ToList();
            SummaryText.Text = $"{className} · {_items.Count} 名学生";
            QueueGrid.ItemsSource = _items;
            DeviceCombo.ItemsSource = FingerprintSelectionData.LoadOnlineCabinets();
            if (DeviceCombo.Items.Count > 0) DeviceCombo.SelectedIndex = 0;
            SelectNext();
        }

        private void EnrollNextButton_Click(object sender, RoutedEventArgs e)
        {
            if (DeviceCombo.SelectedItem is not FingerprintDeviceOption device)
            {
                MessageBox.Show("当前没有可用的在线采集柜机", "连续录入",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            ContinuousEnrollmentItem? item = NextPending();
            if (item == null)
            {
                StatusText.Text = "队列已处理完成";
                return;
            }

            DeviceCombo.IsEnabled = false;
            item.StatusText = "录入中";
            RefreshQueue(item);
            var window = new EnrollFingerprintWindow(device.DeviceId, item.User.UserId)
            {
                Owner = this
            };
            window.ShowDialog();
            if (window.EnrolledFingerprintId > 0)
            {
                item.StatusText = $"完成 #{window.EnrolledFingerprintId}";
                item.Completed = true;
                item.ExistingFingerprintCount++;
                StatusText.Text = $"{item.Name} 录入完成，可继续下一位";
            }
            else
            {
                item.StatusText = "未完成，可重试";
                StatusText.Text = $"{item.Name} 本次未完成";
            }
            RefreshQueue(NextPending());
        }

        private void SkipButton_Click(object sender, RoutedEventArgs e)
        {
            ContinuousEnrollmentItem? item = QueueGrid.SelectedItem as ContinuousEnrollmentItem ?? NextPending();
            if (item == null || item.Completed) return;
            item.Completed = true;
            item.StatusText = "已跳过";
            RefreshQueue(NextPending());
        }

        private ContinuousEnrollmentItem? NextPending() => _items.FirstOrDefault(item => !item.Completed);

        private void SelectNext()
        {
            RefreshQueue(NextPending());
            if (DeviceCombo.Items.Count == 0)
            {
                StatusText.Text = "没有在线柜机，暂时不能开始采集";
                EnrollNextButton.IsEnabled = false;
            }
        }

        private void RefreshQueue(ContinuousEnrollmentItem? selected)
        {
            QueueGrid.ItemsSource = null;
            QueueGrid.ItemsSource = _items;
            QueueGrid.SelectedItem = selected;
            if (selected != null) QueueGrid.ScrollIntoView(selected);
            int completed = _items.Count(item => item.Completed);
            ProgressText.Text = $"{completed}/{_items.Count}";
            EnrollNextButton.Content = completed >= _items.Count ? "已完成" : "录入下一位";
            EnrollNextButton.IsEnabled = completed < _items.Count && DeviceCombo.Items.Count > 0;
            SkipButton.IsEnabled = completed < _items.Count;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }

    public sealed class ContinuousEnrollmentItem
    {
        public int Sequence { get; init; }
        public User User { get; init; } = new();
        public string Name => string.IsNullOrWhiteSpace(User.Name) ? User.UserId : User.Name;
        public string UserId => User.UserId;
        public int ExistingFingerprintCount { get; set; }
        public string ExistingCountText => $"{ExistingFingerprintCount} 枚";
        public string StatusText { get; set; } = "待录入";
        public bool Completed { get; set; }
    }
}
