using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace FingerprintLockManager
{
    /// <summary>
    /// 系统日志页：操作日志 / 通讯日志 / 开锁日志（Tab 切换）。
    /// 每种日志均支持关键词、时间范围、分页、导出 XLS。
    /// </summary>
    public partial class SystemLogPage : Page
    {
        private const int PageSize = 50;

        private int _opPage;
        private int _commPage;
        private int _unlockPage;
        private bool _loaded;
        private bool _busy;

        public SystemLogPage()
        {
            InitializeComponent();
            Loaded += async (_, _) =>
            {
                _loaded = true;
                await LoadActiveTabAsync();
            };
            Unloaded += (_, _) =>
            {
                _loaded = false;
                App.MeshBridge.TraceAdded -= OnTraceAdded;
            };
            App.MeshBridge.TraceAdded += OnTraceAdded;
        }

        private void OnTraceAdded(CommunicationTraceEntry entry)
        {
            // 通讯页签打开时，新消息到来后轻量刷新当前页（不打断用户）
            if (!_loaded || LogTabs.SelectedIndex != 1 || _busy) return;
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (_loaded && LogTabs.SelectedIndex == 1 && !_busy)
                    await LoadCommLogsAsync(quiet: true);
            }));
        }

        private async void LogTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded) return;
            await LoadActiveTabAsync();
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
            await LoadActiveTabAsync();

        private async Task LoadActiveTabAsync()
        {
            switch (LogTabs.SelectedIndex)
            {
                case 0: await LoadOpLogsAsync(); break;
                case 1: await LoadCommLogsAsync(); break;
                case 2: await LoadUnlockLogsAsync(); break;
            }
        }

        // ==================== 操作日志 ====================

        private async void OpQueryButton_Click(object sender, RoutedEventArgs e)
        {
            _opPage = 0;
            await LoadOpLogsAsync();
        }

        private async void OpPrevButton_Click(object sender, RoutedEventArgs e)
        {
            if (_opPage <= 0) return;
            _opPage--;
            await LoadOpLogsAsync();
        }

        private async void OpNextButton_Click(object sender, RoutedEventArgs e)
        {
            _opPage++;
            await LoadOpLogsAsync();
        }

        private async Task LoadOpLogsAsync()
        {
            string? keyword = NullIfEmpty(OpKeywordBox.Text);
            DateTime? start = OpStartDatePicker.SelectedDate;
            DateTime? end = EndOfDay(OpEndDatePicker.SelectedDate);

            SetBusy(true, "正在读取操作日志…");
            try
            {
                int total = await Task.Run(() => App.OperationLogService.Count(keyword, start, end));
                int totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
                if (_opPage >= totalPages) _opPage = totalPages - 1;
                if (_opPage < 0) _opPage = 0;

                var list = await Task.Run(() =>
                    App.OperationLogService.Query(keyword, start, end, PageSize, _opPage * PageSize));

                OpDataGrid.ItemsSource = list;
                OpPageInfoText.Text = $"第 {_opPage + 1} / {totalPages} 页";
                OpStatusText.Text = $"共 {total} 条，本页 {list.Count} 条";
                OpPrevButton.IsEnabled = _opPage > 0;
                OpNextButton.IsEnabled = _opPage + 1 < totalPages;
                PageStatusText.Text = $"操作日志 · 共 {total} 条";
            }
            catch (Exception ex)
            {
                OpDataGrid.ItemsSource = null;
                OpStatusText.Text = ex.Message;
                PageStatusText.Text = "操作日志读取失败";
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void OpExportButton_Click(object sender, RoutedEventArgs e)
        {
            string? keyword = NullIfEmpty(OpKeywordBox.Text);
            DateTime? start = OpStartDatePicker.SelectedDate;
            DateTime? end = EndOfDay(OpEndDatePicker.SelectedDate);

            SetBusy(true, "正在导出操作日志…");
            try
            {
                var all = await Task.Run(() => App.OperationLogService.QueryAll(keyword, start, end));
                if (all.Count == 0)
                {
                    MessageBox.Show("没有可导出的操作日志", "提示");
                    return;
                }

                var dialog = new SaveFileDialog
                {
                    Filter = "Excel 文件 (*.xls)|*.xls|所有文件 (*.*)|*.*",
                    FileName = $"操作日志_{DateTime.Now:yyyyMMdd_HHmmss}.xls"
                };
                if (dialog.ShowDialog() != true) return;

                var headers = new[] { "时间", "操作者ID", "操作者姓名", "模块", "动作", "目标", "结果", "详情" };
                var rows = all.Select(x => (IReadOnlyList<object?>)new object?[]
                {
                    x.Time, x.OperatorId, x.OperatorName, x.Module, x.Action, x.Target, x.Result, x.Detail
                });
                await Task.Run(() => ExcelExportHelper.Export(dialog.FileName, "操作日志", headers, rows));
                App.OperationLogService.Write("系统日志", "导出操作日志", result: "success",
                    detail: $"共 {all.Count} 条 → {dialog.FileName}");
                MessageBox.Show($"导出成功，共 {all.Count} 条\n{dialog.FileName}", "成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ==================== 通讯日志 ====================

        private async void CommQueryButton_Click(object sender, RoutedEventArgs e)
        {
            _commPage = 0;
            await LoadCommLogsAsync();
        }

        private async void CommPrevButton_Click(object sender, RoutedEventArgs e)
        {
            if (_commPage <= 0) return;
            _commPage--;
            await LoadCommLogsAsync();
        }

        private async void CommNextButton_Click(object sender, RoutedEventArgs e)
        {
            _commPage++;
            await LoadCommLogsAsync();
        }

        private void CommDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (CommDataGrid.SelectedItem is CommunicationTraceEntry entry)
                CommDetailBox.Text = entry.CopyText;
            else
                CommDetailBox.Text = "";
        }

        private void CommClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (MessageBox.Show("确认清空内存中的通讯追踪缓存？", "清空通讯日志",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            App.MeshBridge.ClearTrace();
            _commPage = 0;
            _ = LoadCommLogsAsync();
            App.OperationLogService.Write("系统日志", "清空通讯日志缓存", result: "success");
        }

        private async Task LoadCommLogsAsync(bool quiet = false)
        {
            string? keyword = NullIfEmpty(CommKeywordBox.Text);
            DateTime? start = CommStartDatePicker.SelectedDate;
            DateTime? end = EndOfDay(CommEndDatePicker.SelectedDate);
            string? directionTag = null;
            if (CommDirectionBox.SelectedItem is ComboBoxItem item)
                directionTag = string.IsNullOrWhiteSpace(item.Tag?.ToString()) ? null : item.Tag!.ToString();

            if (!quiet) SetBusy(true, "正在读取通讯日志…");
            try
            {
                var all = await Task.Run(() =>
                {
                    IEnumerable<CommunicationTraceEntry> q = App.MeshBridge.RecentTrace;
                    if (start.HasValue) q = q.Where(x => x.Timestamp >= start.Value);
                    if (end.HasValue) q = q.Where(x => x.Timestamp <= end.Value);
                    if (!string.IsNullOrEmpty(directionTag) &&
                        Enum.TryParse<CommunicationDirection>(directionTag, out var dir))
                    {
                        q = q.Where(x => x.Direction == dir);
                    }
                    if (!string.IsNullOrWhiteSpace(keyword))
                    {
                        string k = keyword.Trim();
                        q = q.Where(x =>
                            (x.Category?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false) ||
                            (x.Content?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false) ||
                            (x.DirectionText?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false));
                    }
                    return q.OrderByDescending(x => x.Timestamp).ToList();
                });

                int total = all.Count;
                int totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
                if (_commPage >= totalPages) _commPage = totalPages - 1;
                if (_commPage < 0) _commPage = 0;
                var page = all.Skip(_commPage * PageSize).Take(PageSize).ToList();

                CommDataGrid.ItemsSource = page;
                CommPageInfoText.Text = $"第 {_commPage + 1} / {totalPages} 页";
                CommStatusText.Text = $"共 {total} 条（内存缓存），本页 {page.Count} 条";
                CommPrevButton.IsEnabled = _commPage > 0;
                CommNextButton.IsEnabled = _commPage + 1 < totalPages;
                if (!quiet) PageStatusText.Text = $"通讯日志 · 共 {total} 条（最近缓存）";
            }
            catch (Exception ex)
            {
                CommDataGrid.ItemsSource = null;
                CommStatusText.Text = ex.Message;
                if (!quiet) PageStatusText.Text = "通讯日志读取失败";
            }
            finally
            {
                if (!quiet) SetBusy(false);
            }
        }

        private async void CommExportButton_Click(object sender, RoutedEventArgs e)
        {
            string? keyword = NullIfEmpty(CommKeywordBox.Text);
            DateTime? start = CommStartDatePicker.SelectedDate;
            DateTime? end = EndOfDay(CommEndDatePicker.SelectedDate);
            string? directionTag = null;
            if (CommDirectionBox.SelectedItem is ComboBoxItem item)
                directionTag = string.IsNullOrWhiteSpace(item.Tag?.ToString()) ? null : item.Tag!.ToString();

            SetBusy(true, "正在导出通讯日志…");
            try
            {
                var all = await Task.Run(() =>
                {
                    IEnumerable<CommunicationTraceEntry> q = App.MeshBridge.RecentTrace;
                    if (start.HasValue) q = q.Where(x => x.Timestamp >= start.Value);
                    if (end.HasValue) q = q.Where(x => x.Timestamp <= end.Value);
                    if (!string.IsNullOrEmpty(directionTag) &&
                        Enum.TryParse<CommunicationDirection>(directionTag, out var dir))
                        q = q.Where(x => x.Direction == dir);
                    if (!string.IsNullOrWhiteSpace(keyword))
                    {
                        string k = keyword.Trim();
                        q = q.Where(x =>
                            (x.Category?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false) ||
                            (x.Content?.Contains(k, StringComparison.OrdinalIgnoreCase) ?? false));
                    }
                    return q.OrderByDescending(x => x.Timestamp).ToList();
                });

                if (all.Count == 0)
                {
                    MessageBox.Show("没有可导出的通讯日志", "提示");
                    return;
                }

                var dialog = new SaveFileDialog
                {
                    Filter = "Excel 文件 (*.xls)|*.xls|所有文件 (*.*)|*.*",
                    FileName = $"通讯日志_{DateTime.Now:yyyyMMdd_HHmmss}.xls"
                };
                if (dialog.ShowDialog() != true) return;

                var headers = new[] { "时间", "方向", "类别", "内容" };
                var rows = all.Select(x => (IReadOnlyList<object?>)new object?[]
                {
                    x.Timestamp, x.DirectionText, x.Category, x.Content
                });
                await Task.Run(() => ExcelExportHelper.Export(dialog.FileName, "通讯日志", headers, rows));
                App.OperationLogService.Write("系统日志", "导出通讯日志", result: "success",
                    detail: $"共 {all.Count} 条 → {dialog.FileName}");
                MessageBox.Show($"导出成功，共 {all.Count} 条\n{dialog.FileName}", "成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ==================== 开锁日志 ====================

        private async void UnlockQueryButton_Click(object sender, RoutedEventArgs e)
        {
            _unlockPage = 0;
            await LoadUnlockLogsAsync();
        }

        private async void UnlockPrevButton_Click(object sender, RoutedEventArgs e)
        {
            if (_unlockPage <= 0) return;
            _unlockPage--;
            await LoadUnlockLogsAsync();
        }

        private async void UnlockNextButton_Click(object sender, RoutedEventArgs e)
        {
            _unlockPage++;
            await LoadUnlockLogsAsync();
        }

        private async Task LoadUnlockLogsAsync()
        {
            string? keyword = NullIfEmpty(UnlockKeywordBox.Text);
            string? deviceId = NullIfEmpty(UnlockDeviceIdBox.Text);
            string? userId = NullIfEmpty(UnlockUserIdBox.Text);
            string? result = null;
            if (UnlockResultBox.SelectedItem is ComboBoxItem item)
                result = string.IsNullOrWhiteSpace(item.Tag?.ToString()) ? null : item.Tag!.ToString();
            DateTime? start = UnlockStartDatePicker.SelectedDate;
            DateTime? end = EndOfDay(UnlockEndDatePicker.SelectedDate);

            SetBusy(true, "正在读取开锁日志…");
            try
            {
                int total = await Task.Run(() =>
                    App.LogService.CountVisibleLogs(deviceId, userId, start, end, result, keyword));
                int totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)PageSize));
                if (_unlockPage >= totalPages) _unlockPage = totalPages - 1;
                if (_unlockPage < 0) _unlockPage = 0;

                var list = await Task.Run(() =>
                    App.LogService.QueryVisibleLogs(deviceId, userId, start, end, result,
                        PageSize, _unlockPage * PageSize, keyword));

                UnlockDataGrid.ItemsSource = list;
                UnlockPageInfoText.Text = $"第 {_unlockPage + 1} / {totalPages} 页";
                UnlockStatusText.Text = $"共 {total} 条，本页 {list.Count} 条";
                UnlockPrevButton.IsEnabled = _unlockPage > 0;
                UnlockNextButton.IsEnabled = _unlockPage + 1 < totalPages;
                PageStatusText.Text = $"开锁日志 · 共 {total} 条";
            }
            catch (RootDataUnavailableException ex)
            {
                UnlockDataGrid.ItemsSource = null;
                UnlockStatusText.Text = ex.Message;
                PageStatusText.Text = "开锁日志：根节点数据不可用";
            }
            catch (Exception ex)
            {
                UnlockDataGrid.ItemsSource = null;
                UnlockStatusText.Text = ex.Message;
                PageStatusText.Text = "开锁日志读取失败";
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void UnlockExportButton_Click(object sender, RoutedEventArgs e)
        {
            string? keyword = NullIfEmpty(UnlockKeywordBox.Text);
            string? deviceId = NullIfEmpty(UnlockDeviceIdBox.Text);
            string? userId = NullIfEmpty(UnlockUserIdBox.Text);
            string? result = null;
            if (UnlockResultBox.SelectedItem is ComboBoxItem item)
                result = string.IsNullOrWhiteSpace(item.Tag?.ToString()) ? null : item.Tag!.ToString();
            DateTime? start = UnlockStartDatePicker.SelectedDate;
            DateTime? end = EndOfDay(UnlockEndDatePicker.SelectedDate);

            SetBusy(true, "正在导出开锁日志…");
            try
            {
                var all = await Task.Run(() =>
                    App.LogService.QueryVisibleLogs(deviceId, userId, start, end, result,
                        100000, 0, keyword));
                if (all.Count == 0)
                {
                    MessageBox.Show("没有可导出的开锁日志", "提示");
                    return;
                }

                var dialog = new SaveFileDialog
                {
                    Filter = "Excel 文件 (*.xls)|*.xls|所有文件 (*.*)|*.*",
                    FileName = $"开锁日志_{DateTime.Now:yyyyMMdd_HHmmss}.xls"
                };
                if (dialog.ShowDialog() != true) return;

                var headers = new[] { "时间", "设备ID", "用户ID", "锁号", "操作", "结果", "原因" };
                var rows = all.Select(x => (IReadOnlyList<object?>)new object?[]
                {
                    x.CreateTime, x.DeviceId, x.UserId, x.LockId, x.Action, x.Result, x.Reason
                });
                await Task.Run(() => ExcelExportHelper.Export(dialog.FileName, "开锁日志", headers, rows));
                App.OperationLogService.Write("系统日志", "导出开锁日志", result: "success",
                    detail: $"共 {all.Count} 条 → {dialog.FileName}");
                MessageBox.Show($"导出成功，共 {all.Count} 条\n{dialog.FileName}", "成功",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        // ==================== 辅助 ====================

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            RefreshButton.IsEnabled = !busy;
            OpQueryButton.IsEnabled = !busy;
            OpExportButton.IsEnabled = !busy;
            CommQueryButton.IsEnabled = !busy;
            CommExportButton.IsEnabled = !busy;
            CommClearButton.IsEnabled = !busy;
            UnlockQueryButton.IsEnabled = !busy;
            UnlockExportButton.IsEnabled = !busy;
            if (!string.IsNullOrEmpty(status)) PageStatusText.Text = status;
        }

        private static string? NullIfEmpty(string? text) =>
            string.IsNullOrWhiteSpace(text) ? null : text.Trim();

        private static DateTime? EndOfDay(DateTime? date) =>
            date.HasValue ? date.Value.Date.AddDays(1).AddSeconds(-1) : null;
    }
}
