using System.Windows;
using System.Windows.Controls;

namespace FingerprintLockManager
{
    public partial class EnrollFingerprintWindow : BorderlessWindow
    {
        private readonly string? _presetDeviceId;
        private readonly string? _presetUserId;
        private List<User> _users = new();
        private List<ClassInfo> _classes = new();
        private bool _loadingSelection;
        private bool _enrolling;
        private string? _lastDeviceId;

        public int EnrolledFingerprintId { get; private set; } = -1;
        public byte[]? EnrolledTemplate { get; private set; }
        public string? EnrolledDeviceId => _lastDeviceId;
        public string? EnrolledUserId { get; private set; }

        public EnrollFingerprintWindow() : this(null, null)
        {
        }

        public EnrollFingerprintWindow(string? presetDeviceId, string? presetUserId = null)
        {
            _presetDeviceId = presetDeviceId;
            _presetUserId = presetUserId;
            InitializeComponent();
            LoadData();
        }

        private void LoadData()
        {
            _loadingSelection = true;
            try
            {
                _users = App.UserService.GetVisibleUsers()
                    .Where(user => DataScopeContext.Instance.CanModify(user))
                    .OrderBy(user => user.Name)
                    .ThenBy(user => user.UserId)
                    .ToList();
                _classes = App.ClassService.GetVisible()
                    .Where(item => item.Enabled)
                    .OrderBy(item => item.Name)
                    .ToList();
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
                    if (string.Equals(preset.Role, "student", StringComparison.OrdinalIgnoreCase))
                        ClassCombo.SelectedValue = preset.ClassId;
                }
                else if (RoleCombo.Items.Count > 0)
                {
                    RoleCombo.SelectedIndex = 0;
                }
                bool studentRole = string.Equals(RoleCombo.SelectedValue as string,
                    "student", StringComparison.OrdinalIgnoreCase);
                ClassCombo.IsEnabled = studentRole;
                ClassCombo.Visibility = studentRole ? Visibility.Visible : Visibility.Hidden;
                RefreshUserOptions();
                if (preset != null) UserCombo.SelectedValue = preset.UserId;
                RefreshFingerOptions();

                if (!string.IsNullOrWhiteSpace(_presetDeviceId))
                    DeviceCombo.SelectedValue = _presetDeviceId;
                if (DeviceCombo.SelectedIndex < 0 && DeviceCombo.Items.Count > 0)
                    DeviceCombo.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                StepHint.Text = "用户数据读取失败";
                ResultText.Text = ex.Message;
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
            bool student = string.Equals(role, "student", StringComparison.OrdinalIgnoreCase);
            ClassCombo.IsEnabled = student;
            ClassCombo.Visibility = student ? Visibility.Visible : Visibility.Hidden;
            if (student && ClassCombo.SelectedIndex < 0 && ClassCombo.Items.Count > 0)
                ClassCombo.SelectedIndex = 0;
            if (!student) ClassCombo.SelectedIndex = -1;
            RefreshUserOptions();
            _loadingSelection = false;
            UpdateSelectionState();
        }

        private void ClassCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingSelection) return;
            RefreshUserOptions();
            UpdateSelectionState();
        }

        private void RefreshUserOptions()
        {
            string role = RoleCombo.SelectedValue as string ?? "";
            string classId = ClassCombo.SelectedValue as string ?? "";
            var options = _users
                .Where(user => string.Equals(user.Role, role, StringComparison.OrdinalIgnoreCase))
                .Where(user => !string.Equals(role, "student", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(user.ClassId, classId, StringComparison.OrdinalIgnoreCase))
                .Select(user => new FingerprintUserOption { User = user })
                .ToList();
            UserCombo.ItemsSource = options;
            if (options.Count > 0) UserCombo.SelectedIndex = 0;
        }

        private void SelectionChanged(object sender, SelectionChangedEventArgs e) =>
            RefreshFingerprintSelectionState(sender);

        private void RefreshFingerprintSelectionState(object sender)
        {
            if (ReferenceEquals(sender, UserCombo)) RefreshFingerOptions();
            UpdateSelectionState();
        }

        private void RefreshFingerOptions()
        {
            string userId = (UserCombo.SelectedItem as FingerprintUserOption)?.UserId ?? "";
            HashSet<int> used = App.FingerprintTemplateService.GetUsedFingerIndexes(userId).ToHashSet();
            FingerCombo.ItemsSource = FingerOption.All.Where(item => !used.Contains(item.FingerIndex)).ToList();
            if (FingerCombo.Items.Count > 0) FingerCombo.SelectedIndex = 0;
        }

        private void UpdateSelectionState()
        {
            if (_loadingSelection || _enrolling) return;
            EnrollButton.IsEnabled = UserCombo.SelectedItem is FingerprintUserOption &&
                DeviceCombo.SelectedItem is FingerprintDeviceOption &&
                FingerCombo.SelectedItem is FingerOption;
            if (UserCombo.SelectedItem is FingerprintUserOption selected)
            {
                int count = App.FingerprintTemplateService.GetUsedFingerIndexes(selected.UserId).Count;
                StepHint.Text = FingerCombo.Items.Count == 0
                    ? $"{selected.User.Name} 的 10 枚手指均已录入"
                    : $"已选择 {selected.User.Name}，当前有 {count} 枚用户指纹";
            }
        }

        private async void EnrollButton_Click(object sender, RoutedEventArgs e)
        {
            if (UserCombo.SelectedItem is not FingerprintUserOption userOption ||
                DeviceCombo.SelectedItem is not FingerprintDeviceOption deviceOption ||
                FingerCombo.SelectedItem is not FingerOption fingerOption)
                return;
            User user = userOption.User;

            _enrolling = true;
            _lastDeviceId = deviceOption.DeviceId;
            SetSelectionEnabled(false);
            CancelButton.Content = "取消录入";
            EnrollProgress.Value = 0;
            StepIcon.Text = "\uE928";
            StepHint.Text = "正在发送录入指令";
            StepDetail.Text = "";
            ResultText.Text = "";
            TestButton.Visibility = Visibility.Collapsed;

            try
            {
                int targetFingerprintId = App.UserService.GetNextFingerprintIdLocal();
                FingerprintEnrollmentResult result = await App.CommandService.EnrollFingerprintAsync(
                    deviceOption.DeviceId, user.UserId, targetFingerprintId, true, 210_000,
                    (phase, step, total, hint) => Dispatcher.Invoke(() =>
                    {
                        EnrollProgress.Value = step;
                        EnrollProgress.Maximum = total;
                        StepHint.Text = hint;
                        StepDetail.Text = $"步骤 {step}/{total}（{phase}）";
                        StepIcon.Text = phase.StartsWith("verify") ? "\uE73E" : "\uE928";
                    }));
                if (!result.Success)
                {
                    StepIcon.Text = "\uE783";
                    StepHint.Text = "录入失败";
                    ResultText.Text = string.IsNullOrWhiteSpace(result.ErrorMessage)
                        ? "柜机未能完成指纹录入" : result.ErrorMessage;
                    return;
                }

                EnrolledFingerprintId = result.FingerprintId;
                EnrolledTemplate = result.TemplateBytes;
                EnrolledUserId = user.UserId;
                string summary = $"录入成功，{fingerOption.DisplayText} #{result.FingerprintId} 已绑定 {user.Name}。";
                if (result.TemplateBytes == null || result.TemplateBytes.Length == 0)
                {
                    StepIcon.Text = "\uE783";
                    StepHint.Text = "模板导出失败";
                    ResultText.Text = summary + "\n柜机已录入，但没有取得模板，无法自动同步或测试。";
                    return;
                }

                bool saved = await Task.Run(() =>
                    App.FingerprintTemplateService.SaveEnrolledTemplate(
                        result.FingerprintId, result.TemplateBytes,
                        deviceOption.DeviceId, user.UserId,
                        fingerOption.FingerIndex, fingerOption.DisplayText));
                bool bound = saved && await Task.Run(() =>
                    App.FingerprintTemplateService.BindToUser(result.FingerprintId, user.UserId));
                if (!bound)
                {
                    StepHint.Text = "录入成功，用户绑定失败";
                    ResultText.Text = summary + "\n请检查用户数据后重试绑定。";
                    return;
                }

                user = App.UserService.GetUser(user.UserId) ?? user;
                App.CabinetBindingService.AssignFingerprintToEmptyAssignments(
                    user.UserId, result.FingerprintId);
                user = App.UserService.GetUser(user.UserId) ?? user;
                if (App.SdStorageService.IsAvailable)
                {
                    bool uploaded = await App.FingerprintTemplateService.UploadToSdAsync(result.FingerprintId);
                    summary += uploaded ? "\n模板已备份到 SD。" : "\n模板暂存本机，SD 备份待重试。";
                }
                else
                {
                    summary += "\nSD 当前不可用，模板暂存本机。";
                }

                StepHint.Text = "正在校验绑定柜机";
                IReadOnlyList<UserCabinetSyncResult> sync = await App.CabinetSyncService
                    .VerifyAndSyncUserAsync(user);
                int changed = sync.Count(item => item.Success && item.Changed);
                int unchanged = sync.Count(item => item.Success && !item.Changed);
                int failed = sync.Count(item => !item.Success);
                summary += $"\n在线绑定柜机：已更新 {changed}，无需更新 {unchanged}，失败 {failed}。";

                StepIcon.Text = failed == 0 ? "\uE73E" : "\uE7BA";
                StepHint.Text = failed == 0 ? "录入及同步完成" : "录入完成，部分柜机待重试";
                StepDetail.Text = $"指纹 ID：{result.FingerprintId}";
                ResultText.Text = summary;
                TestButton.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                StepIcon.Text = "\uE783";
                StepHint.Text = "录入异常";
                ResultText.Text = ex.Message;
            }
            finally
            {
                _enrolling = false;
                CancelButton.Content = "关闭";
                SetSelectionEnabled(true);
                UpdateSelectionState();
            }
        }

        private sealed class FingerOption
        {
            public int FingerIndex { get; init; }
            public string DisplayText { get; init; } = "";

            public static IReadOnlyList<FingerOption> All { get; } = new[]
            {
                new FingerOption { FingerIndex = 1, DisplayText = "左手拇指" },
                new FingerOption { FingerIndex = 2, DisplayText = "左手食指" },
                new FingerOption { FingerIndex = 3, DisplayText = "左手中指" },
                new FingerOption { FingerIndex = 4, DisplayText = "左手无名指" },
                new FingerOption { FingerIndex = 5, DisplayText = "左手小指" },
                new FingerOption { FingerIndex = 6, DisplayText = "右手拇指" },
                new FingerOption { FingerIndex = 7, DisplayText = "右手食指" },
                new FingerOption { FingerIndex = 8, DisplayText = "右手中指" },
                new FingerOption { FingerIndex = 9, DisplayText = "右手无名指" },
                new FingerOption { FingerIndex = 10, DisplayText = "右手小指" }
            };
        }

        private void SetSelectionEnabled(bool enabled)
        {
            bool fixedUser = !string.IsNullOrWhiteSpace(_presetUserId);
            RoleCombo.IsEnabled = enabled && !fixedUser;
            ClassCombo.IsEnabled = enabled && !fixedUser &&
                string.Equals(RoleCombo.SelectedValue as string, "student", StringComparison.OrdinalIgnoreCase);
            UserCombo.IsEnabled = enabled && !fixedUser;
            DeviceCombo.IsEnabled = enabled && string.IsNullOrWhiteSpace(_presetDeviceId);
            FingerCombo.IsEnabled = enabled;
            EnrollButton.IsEnabled = enabled;
        }

        private void TestButton_Click(object sender, RoutedEventArgs e)
        {
            if (EnrolledFingerprintId <= 0 || string.IsNullOrWhiteSpace(EnrolledUserId)) return;
            var window = new FingerprintTestWindow(
                EnrolledUserId, EnrolledFingerprintId, _lastDeviceId) { Owner = this };
            window.ShowDialog();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_enrolling)
            {
                Close();
                return;
            }
            if (MessageBox.Show("确定取消本次指纹录入？", "取消录入",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;
            SendCancelEnroll();
            StepHint.Text = "正在取消";
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
            base.OnClosing(e);
        }
    }
}
