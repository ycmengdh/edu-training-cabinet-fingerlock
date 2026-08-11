using System.Windows;

namespace CabinetLock
{
    public partial class MaintenanceModeWindow : Window
    {
        private readonly IReadOnlyList<Device> _devices;
        private bool _busy;

        public MaintenanceModeWindow(IReadOnlyList<Device> devices)
        {
            _devices = devices?.Where(device => device.IsOnline)
                .DistinctBy(device => device.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToArray() ?? throw new ArgumentNullException(nameof(devices));
            if (_devices.Count == 0)
                throw new ArgumentException("至少需要一台在线柜机", nameof(devices));
            InitializeComponent();
            DeviceText.Text = _devices.Count == 1
                ? $"{_devices[0].DisplayIdentity} · {_devices[0].DeviceId}"
                : $"已选择 {_devices.Count} 台在线柜机";
            DeviceText.ToolTip = string.Join("\n", _devices.Select(device =>
                $"{device.DisplayIdentity} · {device.DeviceId}"));
            Loaded += (_, _) => App.MaintenanceService.StateChanged += OnStateChanged;
            Closed += (_, _) => App.MaintenanceService.StateChanged -= OnStateChanged;
            RefreshState();
        }

        private int SelectedMask =>
            (Lock0Check.IsChecked == true ? 1 : 0) |
            (Lock1Check.IsChecked == true ? 2 : 0) |
            (Lock2Check.IsChecked == true ? 4 : 0) |
            (Lock3Check.IsChecked == true ? 8 : 0);

        private async void EnterButton_Click(object sender, RoutedEventArgs e)
        {
            int mask = SelectedMask;
            if (mask == 0)
            {
                StatusText.Text = "至少选择一把允许开启的锁";
                return;
            }
            SetBusy(true, "正在发送维护模式指令");
            try
            {
                (int succeeded, string[] failed) = await ExecuteBatchAsync(device =>
                    App.MaintenanceService.EnterAsync(device.DeviceId, mask));
                StatusText.Text = FormatBatchResult("进入维护模式", succeeded, failed);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(true, "正在退出维护模式");
            try
            {
                (int succeeded, string[] failed) = await ExecuteBatchAsync(device =>
                    App.MaintenanceService.ExitAsync(device.DeviceId));
                StatusText.Text = FormatBatchResult("退出维护模式", succeeded, failed);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void OnStateChanged(string deviceId)
        {
            if (_busy || !_devices.Any(device => string.Equals(
                    device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))) return;
            Dispatcher.BeginInvoke(new Action(RefreshState));
        }

        private void RefreshState()
        {
            foreach (Device device in _devices) App.MaintenanceService.ApplyState(device);
            int active = _devices.Count(device => device.MaintenanceActive);
            StatusText.Text = active == 0
                ? "所选柜机当前均未进入维护模式"
                : $"所选 {_devices.Count} 台中，{active} 台处于维护模式";
        }

        private async Task<(int Succeeded, string[] Failed)> ExecuteBatchAsync(
            Func<Device, Task<CommandResult>> operation)
        {
            int succeeded = 0;
            var failed = new System.Collections.Concurrent.ConcurrentBag<string>();
            await Parallel.ForEachAsync(_devices,
                new ParallelOptions { MaxDegreeOfParallelism = 3 },
                async (device, _) =>
                {
                    try
                    {
                        CommandResult result = await operation(device).ConfigureAwait(false);
                        if (result.Success) Interlocked.Increment(ref succeeded);
                        else failed.Add(device.DisplayIdentity);
                    }
                    catch
                    {
                        failed.Add(device.DisplayIdentity);
                    }
                });
            return (succeeded, failed.OrderBy(name => name).ToArray());
        }

        private string FormatBatchResult(string action, int succeeded, IReadOnlyList<string> failed)
        {
            if (failed.Count == 0) return $"{_devices.Count} 台柜机已{action}";
            string failedNames = string.Join("、", failed.Take(3));
            if (failed.Count > 3) failedNames += $" 等 {failed.Count} 台";
            return $"{action}完成：成功 {succeeded} 台，失败 {failed.Count} 台（{failedNames}）";
        }

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            EnterButton.IsEnabled = !busy;
            ExitButton.IsEnabled = !busy;
            if (!string.IsNullOrWhiteSpace(status)) StatusText.Text = status;
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
