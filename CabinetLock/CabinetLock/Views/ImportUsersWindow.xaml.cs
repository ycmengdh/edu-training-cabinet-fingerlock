using System.IO;
using System.Text;
using System.Windows;

namespace CabinetLock
{
    /// <summary>
    /// CSV 批量导入用户：独立场景窗口，列表页只负责打开入口。
    /// </summary>
    public partial class ImportUsersWindow : BorderlessWindow
    {
        private bool _busy;
        private readonly string? _presetClassId;
        public int SuccessCount { get; private set; }
        public int FailCount { get; private set; }
        public bool AnyImported => SuccessCount > 0;

        public ImportUsersWindow() : this(null)
        {
        }

        public ImportUsersWindow(string? presetClassId)
        {
            _presetClassId = string.IsNullOrWhiteSpace(presetClassId) ? null : presetClassId.Trim();
            InitializeComponent();
            if (_presetClassId != null)
            {
                Title = "导入班级学生";
                HintText.Text = $"class_id 留空时自动使用 {_presetClassId}；学生导入后先录指纹，再分配并同步柜机。";
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "CSV 文件 (*.csv)|*.csv|所有文件 (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true)
                FilePathBox.Text = dialog.FileName;
        }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_busy) return;
            string path = FilePathBox.Text?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                MessageBox.Show("请先选择有效的 CSV 文件", "提示");
                return;
            }

            SetBusy(true, "正在导入 CSV…");
            ResultText.Visibility = Visibility.Collapsed;
            try
            {
                string[] lines = await Task.Run(() => File.ReadAllLines(path, Encoding.UTF8));
                int success = 0, fail = 0;
                var errors = new List<string>();
                HashSet<string> classIds = new(StringComparer.OrdinalIgnoreCase);
                List<Device> cabinets = new();
                try
                {
                    classIds = (await Task.Run(App.ClassService.GetAll))
                        .Select(c => c.ClassId)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                }
                catch (RootDataUnavailableException)
                {
                    // 无班级表时仅允许空 class_id
                }
                try
                {
                    cabinets = (await Task.Run(App.DeviceService.GetAllDevices))
                        .Where(device => !DeviceService.IsTrueRoot(device) &&
                            !string.IsNullOrWhiteSpace(device.DeviceId))
                        .ToList();
                }
                catch
                {
                }
                string[] cabinetIds = cabinets.Select(device => device.DeviceId).ToArray();

                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (i == 0 && (line.Contains("user_code", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("student_no", StringComparison.OrdinalIgnoreCase) ||
                            line.Contains("user_id", StringComparison.OrdinalIgnoreCase))) continue;

                    string[] parts = SplitCsvLine(line);
                    if (parts.Length < 4)
                    {
                        fail++;
                        errors.Add($"第 {i + 1} 行字段不足");
                        continue;
                    }

                    string userCode = parts[0].Trim();
                    string name = parts[1].Trim();
                    string role = parts[2].Trim().ToLowerInvariant();
                    string password = parts[3];
                    string? classId = parts.Length > 4 ? parts[4].Trim() : null;
                    string deviceNumber = parts.Length > 5 ? parts[5].Trim() : "";
                    if (string.IsNullOrWhiteSpace(classId)) classId = null;
                    if (classId == null && role == "student" && _presetClassId != null)
                        classId = _presetClassId;
                    Device? targetCabinet = null;
                    if (!string.IsNullOrWhiteSpace(deviceNumber))
                    {
                        targetCabinet = cabinets.FirstOrDefault(device =>
                            string.Equals(device.DeviceNumber, deviceNumber,
                                StringComparison.OrdinalIgnoreCase));
                        if (role != "student" || targetCabinet == null)
                        {
                            fail++;
                            errors.Add(role != "student"
                                ? $"第 {i + 1} 行只有学生可指定设备编号"
                                : $"第 {i + 1} 行设备编号不存在: {deviceNumber}");
                            continue;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(userCode) || string.IsNullOrWhiteSpace(name) ||
                        role is not ("admin" or "teacher" or "student") ||
                        (role != "student" && !PasswordHelper.IsPasswordAcceptable(password)))
                    {
                        fail++;
                        errors.Add($"第 {i + 1} 行校验失败");
                        continue;
                    }
                    if (classId != null && classIds.Count > 0 && !classIds.Contains(classId))
                    {
                        fail++;
                        errors.Add($"第 {i + 1} 行班级不存在: {classId}");
                        continue;
                    }

                    var importedUser = new User
                    {
                        UserCode = userCode,
                        Name = name,
                        Role = role,
                        ClassId = classId,
                        AssignedDeviceIds = string.Equals(role, "student", StringComparison.OrdinalIgnoreCase)
                            ? new List<string>()
                            : null,
                        CreateTime = DateTime.Now
                    };
                    bool added = await Task.Run(() => App.UserService.AddUser(importedUser, password));
                    if (added)
                    {
                        success++;
                        if (targetCabinet != null && !App.CabinetBindingService.AssignExclusive(
                                importedUser.UserId, targetCabinet.DeviceId, cabinetIds))
                            errors.Add($"第 {i + 1} 行学生已导入，但柜机绑定保存失败");
                    }
                    else
                    {
                        fail++;
                        errors.Add($"第 {i + 1} 行写入失败（编号可能重复）");
                    }
                }

                SuccessCount = success;
                FailCount = fail;

                string msg = $"导入完成：成功 {success}，失败 {fail}";
                if (errors.Count > 0)
                    msg += "\n" + string.Join("\n", errors.Take(8));
                if (success > 0)
                    msg += "\n学生默认权限已保存；录入指纹并分配柜机后再执行同步。";

                ResultText.Text = msg;
                ResultText.Visibility = Visibility.Visible;
                StatusText.Text = fail == 0 ? "导入完成" : "导入完成（含失败行）";
                MessageBox.Show(msg, "导入结果", MessageBoxButton.OK,
                    fail == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
                if (success > 0) DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导入失败：{ex.Message}", "错误",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                StatusText.Text = "导入失败";
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

        private void SetBusy(bool busy, string? status = null)
        {
            _busy = busy;
            ImportButton.IsEnabled = !busy;
            if (!string.IsNullOrEmpty(status)) StatusText.Text = status;
        }

        internal static string[] SplitCsvLine(string line)
        {
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    fields.Add(sb.ToString());
                    sb.Clear();
                }
                else sb.Append(c);
            }
            fields.Add(sb.ToString());
            return fields.ToArray();
        }
    }
}
