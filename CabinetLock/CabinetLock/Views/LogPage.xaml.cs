using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace CabinetLock
{
    /// <summary>
    /// 日志查看页面：筛选、分页、失败原因聚合、导出/归档
    /// </summary>
    public partial class LogPage : Page
    {
        private const int PageSize = 100;
        private int _pageIndex;

        public LogPage()
        {
            InitializeComponent();
            Loaded += async (s, e) => await LoadLogsAsync();
        }

        private async Task LoadLogsAsync()
        {
            string? deviceId = string.IsNullOrWhiteSpace(DeviceIdFilter.Text) ? null : DeviceIdFilter.Text.Trim();
            string? userId = string.IsNullOrWhiteSpace(UserIdFilter.Text) ? null : UserIdFilter.Text.Trim();
            string? result = null;
            if (ResultFilterBox.SelectedItem is ComboBoxItem item)
                result = string.IsNullOrWhiteSpace(item.Tag?.ToString()) ? null : item.Tag!.ToString();

            DateTime? startTime = StartDatePicker.SelectedDate;
            DateTime? endTime = EndDatePicker.SelectedDate;
            if (endTime.HasValue)
                endTime = endTime.Value.Date.AddDays(1).AddSeconds(-1);

            SetBusy(true, "正在读取根节点日志");
            try
            {
                // V2.7：使用 Visible 变体实现教师数据范围隔离
                int total = await Task.Run(() =>
                    App.LogService.CountVisibleLogs(deviceId, userId, startTime, endTime, result));
                int totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
                if (_pageIndex >= totalPages) _pageIndex = totalPages - 1;
                if (_pageIndex < 0) _pageIndex = 0;

                var logs = await Task.Run(() =>
                    App.LogService.QueryVisibleLogs(deviceId, userId, startTime, endTime, result,
                        PageSize, _pageIndex * PageSize));
                var fails = await Task.Run(() =>
                    App.LogService.AggregateVisibleFailReasons(deviceId, userId, startTime, endTime));

                LogDataGrid.ItemsSource = logs;
                PageInfoText.Text = $"第 {_pageIndex + 1} / {totalPages} 页";
                PageStatusText.Text = $"共 {total} 条，当前页 {logs.Count} 条";
                FailAggregateText.Text = fails.Count == 0
                    ? "失败原因聚合：无失败记录"
                    : "失败原因聚合：" + string.Join("；", fails.Select(f => $"{f.Reason}×{f.Count}"));
                PrevPageButton.IsEnabled = _pageIndex > 0;
                NextPageButton.IsEnabled = _pageIndex + 1 < totalPages;
            }
            catch (RootDataUnavailableException ex)
            {
                LogDataGrid.ItemsSource = null;
                PageStatusText.Text = ex.Message;
                FailAggregateText.Text = "失败原因聚合：-";
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void QueryButton_Click(object sender, RoutedEventArgs e)
        {
            _pageIndex = 0;
            await LoadLogsAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadLogsAsync();

        private async void PrevPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_pageIndex <= 0) return;
            _pageIndex--;
            await LoadLogsAsync();
        }

        private async void NextPageButton_Click(object sender, RoutedEventArgs e)
        {
            _pageIndex++;
            await LoadLogsAsync();
        }

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
                WriteCsv(dialog.FileName, logs);
                MessageBox.Show($"导出成功，共 {logs.Count} 条记录\n{dialog.FileName}", "成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void ArchiveButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("将导出当前筛选条件下的全部日志后清空根节点日志表。是否继续？",
                    "归档并清空", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            string? deviceId = string.IsNullOrWhiteSpace(DeviceIdFilter.Text) ? null : DeviceIdFilter.Text.Trim();
            string? userId = string.IsNullOrWhiteSpace(UserIdFilter.Text) ? null : UserIdFilter.Text.Trim();
            DateTime? startTime = StartDatePicker.SelectedDate;
            DateTime? endTime = EndDatePicker.SelectedDate;
            if (endTime.HasValue) endTime = endTime.Value.Date.AddDays(1).AddSeconds(-1);

            SetBusy(true, "正在归档日志");
            try
            {
                var all = await Task.Run(() =>
                    App.LogService.QueryVisibleLogs(deviceId, userId, startTime, endTime, null, 100000, 0));
                var dialog = new SaveFileDialog
                {
                    Filter = "CSV 文件 (*.csv)|*.csv",
                    FileName = $"日志归档_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };
                if (dialog.ShowDialog() != true) return;
                WriteCsv(dialog.FileName, all);
                await Task.Run(App.LogService.ClearLogs);
                MessageBox.Show($"已归档 {all.Count} 条并清空根节点日志。", "完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                _pageIndex = 0;
                await LoadLogsAsync();
            }
            catch (RootDataUnavailableException ex)
            {
                MessageBox.Show(ex.Message, "根节点不可用", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"归档失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private static void WriteCsv(string path, List<LogEntry> logs)
        {
            var sb = new StringBuilder();
            sb.AppendLine("时间,设备ID,用户ID,锁号,操作,结果,原因");
            foreach (var log in logs)
            {
                sb.AppendLine(string.Join(",",
                    CsvEscape(log.CreateTime.ToString("yyyy-MM-dd HH:mm:ss")),
                    CsvEscape(log.DeviceId ?? ""),
                    CsvEscape(log.DisplayUserId),
                    log.LockDisplay,
                    CsvEscape(log.Action ?? ""),
                    CsvEscape(log.Result ?? ""),
                    CsvEscape(log.Reason ?? "")));
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static string CsvEscape(string field)
        {
            if (field.Contains(",") || field.Contains("\"") || field.Contains("\n") || field.Contains("\r"))
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }

        private void SetBusy(bool busy, string? status = null)
        {
            QueryButton.IsEnabled = !busy;
            RefreshButton.IsEnabled = !busy;
            ExportButton.IsEnabled = !busy;
            ArchiveButton.IsEnabled = !busy;
            if (!string.IsNullOrEmpty(status)) PageStatusText.Text = status;
        }
    }
}
