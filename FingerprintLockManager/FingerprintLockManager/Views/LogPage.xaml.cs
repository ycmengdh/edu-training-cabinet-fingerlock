using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FingerprintLockManager
{
    /// <summary>
    /// 日志查看页面
    /// 日志列表展示、按设备/用户/时间范围筛选、导出CSV、刷新
    /// </summary>
    public partial class LogPage : Page
    {
        public LogPage()
        {
            InitializeComponent();
            Loaded += async (s, e) => await LoadLogsAsync();
        }

        /// <summary>根据筛选条件加载日志</summary>
        private async Task LoadLogsAsync()
        {
            string? deviceId = string.IsNullOrWhiteSpace(DeviceIdFilter.Text) ? null : DeviceIdFilter.Text.Trim();
            string? userId = string.IsNullOrWhiteSpace(UserIdFilter.Text) ? null : UserIdFilter.Text.Trim();

            DateTime? startTime = StartDatePicker.SelectedDate;
            DateTime? endTime = EndDatePicker.SelectedDate;
            // 结束时间包含当天整天
            if (endTime.HasValue)
            {
                endTime = endTime.Value.Date.AddDays(1).AddSeconds(-1);
            }

            SetBusy(true, "正在读取根节点日志");
            try
            {
                var logs = await Task.Run(() =>
                    App.LogService.QueryLogs(deviceId, userId, startTime, endTime, 2000));
                LogDataGrid.ItemsSource = logs;
                PageStatusText.Text = $"当前显示 {logs.Count} 条记录";
            }
            catch (RootDataUnavailableException ex)
            {
                LogDataGrid.ItemsSource = null;
                PageStatusText.Text = ex.Message;
            }
            finally
            {
                SetBusy(false);
            }
        }

        /// <summary>查询按钮</summary>
        private async void QueryButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadLogsAsync();
        }

        /// <summary>刷新按钮</summary>
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            await LoadLogsAsync();
        }

        /// <summary>导出CSV</summary>
        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var logs = LogDataGrid.ItemsSource as List<LogEntry>;
            if (logs == null || logs.Count == 0)
            {
                MessageBox.Show("没有可导出的日志数据", "提示");
                return;
            }

            var dialog = new SaveFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*",
                FileName = $"日志_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (dialog.ShowDialog() != true) return;

            try
            {
                // 使用 UTF-8 BOM，确保 Excel 正确识别中文
                var sb = new StringBuilder();
                // 表头
                sb.AppendLine("时间,设备ID,用户ID,锁号,操作,结果,原因");
                // 数据行
                foreach (var log in logs)
                {
                    sb.AppendLine(string.Join(",",
                        CsvEscape(log.CreateTime.ToString("yyyy-MM-dd HH:mm:ss")),
                        CsvEscape(log.DeviceId ?? ""),
                        CsvEscape(log.UserId ?? ""),
                        log.LockId,
                        CsvEscape(log.Action ?? ""),
                        CsvEscape(log.Result ?? ""),
                        CsvEscape(log.Reason ?? "")));
                }

                File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(true));

                MessageBox.Show($"导出成功，共 {logs.Count} 条记录\n{dialog.FileName}", "成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>CSV 字段转义（包含逗号、引号、换行时用双引号包裹）</summary>
        private static string CsvEscape(string field)
        {
            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
            {
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            }
            return field;
        }

        private void SetBusy(bool busy, string? status = null)
        {
            QueryButton.IsEnabled = !busy;
            RefreshButton.IsEnabled = !busy;
            ExportButton.IsEnabled = !busy;
            if (!string.IsNullOrEmpty(status)) PageStatusText.Text = status;
        }
    }
}
