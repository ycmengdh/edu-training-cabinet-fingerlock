using ClosedXML.Excel;

namespace CabinetLock.Tests;

public class UserImportWorkbookTests
{
    [Fact]
    public void Template_RoundTripsStudentTeacherAndAdminRows()
    {
        string path = TemporaryPath("template.xlsx");
        try
        {
            UserImportWorkbook.WriteTemplate(path);
            using (var workbook = new XLWorkbook(path))
            {
                IXLWorksheet sheet = workbook.Worksheet("用户导入");
                sheet.Cell("A2").Value = "000001";
                sheet.Cell("B2").Value = "学生一";
                sheet.Cell("C2").Value = "学生";
                sheet.Cell("E2").Value = "CLASS_01";
                sheet.Cell("A3").Value = "teacher01";
                sheet.Cell("B3").Value = "教师一";
                sheet.Cell("C3").Value = "教师";
                sheet.Cell("D3").Value = "123456";
                sheet.Cell("E3").Value = "CLASS_01;CLASS_02";
                sheet.Cell("A4").Value = "admin02";
                sheet.Cell("B4").Value = "管理员二";
                sheet.Cell("C4").Value = "管理员";
                sheet.Cell("D4").Value = "123456";
                workbook.Save();
            }

            IReadOnlyList<UserImportRow> rows = UserImportWorkbook.Read(path);

            Assert.Equal(3, rows.Count);
            Assert.Equal("000001", rows[0].UserCode);
            Assert.Equal("student", rows[0].Role);
            Assert.Equal("teacher", rows[1].Role);
            Assert.Equal("CLASS_01;CLASS_02", rows[1].ClassIds);
            Assert.Equal("admin", rows[2].Role);
        }
        finally
        {
            DeleteTemporaryFile(path);
        }
    }

    [Fact]
    public void ClassTemplate_PrefillsStudentRoleAndClassId()
    {
        string path = TemporaryPath("class-template.xlsx");
        try
        {
            UserImportWorkbook.WriteTemplate(path, "CLASS_08");

            using var workbook = new XLWorkbook(path);
            IXLWorksheet sheet = workbook.Worksheet("用户导入");
            Assert.Equal("学生", sheet.Cell("C2").GetString());
            Assert.Equal("CLASS_08", sheet.Cell("E2").GetString());
            Assert.Contains("CLASS_08", workbook.Worksheet("填写说明").Cell("B6").GetString());
        }
        finally
        {
            DeleteTemporaryFile(path);
        }
    }

    [Fact]
    public void Results_ExportsStatusAndErrorColumns()
    {
        string path = TemporaryPath("results.xlsx");
        try
        {
            UserImportWorkbook.WriteResults(path, "导入异常", new[]
            {
                new UserImportRow
                {
                    RowNumber = 3,
                    UserCode = "student03",
                    Name = "学生三",
                    Role = "student",
                    Result = "未导入",
                    Error = "用户编号已存在"
                }
            });

            using var workbook = new XLWorkbook(path);
            IXLWorksheet sheet = workbook.Worksheet("导入异常");
            Assert.Equal("结果", sheet.Cell("G1").GetString());
            Assert.Equal("未导入", sheet.Cell("G2").GetString());
            Assert.Equal("用户编号已存在", sheet.Cell("H2").GetString());
        }
        finally
        {
            DeleteTemporaryFile(path);
        }
    }

    [Fact]
    public void TeacherTemplate_PrefillsTeacherRole()
    {
        string path = TemporaryPath("teacher-template.xlsx");
        try
        {
            UserImportWorkbook.WriteTemplate(path, restrictedRole: "teacher");

            using var workbook = new XLWorkbook(path);
            Assert.Equal("教师", workbook.Worksheet("用户导入").Cell("C2").GetString());
            Assert.Contains("教师", workbook.Worksheet("填写说明").Cell("B4").GetString());
        }
        finally
        {
            DeleteTemporaryFile(path);
        }
    }

    private static string TemporaryPath(string fileName) =>
        Path.Combine(Path.GetTempPath(), $"CabinetLock-{Guid.NewGuid():N}-{fileName}");

    private static void DeleteTemporaryFile(string path)
    {
        if (File.Exists(path)) File.Delete(path);
    }
}
