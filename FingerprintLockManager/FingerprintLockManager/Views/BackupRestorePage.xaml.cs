using System.Windows;
using System.Windows.Controls;

namespace FingerprintLockManager
{
    /// <summary>
    /// 备份与还原页面（需求 11）
    ///
    /// 需求 11：每次系统用户操作前自动备份上位机数据目录。
    /// 备份仅用于还原，根节点数据为主。分配成功才更新根节点。
    /// </summary>
    public partial class BackupRestorePage : Page
    {
        public BackupRestorePage()
        {
            InitializeComponent();
            Loaded += (s, e) => LoadBackups();
        }

        /// <summary>加载备份记录列表</summary>
        private void LoadBackups()
        {
            var records = App.BackupService.GetBackupRecords();
            var list = records.Select(r => new BackupDisplay
            {
                BackupId = r.BackupId,
                TriggerAction = r.TriggerAction,
                OperatorUserId = r.OperatorUserId ?? "",
                FilePath = r.FilePath,
                FileSize = r.FileSize,
                FileSizeText = FormatFileSize(r.FileSize),
                Tables = r.Tables ?? "",
                CreateTime = r.CreateTime
            }).ToList();
            BackupDataGrid.ItemsSource = list;
        }

        /// <summary>格式化文件大小</summary>
        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / 1024.0 / 1024.0:F2} MB";
        }

        /// <summary>立即手动备份</summary>
        private void ManualBackupButton_Click(object sender, RoutedEventArgs e)
        {
            var record = App.BackupService.BackupBeforeAction("手动备份", App.CurrentUser?.UserId);
            if (record != null)
            {
                MessageBox.Show($"手动备份成功！\n备份 ID：{record.BackupId}", "成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                LoadBackups();
            }
            else
            {
                MessageBox.Show("备份失败，请查看日志", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>清理过期备份</summary>
        private void CleanupButton_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show("确认清理过期备份（保留最近 30 个）？",
                "确认清理", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            App.BackupService.CleanupOldBackups(30);
            MessageBox.Show("清理完成", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            LoadBackups();
        }

        /// <summary>刷新列表</summary>
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            LoadBackups();
        }

        /// <summary>还原按钮</summary>
        private async void RestoreButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            if (btn.Tag is not string backupId) return;

            var result = MessageBox.Show(
                $"确认从备份 {backupId} 还原数据？\n\n" +
                "注意：还原将覆盖根节点 SD 卡上的所有业务数据！\n" +
                "还原前会自动备份当前状态。\n" +
                "还原后请重新启动上位机以刷新数据。",
                "确认还原", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            try
            {
                string? err = await App.BackupService.RestoreFromBackupAsync(backupId, App.CurrentUser?.UserId);
                if (err == null)
                {
                    MessageBox.Show("还原成功！请重新启动上位机以刷新所有数据。", "成功",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                    LoadBackups();
                }
                else
                {
                    MessageBox.Show($"还原失败：{err}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"还原异常：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>备份展示包装类</summary>
        private class BackupDisplay
        {
            public string BackupId { get; set; }
            public string TriggerAction { get; set; }
            public string OperatorUserId { get; set; }
            public string FilePath { get; set; }
            public long FileSize { get; set; }
            public string FileSizeText { get; set; }
            public string Tables { get; set; }
            public DateTime CreateTime { get; set; }
        }
    }
}
