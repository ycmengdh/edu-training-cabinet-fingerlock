using System.IO;
using System.Text;
using System.Xml;

namespace FingerprintLockManager
{
    /// <summary>
    /// 导出 Excel 2003 XML Spreadsheet（.xls，无需第三方库，Excel/WPS 可直接打开）。
    /// </summary>
    public static class ExcelExportHelper
    {
        public static void Export(
            string filePath,
            string sheetName,
            IReadOnlyList<string> headers,
            IEnumerable<IReadOnlyList<object?>> rows)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("filePath empty", nameof(filePath));
            headers ??= Array.Empty<string>();
            rows ??= Array.Empty<IReadOnlyList<object?>>();

            string dir = Path.GetDirectoryName(filePath) ?? "";
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false),
                Indent = true,
                OmitXmlDeclaration = false
            };

            using var writer = XmlWriter.Create(filePath, settings);
            writer.WriteStartDocument();
            writer.WriteProcessingInstruction("mso-application", "progid=\"Excel.Sheet\"");
            writer.WriteStartElement("Workbook", "urn:schemas-microsoft-com:office:spreadsheet");
            writer.WriteAttributeString("xmlns", "o", null, "urn:schemas-microsoft-com:office:office");
            writer.WriteAttributeString("xmlns", "x", null, "urn:schemas-microsoft-com:office:excel");
            writer.WriteAttributeString("xmlns", "ss", null, "urn:schemas-microsoft-com:office:spreadsheet");
            writer.WriteAttributeString("xmlns", "html", null, "http://www.w3.org/TR/REC-html40");

            // 表头样式
            writer.WriteStartElement("Styles", "urn:schemas-microsoft-com:office:spreadsheet");
            writer.WriteStartElement("Style", "urn:schemas-microsoft-com:office:spreadsheet");
            writer.WriteAttributeString("ss", "ID", null, "Header");
            writer.WriteStartElement("Font", "urn:schemas-microsoft-com:office:spreadsheet");
            writer.WriteAttributeString("ss", "Bold", null, "1");
            writer.WriteEndElement(); // Font
            writer.WriteStartElement("Interior", "urn:schemas-microsoft-com:office:spreadsheet");
            writer.WriteAttributeString("ss", "Color", null, "#D9E2F3");
            writer.WriteAttributeString("ss", "Pattern", null, "Solid");
            writer.WriteEndElement();
            writer.WriteEndElement(); // Style
            writer.WriteEndElement(); // Styles

            writer.WriteStartElement("Worksheet", "urn:schemas-microsoft-com:office:spreadsheet");
            writer.WriteAttributeString("ss", "Name", null, SanitizeSheetName(sheetName));
            writer.WriteStartElement("Table", "urn:schemas-microsoft-com:office:spreadsheet");

            // 表头
            writer.WriteStartElement("Row", "urn:schemas-microsoft-com:office:spreadsheet");
            foreach (var h in headers)
                WriteCell(writer, h ?? "", "Header");
            writer.WriteEndElement();

            foreach (var row in rows)
            {
                writer.WriteStartElement("Row", "urn:schemas-microsoft-com:office:spreadsheet");
                int colCount = headers.Count;
                for (int i = 0; i < colCount; i++)
                {
                    object? value = row != null && i < row.Count ? row[i] : null;
                    WriteCell(writer, value);
                }
                writer.WriteEndElement();
            }

            writer.WriteEndElement(); // Table
            writer.WriteEndElement(); // Worksheet
            writer.WriteEndElement(); // Workbook
            writer.WriteEndDocument();
        }

        private static void WriteCell(XmlWriter writer, object? value, string? styleId = null)
        {
            writer.WriteStartElement("Cell", "urn:schemas-microsoft-com:office:spreadsheet");
            if (!string.IsNullOrEmpty(styleId))
                writer.WriteAttributeString("ss", "StyleID", null, styleId);

            writer.WriteStartElement("Data", "urn:schemas-microsoft-com:office:spreadsheet");
            if (value is int or long or short or byte or float or double or decimal)
            {
                writer.WriteAttributeString("ss", "Type", null, "Number");
                writer.WriteString(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "");
            }
            else if (value is DateTime dt)
            {
                writer.WriteAttributeString("ss", "Type", null, "String");
                writer.WriteString(dt.ToString("yyyy-MM-dd HH:mm:ss"));
            }
            else if (value is bool b)
            {
                writer.WriteAttributeString("ss", "Type", null, "String");
                writer.WriteString(b ? "TRUE" : "FALSE");
            }
            else
            {
                writer.WriteAttributeString("ss", "Type", null, "String");
                writer.WriteString(value?.ToString() ?? "");
            }
            writer.WriteEndElement(); // Data
            writer.WriteEndElement(); // Cell
        }

        private static string SanitizeSheetName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "Sheet1";
            var invalid = new[] { ':', '\\', '/', '?', '*', '[', ']' };
            string s = name.Trim();
            foreach (char c in invalid) s = s.Replace(c, '_');
            if (s.Length > 31) s = s.Substring(0, 31);
            return string.IsNullOrEmpty(s) ? "Sheet1" : s;
        }
    }
}
