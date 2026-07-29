using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FingerprintLockManager
{
    public partial class ClassStudentsPage : Page
    {
        private readonly string _classId;
        private readonly string _className;
        private List<ClassStudentRow> _rows = new();
        private readonly ListPager _studentPager = new(50);
        private bool _busy;

        public ClassStudentsPage(string classId, string className)
        {
            InitializeComponent();
            _classId = classId;
            _className = className;
            PageTitleText.Text = $"{className} · 学生";
            ClassNameText.Text = className;
            ClassIdText.Text = $"班级 ID：{classId}";
            Loaded += async (_, _) => await LoadAsync();
        }

        private async Task LoadAsync(bool resetStudentPage = true)
        {
            if (resetStudentPage) _studentPager.Reset();
            SetBusy(true, "正在读取学生、柜子和指纹数据");
            try
            {
                ClassWorkspaceSnapshot workspace = await Task.Run(BuildWorkspace);
                _rows = workspace.Students;
                ApplyStudentPage();
                CabinetOverviewGrid.ItemsSource = workspace.Cabinets;
                SyncStatusGrid.ItemsSource = workspace.SyncRows;
                StudentCountText.Text = _rows.Count.ToString();
                FingerprintCountText.Text = _rows.Count(row => row.FingerprintCount > 0).ToString();
                AssignedCountText.Text = _rows.Count(row => row.BoundCabinetIds.Count > 0).ToString();
                ReadyCountText.Text = _rows.Count(row => row.IsReady).ToString();
                PendingCountText.Text = _rows.Count(row => !row.IsReady).ToString();
                var teachers = await Task.Run(() => App.UserService.GetAllUsers()
                    .Where(user => string.Equals(user.Role, "teacher", StringComparison.OrdinalIgnoreCase)
                        && string.Equals(user.ClassId, _classId, StringComparison.OrdinalIgnoreCase))
                    .Select(user => string.IsNullOrWhiteSpace(user.Name) ? user.UserId : user.Name)
                    .ToList());
                TeacherText.Text = teachers.Count == 0
                    ? "老师：未分配"
                    : "老师：" + string.Join("、", teachers);
                int pageCount = (StudentDataGrid.ItemsSource as System.Collections.ICollection)?.Count ?? 0;
                PageStatusText.Text =
                    $"{_studentPager.StatusText(pageCount)} · 可使用 {_rows.Count(row => row.IsReady)} · 待处理 {_rows.Count(row => !row.IsReady)}";
            }
            catch (RootDataUnavailableException ex)
            {
                StudentDataGrid.ItemsSource = null;
                PageStatusText.Text = ex.Message;
            }
            catch (Exception ex)
            {
                StudentDataGrid.ItemsSource = null;
                PageStatusText.Text = $"学生数据读取失败：{ex.Message}";
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ApplyStudentPage()
        {
            var page = _studentPager.Slice(_rows);
            StudentDataGrid.ItemsSource = page;
            _studentPager.BindChrome(StudentPrevPageButton, StudentNextPageButton, StudentPageInfoText);
        }

        private void StudentPrevPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_studentPager.Prev()) ApplyStudentPage();
        }

        private void StudentNextPageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_studentPager.Next()) ApplyStudentPage();
        }

        private ClassWorkspaceSnapshot BuildWorkspace()
        {
            var users = App.UserService.GetAllUsers()
                .Where(user => string.Equals(user.Role, "student", StringComparison.OrdinalIgnoreCase)
                    && string.Equals(user.ClassId, _classId, StringComparison.OrdinalIgnoreCase))
                .OrderBy(user => user.UserId)
                .ToList();
            var devices = App.DeviceService.GetAllDevices()
                .Where(device => !DeviceService.IsTrueRoot(device) && !string.IsNullOrWhiteSpace(device.DeviceId))
                .ToList();
            var templates = BusinessDatabase.ReadAllFpTemplateMetas();
            IReadOnlyList<CabinetSyncJob> syncJobs = App.CabinetSyncQueueService.GetAll();

            List<ClassStudentRow> students = users.Select(user =>
            {
                IReadOnlyList<CabinetAssignment> assignments = App.CabinetBindingService
                    .GetAssignments(user, devices.Select(device => device.DeviceId));
                var bound = devices.Where(device => assignments.Any(item => string.Equals(
                    item.DeviceId, device.DeviceId, StringComparison.OrdinalIgnoreCase))).ToList();
                List<FingerprintTemplate> userTemplates = templates.Where(template =>
                        string.Equals(template.UserId, user.UserId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(template => template.FingerIndex)
                    .ToList();
                return new ClassStudentRow(user, bound, userTemplates, assignments,
                    syncJobs.Where(job =>
                        (job.JobKind == "user" && string.Equals(
                            job.UserId, user.UserId, StringComparison.OrdinalIgnoreCase)) ||
                        (job.JobKind == "cabinet" && assignments.Any(assignment => string.Equals(
                            assignment.DeviceId, job.DeviceId, StringComparison.OrdinalIgnoreCase))))
                    .ToList());
            }).ToList();

            List<ClassCabinetOverviewRow> cabinets = devices
                .OrderBy(device => device.DeviceNumber).ThenBy(device => device.DeviceName)
                .Select(device => new ClassCabinetOverviewRow(device, students))
                .ToList();
            List<ClassStudentSyncRow> syncRows = students
                .SelectMany(student => student.BuildSyncRows(devices))
                .OrderBy(row => row.DeviceNumber).ThenBy(row => row.UserId)
                .ToList();
            return new ClassWorkspaceSnapshot(students, cabinets, syncRows);
        }

        private async void RefreshButton_Click(object sender, RoutedEventArgs e) => await LoadAsync();

        private void ImportStudentsButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new ImportUsersWindow(_classId)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
            if (window.AnyImported) _ = LoadAsync();
        }

        private void BatchEnrollButton_Click(object sender, RoutedEventArgs e)
        {
            List<User> selected = StudentDataGrid.SelectedItems.OfType<ClassStudentRow>()
                .Select(row => row.User).ToList();
            if (selected.Count == 0)
                selected = _rows.Where(row => row.FingerprintCount == 0).Select(row => row.User).ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("请选择需要录入的学生", "连续录入",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var window = new ContinuousEnrollmentWindow(_className, selected)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
            _ = LoadAsync();
        }

        private void CabinetSyncButton_Click(object sender, RoutedEventArgs e)
        {
            var window = new ClassCabinetSyncWindow(_classId, _className)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
            _ = LoadAsync();
        }

        private void OpenCabinetAssignmentButton_Click(object sender, RoutedEventArgs e)
        {
            WorkspaceTabs.SelectedIndex = 1;
            CabinetSyncButton_Click(sender, e);
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            if (NavigationService?.CanGoBack == true) NavigationService.GoBack();
            else NavigationService?.Navigate(new ClassManagePage());
        }

        private async void AddStudentButton_Click(object sender, RoutedEventArgs e)
        {
            if (!ShowStudentDialog(null, out string userId, out string name, out string gender,
                    out bool enabled, out string? deviceId)) return;
            SetBusy(true, "正在添加学生");
            try
            {
                bool saved = await Task.Run(() => App.UserService.AddUser(new User
                {
                    UserId = userId.Trim(),
                    Name = name.Trim(),
                    Gender = gender,
                    Role = "student",
                    ClassId = _classId,
                    AssignedDeviceIds = new List<string>(),
                    Enabled = enabled,
                    CreateTime = DateTime.Now
                }));
                if (saved && !string.IsNullOrWhiteSpace(deviceId))
                {
                    var ids = App.DeviceService.GetAllDevices()
                        .Where(device => !DeviceService.IsTrueRoot(device))
                        .Select(device => device.DeviceId)
                        .ToArray();
                    App.CabinetBindingService.AssignExclusive(userId, deviceId, ids);
                }
                MessageBox.Show(saved ? "学生已添加" : "添加失败，学号可能已存在",
                    saved ? "完成" : "错误", MessageBoxButton.OK,
                    saved ? MessageBoxImage.Information : MessageBoxImage.Error);
                if (saved) await LoadAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "添加学生失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void EditStudentButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ClassStudentRow row) return;
            await EditStudentAsync(row.User);
        }

        private async void ManageStudentButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ClassStudentRow row) return;
            OpenStudentDetail(row.User);
        }

        private void StudentDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source && FindVisualParent<DataGridRow>(source) == null) return;
            if (StudentDataGrid.SelectedItem is ClassStudentRow row) OpenStudentDetail(row.User);
        }

        private void OpenStudentDetail(User user)
        {
            var window = new StudentDetailWindow(user, _className)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
            _ = LoadAsync();
        }

        private async void DeleteStudentButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ClassStudentRow row) return;
            if (MessageBox.Show($"确认删除学生「{row.Name}（{row.UserId}）」？\n将同时删除其权限、柜子绑定和全部用户指纹。",
                    "确认删除学生", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            SetBusy(true, "正在清理学生业务数据和柜子指纹");
            try
            {
                string? cleanupWarning = null;
                bool deleted = await Task.Run(() => App.UserService.DeleteUser(row.UserId));
                if (!deleted)
                {
                    MessageBox.Show("学生删除失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                foreach (int fingerprintId in row.Templates.Select(item => item.FingerprintId).Distinct())
                {
                    var deleteResult = await App.CabinetSyncService
                        .DeleteFingerprintFromOnlineCabinetsAsync(fingerprintId);
                    App.FingerprintTemplateService.DeleteTemplate(fingerprintId);
                    if (!deleteResult.Success)
                        cleanupWarning = "学生已从业务库删除，但部分在线柜子未确认全部指纹清理";
                }
                App.CabinetBindingService.RemoveFromAll(row.UserId);
                try { await App.SdStorageService.DeleteTemplateAsync(row.UserId); } catch { }
                try { await Task.Run(App.CabinetSyncService.SyncAllPermissions); } catch { }
                await LoadAsync();
                if (!string.IsNullOrWhiteSpace(cleanupWarning)) PageStatusText.Text = cleanupWarning;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "删除学生失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async Task EditStudentAsync(User user)
        {
            if (!ShowStudentDialog(user, out _, out string name, out string gender,
                    out bool enabled, out string? deviceId)) return;
            var updated = new User
            {
                UserId = user.UserId,
                Name = name.Trim(),
                Gender = gender,
                Role = user.Role,
                ClassId = user.ClassId,
                AssignedDeviceIds = user.AssignedDeviceIds?.ToList(),
                CabinetAssignments = user.CabinetAssignments?.Select(item => new CabinetAssignment
                {
                    DeviceId = item.DeviceId,
                    ActiveFingerprintId = item.ActiveFingerprintId,
                    UpdateTime = item.UpdateTime
                }).ToList(),
                FingerprintId = user.FingerprintId,
                PasswordSalt = user.PasswordSalt,
                PasswordHash = user.PasswordHash,
                Enabled = enabled,
                CreateTime = user.CreateTime,
                UpdateTime = user.UpdateTime
            };
            SetBusy(true, "正在保存学生信息");
            try
            {
                bool saved = await Task.Run(() => App.UserService.UpdateUser(updated));
                if (!saved) MessageBox.Show("学生信息保存失败", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                else
                {
                    if (!string.IsNullOrWhiteSpace(deviceId))
                    {
                        var ids = App.DeviceService.GetAllDevices()
                            .Where(device => !DeviceService.IsTrueRoot(device))
                            .Select(device => device.DeviceId)
                            .ToArray();
                        App.CabinetBindingService.AssignExclusive(user.UserId, deviceId, ids);
                    }
                    await LoadAsync();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "保存学生失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private bool ShowStudentDialog(User? existing, out string userId, out string name,
            out string gender, out bool enabled, out string? deviceId)
        {
            userId = existing?.UserId ?? "";
            name = existing?.Name ?? "";
            gender = existing?.Gender ?? "";
            enabled = existing?.Enabled ?? true;
            deviceId = null;
            bool edit = existing != null;
            var dialog = new Window
            {
                Title = edit ? "编辑学生" : "添加学生",
                Width = 390,
                Height = 440,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Window.GetWindow(this),
                ResizeMode = ResizeMode.NoResize,
                Background = FindResource("BackgroundBrush") as Brush
            };
            var panel = new StackPanel { Margin = new Thickness(22) };
            panel.Children.Add(new TextBlock { Text = "学号", Style = FindResource("LabelText") as Style, Margin = new Thickness(0, 0, 0, 6) });
            var idBox = new TextBox { Text = userId, IsEnabled = !edit, Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(idBox);
            panel.Children.Add(new TextBlock { Text = "姓名", Style = FindResource("LabelText") as Style, Margin = new Thickness(0, 0, 0, 6) });
            var nameBox = new TextBox { Text = name, Margin = new Thickness(0, 0, 0, 12) };
            panel.Children.Add(nameBox);
            panel.Children.Add(new TextBlock { Text = "性别", Style = FindResource("LabelText") as Style, Margin = new Thickness(0, 0, 0, 6) });
            var genderBox = new ComboBox { Height = 36, Margin = new Thickness(0, 0, 0, 12) };
            genderBox.Items.Add(new ComboBoxItem { Content = "未填写", Tag = "" });
            genderBox.Items.Add(new ComboBoxItem { Content = "男", Tag = "male" });
            genderBox.Items.Add(new ComboBoxItem { Content = "女", Tag = "female" });
            genderBox.Items.Add(new ComboBoxItem { Content = "其他", Tag = "other" });
            string initialGender = gender;
            genderBox.SelectedItem = genderBox.Items.OfType<ComboBoxItem>().FirstOrDefault(item =>
                string.Equals(item.Tag?.ToString(), initialGender, StringComparison.OrdinalIgnoreCase)) ?? genderBox.Items[0];
            panel.Children.Add(genderBox);
            panel.Children.Add(new TextBlock { Text = "绑定柜机（可选）", Style = FindResource("LabelText") as Style, Margin = new Thickness(0, 0, 0, 6) });
            var cabinets = App.DeviceService.GetAllDevices()
                .Where(device => !DeviceService.IsTrueRoot(device) && !string.IsNullOrWhiteSpace(device.DeviceId))
                .OrderBy(device => device.DeviceNumber).ThenBy(device => device.DeviceName).ToList();
            var cabinetBox = new ComboBox { Height = 36, Margin = new Thickness(0, 0, 0, 12) };
            cabinetBox.Items.Add(new ComboBoxItem
            {
                Content = edit ? "保持当前柜机分配" : "暂不指定",
                Tag = ""
            });
            foreach (var cabinet in cabinets)
            {
                string label = string.IsNullOrWhiteSpace(cabinet.DeviceNumber)
                    ? $"未编号 · {cabinet.DeviceName}"
                    : $"{cabinet.DeviceNumber} · {cabinet.DeviceName}";
                cabinetBox.Items.Add(new ComboBoxItem { Content = label, Tag = cabinet.DeviceId });
            }
            cabinetBox.SelectedIndex = 0;
            panel.Children.Add(cabinetBox);
            var enabledBox = new CheckBox { Content = "启用学生账号及柜子权限", IsChecked = enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(enabledBox);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var ok = new Button { Content = "确定", Width = 78 };
            var cancel = new Button { Content = "取消", Width = 78, Margin = new Thickness(8, 0, 0, 0), Style = FindResource("SecondaryButton") as Style };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            dialog.Content = panel;
            bool confirmed = false;
            string resultUserId = userId;
            string resultName = name;
            string resultGender = gender;
            bool resultEnabled = enabled;
            string? resultDeviceId = deviceId;
            ok.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(idBox.Text) || string.IsNullOrWhiteSpace(nameBox.Text))
                {
                    MessageBox.Show("学号和姓名不能为空", "提示");
                    return;
                }
                resultUserId = idBox.Text.Trim();
                resultName = nameBox.Text.Trim();
                resultGender = (genderBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
                resultEnabled = enabledBox.IsChecked == true;
                resultDeviceId = (cabinetBox.SelectedItem as ComboBoxItem)?.Tag?.ToString();
                if (string.IsNullOrWhiteSpace(resultDeviceId)) resultDeviceId = null;
                confirmed = true;
                dialog.DialogResult = true;
            };
            cancel.Click += (_, _) => dialog.DialogResult = false;
            dialog.ShowDialog();
            if (confirmed)
            {
                userId = resultUserId;
                name = resultName;
                gender = resultGender;
                enabled = resultEnabled;
                deviceId = resultDeviceId;
            }
            return confirmed;
        }

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            RefreshButton.IsEnabled = !busy;
            AddStudentButton.IsEnabled = !busy;
            ImportStudentsButton.IsEnabled = !busy;
            BatchEnrollButton.IsEnabled = !busy;
            CabinetSyncButton.IsEnabled = !busy;
            StudentDataGrid.IsEnabled = !busy;
            CabinetOverviewGrid.IsEnabled = !busy;
            SyncStatusGrid.IsEnabled = !busy;
            if (!string.IsNullOrWhiteSpace(status)) PageStatusText.Text = status;
        }

        private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T match) return match;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }
    }

    public sealed class ClassStudentRow
    {
        public ClassStudentRow(User user, IReadOnlyList<Device> cabinets,
            IReadOnlyList<FingerprintTemplate> templates,
            IReadOnlyList<CabinetAssignment> assignments,
            IReadOnlyList<CabinetSyncJob> syncJobs)
        {
            User = user;
            Templates = templates;
            Assignments = assignments;
            SyncJobs = syncJobs;
            BoundCabinetIds = cabinets.Select(device => device.DeviceId).ToArray();
            CabinetText = cabinets.Count == 0 ? "未分配" : string.Join("、", cabinets.Select(device =>
                string.IsNullOrWhiteSpace(device.DeviceNumber)
                    ? (string.IsNullOrWhiteSpace(device.DeviceName) ? device.DeviceId : device.DeviceName)
                    : device.DeviceNumber));
            FingerprintText = templates.Count == 0
                ? "未录入"
                : $"{templates.Count} 枚 · " + string.Join("、", templates.Take(2).Select(item => item.FingerDisplayName)) +
                  (templates.Count > 2 ? "…" : "");
            IsReady = user.Enabled && templates.Any(item => item.Enabled) && assignments.Count > 0 &&
                assignments.All(assignment => assignment.ActiveFingerprintId.HasValue && templates.Any(item =>
                    item.Enabled && item.FingerprintId == assignment.ActiveFingerprintId.Value)) &&
                !syncJobs.Any(job => job.State != "completed");
            WorkflowStatus = ResolveWorkflowStatus();
        }

        public User User { get; }
        public IReadOnlyList<FingerprintTemplate> Templates { get; }
        public IReadOnlyList<CabinetAssignment> Assignments { get; }
        public IReadOnlyList<CabinetSyncJob> SyncJobs { get; }
        public string Name => User.Name;
        public string UserId => User.UserId;
        public int FingerprintCount => Templates.Count;
        public string GenderText => User.Gender switch
        {
            "male" => "男",
            "female" => "女",
            "other" => "其他",
            _ => "未填写"
        };
        public bool IsReady { get; }
        public string WorkflowStatus { get; }
        public string CabinetText { get; }
        public string FingerprintText { get; }
        public IReadOnlyList<string> BoundCabinetIds { get; }

        public IEnumerable<ClassStudentSyncRow> BuildSyncRows(IEnumerable<Device> devices)
        {
            Dictionary<string, Device> map = devices.ToDictionary(
                device => device.DeviceId, StringComparer.OrdinalIgnoreCase);
            foreach (CabinetAssignment assignment in Assignments)
            {
                map.TryGetValue(assignment.DeviceId, out Device? device);
                FingerprintTemplate? fingerprint = assignment.ActiveFingerprintId.HasValue
                    ? Templates.FirstOrDefault(item => item.Enabled &&
                        item.FingerprintId == assignment.ActiveFingerprintId.Value)
                    : null;
                string status = !assignment.ActiveFingerprintId.HasValue
                    ? "待选指纹"
                    : fingerprint == null
                        ? "指纹不可用"
                        : ResolveSyncStatus(assignment.DeviceId, device);
                string action = status switch
                {
                    "待选指纹" => "在学生管理中选择一枚指纹",
                    "指纹不可用" => "重新录入或选择其他指纹",
                    "离线待同步" => "柜机上线后自动重试",
                    _ => "执行校验同步"
                };
                yield return new ClassStudentSyncRow
                {
                    StudentName = Name,
                    UserId = UserId,
                    DeviceId = assignment.DeviceId,
                    DeviceNumber = string.IsNullOrWhiteSpace(device?.DeviceNumber)
                        ? assignment.DeviceId : device.DeviceNumber,
                    FingerprintText = fingerprint == null
                        ? "未选择" : $"{fingerprint.FingerDisplayName} #{fingerprint.FingerprintId}",
                    StatusText = status,
                    ActionText = action
                };
            }
        }

        private string ResolveWorkflowStatus()
        {
            if (!User.Enabled) return "已停用";
            if (Templates.Count == 0) return "待录指纹";
            if (Assignments.Count == 0) return "待分配柜机";
            if (Assignments.Any(assignment => !assignment.ActiveFingerprintId.HasValue)) return "待选指纹";
            if (SyncJobs.Any(job => job.State == "failed")) return "部分失败";
            if (SyncJobs.Any(job => job.State == "running")) return "同步中";
            if (SyncJobs.Any(job => job.State != "completed")) return "待同步";
            return "可使用";
        }

        private string ResolveSyncStatus(string deviceId, Device? device)
        {
            CabinetSyncJob? job = SyncJobs.FirstOrDefault(item => string.Equals(
                item.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (job != null) return job.StatusText;
            return device?.IsOnline == true ? "已配置" : "离线";
        }
    }

    public sealed class ClassCabinetOverviewRow
    {
        public ClassCabinetOverviewRow(Device device, IReadOnlyList<ClassStudentRow> students)
        {
            DeviceId = device.DeviceId;
            DeviceNumber = string.IsNullOrWhiteSpace(device.DeviceNumber) ? "未编号" : device.DeviceNumber;
            DeviceName = string.IsNullOrWhiteSpace(device.DeviceName) ? device.DeviceId : device.DeviceName;
            OnlineText = device.IsOnline ? "在线" : "离线";
            List<ClassStudentRow> assigned = students.Where(student =>
                student.BoundCabinetIds.Contains(device.DeviceId, StringComparer.OrdinalIgnoreCase)).ToList();
            StudentCount = assigned.Count;
            ReadyCount = assigned.Count(student => student.IsReady);
            PendingCount = StudentCount - ReadyCount;
        }

        public string DeviceId { get; }
        public string DeviceNumber { get; }
        public string DeviceName { get; }
        public string OnlineText { get; }
        public int StudentCount { get; }
        public int ReadyCount { get; }
        public int PendingCount { get; }
    }

    public sealed class ClassStudentSyncRow
    {
        public string StudentName { get; init; } = "";
        public string UserId { get; init; } = "";
        public string DeviceId { get; init; } = "";
        public string DeviceNumber { get; init; } = "";
        public string FingerprintText { get; init; } = "";
        public string StatusText { get; init; } = "";
        public string ActionText { get; init; } = "";
    }

    public sealed record ClassWorkspaceSnapshot(
        List<ClassStudentRow> Students,
        List<ClassCabinetOverviewRow> Cabinets,
        List<ClassStudentSyncRow> SyncRows);
}
