using System.Diagnostics;
using System.IO;
using System.Windows;

namespace FingerprintLockManager
{
    public partial class ExitBusinessSyncWindow : BorderlessWindow
    {
        private const int MaxAttempts = 3;
        private readonly CancellationTokenSource _cancellation = new();
        private bool _started;
        private bool _closed;
        private bool _uploading;
        private string _lastError = "";

        public bool ExitAllowed { get; private set; }

        public ExitBusinessSyncWindow(string reason)
        {
            InitializeComponent();
            StatusDetailText.Text = reason;
            ContentRendered += ExitBusinessSyncWindow_ContentRendered;
            Closed += (_, _) =>
            {
                _closed = true;
                try { _cancellation.Cancel(); } catch { }
            };
        }

        private async void ExitBusinessSyncWindow_ContentRendered(object? sender, EventArgs e)
        {
            if (_started) return;
            _started = true;
            await UploadWithRetriesAsync();
        }

        private async Task UploadWithRetriesAsync()
        {
            if (_uploading || _closed || _cancellation.IsCancellationRequested) return;
            _uploading = true;
            RetryButton.Visibility = Visibility.Collapsed;
            ForceExitButton.Visibility = Visibility.Collapsed;
            FailurePanel.Visibility = Visibility.Collapsed;
            UploadProgress.IsIndeterminate = true;
            _lastError = "";
            try
            {
                for (int attempt = 1; attempt <= MaxAttempts; attempt++)
                {
                    if (_closed || _cancellation.IsCancellationRequested) return;
                    AttemptText.Text = $"第 {attempt}/{MaxAttempts} 次";
                    StatusTitleText.Text = attempt == 1 ? "正在上传业务数据" : "正在重新上传";
                    StatusDetailText.Text = "正在连接根节点 SD";
                    LastErrorText.Text = "";

                    var progress = new Progress<string>(message =>
                    {
                        if (!_closed) StatusDetailText.Text = message;
                    });

                    try
                    {
                        SdBusinessSyncService.SyncResult result =
                            await App.SdBusinessSyncService.PushBusinessToSdAsync(
                                progress, timeoutMs: 8000,
                                cancellationToken: _cancellation.Token);
                        if (_closed) return;
                        if (result.Success)
                        {
                            StatusTitleText.Text = "业务数据上传完成";
                            StatusDetailText.Text = string.IsNullOrWhiteSpace(result.Message)
                                ? "SD 已确认接收，应用即将退出"
                                : result.Message + "，应用即将退出";
                            AttemptText.Text = "已完成";
                            UploadProgress.IsIndeterminate = false;
                            UploadProgress.Value = 100;
                            CancelExitButton.IsEnabled = false;
                            ExitAllowed = true;
                            await Task.Delay(650);
                            if (!_closed) DialogResult = true;
                            return;
                        }

                        _lastError = string.IsNullOrWhiteSpace(result.Message)
                            ? "SD 未确认业务数据上传"
                            : result.Message;
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception ex)
                    {
                        _lastError = ex.Message;
                    }

                    if (_closed) return;
                    LastErrorText.Text = $"第 {attempt} 次失败：{_lastError}";
                    if (attempt < MaxAttempts)
                    {
                        StatusDetailText.Text = "上传失败，稍后自动重试";
                        try { await Task.Delay(900, _cancellation.Token); }
                        catch (OperationCanceledException) { return; }
                    }
                }

                ShowForceExitState();
            }
            finally
            {
                _uploading = false;
            }
        }

        private void ShowForceExitState()
        {
            if (_closed) return;
            StatusTitleText.Text = "连续 3 次上传失败";
            StatusDetailText.Text = "请选择取消退出并检查设备，或备份本机业务库后强制退出";
            AttemptText.Text = "未上传";
            UploadProgress.IsIndeterminate = false;
            UploadProgress.Value = 0;
            FailurePanel.Visibility = Visibility.Visible;
            RetryButton.Visibility = Visibility.Visible;
            ForceExitButton.Visibility = Visibility.Visible;
            LastErrorText.Text = string.IsNullOrWhiteSpace(_lastError)
                ? "根节点 SD 未确认接收业务数据"
                : _lastError;
        }

        private async void RetryButton_Click(object sender, RoutedEventArgs e)
        {
            await UploadWithRetriesAsync();
        }

        private void CancelExitButton_Click(object sender, RoutedEventArgs e)
        {
            try { _cancellation.Cancel(); } catch { }
            ExitAllowed = false;
            DialogResult = false;
        }

        private void ForceExitButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show(
                    "业务数据尚未上传到 SD。\n\n" +
                    "系统将生成本地快照，并打开目录选中 business.db。请自行复制备份后再继续使用设备。\n\n" +
                    "仍要强制退出吗？",
                    "确认强制退出", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
                return;

            string databasePath = BusinessDatabaseBackupService.MainDbPath;
            try
            {
                BusinessDatabase.Checkpoint();
                BusinessDatabaseBackupService.BackupCurrent(
                    "exit_failed_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            }
            catch
            {
            }

            try
            {
                if (File.Exists(databasePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{databasePath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = SqlitePaths.GetDataDirectory(),
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
            }

            ExitAllowed = true;
            DialogResult = true;
        }
    }
}
