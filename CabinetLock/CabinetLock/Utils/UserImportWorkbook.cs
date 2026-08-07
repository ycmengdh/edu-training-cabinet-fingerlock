using ClosedXML.Excel;
using System.IO;

namespace CabinetLock
{
    public sealed class UserImportRow
    {
        public int RowNumber { get; set; }
        public string UserCode { get; set; } = "";
        public string Name { get; set; } = "";
        public string Role { get; set; } = "";
        public string Password { get; set; } = "";
        public string ClassIds { get; set; } = "";
        public string DeviceNumber { get; set; } = "";
        public string Result { get; set; } = "待处理";
        public string Error { get; set; } = "";

        public string RoleDisplay => Role switch
        {
            "student" => "学生",
            "teacher" => "教师",
            "admin" => "管理员",
            _ => Role
        };
    }

    public static class UserImportWorkbook
    {
        private static readonly string[] Headers =
        {
            "用户编号", "姓名", "角色", "密码", "班级ID", "设备编号"
        };

        public static IReadOnlyList<UserImportRow> Read(string filePath)
        {
            using var workbook = new XLWorkbook(filePath);
            IXLWorksheet worksheet = workbook.Worksheets.FirstOrDefault(sheet =>
                string.Equals(sheet.Name, "用户导入", StringComparison.OrdinalIgnoreCase))
                ?? workbook.Worksheet(1);
            IXLRow? headerRow = worksheet.RowsUsed().FirstOrDefault();
            if (headerRow == null) throw new InvalidDataException("Excel 中没有可读取的表头");

            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (IXLCell cell in headerRow.CellsUsed())
            {
                string header = NormalizeHeader(cell.GetString());
                if (!string.IsNullOrEmpty(header)) columns[header] = cell.Address.ColumnNumber;
            }

            int userCodeColumn = FindColumn(columns, "用户编号", "用户ID", "学号", "账号ID", "usercode", "userid");
            int nameColumn = FindColumn(columns, "姓名", "name");
            int roleColumn = FindColumn(columns, "角色", "role");
            int passwordColumn = FindColumn(columns, "密码", "password");
            int classColumn = FindColumn(columns, "班级ID", "班级", "classid", required: false);
            int deviceColumn = FindColumn(columns, "设备编号", "柜机编号", "devicenumber", required: false);

            var rows = new List<UserImportRow>();
            foreach (IXLRow row in worksheet.RowsUsed().Where(row => row.RowNumber() > headerRow.RowNumber()))
            {
                string userCode = CellText(row, userCodeColumn);
                string name = CellText(row, nameColumn);
                string role = NormalizeRole(CellText(row, roleColumn));
                string password = CellText(row, passwordColumn, trim: false);
                string classIds = CellText(row, classColumn);
                string deviceNumber = CellText(row, deviceColumn);
                if (string.IsNullOrWhiteSpace(userCode) && string.IsNullOrWhiteSpace(name) &&
                    string.IsNullOrWhiteSpace(role) && string.IsNullOrWhiteSpace(password) &&
                    string.IsNullOrWhiteSpace(classIds) && string.IsNullOrWhiteSpace(deviceNumber))
                    continue;

                rows.Add(new UserImportRow
                {
                    RowNumber = row.RowNumber(),
                    UserCode = userCode,
                    Name = name,
                    Role = role,
                    Password = password,
                    ClassIds = classIds,
                    DeviceNumber = deviceNumber
                });
            }
            return rows;
        }

        public static void WriteTemplate(
            string filePath,
            string? presetClassId = null,
            string? restrictedRole = null)
        {
            using var workbook = new XLWorkbook();
            string? effectiveRole = string.IsNullOrWhiteSpace(restrictedRole) &&
                !string.IsNullOrWhiteSpace(presetClassId)
                ? "student"
                : restrictedRole?.Trim().ToLowerInvariant();
            IXLWorksheet data = workbook.AddWorksheet("用户导入");
            for (int index = 0; index < Headers.Length; index++)
                data.Cell(1, index + 1).Value = Headers[index];
            StyleHeader(data.Range(1, 1, 1, Headers.Length));
            data.SheetView.FreezeRows(1);
            data.Range(1, 1, 1000, Headers.Length).SetAutoFilter();
            data.Column(1).Style.NumberFormat.Format = "@";
            data.Column(4).Style.NumberFormat.Format = "@";
            data.Column(5).Style.NumberFormat.Format = "@";
            data.Column(6).Style.NumberFormat.Format = "@";
            data.Column(1).Width = 20;
            data.Column(2).Width = 16;
            data.Column(3).Width = 12;
            data.Column(4).Width = 20;
            data.Column(5).Width = 26;
            data.Column(6).Width = 18;
            string roleList = effectiveRole switch
            {
                "student" => "学生",
                "teacher" => "教师",
                "admin" => "管理员",
                _ => "学生,教师,管理员"
            };
            data.Range("C2:C1000").CreateDataValidation().List($"\"{roleList}\"");
            string? presetRole = effectiveRole switch
            {
                "student" => "学生",
                "teacher" => "教师",
                "admin" => "管理员",
                _ => null
            };
            if (presetRole != null) data.Cell("C2").Value = presetRole;
            if (!string.IsNullOrWhiteSpace(presetClassId))
            {
                data.Cell("E2").Value = presetClassId.Trim();
            }

            IXLWorksheet help = workbook.AddWorksheet("填写说明");
            help.Cell("A1").Value = "字段";
            help.Cell("B1").Value = "填写规则";
            help.Cell("A2").Value = "用户编号";
            help.Cell("B2").Value = "必填且全系统唯一；学生填写学号，教师填写登录账号";
            help.Cell("A3").Value = "姓名";
            help.Cell("B3").Value = "必填";
            help.Cell("A4").Value = "角色";
            help.Cell("B4").Value = presetRole == null
                ? "填写学生、教师或管理员"
                : $"当前模板固定填写{presetRole}";
            help.Cell("A5").Value = "密码";
            help.Cell("B5").Value = $"教师和管理员必填，长度 {PasswordHelper.MinimumPasswordLength}-{PasswordHelper.MaximumPasswordLength} 个字符；学生留空";
            help.Cell("A6").Value = "班级ID";
            help.Cell("B6").Value = string.IsNullOrWhiteSpace(presetClassId)
                ? "学生可填一个班级；教师负责多个班级时使用英文分号分隔"
                : $"当前班级导入固定为 {presetClassId.Trim()}；此列留空时系统也会自动填写";
            help.Cell("A7").Value = "设备编号";
            help.Cell("B7").Value = "仅学生可填，填写实训柜列表中的现场设备编号";
            help.Cell("A9").Value = "示例";
            help.Cell("B9").Value = "20260001 | 张三 | 学生 | [留空] | 204 | 111";
            StyleHeader(help.Range("A1:B1"));
            help.Column(1).Width = 18;
            help.Column(2).Width = 78;
            help.Column(2).Style.Alignment.WrapText = true;

            EnsureDirectory(filePath);
            workbook.SaveAs(filePath);
        }

        public static void WriteResults(
            string filePath,
            string sheetName,
            IReadOnlyCollection<UserImportRow> rows)
        {
            using var workbook = new XLWorkbook();
            IXLWorksheet worksheet = workbook.AddWorksheet(sheetName);
            string[] headers = { "Excel行", "用户编号", "姓名", "角色", "班级ID", "设备编号", "结果", "说明" };
            for (int index = 0; index < headers.Length; index++)
                worksheet.Cell(1, index + 1).Value = headers[index];
            StyleHeader(worksheet.Range(1, 1, 1, headers.Length));
            int outputRow = 2;
            foreach (UserImportRow row in rows)
            {
                worksheet.Cell(outputRow, 1).Value = row.RowNumber;
                worksheet.Cell(outputRow, 2).Value = row.UserCode;
                worksheet.Cell(outputRow, 3).Value = row.Name;
                worksheet.Cell(outputRow, 4).Value = row.RoleDisplay;
                worksheet.Cell(outputRow, 5).Value = row.ClassIds;
                worksheet.Cell(outputRow, 6).Value = row.DeviceNumber;
                worksheet.Cell(outputRow, 7).Value = row.Result;
                worksheet.Cell(outputRow, 8).Value = row.Error;
                outputRow++;
            }
            worksheet.SheetView.FreezeRows(1);
            worksheet.Columns().AdjustToContents(8, 48);
            worksheet.Column(8).Width = Math.Min(Math.Max(worksheet.Column(8).Width, 28), 60);
            worksheet.Column(8).Style.Alignment.WrapText = true;
            EnsureDirectory(filePath);
            workbook.SaveAs(filePath);
        }

        private static int FindColumn(
            IReadOnlyDictionary<string, int> columns,
            string name,
            string alias1,
            string alias2 = "",
            string alias3 = "",
            string alias4 = "",
            string alias5 = "",
            bool required = true)
        {
            foreach (string candidate in new[] { name, alias1, alias2, alias3, alias4, alias5 })
            {
                string normalized = NormalizeHeader(candidate);
                if (!string.IsNullOrEmpty(normalized) && columns.TryGetValue(normalized, out int column))
                    return column;
            }
            if (!required) return 0;
            throw new InvalidDataException($"缺少必填列：{name}");
        }

        private static string CellText(IXLRow row, int column, bool trim = true)
        {
            if (column <= 0) return "";
            string value = row.Cell(column).GetFormattedString();
            return trim ? value.Trim() : value;
        }

        private static string NormalizeHeader(string? value) =>
            (value ?? "").Trim().Replace("_", "").Replace(" ", "").ToLowerInvariant();

        private static string NormalizeRole(string? value) => (value ?? "").Trim().ToLowerInvariant() switch
        {
            "学生" or "student" => "student",
            "教师" or "老师" or "teacher" => "teacher",
            "管理员" or "admin" => "admin",
            var role => role
        };

        private static void StyleHeader(IXLRange range)
        {
            range.Style.Font.Bold = true;
            range.Style.Fill.BackgroundColor = XLColor.FromHtml("#DCEFEA");
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        }

        private static void EnsureDirectory(string filePath)
        {
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        }
    }
}
