using System.Windows;

namespace FingerprintLockManager
{
    public partial class SyncQueueWindow : BorderlessWindow
    {
        public SyncQueueWindow()
        {
            InitializeComponent();
            Loaded += (_, _) => Reload();
        }

        private void Reload()
        {
            List<CabinetSyncJob> open = App.CabinetSyncQueueService.GetOpen().ToList();
            JobGrid.ItemsSource = open.Select(job => new SyncQueueRow(job)).ToList();
            int failed = open.Count(job =>
                string.Equals(job.State, "failed", StringComparison.OrdinalIgnoreCase));
            StatusText.Text = open.Count == 0
                ? "当前没有待同步任务。用户可多指纹入库；每柜每用户下发仅一枚，节省约 200 槽。"
                : $"待处理 {open.Count} 项" + (failed > 0 ? $"（失败待重试 {failed}）" : "") +
                  " · 每柜每用户只同步一枚当前指纹";
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e) => Reload();

        private async void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            RetryButton.IsEnabled = false;
            RefreshButton.IsEnabled = false;
            StatusText.Text = "正在触发队列处理…";
            try
            {
                int before = App.CabinetSyncQueueService.CountOpen();
                int done = await Task.Run(() =>
                        App.CabinetSyncQueueService.ProcessPendingAsync(CancellationToken.None))
                    .ConfigureAwait(true);
                int after = App.CabinetSyncQueueService.CountOpen();
                if (done > 0 || after < before)
                    AppToast.Success(after == 0 ? "待同步队列已清空" : $"已处理部分任务，剩余 {after}");
                else if (before == 0)
                    AppToast.Info("当前没有可处理的待同步任务");
                else
                    AppToast.Warning("本轮未能完成待同步（柜可能离线）");
            }
            catch (Exception ex)
            {
                StatusText.Text = "重试异常：" + ex.Message;
                AppToast.Error("队列重试失败：" + ex.Message);
            }
            finally
            {
                RetryButton.IsEnabled = true;
                RefreshButton.IsEnabled = true;
                Reload();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private sealed class SyncQueueRow
        {
            public SyncQueueRow(CabinetSyncJob job)
            {
                StatusText = job.StatusText;
                KindText = job.JobKind switch
                {
                    "cabinet" => "整柜",
                    "user" => "用户",
                    _ => job.JobKind
                };
                UserId = string.IsNullOrWhiteSpace(job.UserId) ? "—" : job.UserId;
                DeviceId = job.DeviceId;
                Reason = job.Reason;
                AttemptCount = job.AttemptCount;
                LastError = job.LastError;
                UpdateTime = job.UpdateTime;
            }

            public string StatusText { get; }
            public string KindText { get; }
            public string UserId { get; }
            public string DeviceId { get; }
            public string Reason { get; }
            public int AttemptCount { get; }
            public string LastError { get; }
            public DateTime UpdateTime { get; }
        }
    }
}
