using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace CabinetLock
{
    public partial class FingerprintTestWindow : BorderlessWindow
    {
        private readonly string? _presetUserId;
        private readonly int? _presetFingerprintId;
        private readonly string? _presetDeviceId;
        private List<User> _users = new();
        private List<ClassInfo> _classes = new();
        private List<FingerprintTemplate> _templates = new();
        private readonly DispatcherTimer _countdownTimer;
        private bool _loadingSelection;
        private bool _testActive;
        private bool _closingAfterStop;
        private string _testToken = "";
        private string _testDeviceId = "";
        private DateTime _lastActivity;
        private int _eventSequence;
        private int _matchedCount;
        private int _notMatchedCount;
        private int _errorCount;

        public FingerprintTestWindow(
            string? presetUserId = null, int? presetFingerprintId = null,
            string? presetDeviceId = null)
        {
            _presetUserId = presetUserId;
            _presetFingerprintId = presetFingerprintId;
            _presetDeviceId = presetDeviceId;
            InitializeComponent();
            _countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _countdownTimer.Tick += CountdownTimer_Tick;
            App.MessageHandler.OnFingerprintTestEvent += OnFingerprintTestEvent;
            LoadData();
        }

        private void LoadData()
        {
            _loadingSelection = true;
            try
            {
                _users = App.UserService.GetVisibleUsers()
                    .Where(user => user.FingerprintId.HasValue)
                    .OrderBy(user => user.Name).ThenBy(user => user.UserId).ToList();
                _classes = App.ClassService.GetVisible().Where(item => item.Enabled).ToList();
                _templates = App.FingerprintTemplateService.GetAllTemplates();
                RoleCombo.ItemsSource = FingerprintSelectionData.BuildRoles(_users);
                ClassCombo.ItemsSource = _classes.Select(item => new FingerprintClassOption
                {
                    ClassId = item.ClassId,
                    DisplayText = $"{item.Name} ({item.ClassId})"
                }).ToList();
                DeviceCombo.ItemsSource = FingerprintSelectionData.LoadOnlineCabinets();

                User? preset = _users.FirstOrDefault(user =>
                    string.Equals(user.UserId, _presetUserId, StringComparison.OrdinalIgnoreCase));
                if (preset != null)
                {
                    RoleCombo.SelectedValue = preset.Role;
                    if (preset.Role == "student") ClassCombo.SelectedValue = preset.ClassId;
                }
                else if (RoleCombo.Items.Count > 0) RoleCombo.SelectedIndex = 0;
                bool studentRole = string.Equals(RoleCombo.SelectedValue as string,
                    "student", StringComparison.OrdinalIgnoreCase);
                ClassCombo.IsEnabled = studentRole;
                ClassCombo.Visibility = studentRole ? Visibility.Visible : Visibility.Hidden;
                RefreshUserOptions();
                if (preset != null) UserCombo.SelectedValue = preset.UserId;
                RefreshFingerprintOptions();
                if (_presetFingerprintId.HasValue)
                    FingerprintCombo.SelectedValue = _presetFingerprintId.Value;
                if (!string.IsNullOrWhiteSpace(_presetDeviceId))
                    DeviceCombo.SelectedValue = _presetDeviceId;
                if (DeviceCombo.SelectedIndex < 0 && DeviceCombo.Items.Count > 0)
                    DeviceCombo.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                TestStateText.Text = "数据读取失败";
                EventDetailText.Text = ex.Message;
            }
            finally
            {
                _loadingSelection = false;
                UpdateSelectionState();
            }
        }

        private void RoleCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingSelection) return;
            _loadingSelection = true;
            string role = RoleCombo.SelectedValue as string ?? "";
            bool student = role == "student";
            ClassCombo.IsEnabled = student;
            ClassCombo.Visibility = student ? Visibility.Visible : Visibility.Hidden;
            if (student && ClassCombo.SelectedIndex < 0 && ClassCombo.Items.Count > 0)
                ClassCombo.SelectedIndex = 0;
            if (!student) ClassCombo.SelectedIndex = -1;
            RefreshUserOptions();
            RefreshFingerprintOptions();
            _loadingSelection = false;
            UpdateSelectionState();
        }

        private void ClassCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingSelection) return;
            RefreshUserOptions();
            RefreshFingerprintOptions();
            UpdateSelectionState();
        }

        private void UserCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingSelection) return;
            RefreshFingerprintOptions();
            UpdateSelectionState();
        }

        private void RefreshUserOptions()
        {
            string role = RoleCombo.SelectedValue as string ?? "";
            string classId = ClassCombo.SelectedValue as string ?? "";
            var options = _users.Where(user => user.Role == role)
                .Where(user => role != "student" ||
                    string.Equals(user.ClassId, classId, StringComparison.OrdinalIgnoreCase))
                .Select(user => new FingerprintUserOption { User = user }).ToList();
            UserCombo.ItemsSource = options;
            if (options.Count > 0) UserCombo.SelectedIndex = 0;
        }

        private void RefreshFingerprintOptions()
        {
            if (UserCombo.SelectedItem is not FingerprintUserOption userOption)
            {
                FingerprintCombo.ItemsSource = null;
                return;
            }
            var ids = _templates.Where(item =>
                    string.Equals(item.UserId, userOption.UserId, StringComparison.OrdinalIgnoreCase))
                .Select(item => (item.FingerprintId, item.FingerIndex))
                .ToList();
            if (userOption.User.FingerprintId.HasValue &&
                ids.All(item => item.FingerprintId != userOption.User.FingerprintId.Value))
                ids.Insert(0, (userOption.User.FingerprintId.Value, 1));
            var options = ids.Distinct().Select(item => new FingerprintTemplateOption
            {
                FingerprintId = item.FingerprintId,
                FingerIndex = item.FingerIndex,
                DisplayText = $"指纹 #{item.FingerprintId} · 手指 {item.FingerIndex}"
            }).ToList();
            FingerprintCombo.ItemsSource = options;
            if (options.Count > 0) FingerprintCombo.SelectedIndex = 0;
        }

        private void SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            UpdateSelectionState();

        private void UpdateSelectionState()
        {
            if (_loadingSelection || _testActive) return;
            StartButton.IsEnabled = UserCombo.SelectedItem is FingerprintUserOption &&
                FingerprintCombo.SelectedItem is FingerprintTemplateOption &&
                DeviceCombo.SelectedItem is FingerprintDeviceOption;
            if (StartButton.IsEnabled)
            {
                var user = (FingerprintUserOption)UserCombo.SelectedItem;
                var fp = (FingerprintTemplateOption)FingerprintCombo.SelectedItem;
                var device = (FingerprintDeviceOption)DeviceCombo.SelectedItem;
                TestTargetText.Text = $"{user.User.Name} · 指纹 #{fp.FingerprintId} · {device.DisplayText}";
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            if (FingerprintCombo.SelectedItem is not FingerprintTemplateOption fingerprint ||
                DeviceCombo.SelectedItem is not FingerprintDeviceOption device)
                return;
            SetSelectionEnabled(false);
            TestStateText.Text = "正在下发测试模板";
            byte[]? bytes = await App.FingerprintTemplateService
                .GetTemplateBytesAsync(fingerprint.FingerprintId);
            if (bytes == null || bytes.Length == 0)
            {
                TestStateText.Text = "模板不可用";
                EventText.Text = "无法开始测试";
                EventDetailText.Text = "本机和 SD 均没有该指纹模板字节";
                SetSelectionEnabled(true);
                return;
            }

            _testToken = Guid.NewGuid().ToString("N");
            _testDeviceId = device.DeviceId;
            CommandResult result = await App.CommandService.StartFingerprintTestAsync(
                device.DeviceId, fingerprint.FingerprintId, bytes, _testToken);
            if (!result.Success)
            {
                TestStateText.Text = "测试启动失败";
                EventDetailText.Text = result.ErrorMessage;
                _testToken = "";
                _testDeviceId = "";
                SetSelectionEnabled(true);
                return;
            }

            _testActive = true;
            _lastActivity = DateTime.UtcNow;
            _matchedCount = _notMatchedCount = _errorCount = 0;
            UpdateCounters();
            TestStateText.Text = "测试中";
            EventText.Text = "等待按指纹";
            EventDetailText.Text = "";
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            _countdownTimer.Start();
        }

        private void OnFingerprintTestEvent(FingerprintTestEvent evt)
        {
            if (string.IsNullOrWhiteSpace(_testToken) || evt.TestToken != _testToken ||
                !string.Equals(evt.DeviceId, _testDeviceId, StringComparison.OrdinalIgnoreCase))
                return;
            Dispatcher.BeginInvoke(new Action(() => ApplyTestEvent(evt)));
        }

        private void ApplyTestEvent(FingerprintTestEvent evt)
        {
            _eventSequence = (_eventSequence + 1) % 100;
            EventSequenceText.Text = _eventSequence.ToString("D2");
            if (evt.Event is "matched" or "not_matched" or "read_error" or "activity")
                _lastActivity = DateTime.UtcNow;
            switch (evt.Event)
            {
                case "started":
                    EventText.Text = "等待按指纹";
                    break;
                case "matched":
                    _matchedCount++;
                    EventIconText.Text = "\uE73E";
                    EventText.Text = "指纹匹配";
                    EventDetailText.Text = evt.Confidence > 0 ? $"匹配分数 {evt.Confidence}" : "";
                    break;
                case "not_matched":
                    _notMatchedCount++;
                    EventIconText.Text = "\uE711";
                    EventText.Text = "指纹不匹配";
                    EventDetailText.Text = "请确认所选用户与按压手指";
                    break;
                case "read_error":
                    _errorCount++;
                    EventIconText.Text = "\uE783";
                    EventText.Text = "指纹读取异常";
                    break;
                case "timeout":
                    EventText.Text = "测试已超时退出";
                    FinishTestState();
                    break;
                case "stopped":
                case "cancelled":
                    EventText.Text = "测试已结束";
                    FinishTestState();
                    break;
            }
            UpdateCounters();
        }

        private void CountdownTimer_Tick(object? sender, EventArgs e)
        {
            int remaining = Math.Max(0, 60 - (int)(DateTime.UtcNow - _lastActivity).TotalSeconds);
            CountdownText.Text = remaining.ToString();
        }

        private async void StopButton_Click(object sender, RoutedEventArgs e) =>
            await StopTestAsync();

        private async Task StopTestAsync()
        {
            if (!_testActive)
            {
                FinishTestState();
                return;
            }
            StopButton.IsEnabled = false;
            TestStateText.Text = "正在结束测试";
            await App.CommandService.StopFingerprintTestAsync(_testDeviceId, _testToken);
            FinishTestState();
        }

        private void FinishTestState()
        {
            _testActive = false;
            _countdownTimer.Stop();
            CountdownText.Text = "60";
            TestStateText.Text = "测试已结束";
            StopButton.IsEnabled = false;
            SetSelectionEnabled(true);
            _testToken = "";
            _testDeviceId = "";
        }

        private void SetSelectionEnabled(bool enabled)
        {
            RoleCombo.IsEnabled = enabled;
            ClassCombo.IsEnabled = enabled && RoleCombo.SelectedValue as string == "student";
            UserCombo.IsEnabled = enabled;
            FingerprintCombo.IsEnabled = enabled;
            DeviceCombo.IsEnabled = enabled;
            StartButton.IsEnabled = enabled;
            if (enabled) UpdateSelectionState();
        }

        private void UpdateCounters()
        {
            MatchedCountText.Text = $"匹配 {_matchedCount}";
            NotMatchedCountText.Text = $"不匹配 {_notMatchedCount}";
            ErrorCountText.Text = $"异常 {_errorCount}";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_testActive && !_closingAfterStop)
            {
                e.Cancel = true;
                _ = StopAndCloseAsync();
                return;
            }
            App.MessageHandler.OnFingerprintTestEvent -= OnFingerprintTestEvent;
            _countdownTimer.Stop();
            base.OnClosing(e);
        }

        private async Task StopAndCloseAsync()
        {
            await StopTestAsync();
            _closingAfterStop = true;
            Close();
        }
    }
}
