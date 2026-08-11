using System.Windows;

namespace CabinetLock
{
    /// <summary>
    /// V2.7 设备专属副指纹录入窗口。
    /// 流程：选择柜子+用户 -> 发送 ADD_BACKUP_FINGERPRINT -> 监听 ENROLL_PROGRESS ->
    ///       录入成功后弹窗二选一：①覆盖全局主指纹 ②仅作为本机备用指纹
    /// </summary>
    public partial class BackupFingerprintWindow : BorderlessWindow
    {
        private bool _enrolling;
        private string? _lastDeviceId;
        private string? _lastEnrollmentPhase;
        private CancellationTokenSource? _progressPromptCts;

        public BackupFingerprintWindow()
        {
            InitializeComponent();
            LoadData();
        }

        public BackupFingerprintWindow(string? presetDeviceId, string? presetUserId = null)
            : this()
        {
            SelectById(DeviceCombo, presetDeviceId);
            SelectById(UserCombo, presetUserId);
        }

        private static void SelectById(System.Windows.Controls.ComboBox comboBox, string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return;
            for (int index = 0; index < comboBox.Items.Count; index++)
            {
                object item = comboBox.Items[index];
                string? itemId = item switch
                {
                    DeviceItem device => device.DeviceId,
                    UserItem user => user.UserId,
                    _ => null
                };
                if (!string.Equals(itemId, id, StringComparison.OrdinalIgnoreCase)) continue;
                comboBox.SelectedIndex = index;
                break;
            }
        }

        private void LoadData()
        {
            // 加载在线柜子列表（非根节点）
            try
            {
                var devices = App.MeshBridge.GetOnlineDevices()
                    .Where(d => d.IsOnline && !d.IsRoot)
                    .Select(d => new DeviceItem { DeviceId = d.DeviceId, DisplayText = $"{d.DeviceName} ({d.DeviceId})" })
                    .ToList();
                DeviceCombo.ItemsSource = devices;
                if (devices.Count > 0) DeviceCombo.SelectedIndex = 0;
            }
            catch { /* 忽略 */ }

            // 加载可见用户列表
            try
            {
                var users = App.UserService.GetVisibleUsers()
                    .Where(u => u.Enabled)
                    .Select(u => new UserItem { UserId = u.UserId, DisplayText = $"{u.Name} ({u.DisplayId}) [{u.Role}]" })
                    .ToList();
                UserCombo.ItemsSource = users;
                if (users.Count > 0) UserCombo.SelectedIndex = 0;
            }
            catch { /* 忽略 */ }
        }

        private void ShowEnrollmentProgress(string phase, int step, int total, string hint)
        {
            _lastEnrollmentPhase = phase;
            _progressPromptCts?.Cancel();
            var cancellation = new CancellationTokenSource();
            _progressPromptCts = cancellation;
            _ = ShowEnrollmentProgressAsync(phase, step, total, hint, cancellation.Token);
        }

        private async Task ShowEnrollmentProgressAsync(
            string phase, int step, int total, string hint, CancellationToken cancellationToken)
        {
            try
            {
                int delay = FingerprintEnrollmentPrompts.GetDisplayDelayMilliseconds(phase);
                if (delay > 0) await Task.Delay(delay, cancellationToken);
                await Dispatcher.InvokeAsync(() =>
                {
                    EnrollProgress.Maximum = total;
                    EnrollProgress.Value = FingerprintEnrollmentPrompts.GetProgressValue(
                        phase, step, total);
                    ProgressHint.Text = hint;
                    ProgressDetail.Text = FormatProgressDetail(phase, step, total);
                    StepIcon.Text = FingerprintEnrollmentPrompts.IsVerificationPhase(phase)
                        ? "\uE73E" : "\uE928";
                });
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static string FormatProgressDetail(string phase, int step, int total) => phase switch
        {
            "place_1" or "lift_1" => "采集 1/4",
            "place_2" or "lift_2" => "采集 2/4",
            "place_3" or "lift_3" => "采集 3/4",
            "place_4" or "store" or "storing" => "采集 4/4",
            "verify_lift_1" or "verify_place_1" or "verify_retry_lift_1" or "verify_1" => "验证 1/2",
            "verify_lift_2" or "verify_place_2" or "verify_retry_lift_2" or "verify_2" => "验证 2/2",
            _ => $"进度 {step}/{total}"
        };

        private async void EnrollButton_Click(object sender, RoutedEventArgs e)
        {
            string? deviceId = DeviceCombo.SelectedValue as string;
            string? userId = UserCombo.SelectedValue as string;
            if (string.IsNullOrEmpty(deviceId))
            {
                MessageBox.Show("请选择目标柜子", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (string.IsNullOrEmpty(userId))
            {
                MessageBox.Show("请选择目标用户", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            EnrollButton.IsEnabled = false;
            DeviceCombo.IsEnabled = false;
            UserCombo.IsEnabled = false;
            _enrolling = true;
            _lastDeviceId = deviceId;
            _lastEnrollmentPhase = null;
            CancelButton.Content = "取消录入";
            _progressPromptCts?.Cancel();
            EnrollProgress.Value = 0;
            StepIcon.Text = "\uE928";
            ProgressHint.Text = "正在发送录入指令";
            ProgressDetail.Text = "请将手指放在指纹模块附近，等待开始提示";
            ResultText.Text = "";

            try
            {
                var result = await App.CommandService.EnrollBackupFingerprintAsync(
                    deviceId, userId,
                    onProgress: ShowEnrollmentProgress);
                _progressPromptCts?.Cancel();

                if (!result.Success)
                {
                    StepIcon.Text = "\uE783";
                    ResultText.Text = FingerprintEnrollmentPrompts.EnhanceFailureForPhase(
                        result.ErrorMessage, _lastEnrollmentPhase);
                    ProgressHint.Text = "录入未完成";
                    ProgressDetail.Text = "请根据提示检查手指位置后重新尝试";
                    return;
                }

                EnrollProgress.Value = EnrollProgress.Maximum;
                StepIcon.Text = "\uE73E";
                ProgressHint.Text = "两次验证通过，副指纹录入成功";
                ProgressDetail.Text = "指纹已保存到当前柜机";
                ResultText.Text = $"本机槽位 ID: {result.FingerprintId}";

                // 弹窗二选一：覆盖全局主指纹 / 仅作为本机备用
                ShowPostEnrollChoice(deviceId, userId, result.FingerprintId);
            }
            catch (Exception ex)
            {
                StepIcon.Text = "\uE783";
                ProgressHint.Text = "录入异常";
                ProgressDetail.Text = "请检查柜机连接后重新尝试";
                ResultText.Text = FingerprintEnrollmentPrompts.EnhanceFailureForPhase(
                    ex.Message, _lastEnrollmentPhase);
            }
            finally
            {
                _enrolling = false;
                _progressPromptCts?.Cancel();
                DeviceCombo.IsEnabled = true;
                UserCombo.IsEnabled = true;
                EnrollButton.IsEnabled = true;
                CancelButton.Content = "关闭";
            }
        }

        /// <summary>
        /// 副指纹录入成功后的二选一对话框（需求 §2.2）。
        /// ① 覆盖全局主指纹：更新 SD 卡 users 表的 fingerprint_id，并全局下发同步
        /// ② 仅作为本机备用指纹：不修改全局数据，保留柜子本地映射
        /// </summary>
        private void ShowPostEnrollChoice(string deviceId, string userId, int localFpId)
        {
            string msg = $"副指纹已录入本机（槽位 {localFpId}）。\n\n" +
                         "请选择后续处理方式：\n" +
                         "  是 = 覆盖全局主指纹（更新 SD 卡 users 表并全局下发）\n" +
                         "  否 = 仅作为本机备用指纹（不影响其他设备）\n" +
                         "  取消 = 暂不处理，稍后可手动同步";
            var choice = MessageBox.Show(msg, "副指纹处理方式", MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question, MessageBoxResult.No);

            if (choice == MessageBoxResult.Yes)
            {
                // 覆盖全局主指纹
                try
                {
                    bool ok = App.UserService.AssignFingerprint(userId, localFpId);
                    if (ok)
                    {
                        ResultText.Text += $"\n已覆盖全局主指纹 (fp_id={localFpId})，在线柜立即处理，离线柜已排队";
                    }
                    else
                    {
                        ResultText.Text += "\n覆盖全局主指纹失败：指纹 ID 可能已被其他用户占用";
                    }
                }
                catch (Exception ex)
                {
                    ResultText.Text += "\n覆盖全局主指纹异常：" + ex.Message;
                }
            }
            else if (choice == MessageBoxResult.No)
            {
                // 仅作为本机备用，无需操作
                ResultText.Text += "\n已保留为本机备用指纹，不影响全局主指纹";
            }
            // Cancel: 暂不处理
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_enrolling)
            {
                Close();
                return;
            }
            if (MessageBox.Show("确定取消本次副指纹录入？", "取消录入",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            SendCancelEnroll();
            ProgressHint.Text = "正在取消本次录入";
            ProgressDetail.Text = "请稍候，柜机会清理本次临时数据";
        }

        private void SendCancelEnroll()
        {
            if (string.IsNullOrWhiteSpace(_lastDeviceId)) return;
            try
            {
                App.MeshBridge.SendToDevice(_lastDeviceId,
                    Message.Create(Protocol.CmdCancelEnroll, _lastDeviceId));
            }
            catch
            {
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_enrolling)
            {
                if (MessageBox.Show("录入进行中，关闭将取消本次录入。确定关闭？", "关闭确认",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
                SendCancelEnroll();
            }
            _progressPromptCts?.Cancel();
            base.OnClosing(e);
        }

        private class DeviceItem
        {
            public string DeviceId { get; set; } = "";
            public string DisplayText { get; set; } = "";
        }

        private class UserItem
        {
            public string UserId { get; set; } = "";
            public string DisplayText { get; set; } = "";
        }
    }
}
