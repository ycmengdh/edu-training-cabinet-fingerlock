using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace FingerprintLockManager
{
    /// <summary>
    /// 指纹录入页面（需求 5）
    ///
    /// 4+2 录入流程：4 次采集 + 2 次验证 = 共 6 次按手指。
    /// 可在任意柜子录入，录入后存根节点 SD 卡，本机不存。
    /// 老师录入后自动下发到所有柜子（由 FingerprintEnrollService 自动触发）。
    /// </summary>
    public partial class FingerprintEnrollPage : Page
    {
        /// <summary>6 个步骤圆圈</summary>
        private readonly List<Ellipse> _stepDots = new();

        /// <summary>录入进行中标志</summary>
        private bool _enrolling;

        /// <summary>录入所在设备 ID</summary>
        private string? _enrollDeviceId;

        /// <summary>状态刷新定时器</summary>
        private DispatcherTimer? _refreshTimer;

        public FingerprintEnrollPage()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 构造 6 个步骤圆圈
            BuildStepDots();

            // 加载用户列表（老师 + 学生）和在线柜子列表
            LoadUsers();
            LoadDevices();

            // 订阅录入完成事件
            App.FingerprintEnrollService.EnrollCompleted += OnEnrollCompleted;

            // 启动定时器刷新进度（柜子返回阶段响应时通过事件推进）
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _refreshTimer.Tick += (s, args) => RefreshProgress();
            _refreshTimer.Start();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            App.FingerprintEnrollService.EnrollCompleted -= OnEnrollCompleted;
            _refreshTimer?.Stop();
        }

        /// <summary>构造 6 个步骤圆圈</summary>
        private void BuildStepDots()
        {
            StepPanel.Children.Clear();
            _stepDots.Clear();
            for (int i = 1; i <= 6; i++)
            {
                var dot = new Ellipse
                {
                    Width = 28,
                    Height = 28,
                    Fill = FindResource("BorderBrush") as Brush,
                    Stroke = FindResource("BorderBrush") as Brush,
                    StrokeThickness = 1,
                    Margin = new Thickness(i > 1 ? 16 : 0, 0, 0, 0)
                };
                StepPanel.Children.Add(dot);

                var label = new TextBlock
                {
                    Text = i.ToString(),
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 12,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(i > 1 ? -44 : -28, 0, 0, 0)
                };
                StepPanel.Children.Add(label);

                _stepDots.Add(dot);
            }
        }

        /// <summary>加载用户列表（老师 + 学生）</summary>
        private void LoadUsers()
        {
            var users = App.UserService.GetAllUsers()
                .Where(u => u.Role == "teacher" || u.Role == "student")
                .ToList();
            UserCombo.ItemsSource = users;
        }

        /// <summary>加载在线柜子列表</summary>
        private void LoadDevices()
        {
            var devices = App.DeviceService.GetOnlineDevices()
                .Where(d => !d.IsRoot)
                .ToList();
            DeviceCombo.ItemsSource = devices;
        }

        /// <summary>用户选择变化：更新指纹 ID 与状态</summary>
        private void UserCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (UserCombo.SelectedItem is not User user) return;

            if (user.FingerprintId.HasValue)
            {
                FingerprintIdBox.Text = user.FingerprintId.Value.ToString();
                UserStatusText.Text = $"用户已分配指纹 ID {user.FingerprintId.Value}，重新录入将覆盖原指纹。";
                UserStatusText.Foreground = FindResource("DangerBrush") as Brush;
            }
            else
            {
                int nextId = App.UserService.GetNextFingerprintId();
                FingerprintIdBox.Text = nextId.ToString();
                UserStatusText.Text = "用户未分配指纹，将自动分配下一个可用指纹 ID。";
                UserStatusText.Foreground = FindResource("SubTextBrush") as Brush;
            }
        }

        /// <summary>开始录入</summary>
        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (UserCombo.SelectedItem is not User user)
            {
                MessageBox.Show("请选择要录入指纹的用户", "提示");
                return;
            }
            if (DeviceCombo.SelectedItem is not Device device)
            {
                MessageBox.Show("请选择录入所在的柜子", "提示");
                return;
            }
            if (!int.TryParse(FingerprintIdBox.Text?.Trim(), out int fpId) || fpId <= 0)
            {
                MessageBox.Show("指纹 ID 无效", "提示");
                return;
            }

            // 检查指纹 ID 冲突
            var existUser = App.UserService.GetUserByFingerprint(fpId);
            if (existUser != null && existUser.UserId != user.UserId)
            {
                MessageBox.Show($"指纹 ID {fpId} 已被用户「{existUser.Name}」占用", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 操作前自动备份（需求 11）
            App.BackupService.BackupBeforeAction($"录入指纹 user={user.UserId} fp={fpId}", App.CurrentUser?.UserId);

            // 重置 UI 状态
            ResultBanner.Visibility = Visibility.Collapsed;
            ResetStepDots();

            // 开始录入
            _enrolling = true;
            _enrollDeviceId = device.DeviceId;
            StartButton.IsEnabled = false;
            CancelButton.IsEnabled = true;
            UserCombo.IsEnabled = false;
            DeviceCombo.IsEnabled = false;
            StatusText.Text = $"正在 {device.DeviceName} 上录入...";

            // 调用录入服务开始第 1 步
            App.FingerprintEnrollService.StartEnroll(device.DeviceId, user.UserId, fpId);
            RefreshProgress();
        }

        /// <summary>取消录入</summary>
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_enrollDeviceId)) return;

            App.FingerprintEnrollService.CancelEnroll(_enrollDeviceId);
            OnEnrollCompleted(_enrollDeviceId, "", false, "用户取消录入");
        }

        /// <summary>录入完成回调（来自后台线程，需切到 UI 线程）</summary>
        private void OnEnrollCompleted(string deviceId, string userId, bool success, string message)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _enrolling = false;
                StartButton.IsEnabled = true;
                CancelButton.IsEnabled = false;
                UserCombo.IsEnabled = true;
                DeviceCombo.IsEnabled = true;
                StatusText.Text = "";

                ResultBanner.Visibility = Visibility.Visible;
                if (success)
                {
                    ResultTitle.Text = "✓ 录入成功";
                    ResultTitle.Foreground = FindResource("SuccessBrush") as Brush;
                    // 全部点亮
                    foreach (var dot in _stepDots)
                    {
                        dot.Fill = FindResource("SuccessBrush") as Brush;
                        dot.Stroke = FindResource("SuccessBrush") as Brush;
                    }
                }
                else
                {
                    ResultTitle.Text = "✗ 录入失败或已取消";
                    ResultTitle.Foreground = FindResource("DangerBrush") as Brush;
                }
                ResultMessage.Text = message;

                // 重新加载用户列表（指纹 ID 可能已更新）
                LoadUsers();
            }));
        }

        /// <summary>刷新当前进度（从 FingerprintEnrollService 读取会话状态）</summary>
        private void RefreshProgress()
        {
            if (!_enrolling || string.IsNullOrEmpty(_enrollDeviceId)) return;

            var session = App.FingerprintEnrollService.GetSession(_enrollDeviceId);
            if (session == null) return;

            // 更新圆圈颜色
            int step = session.StepNumber;
            for (int i = 0; i < _stepDots.Count; i++)
            {
                if (i < step - 1)
                {
                    _stepDots[i].Fill = FindResource("SuccessBrush") as Brush;
                    _stepDots[i].Stroke = FindResource("SuccessBrush") as Brush;
                }
                else if (i == step - 1)
                {
                    _stepDots[i].Fill = FindResource("PrimaryBrush") as Brush;
                    _stepDots[i].Stroke = FindResource("PrimaryBrush") as Brush;
                }
                else
                {
                    _stepDots[i].Fill = FindResource("BorderBrush") as Brush;
                    _stepDots[i].Stroke = FindResource("BorderBrush") as Brush;
                }
            }

            CurrentStepText.Text = $"第 {step} / {session.TotalSteps} 步";
            StepDescriptionText.Text = session.StepDescription;
        }

        /// <summary>重置所有步骤圆圈为未完成状态</summary>
        private void ResetStepDots()
        {
            foreach (var dot in _stepDots)
            {
                dot.Fill = FindResource("BorderBrush") as Brush;
                dot.Stroke = FindResource("BorderBrush") as Brush;
            }
            CurrentStepText.Text = "等待柜子响应...";
            StepDescriptionText.Text = "请将手指放在传感器上，按提示重复按压。";
        }
    }
}
