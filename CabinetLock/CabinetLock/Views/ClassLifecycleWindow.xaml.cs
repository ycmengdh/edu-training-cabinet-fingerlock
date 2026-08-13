using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;

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
            var skipped = new List<string>();
            var partial = new List<string>();
            StatusText.Text = _action == ClassLifecycleAction.Delete
                ? "正在准备删除数据"
                : "正在准备班级数据";
            ProgressText.Text = _action == ClassLifecycleAction.Delete
                ? "正在读取学生、柜机绑定和指纹信息"
                : "正在读取班级与柜机信息";
            await Dispatcher.Yield(DispatcherPriority.Background);

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
                ClassLifecycleResult result = await Task.Run(() =>
                    App.ClassLifecycleService.ExecuteAsync(
                        classInfo.ClassId, _action, progress));
                _details.Add(result.IsPartial
                    ? $"部分完成 · {classInfo.Name}：{result.Message}"
                    : result.WasSkipped
                        ? $"跳过 · {classInfo.Name}：{result.Message}"
                    : result.Success
                        ? $"完成 · {classInfo.Name}：{result.Message}"
                        : $"失败 · {classInfo.Name}：{result.Message}");
                foreach (string failure in result.Failures)
                    _details.Add("    " + failure.Replace(Environment.NewLine, " "));
                if (result.Success) DataChanged = true;
                if (result.IsPartial) partial.Add(classInfo.Name);
                else if (result.WasSkipped) skipped.Add(classInfo.Name);
                else if (!result.Success) failures.Add(classInfo.Name);
            }

            OperationProgress.Value = failures.Count == 0 ? 100 : OperationProgress.Value;
            int completedCount = _classes.Count - partial.Count - skipped.Count - failures.Count;
            StatusText.Text = partial.Count == 0 && skipped.Count == 0 && failures.Count == 0
                ? "全部操作已完成"
                : $"处理完成：完成 {completedCount} 个，部分完成 {partial.Count} 个，" +
                  $"跳过 {skipped.Count} 个，失败 {failures.Count} 个";
            ProgressText.Text = partial.Count > 0 || skipped.Count > 0
                ? "未删除的学生及其班级已保留；柜机在线后可重新删除"
                : failures.Count == 0
                    ? "所有步骤均已收到柜机确认"
                    : "失败操作不会提交最终删除或停用状态";
            RetryButton.Visibility = failures.Count == 0 && skipped.Count == 0 && partial.Count == 0
                ? Visibility.Collapsed : Visibility.Visible;
            CloseButton.IsEnabled = true;
            _running = false;
        }

        private async void RetryButton_Click(object sender, RoutedEventArgs e) => await ExecuteAsync();
        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
