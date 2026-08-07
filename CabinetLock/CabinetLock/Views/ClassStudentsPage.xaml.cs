using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CabinetLock
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
            Title = $"{className} · 班级管理";
            PageTitleText.Text = Title;
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
                StudentCountText.Text = _rows.Count.ToString();
                FingerprintCountText.Text = _rows.Count(row => row.FingerprintCount > 0).ToString();
                AssignedCountText.Text = _rows.Count(row => row.BoundCabinetIds.Count > 0).ToString();
                ReadyCountText.Text = _rows.Count(row => row.IsReady).ToString();
                PendingCountText.Text = _rows.Count(row => !row.IsReady).ToString();
                TeacherText.Text = workspace.TeacherNames.Count == 0
                    ? "教师：未分配"
                    : "教师：" + string.Join("、", workspace.TeacherNames);
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
            _studentPager.BindChrome(StudentPager, "名学生");
        }

        private void StudentPager_PageRequested(object sender, Controls.PaginationRequestedEventArgs e)
        {
            _studentPager.ApplyRequest(e);
            ApplyStudentPage();
        }

        private ClassWorkspaceSnapshot BuildWorkspace()
        {
            List<User> users = App.UserService.QueryVisibleUsersPage(
                    0, 500, role: "student", classId: _classId)
                .Items.OrderBy(user => user.DisplayId).ToList();
            var devices = App.DeviceService.GetAllDevices()
                .Where(device => !DeviceService.IsTrueRoot(device) && !string.IsNullOrWhiteSpace(device.DeviceId))
                .ToList();
            string[] deviceIds = devices.Select(device => device.DeviceId).ToArray();
            string[] userIds = users.Select(user => user.UserId).ToArray();
            var devicesById = devices.ToDictionary(device => device.DeviceId,
                StringComparer.OrdinalIgnoreCase);
            Dictionary<string, IReadOnlyList<CabinetAssignment>> assignmentsByUser =
                App.CabinetBindingService.GetAssignments(users, deviceIds);
            Dictionary<string, bool[]> permissionsByUser =
                App.PermissionService.GetFinalPermissions(users);
            Dictionary<string, List<FingerprintTemplate>> templatesByUser =
                BusinessDatabase.ReadFpTemplateMetasForUsers(userIds)
                    .Where(template => !string.IsNullOrWhiteSpace(template.UserId))
                    .GroupBy(template => template.UserId!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key,
                        group => group.OrderBy(template => template.FingerIndex).ToList(),
                        StringComparer.OrdinalIgnoreCase);
            IReadOnlyList<CabinetSyncJob> syncJobs =
                App.CabinetSyncQueueService.GetRelevant(userIds, deviceIds);
            Dictionary<string, List<CabinetSyncJob>> userJobs = syncJobs
                .Where(job => job.JobKind == "user" && !string.IsNullOrWhiteSpace(job.UserId))
                .GroupBy(job => job.UserId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(),
                    StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<CabinetSyncJob>> cabinetJobs = syncJobs
                .Where(job => job.JobKind == "cabinet" && !string.IsNullOrWhiteSpace(job.DeviceId))
                .GroupBy(job => job.DeviceId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.ToList(),
                    StringComparer.OrdinalIgnoreCase);

            List<ClassStudentRow> students = users.Select(user =>
            {
                IReadOnlyList<CabinetAssignment> assignments = assignmentsByUser.TryGetValue(
                    user.UserId, out IReadOnlyList<CabinetAssignment>? assigned)
                    ? assigned : Array.Empty<CabinetAssignment>();
                List<Device> bound = assignments
                    .Select(assignment => devicesById.GetValueOrDefault(assignment.DeviceId))
                    .Where(device => device != null)
                    .Cast<Device>()
                    .ToList();
                List<FingerprintTemplate> userTemplates = templatesByUser.GetValueOrDefault(
                    user.UserId) ?? new List<FingerprintTemplate>();
                var relevantJobs = new List<CabinetSyncJob>();
                if (userJobs.TryGetValue(user.UserId, out List<CabinetSyncJob>? ownJobs))
                    relevantJobs.AddRange(ownJobs);
                foreach (CabinetAssignment assignment in assignments)
                {
                    if (cabinetJobs.TryGetValue(assignment.DeviceId,
                            out List<CabinetSyncJob>? assignedCabinetJobs))
                        relevantJobs.AddRange(assignedCabinetJobs);
                }
                bool[] permissions = permissionsByUser.GetValueOrDefault(user.UserId) ?? new bool[4];
                return new ClassStudentRow(user, bound, userTemplates, assignments,
                    relevantJobs, permissions);
            }).ToList();

            List<ClassCabinetOverviewRow> cabinets = devices
                .Select(device => new ClassCabinetOverviewRow(device, students))
                .OrderByDescending(row => row.IsOnline)
                .ThenBy(row => row.StudentCount)
                .ThenBy(row => row.DeviceNumber)
                .ThenBy(row => row.DeviceName)
                .ToList();
            IReadOnlyList<string> teacherNames =
                BusinessDatabase.ReadTeacherNamesForClass(_classId);
            return new ClassWorkspaceSnapshot(students, cabinets, teacherNames);
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
            List<User> users = StudentDataGrid.SelectedItems.OfType<ClassStudentRow>()
                .Select(row => row.User).ToList();
            if (users.Count == 0)
                users = _rows.Where(row => row.Assignments.Count == 0).Select(row => row.User).ToList();
            if (users.Count == 0)
                users = _rows.Select(row => row.User).ToList();
            if (users.Count == 0)
            {
                AppToast.Info("班级中没有可分配的学生");
                return;
            }
            var window = new BatchAssignPermissionWindow(users)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
            _ = LoadAsync();
        }

        private void OpenCabinetAssignmentButton_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not ClassCabinetOverviewRow cabinet) return;
            var picker = new CabinetStudentPickerWindow(cabinet, _rows)
            {
                Owner = Window.GetWindow(this)
            };
            if (picker.ShowDialog() != true || picker.SelectedUsers.Count == 0) return;
            var window = new BatchAssignPermissionWindow(picker.SelectedUsers, cabinet.DeviceId)
            {
                Owner = Window.GetWindow(this)
            };
            window.ShowDialog();
            _ = LoadAsync();
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
                    UserCode = userId.Trim(),
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
                    User? savedUser = App.UserService.GetUserByCode(userId);
                    var ids = App.DeviceService.GetAllDevices()
                        .Where(device => !DeviceService.IsTrueRoot(device))
                        .Select(device => device.DeviceId)
                        .ToArray();
                    if (savedUser != null)
                        App.CabinetBindingService.AssignExclusive(savedUser.UserId, deviceId, ids);
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
            if (MessageBox.Show($"确认删除学生「{row.Name}（{row.StudentNo}）」？\n将同时删除其权限、柜子绑定和全部用户指纹。",
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
            if (!ShowStudentDialog(user, out string studentNo, out string name, out string gender,
                    out bool enabled, out string? deviceId)) return;
            var updated = new User
            {
                UserId = user.UserId,
                UserCode = studentNo,
                Name = name.Trim(),
                Gender = gender,
                Role = user.Role,
                ClassId = user.ClassId,
                AssignedDeviceIds = user.AssignedDeviceIds?.ToList(),
                CabinetAssignments = user.CabinetAssignments?.Select(item => new CabinetAssignment
                {
                    DeviceId = item.DeviceId,
                    FingerprintIds = item.FingerprintIds.ToList(),
                    LockIds = item.LockIds?.ToList(),
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
            userId = existing?.DisplayId ?? "";
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
            var idBox = new TextBox { Text = userId, Margin = new Thickness(0, 0, 0, 12) };
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
            IReadOnlyList<CabinetSyncJob> syncJobs,
            IReadOnlyList<bool> defaultPermissions)
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
            HasCompleteCabinetPermissions = assignments.Count > 0 && assignments.All(assignment =>
                ResolveLockPermissions(user.Role, assignment, defaultPermissions)
                    .Skip(1).Any(value => value));
            IsReady = user.Enabled && templates.Any(item => item.Enabled) && assignments.Count > 0 &&
                HasCompleteCabinetPermissions &&
                assignments.All(assignment => SelectedIds(assignment).Any(id => templates.Any(item =>
                    item.Enabled && item.FingerprintId == id))) &&
                !syncJobs.Any(job => job.State != "completed");
            WorkflowStatus = ResolveWorkflowStatus();
        }

        public User User { get; }
        public IReadOnlyList<FingerprintTemplate> Templates { get; }
        public IReadOnlyList<CabinetAssignment> Assignments { get; }
        public IReadOnlyList<CabinetSyncJob> SyncJobs { get; }
        public string Name => User.Name;
        public string UserId => User.UserId;
        public string StudentNo => User.DisplayId;
        public int FingerprintCount => Templates.Count;
        public string GenderText => User.Gender switch
        {
            "male" => "男",
            "female" => "女",
            "other" => "其他",
            _ => "未填写"
        };
        public bool IsReady { get; }
        public bool HasCompleteCabinetPermissions { get; }
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
                List<FingerprintTemplate> fingerprints = Templates.Where(item => item.Enabled &&
                        SelectedIds(assignment).Contains(item.FingerprintId)).ToList();
                string status = fingerprints.Count == 0
                    ? "待选指纹"
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
                    FingerprintText = fingerprints.Count == 0
                        ? "未选择" : string.Join("、", fingerprints.Select(item =>
                            $"{item.FingerDisplayName} #{item.FingerprintId}")),
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
            if (!HasCompleteCabinetPermissions) return "待配权限";
            if (Assignments.Any(assignment => SelectedIds(assignment).Count == 0)) return "待选指纹";
            if (SyncJobs.Any(job => job.State == "failed")) return "部分失败";
            if (SyncJobs.Any(job => job.State == "running")) return "同步中";
            if (SyncJobs.Any(job => job.State != "completed")) return "待同步";
            return "可使用";
        }

        private static IReadOnlyList<int> SelectedIds(CabinetAssignment assignment)
        {
            return assignment.FingerprintIds.Where(id => id > 0).Distinct().ToArray();
        }

        private static bool[] ResolveLockPermissions(string role,
            CabinetAssignment assignment, IReadOnlyList<bool> defaults)
        {
            bool[] permissions = defaults.Take(4).ToArray();
            Array.Resize(ref permissions, 4);
            if (assignment.LockIds != null)
            {
                permissions = new bool[4];
                foreach (int lockId in assignment.LockIds.Where(id => id >= 0 && id < 4))
                    permissions[lockId] = true;
            }
            PermissionPolicy.Enforce(role, permissions);
            return permissions;
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
            IsOnline = device.IsOnline;
            List<ClassStudentRow> assigned = students.Where(student =>
                student.BoundCabinetIds.Contains(device.DeviceId, StringComparer.OrdinalIgnoreCase)).ToList();
            StudentCount = assigned.Count;
            IsAvailable = StudentCount == 0;
            AvailabilityText = StudentCount == 0
                ? "空闲 · 优先分配"
                : $"已分配 {StudentCount} 人 · " + string.Join("、", assigned.Select(student =>
                    string.IsNullOrWhiteSpace(student.Name)
                        ? student.StudentNo
                        : $"{student.Name}（{student.StudentNo}）"));
            ReadyCount = assigned.Count(student => student.IsReady);
            PendingCount = StudentCount - ReadyCount;
        }

        public string DeviceId { get; }
        public string DeviceNumber { get; }
        public string DeviceName { get; }
        public string OnlineText { get; }
        public bool IsOnline { get; }
        public int StudentCount { get; }
        public bool IsAvailable { get; }
        public string AvailabilityText { get; }
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
        IReadOnlyList<string> TeacherNames);
}
