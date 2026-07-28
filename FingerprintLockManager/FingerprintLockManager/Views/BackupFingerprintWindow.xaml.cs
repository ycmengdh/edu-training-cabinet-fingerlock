using System.Windows;

namespace FingerprintLockManager
{
    /// <summary>
    /// V2.7 设备专属副指纹录入窗口。
    /// 流程：选择柜子+用户 -> 发送 ADD_BACKUP_FINGERPRINT -> 监听 ENROLL_PROGRESS ->
    ///       录入成功后弹窗二选一：①覆盖全局主指纹 ②仅作为本机备用指纹
    /// </summary>
    public partial class BackupFingerprintWindow : BorderlessWindow
    {
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
                    .Select(u => new UserItem { UserId = u.UserId, DisplayText = $"{u.Name} ({u.UserId}) [{u.Role}]" })
                    .ToList();
                UserCombo.ItemsSource = users;
                if (users.Count > 0) UserCombo.SelectedIndex = 0;
            }
            catch { /* 忽略 */ }
        }

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
            EnrollProgress.Value = 0;
            ProgressHint.Text = "正在发送录入指令...";
            ResultText.Text = "";

            try
            {
                var result = await App.CommandService.EnrollBackupFingerprintAsync(
                    deviceId, userId,
                    onProgress: (phase, step, total, hint) =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            EnrollProgress.Value = step;
                            EnrollProgress.Maximum = total;
                            ProgressHint.Text = hint;
                        });
                    });

                if (!result.Success)
                {
                    ResultText.Text = "录入失败：" + result.ErrorMessage;
                    ProgressHint.Text = "录入未完成";
                    return;
                }

                ProgressHint.Text = "副指纹录入成功";
                ResultText.Text = $"本机槽位 ID: {result.FingerprintId}";

                // 弹窗二选一：覆盖全局主指纹 / 仅作为本机备用
                ShowPostEnrollChoice(deviceId, userId, result.FingerprintId);
            }
            catch (Exception ex)
            {
                ResultText.Text = "异常：" + ex.Message;
            }
            finally
            {
                EnrollButton.IsEnabled = true;
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
                        // 触发全局权限同步，让其他柜子也更新该用户的指纹 ID
                        var syncResult = App.CabinetSyncService.SyncAllPermissions();
                        ResultText.Text += $"\n已覆盖全局主指纹 (fp_id={localFpId})，同步：{syncResult}";
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
            Close();
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
