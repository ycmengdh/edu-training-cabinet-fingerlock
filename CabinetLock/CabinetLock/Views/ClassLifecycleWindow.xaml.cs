using System.Collections.ObjectModel;
using System.Windows;

namespace CabinetLock
{
    public partial class ClassLifecycleWindow : BorderlessWindow
    {
        private readonly IReadOnlyList<ClassInfo> _classes;
        private readonly ClassLifecycleAction _action;
        private readonly ObservableCollection<string> _details = new();
        private bool _running;

        public ClassLifecycleWindow(
            IReadOnlyList<ClassInfo> classes, ClassLifecycleAction action)
        {
            InitializeComponent();
            _classes = classes;
            _action = action;
            DetailList.ItemsSource = _details;
            TitleText.Text = action switch
            {
                ClassLifecycleAction.Enable => "启用班级并恢复柜机数据",
                ClassLifecycleAction.Disable => "停用班级并清理柜机数据",
                _ => classes.Count > 1 ? $"批量删除 {classes.Count} 个班级" : "删除班级及关联数据"
            };
            Loaded += async (_, _) => await ExecuteAsync();
        }

        public bool DataChanged { get; private set; }

        private async Task ExecuteAsync()
        {
            if (_running) return;
            _running = true;
            RetryButton.Visibility = Visibility.Collapsed;
            CloseButton.IsEnabled = false;
            _details.Clear();
            var failures = new List<string>();

            for (int index = 0; index < _classes.Count; index++)
            {
                ClassInfo classInfo = _classes[index];
                StatusText.Text = $"正在处理 {classInfo.Name}（{index + 1}/{_classes.Count}）";
                var progress = new Progress<ClassLifecycleProgress>(item =>
                {
                    double classBase = index * 100.0 / _classes.Count;
                    OperationProgress.Value = classBase + item.Percent / (double)_classes.Count;
                    ProgressText.Text = $"{classInfo.Name}：{item.Message}";
                });
                ClassLifecycleResult result = await App.ClassLifecycleService.ExecuteAsync(
                    classInfo.ClassId, _action, progress);
                _details.Add(result.Success
                    ? $"完成 · {classInfo.Name}：{result.Message}"
                    : $"失败 · {classInfo.Name}：{result.Message}");
                foreach (string failure in result.Failures)
                    _details.Add("    " + failure.Replace(Environment.NewLine, " "));
                if (result.Success) DataChanged = true;
                else failures.Add(classInfo.Name);
            }

            OperationProgress.Value = failures.Count == 0 ? 100 : OperationProgress.Value;
            StatusText.Text = failures.Count == 0
                ? "全部操作已完成"
                : $"{failures.Count} 个班级未完成，可检查设备后重试";
            ProgressText.Text = failures.Count == 0
                ? "所有步骤均已收到柜机确认"
                : "失败操作不会提交最终删除或停用状态";
            RetryButton.Visibility = failures.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            CloseButton.IsEnabled = true;
            _running = false;
        }

        private async void RetryButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync();
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
