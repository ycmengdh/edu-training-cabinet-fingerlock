using System.Windows;
using System.Windows.Controls;

namespace CabinetLock
{
    public partial class EnrollFingerprintWindow : BorderlessWindow
    {
        private readonly string? _presetDeviceId;
        private readonly string? _presetUserId;
        private readonly bool _fixedUserMode;
        private List<User> _users = new();
        private List<ClassInfo> _classes = new();
        private bool _loadingSelection;
        private bool _dataLoaded;
        private bool _enrolling;
        private string? _lastDeviceId;
        private string? _lastEnrollmentPhase;
        private CancellationTokenSource? _progressPromptCts;

        public int EnrolledFingerprintId { get; private set; } = -1;
        public byte[]? EnrolledTemplate { get; private set; }
        public string? EnrolledDeviceId => _lastDeviceId;
        public string? EnrolledUserId { get; private set; }

        public EnrollFingerprintWindow() : this(null, null, false)
        {
        }

        public EnrollFingerprintWindow(
            string? presetDeviceId, string? presetUserId = null,
            bool fixedStudentMode = false, bool fixedUserMode = false)
        {
            _presetDeviceId = presetDeviceId;
            _presetUserId = presetUserId;
            _fixedUserMode = fixedStudentMode || fixedUserMode;
            InitializeComponent();
            if (_fixedUserMode)
            {
                Height = 620;
                SubtitleText.Text = "当前用户已锁定；采集使用柜机 0 号临时槽，验证后清空，只保存用户模板。";
                UserSelectionPanel.Visibility = Visibility.Collapsed;
                FixedStudentPanel.Visibility = Visibility.Visible;
            }
            StepHint.Text = "正在读取用户和采集设备";
            StepDetail.Text = "窗口已打开，数据加载完成后即可录入";
            SetSelectionEnabled(false);
            Loaded += async (_, _) => await LoadDataAsync();
        }

        private async Task LoadDataAsync()
        {
            if (_dataLoaded) return;
            _dataLoaded = true;
            _loadingSelection = true;
            try
            {
                SelectionData data = await Task.Run(BuildSelectionData);
                _users = data.Users;
                _classes = data.Classes;
                RoleCombo.ItemsSource = FingerprintSelectionData.BuildRoles(_users);
                ClassCombo.ItemsSource = _classes.Select(item => new FingerprintClassOption
                {
                    ClassId = item.ClassId,
                    DisplayText = $"{item.Name} ({item.ClassId})"
                }).ToList();
                DeviceCombo.ItemsSource = data.Devices;

                User? preset = _users.FirstOrDefault(user =>
                    string.Equals(user.UserId, _presetUserId, StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(_presetUserId) && preset == null)
                {
                    FixedStudentNameText.Text = "用户信息不可用";
                    FixedStudentMetaText.Text = "当前用户不存在，或没有该用户的管理权限";
                    StepHint.Text = "无法读取当前用户";
                    StepDetail.Text = "请关闭窗口并刷新用户数据后重试";
                    ResultText.Text = "为了避免将指纹录入到其他用户，已停止本次录入。";
                    return;
                }
                if (preset != null)
                {
                    RoleCombo.SelectedValue = preset.Role;
                    if (string.Equals(preset.Role, "student", StringComparison.OrdinalIgnoreCase))
                        ClassCombo.SelectedValue = preset.ClassId;
                    if (_fixedUserMode) LoadFixedUser(preset);
                }
                else if (RoleCombo.Items.Count > 0)
                {
                    RoleCombo.SelectedIndex = 0;
                }
                bool studentRole = string.Equals(RoleCombo.SelectedValue as string,
                    "student", StringComparison.OrdinalIgnoreCase);
                ClassCombo.IsEnabled = studentRole;
                ClassCombo.Visibility = studentRole ? Visibility.Visible : Visibility.Hidden;
                if (_fixedUserMode && preset != null)
                {
                    UserCombo.ItemsSource = new[]
                    {
                        new FingerprintUserOption { User = preset }
                    };
                    UserCombo.SelectedIndex = 0;
                }
                else
                {
                    RefreshUserOptions();
                    if (preset != null) UserCombo.SelectedValue = preset.UserId;
                }
                RefreshFingerOptions();

                if (!string.IsNullOrWhiteSpace(_presetDeviceId))
                    DeviceCombo.SelectedValue = _presetDeviceId;
                if (DeviceCombo.SelectedIndex < 0 && DeviceCombo.Items.Count > 0)
                    DeviceCombo.SelectedIndex = 0;
                SetSelectionEnabled(true);
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

        private SelectionData BuildSelectionData()
        {
            List<User> users;
            if (!string.IsNullOrWhiteSpace(_presetUserId))
            {
                User? preset = App.UserService.GetUser(_presetUserId);
                users = preset != null && DataScopeContext.Instance.CanModify(preset)
                    ? new List<User> { preset }
                    : new List<User>();
            }
            else
            {
                users = App.UserService.GetVisibleUsers()
                    .Where(user => DataScopeContext.Instance.CanModify(user))
                    .OrderBy(user => user.Name)
                    .ThenBy(user => user.UserId)
                    .ToList();
            }

            List<ClassInfo> classes = App.ClassService.GetVisible()
                .Where(item => item.Enabled)
                .OrderBy(item => item.Name)
                .ToList();
            List<FingerprintDeviceOption> devices =
                FingerprintSelectionData.LoadOnlineCabinets();
            return new SelectionData(users, classes, devices);
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
            List<FingerprintTemplate> templates =
                App.FingerprintTemplateService.GetTemplatesForUser(userId);
            List<FingerOption> options = FingerOption.All.Select(item =>
            {
                FingerprintTemplate? existing = templates.FirstOrDefault(template =>
                    template.FingerIndex == item.FingerIndex);
                return new FingerOption
                {
                    FingerIndex = item.FingerIndex,
                    BaseName = item.BaseName,
                    ExistingFingerprintId = existing?.FingerprintId,
                    DisplayText = existing == null
                        ? $"{item.BaseName} · 新增"
                        : $"{item.BaseName} · 覆盖现有 #{existing.FingerprintId}"
                };
            }).ToList();
            FingerCombo.ItemsSource = options;
            int firstUnused = options.FindIndex(item => !item.ExistingFingerprintId.HasValue);
            FingerCombo.SelectedIndex = firstUnused >= 0 ? firstUnused : 0;
        }

        private void LoadFixedUser(User user)
        {
            string className = _classes.FirstOrDefault(item => string.Equals(
                item.ClassId, user.ClassId, StringComparison.OrdinalIgnoreCase))?.Name ??
                (string.IsNullOrWhiteSpace(user.ClassId) ? "未分班" : user.ClassId);
            FixedStudentNameText.Text = string.IsNullOrWhiteSpace(user.Name)
                ? user.DisplayId : user.Name;
            FixedStudentMetaText.Text = user.Role switch
            {
                "student" => $"学号：{user.DisplayId}  ·  班级：{className}  ·  用户已锁定",
                "teacher" => $"教师 ID：{user.DisplayId}  ·  用户已锁定",
                _ => $"账号 ID：{user.DisplayId}  ·  用户已锁定"
            };
        }

        private void UpdateSelectionState(bool updateMessage = true)
        {
            if (_loadingSelection || _enrolling) return;
            EnrollButton.IsEnabled = UserCombo.SelectedItem is FingerprintUserOption &&
                DeviceCombo.SelectedItem is FingerprintDeviceOption &&
                FingerCombo.SelectedItem is FingerOption;
            if (_fixedUserMode && DeviceCombo.SelectedItem is not FingerprintDeviceOption)
            {
                if (updateMessage)
                {
                    StepHint.Text = DeviceCombo.Items.Count == 0
                        ? "没有在线采集设备" : "请选择采集设备";
                    StepDetail.Text = DeviceCombo.Items.Count == 0
                        ? "请检查柜机连接状态后重试" : "用户信息已锁定，可调整要录入的手指";
                }
                return;
            }
            if (updateMessage &&
                UserCombo.SelectedItem is FingerprintUserOption selected &&
                FingerCombo.SelectedItem is FingerOption finger)
            {
                int targetId;
                try
                {
                    targetId = ResolveTargetFingerprintId(selected, finger);
                }
                catch (InvalidOperationException ex)
                {
                    EnrollButton.IsEnabled = false;
                    StepHint.Text = "没有可用的指纹 ID";
                    StepDetail.Text = ex.Message;
                    return;
                }
                StepHint.Text = finger.ExistingFingerprintId.HasValue
                    ? $"{finger.BaseName}将覆盖指纹 ID #{targetId}"
                    : $"{finger.BaseName}将录入到指纹 ID #{targetId}";
                StepDetail.Text = $"{selected.User.Name} · 采集 4 次后验证 2 次，两次通过才保存";
            }
        }

        private static int ResolveTargetFingerprintId(
            FingerprintUserOption user, FingerOption finger)
        {
            if (finger.ExistingFingerprintId.HasValue)
                return finger.ExistingFingerprintId.Value;

            int? specifiedId = user.User.FingerprintId;
            if (specifiedId is > 0 &&
                App.FingerprintTemplateService.GetTemplate(specifiedId.Value) == null)
            {
                return specifiedId.Value;
            }

            return App.UserService.GetNextFingerprintIdLocal();
        }

        private static string FormatProgressDetail(
            string phase, int step, int total, int targetFingerprintId) => phase switch
            {
                "place_1" or "lift_1" => $"采集 1/4 · 目标指纹 ID #{targetFingerprintId}",
                "place_2" or "lift_2" => $"采集 2/4 · 目标指纹 ID #{targetFingerprintId}",
                "place_3" or "lift_3" => $"采集 3/4 · 目标指纹 ID #{targetFingerprintId}",
                "place_4" or "storing" => $"采集 4/4 · 目标指纹 ID #{targetFingerprintId}",
                "verify_lift_1" => $"验证准备 1/2 · 等待手指松开 · 目标指纹 ID #{targetFingerprintId}",
                "verify_retry_lift_1" => $"验证 1/2 · 正在等待重新验证 · 目标指纹 ID #{targetFingerprintId}",
                "verify_place_1" or "verify_1" => $"验证 1/2 · 目标指纹 ID #{targetFingerprintId}",
                "verify_lift_2" => $"验证准备 2/2 · 等待手指松开 · 目标指纹 ID #{targetFingerprintId}",
                "verify_retry_lift_2" => $"验证 2/2 · 正在等待重新验证 · 目标指纹 ID #{targetFingerprintId}",
                "verify_place_2" or "verify_2" => $"验证 2/2 · 目标指纹 ID #{targetFingerprintId}",
                "success" => $"验证 2/2 · 模板已导出，临时槽 0 已清空",
                _ => $"进度 {step}/{total} · 目标指纹 ID #{targetFingerprintId}"
            };

        private void ShowEnrollmentProgress(
            string phase, int step, int total, string hint, int targetFingerprintId)
        {
            _lastEnrollmentPhase = phase;
            _progressPromptCts?.Cancel();
            var cancellation = new CancellationTokenSource();
            _progressPromptCts = cancellation;
            _ = ShowEnrollmentProgressAsync(
                phase, step, total, hint, targetFingerprintId, cancellation.Token);
        }

        private async Task ShowEnrollmentProgressAsync(
            string phase, int step, int total, string hint,
            int targetFingerprintId, CancellationToken cancellationToken)
        {
            try
            {
                int delay = FingerprintEnrollmentPrompts.GetDisplayDelayMilliseconds(phase);
                if (delay > 0) await Task.Delay(delay, cancellationToken);
                await Dispatcher.InvokeAsync(() =>
                {
                    EnrollProgress.Maximum = total;
                    EnrollProgress.Value = FingerprintEnrollmentPrompts.GetProgressValue(
                        phase, step, total);
                    StepHint.Text = hint;
                    StepDetail.Text = FormatProgressDetail(
                        phase, step, total, targetFingerprintId);
                    StepIcon.Text = FingerprintEnrollmentPrompts.IsVerificationPhase(phase)
                        ? "\uE73E" : "\uE928";
                });
            }
            catch (OperationCanceledException)
            {
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
            _lastEnrollmentPhase = null;
            SetSelectionEnabled(false);
            CancelButton.Content = "取消录入";
            EnrollProgress.Value = 0;
            _progressPromptCts?.Cancel();
            StepIcon.Text = "\uE928";
            StepHint.Text = "正在发送录入指令";
            StepDetail.Text = "";
            ResultText.Text = "";
            TestButton.Visibility = Visibility.Collapsed;

            try
            {
                bool overwrite = fingerOption.ExistingFingerprintId.HasValue;
                int targetFingerprintId = ResolveTargetFingerprintId(userOption, fingerOption);
                StepHint.Text = $"准备录入{fingerOption.BaseName}";
                StepDetail.Text = $"临时槽 0 采集 · 模板 ID #{targetFingerprintId} · 按提示完成 4 次采集和 2 次验证";
                FingerprintEnrollmentResult result = await App.CommandService.EnrollFingerprintAsync(
                    deviceOption.DeviceId, user.UserId, targetFingerprintId, true, 210_000,
                    (phase, step, total, hint) => ShowEnrollmentProgress(
                        phase, step, total, hint, targetFingerprintId));
                _progressPromptCts?.Cancel();
                if (!result.Success)
                {
                    StepIcon.Text = "\uE783";
                    StepHint.Text = "录入失败";
                    StepDetail.Text = $"指纹 ID #{targetFingerprintId} · 未保存";
                    ResultText.Text = FingerprintEnrollmentPrompts.EnhanceFailureForPhase(
                        result.ErrorMessage, _lastEnrollmentPhase);
                    return;
                }

                EnrolledFingerprintId = result.FingerprintId;
                EnrolledTemplate = result.TemplateBytes;
                EnrolledUserId = user.UserId;
                EnrollProgress.Value = EnrollProgress.Maximum;
                StepIcon.Text = "\uE73E";
                StepHint.Text = "两次验证通过，正在保存指纹模板";
                StepDetail.Text = $"目标指纹 ID #{result.FingerprintId} · 请稍候";
                await Task.Delay(450);
                string summary = overwrite
                    ? $"覆盖成功，{user.Name} 的{fingerOption.BaseName}模板 #{result.FingerprintId} 已更新。"
                    : $"录入成功，{user.Name} 的{fingerOption.BaseName}模板 #{result.FingerprintId} 已保存。";
                if (result.TemplateBytes == null || result.TemplateBytes.Length == 0)
                {
                    StepIcon.Text = "\uE783";
                    StepHint.Text = "模板导出失败";
                    ResultText.Text = summary + "\n没有取得模板，无法保存到用户模板库。";
                    return;
                }

                bool saved = await Task.Run(() =>
                    App.FingerprintTemplateService.SaveEnrolledTemplate(
                        result.FingerprintId, result.TemplateBytes,
                        deviceOption.DeviceId, user.UserId,
                        fingerOption.FingerIndex, fingerOption.BaseName));
                bool bound = saved && await Task.Run(() =>
                    App.FingerprintTemplateService.BindToUser(result.FingerprintId, user.UserId));
                if (!bound)
                {
                    StepHint.Text = "录入成功，用户绑定失败";
                    ResultText.Text = summary + "\n请检查用户数据后重试绑定。";
                    return;
                }

                if (App.SdStorageService.IsAvailable)
                {
                    bool uploaded = await App.FingerprintTemplateService.UploadToSdAsync(result.FingerprintId);
                    summary += uploaded ? "\n模板已备份到 SD。" : "\n模板暂存本机，SD 备份待重试。";
                }
                else
                {
                    summary += "\nSD 当前不可用，模板暂存本机。";
                }

                summary += string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase)
                    ? "\n柜机 0 号临时槽已清空。需要使用时，请在柜子详情中点击“绑定用户”再下发。"
                    : "\n柜机 0 号临时槽已清空。管理员和教师指纹已自动绑定全部柜机；在线柜立即处理，离线柜上线后继续同步。";
                StepIcon.Text = "\uE73E";
                StepHint.Text = "指纹录入成功";
                StepDetail.Text = $"两次验证均已通过 · 用户模板 ID：{result.FingerprintId}";
                ResultText.Text = summary;
                TestButton.Visibility = Visibility.Visible;
                RefreshFingerOptions();
            }
            catch (Exception ex)
            {
                StepIcon.Text = "\uE783";
                StepHint.Text = "录入异常";
                ResultText.Text = FingerprintEnrollmentPrompts.EnhanceFailureForPhase(
                    ex.Message, _lastEnrollmentPhase);
            }
            finally
            {
                _enrolling = false;
                _progressPromptCts?.Cancel();
                CancelButton.Content = "关闭";
                SetSelectionEnabled(true);
                UpdateSelectionState(updateMessage: false);
            }
        }

        private sealed class FingerOption
        {
            public int FingerIndex { get; init; }
            public string DisplayText { get; init; } = "";
            public string BaseName { get; init; } = "";
            public int? ExistingFingerprintId { get; init; }

            public static IReadOnlyList<FingerOption> All { get; } = new[]
            {
                new FingerOption { FingerIndex = 6, BaseName = "右手拇指", DisplayText = "右手拇指" },
                new FingerOption { FingerIndex = 7, BaseName = "右手食指", DisplayText = "右手食指" },
                new FingerOption { FingerIndex = 8, BaseName = "右手中指", DisplayText = "右手中指" },
                new FingerOption { FingerIndex = 9, BaseName = "右手无名指", DisplayText = "右手无名指" },
                new FingerOption { FingerIndex = 10, BaseName = "右手小指", DisplayText = "右手小指" },
                new FingerOption { FingerIndex = 1, BaseName = "左手拇指", DisplayText = "左手拇指" },
                new FingerOption { FingerIndex = 2, BaseName = "左手食指", DisplayText = "左手食指" },
                new FingerOption { FingerIndex = 3, BaseName = "左手中指", DisplayText = "左手中指" },
                new FingerOption { FingerIndex = 4, BaseName = "左手无名指", DisplayText = "左手无名指" },
                new FingerOption { FingerIndex = 5, BaseName = "左手小指", DisplayText = "左手小指" }
            };
        }

        private sealed record SelectionData(
            List<User> Users,
            List<ClassInfo> Classes,
            List<FingerprintDeviceOption> Devices);

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
                EnrolledUserId, EnrolledFingerprintId, _lastDeviceId)
            { Owner = this };
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
            _progressPromptCts?.Cancel();
            base.OnClosing(e);
        }
    }
}
