using System.IO;
using System.Windows;

namespace CabinetLock
{
    public partial class ImportUsersWindow : BorderlessWindow
    {
        private static readonly char[] ClassSeparators = { ';', ',', '，', '、' };
        private readonly List<UserImportRow> _importedRows = new();
        private readonly List<UserImportRow> _failedRows = new();
        private readonly string? _presetClassId;
        private readonly string? _restrictedRole;
        private bool _busy;

        public int SuccessCount { get; private set; }
        public int FailCount { get; private set; }
        public bool AnyImported => SuccessCount > 0;

        public ImportUsersWindow() : this(null, null)
        {
        }

        public ImportUsersWindow(string? presetClassId) : this(
            presetClassId,
            string.IsNullOrWhiteSpace(presetClassId) ? null : "student")
        {
        }

        public ImportUsersWindow(string? presetClassId, string? restrictedRole)
        {
            _presetClassId = string.IsNullOrWhiteSpace(presetClassId) ? null : presetClassId.Trim();
            _restrictedRole = string.IsNullOrWhiteSpace(restrictedRole)
                ? null : restrictedRole.Trim().ToLowerInvariant();
            InitializeComponent();
            if (_presetClassId != null)
            {
                Title = "导入班级学生";
                SubtitleText.Text = $"使用 Excel 模板导入当前班级学生；班级 ID 留空时自动填写 {_presetClassId}";
            }
            else if (_restrictedRole == "teacher")
            {
                Title = "导入教师";
                SubtitleText.Text = "使用 Excel 模板批量导入教师账号及负责班级";
            }
        }

        private void DownloadTemplateButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "保存用户导入模板",
                Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
                FileName = _presetClassId != null
                    ? $"{_presetClassId}_学生导入模板.xlsx"
                    : _restrictedRole == "teacher" ? "教师导入模板.xlsx" : "用户导入模板.xlsx",
                AddExtension = true,
                DefaultExt = ".xlsx"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                UserImportWorkbook.WriteTemplate(dialog.FileName, _presetClassId, _restrictedRole);
                StatusText.Text = $"模板已保存：{dialog.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"模板保存失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择用户导入表格",
                Filter = "Excel 工作簿 (*.xlsx)|*.xlsx"
            };
            if (dialog.ShowDialog(this) == true)
            {
                FilePathBox.Text = dialog.FileName;
                StatusText.Text = "文件已选择，点击“开始导入”执行整表校验";
            }
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            string path = FilePathBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                MessageBox.Show("请先选择有效的 .xlsx 文件", "提示");
                return;
            }

            ResetResults();
            SetBusy(true, "正在读取 Excel 表格");
            try
            {
                IReadOnlyList<UserImportRow> rows = await Task.Run(() => UserImportWorkbook.Read(path));
                if (rows.Count == 0)
                {
                    StatusText.Text = "表格中没有可导入的数据";
                    return;
                }

                ImportContext context = await Task.Run(LoadImportContext);
                SetProgress(5, $"正在预校验 0/{rows.Count} 行");
                var candidates = new List<ImportCandidate>();
                Dictionary<string, int> workbookCodeCounts = rows
                    .Where(row => !string.IsNullOrWhiteSpace(row.UserCode))
                    .GroupBy(row => row.UserCode.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

                for (int index = 0; index < rows.Count; index++)
                {
                    UserImportRow row = rows[index];
                    ImportCandidate? candidate = ValidateRow(row, context, workbookCodeCounts);
                    if (candidate == null) _failedRows.Add(row);
                    else candidates.Add(candidate);
                    SetProgress(5 + (index + 1) * 35d / rows.Count,
                        $"正在预校验 {index + 1}/{rows.Count} 行");
                    await Task.Yield();
                }

                RefreshResults();
                for (int index = 0; index < candidates.Count; index++)
                {
                    ImportCandidate candidate = candidates[index];
                    await ImportCandidateAsync(candidate, context);
                    SetProgress(40 + (index + 1) * 60d / Math.Max(candidates.Count, 1),
                        $"正在导入 {index + 1}/{candidates.Count} 行");
                }

                SuccessCount = _importedRows.Count;
                FailCount = _failedRows.Count;
                RefreshResults();
                SetProgress(100, FailCount == 0 ? "导入完成" : "导入完成，存在需要处理的异常");
                if (_failedRows.Count > 0) ResultTabs.SelectedIndex = 1;
            }
            catch (Exception ex)
            {
                StatusText.Text = "导入失败";
                MessageBox.Show($"导入失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private static ImportContext LoadImportContext()
        {
            List<User> users = App.UserService.GetAllUsers();
            List<ClassInfo> classes = App.ClassService.GetAll();
            List<Device> cabinets = App.DeviceService.GetAllDevices()
                .Where(device => !DeviceService.IsTrueRoot(device) &&
                    !string.IsNullOrWhiteSpace(device.DeviceId))
                .ToList();
            return new ImportContext(users, classes, cabinets);
        }

        private ImportCandidate? ValidateRow(
            UserImportRow row,
            ImportContext context,
            IReadOnlyDictionary<string, int> workbookCodeCounts)
        {
            var errors = new List<string>();
            row.UserCode = row.UserCode.Trim();
            row.Name = row.Name.Trim();
            row.Role = row.Role.Trim().ToLowerInvariant();
            row.ClassIds = row.ClassIds.Trim();
            row.DeviceNumber = row.DeviceNumber.Trim();

            if (string.IsNullOrWhiteSpace(row.UserCode)) errors.Add("用户编号不能为空");
            if (string.IsNullOrWhiteSpace(row.Name)) errors.Add("姓名不能为空");
            if (row.Role is not ("student" or "teacher" or "admin"))
                errors.Add("角色必须是学生、教师或管理员");
            if (_presetClassId != null && row.Role != "student")
                errors.Add("班级导入页面只允许导入学生");
            else if (_restrictedRole != null && !string.Equals(
                    row.Role, _restrictedRole, StringComparison.OrdinalIgnoreCase))
                errors.Add($"当前导入页面只允许导入{RoleName(_restrictedRole)}");
            if (!string.IsNullOrWhiteSpace(row.UserCode) &&
                context.ExistingCodes.Contains(row.UserCode))
                errors.Add("用户编号已存在");
            if (!string.IsNullOrWhiteSpace(row.UserCode) &&
                workbookCodeCounts.TryGetValue(row.UserCode, out int codeCount) && codeCount > 1)
                errors.Add("用户编号在当前表格中重复");

            bool requiresPassword = row.Role is "teacher" or "admin";
            if (requiresPassword && !PasswordHelper.IsPasswordAcceptable(row.Password))
                errors.Add($"密码长度必须为 {PasswordHelper.MinimumPasswordLength}-{PasswordHelper.MaximumPasswordLength} 个字符");
            if (row.Role == "student" && !string.IsNullOrEmpty(row.Password))
                errors.Add("学生不需要密码，请留空");

            List<string> classIds = ParseClassIds(row.ClassIds);
            if (row.Role == "student" && classIds.Count == 0 && _presetClassId != null)
                classIds.Add(_presetClassId);
            if (row.Role == "student" && classIds.Count > 1)
                errors.Add("学生只能填写一个班级 ID");
            if (row.Role == "admin" && classIds.Count > 0)
                errors.Add("管理员不能指定班级");
            if (_presetClassId != null && classIds.Any(classId => !string.Equals(
                    classId, _presetClassId, StringComparison.OrdinalIgnoreCase)))
                errors.Add($"只能导入当前班级 {_presetClassId} 的学生");
            foreach (string classId in classIds)
            {
                if (!context.Classes.TryGetValue(classId, out ClassInfo? classInfo))
                    errors.Add($"班级不存在：{classId}");
                else if (!classInfo.Enabled)
                    errors.Add($"班级已停用：{classId}");
            }

            Device? cabinet = null;
            if (!string.IsNullOrWhiteSpace(row.DeviceNumber))
            {
                if (row.Role != "student") errors.Add("只有学生可以指定设备编号");
                else if (!context.CabinetsByNumber.TryGetValue(row.DeviceNumber, out cabinet))
                    errors.Add($"设备编号不存在：{row.DeviceNumber}");
            }

            string? studentClassId = row.Role == "student" ? classIds.FirstOrDefault() : null;
            var user = new User
            {
                UserCode = row.UserCode,
                Name = row.Name,
                Role = row.Role,
                ClassId = studentClassId,
                AssignedDeviceIds = row.Role == "student" ? new List<string>() : null,
                CreateTime = DateTime.Now
            };
            if (row.Role == "teacher") user.SetResponsibleClassIds(classIds);
            if (errors.Count == 0 && !DataScopeContext.Instance.CanCreate(user))
                errors.Add("当前账号无权创建该用户或操作指定班级");

            row.ClassIds = string.Join(";", classIds);
            if (errors.Count > 0)
            {
                row.Result = "未导入";
                row.Error = string.Join("；", errors.Distinct());
                return null;
            }
            return new ImportCandidate(row, user, row.Password, cabinet);
        }

        private async Task ImportCandidateAsync(ImportCandidate candidate, ImportContext context)
        {
            UserImportRow row = candidate.Row;
            try
            {
                bool added = await Task.Run(() => App.UserService.AddUser(candidate.User, candidate.Password));
                if (!added)
                {
                    row.Result = "未导入";
                    row.Error = "写入失败，用户编号可能已被占用";
                    _failedRows.Add(row);
                    return;
                }

                row.Result = "导入成功";
                _importedRows.Add(row);
                if (candidate.Cabinet != null)
                {
                    try
                    {
                        bool assigned = await Task.Run(() => App.CabinetBindingService.AssignExclusive(
                            candidate.User.UserId, candidate.Cabinet.DeviceId, context.CabinetIds));
                        if (!assigned) AddBindingFailure(row, "柜机绑定保存失败");
                    }
                    catch (Exception ex)
                    {
                        AddBindingFailure(row, ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                row.Result = "未导入";
                row.Error = ex.Message;
                _failedRows.Add(row);
            }
        }

        private void AddBindingFailure(UserImportRow row, string reason)
        {
            row.Result = "用户已导入，柜机未绑定";
            row.Error = $"用户创建成功，但{reason}，请在班级管理中重新分配";
            _failedRows.Add(row);
        }

        private static List<string> ParseClassIds(string value) => value
            .Split(ClassSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(classId => !string.IsNullOrWhiteSpace(classId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        private static string RoleName(string role) => role switch
        {
            "student" => "学生",
            "teacher" => "教师",
            "admin" => "管理员",
            _ => role
        };

        private void ExportImportedButton_Click(object sender, RoutedEventArgs e) =>
            ExportResults(_importedRows, "已导入", "用户导入成功结果.xlsx");

        private void ExportFailedButton_Click(object sender, RoutedEventArgs e) =>
            ExportResults(_failedRows, "导入异常", "用户导入异常结果.xlsx");

        private void ExportResults(IReadOnlyCollection<UserImportRow> rows, string sheetName, string fileName)
        {
            if (rows.Count == 0) return;
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = $"导出{sheetName}",
                Filter = "Excel 工作簿 (*.xlsx)|*.xlsx",
                FileName = fileName,
                AddExtension = true,
                DefaultExt = ".xlsx"
            };
            if (dialog.ShowDialog(this) != true) return;

            try
            {
                UserImportWorkbook.WriteResults(dialog.FileName, sheetName, rows);
                StatusText.Text = $"{sheetName}结果已导出：{dialog.FileName}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"结果导出失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void ResetResults()
        {
            _importedRows.Clear();
            _failedRows.Clear();
            SuccessCount = 0;
            FailCount = 0;
            ImportProgressBar.Value = 0;
            RefreshResults();
        }

        private void RefreshResults()
        {
            ImportedDataGrid.ItemsSource = null;
            ImportedDataGrid.ItemsSource = _importedRows;
            FailedDataGrid.ItemsSource = null;
            FailedDataGrid.ItemsSource = _failedRows;
            ImportedTabText.Text = $"已导入 {_importedRows.Count}";
            FailedTabText.Text = $"导入异常 {_failedRows.Count}";
            ResultSummaryText.Text = $"已导入 {_importedRows.Count} · 异常 {_failedRows.Count}";
            ExportImportedButton.IsEnabled = !_busy && _importedRows.Count > 0;
            ExportFailedButton.IsEnabled = !_busy && _failedRows.Count > 0;
        }

        private void SetProgress(double value, string status)
        {
            ImportProgressBar.Value = value;
            StatusText.Text = status;
            ResultSummaryText.Text = $"已导入 {_importedRows.Count} · 异常 {_failedRows.Count}";
        }

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            DownloadTemplateButton.IsEnabled = !busy;
            BrowseButton.IsEnabled = !busy;
            ImportButton.IsEnabled = !busy;
            CloseButton.IsEnabled = !busy;
            FilePathBox.IsEnabled = !busy;
            if (!string.IsNullOrWhiteSpace(status)) StatusText.Text = status;
            RefreshResults();
        }

        private sealed record ImportCandidate(
            UserImportRow Row,
            User User,
            string Password,
            Device? Cabinet);

        private sealed class ImportContext
        {
            public ImportContext(
                IEnumerable<User> users,
                IEnumerable<ClassInfo> classes,
                IEnumerable<Device> cabinets)
            {
                ExistingCodes = users.Select(user => user.DisplayId)
                    .Where(code => !string.IsNullOrWhiteSpace(code))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                Classes = classes.Where(item => !string.IsNullOrWhiteSpace(item.ClassId))
                    .GroupBy(item => item.ClassId, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                List<Device> cabinetList = cabinets.ToList();
                CabinetIds = cabinetList.Select(device => device.DeviceId).ToArray();
                CabinetsByNumber = cabinetList
                    .Where(device => !string.IsNullOrWhiteSpace(device.DeviceNumber))
                    .GroupBy(device => device.DeviceNumber.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            }

            public HashSet<string> ExistingCodes { get; }
            public Dictionary<string, ClassInfo> Classes { get; }
            public Dictionary<string, Device> CabinetsByNumber { get; }
            public string[] CabinetIds { get; }
        }
    }
}
