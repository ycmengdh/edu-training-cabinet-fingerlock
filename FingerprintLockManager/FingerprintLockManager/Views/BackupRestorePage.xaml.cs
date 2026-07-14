using System.Windows;
using System.Windows.Controls;

namespace FingerprintLockManager
{
    /// <summary>
    /// 备份与还原页面（需求 11）
    ///
    /// 需求 11：每次系统用户操作前自动备份上位机数据目录。
    /// 备份仅用于还原，根节点数据为主。分配成功才更新根节点。
    ///
    /// 备份模式：
    ///   - 自动备份（操作前触发）：仅业务表 JSON，快速
    ///   - 手动备份（本页"立即手动备份"按钮）：业务表 JSON + 指纹模板二进制文件，完整恢复点
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
                IsFullBackup = (r.Tables ?? "").Contains("templates"),
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

        /// <summary>立即手动备份（完整备份：业务表 + 指纹模板文件）</summary>
        private async void ManualBackupButton_Click(object sender, RoutedEventArgs e)
        {
            ManualBackupButton.IsEnabled = false;
            ManualBackupButton.Content = "备份中...";

            try
            {
                if (!App.SdStorageService.IsAvailable)
                {
                    MessageBox.Show("根节点未连接，无法创建完整备份。\n仅业务表 JSON 的自动备份在操作时会自动触发。",
                        "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var record = await App.BackupService.CreateFullBackupAsync("手动完整备份", App.CurrentUser?.UserId);
                if (record != null)
                {
                    bool hasTemplates = (record.Tables ?? "").Contains("templates");
                    string msg = $"完整备份成功！\n备份 ID：{record.BackupId}\n大小：{FormatFileSize(record.FileSize)}";
                    if (hasTemplates)
                    {
                        msg += "\n含指纹模板二进制文件，可作为完整恢复点。";
                    }
                    else
                    {
                        msg += "\n（未包含指纹模板，可能无指纹数据或根节点 SD 卡异常）";
                    }
                    MessageBox.Show(msg, "成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    LoadBackups();
                }
                else
                {
                    MessageBox.Show("备份失败，请查看日志", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"备份异常：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ManualBackupButton.IsEnabled = true;
                ManualBackupButton.Content = "立即手动备份";
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

            // 查找选中的备份记录，判断是否为完整备份
            var records = App.BackupService.GetBackupRecords();
            var target = records.FirstOrDefault(r => r.BackupId == backupId);
            bool isFull = target != null && (target.Tables ?? "").Contains("templates");

            string warn = $"确认从备份 {backupId} 还原数据？\n\n";
            if (isFull)
            {
                warn += "该备份为完整备份（含指纹模板文件）。\n";
            }
            else
            {
                warn += "⚠ 该备份仅含业务表 JSON，不含指纹模板文件。\n" +
                        "还原后指纹模板文件将保持当前状态（不会恢复）。\n";
            }
            warn += "\n注意：\n" +
                    "- 还原将覆盖根节点 SD 卡上的所有业务数据！\n" +
                    "- 还原前会自动备份当前状态。\n" +
                    "- 还原后请刷新或重启上位机以查看最新数据。";

            var result = MessageBox.Show(warn, "确认还原", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;

            btn.IsEnabled = false;
            btn.Content = "还原中...";

            try
            {
                var restoreResult = await App.BackupService.RestoreFromBackupAsync(backupId, App.CurrentUser?.UserId);
                if (restoreResult == null || !restoreResult.Success)
                {
                    MessageBox.Show($"还原失败：{restoreResult?.ErrorMessage ?? "未知错误"}",
                        "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    string msg = "还原成功！\n\n";
                    msg += $"- 业务表：{restoreResult.TableCount} 张已写回根节点\n";
                    if (restoreResult.TemplateTotal > 0)
                    {
                        msg += $"- 指纹模板：{restoreResult.TemplateRestored}/{restoreResult.TemplateTotal} 枚已上传";
                        if (restoreResult.TemplateFailed > 0)
                        {
                            msg += $"（{restoreResult.TemplateFailed} 枚失败）";
                        }
                        msg += "\n";
                    }
                    else
                    {
                        msg += "- 指纹模板：本备份未包含模板文件\n";
                    }
                    msg += "\n建议重新打开各管理页面以刷新数据。";
                    MessageBox.Show(msg, "还原成功", MessageBoxButton.OK, MessageBoxImage.Information);
                    LoadBackups();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"还原异常：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btn.IsEnabled = true;
                btn.Content = "还原";
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
            /// <summary>是否为完整备份（含指纹模板文件）</summary>
            public bool IsFullBackup { get; set; }
            /// <summary>备份类型显示文本</summary>
            public string BackupType => IsFullBackup ? "完整" : "快速";
            public DateTime CreateTime { get; set; }
        }
    }
}
