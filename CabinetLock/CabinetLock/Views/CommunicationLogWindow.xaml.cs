using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace CabinetLock
{
    /// <summary>
    /// 通讯日志窗口（V2.7）。
    /// 订阅 MeshBridge.TraceAdded 事件，实时展示链路收发数据、状态变化、协议帧等。
    /// 支持方向/类别/关键字过滤、自动滚动、清空、导出。
    /// </summary>
    public partial class CommunicationLogWindow : BorderlessWindow
    {
        private readonly ObservableCollection<CommunicationTraceEntry> _allEntries = new();
        private readonly System.Windows.Threading.DispatcherTimer _refreshTimer;
        private int _sentCount;
        private int _receivedCount;
        private const int MaxEntries = 5000; // 限制内存占用
        private bool _filterDirty;
        private DateTime _lastFilterAt = DateTime.MinValue;

        public CommunicationLogWindow()
        {
            InitializeComponent();
            LogGrid.ItemsSource = _allEntries;

            // 监听链路追踪事件
            App.MeshBridge.TraceAdded += OnTraceAdded;

            // 状态刷新定时器（链路状态、计数）
            _refreshTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _refreshTimer.Tick += (_, _) => UpdateLinkStatus();
            _refreshTimer.Start();

            UpdateLinkStatus();

            // 选中行展示完整内容
            LogGrid.SelectionChanged += (_, _) =>
            {
                if (LogGrid.SelectedItem is CommunicationTraceEntry entry)
                {
                    DetailTextBox.Text = entry.CopyText;
                }
            };

            Closed += (_, _) =>
            {
                App.MeshBridge.TraceAdded -= OnTraceAdded;
                _refreshTimer.Stop();
            };
        }

        private void OnTraceAdded(CommunicationTraceEntry entry)
        {
            // 串口/线程池回调：始终异步切回 UI，避免与串口 I/O / CollectionView 交叉占用。
            try
            {
                Dispatcher.BeginInvoke(new Action(() => AppendTrace(entry)),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CommLog] OnTraceAdded: {ex.Message}");
            }
        }

        private void AppendTrace(CommunicationTraceEntry entry)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => AppendTrace(entry)),
                    System.Windows.Threading.DispatcherPriority.Background);
                return;
            }
            try
            {
                if (entry.Direction == CommunicationDirection.Transmit) _sentCount++;
                else if (entry.Direction == CommunicationDirection.Receive) _receivedCount++;
                _allEntries.Add(entry);

                while (_allEntries.Count > MaxEntries)
                {
                    _allEntries.RemoveAt(0);
                }

                // 高频收包时合并过滤，避免 CollectionView 每条重建导致卡顿/线程竞争表象。
                _filterDirty = true;
                if ((DateTime.Now - _lastFilterAt).TotalMilliseconds >= 200)
                {
                    _lastFilterAt = DateTime.Now;
                    _filterDirty = false;
                    ApplyFilter();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CommLog] AppendTrace: {ex.Message}");
            }
        }

        private void UpdateLinkStatus()
        {
            if (_filterDirty)
            {
                _filterDirty = false;
                _lastFilterAt = DateTime.Now;
                ApplyFilter();
            }
            bool connected = App.MeshBridge.IsConnected;
            LinkStatusDot.Fill = connected
                ? (System.Windows.Media.Brush)FindResource("SuccessBrush")
                : (System.Windows.Media.Brush)FindResource("DangerBrush");
            LinkStatusText.Text = connected ? "链路已连接" : "链路未连接";

            // 链路类型描述
            try
            {
                var cfg = ConfigHelper.Current;
                string t = cfg?.TransportType?.ToString() ?? "Unknown";
                TransportText.Text = $"链路类型: {t}";
            }
            catch
            {
                TransportText.Text = "链路类型: --";
            }

            SentCountText.Text = _sentCount.ToString();
            ReceivedCountText.Text = _receivedCount.ToString();
        }

        // ===== 过滤 =====

        private bool MatchesFilter(CommunicationTraceEntry e)
        {
            if (e.Direction == CommunicationDirection.Transmit && ShowSendCheck.IsChecked != true) return false;
            if (e.Direction == CommunicationDirection.Receive && ShowRecvCheck.IsChecked != true) return false;
            if (e.Direction == CommunicationDirection.System && ShowSystemCheck.IsChecked != true) return false;

            // 类别过滤（子串匹配，兼容 "协议 JSON" / "ACK" / "ERROR" 等多种取值）
            string? cat = (CategoryFilterBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
            if (!string.IsNullOrEmpty(cat) && e.Category.IndexOf(cat, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            // 关键字
            string kw = KeywordBox.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(kw) &&
                e.Content.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0 &&
                e.Category.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            return true;
        }

        private void ApplyFilter()
        {
            // DataGrid 已绑定到 _allEntries；过滤通过 CollectionView 实现，避免重建列表
            if (LogGrid.ItemsSource is ICollectionView view)
            {
                view.Filter = item => item is CommunicationTraceEntry e && MatchesFilter(e);
            }
            else
            {
                // 首次设置 CollectionView
                var cvs = System.Windows.Data.CollectionViewSource.GetDefaultView(_allEntries);
                cvs.Filter = item => item is CommunicationTraceEntry e && MatchesFilter(e);
            }

            if (AutoScrollCheck.IsChecked == true && _allEntries.Count > 0)
            {
                // 滚动到最末（仅在过滤后仍可见的最后一条）
                var listView = LogGrid.Items;
                if (listView.Count > 0)
                {
                    LogGrid.ScrollIntoView(listView[listView.Count - 1]);
                }
            }
        }

        private void FilterCheck_Changed(object sender, RoutedEventArgs e) => ApplyFilter();
        private void CategoryFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();
        private void KeywordBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            _allEntries.Clear();
            _sentCount = 0;
            _receivedCount = 0;
            DetailTextBox.Clear();
            App.MeshBridge.ClearTrace();
            UpdateLinkStatus();
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "日志文件 (*.log)|*.log|文本文件 (*.txt)|*.txt",
                FileName = $"通讯日志_{DateTime.Now:yyyyMMdd_HHmmss}.log"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine($"# 通讯日志  导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                sb.AppendLine($"# 链路状态: {(App.MeshBridge.IsConnected ? "已连接" : "未连接")}");
                sb.AppendLine($"# 累计 TX: {_sentCount}  RX: {_receivedCount}");
                sb.AppendLine("# " + new string('-', 80));
                foreach (var entry in _allEntries)
                {
                    sb.AppendLine(entry.CopyText);
                }
                File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
                MessageBox.Show($"已导出 {_allEntries.Count} 条记录到\n{dialog.FileName}", "导出完成",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("导出失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
