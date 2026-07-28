using System.Security.Cryptography;
using System.Text;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FingerprintLockManager
{
    public static class BusinessUploadStateService
    {
        private const string StateFileName = "business.sd-sync";

        private static string StateFilePath =>
            Path.Combine(SqlitePaths.GetDataDirectory(), StateFileName);

        public static bool IsUploadRequired(out string reason)
        {
            try
            {
                BusinessDatabase.Initialize();
                bool hasBusinessData = BusinessDatabase.HasAnyBusinessData();
                bool hasFingerprints = BusinessDatabase.ReadAllFpTemplateMetas().Count > 0;
                if (!hasBusinessData && !hasFingerprints)
                {
                    reason = "本机没有需要上传的业务数据";
                    return false;
                }

                string currentHash = CaptureCurrentDataHash();
                if (!File.Exists(StateFilePath))
                {
                    reason = "尚未确认本机业务数据已上传到 SD";
                    return true;
                }

                string uploadedHash = File.ReadAllText(StateFilePath, Encoding.UTF8).Trim();
                if (string.Equals(currentHash, uploadedHash, StringComparison.OrdinalIgnoreCase))
                {
                    reason = "本机业务数据已上传到 SD";
                    return false;
                }

                reason = "本机业务数据在上次上传后已有变更";
                return true;
            }
            catch (Exception ex)
            {
                reason = $"无法确认业务数据上传状态：{ex.Message}";
                return true;
            }
        }

        public static string CaptureCurrentDataHash() => ComputeCurrentHash();

        public static bool TryMarkUploadedIfUnchanged(string expectedHash)
        {
            string currentHash = ComputeCurrentHash();
            if (!string.Equals(currentHash, expectedHash, StringComparison.Ordinal))
                return false;

            string tempPath = StateFilePath + ".tmp";
            File.WriteAllText(tempPath, currentHash, new UTF8Encoding(false));
            File.Move(tempPath, StateFilePath, true);
            return true;
        }

        private static string ComputeCurrentHash()
        {
            BusinessDatabase.Initialize();
            var builder = new StringBuilder(8192);

            foreach (string table in BusinessDatabase.BusinessTables.OrderBy(name => name))
            {
                builder.Append("table:").Append(table).Append('\n');
                var items = BusinessDatabase.ReadArray(table)
                    .Select(Canonicalize)
                    .Select(token => token.ToString(Formatting.None))
                    .OrderBy(json => json, StringComparer.Ordinal);
                foreach (string item in items)
                    builder.Append(item).Append('\n');
            }

            foreach (FingerprintTemplate meta in BusinessDatabase.ReadAllFpTemplateMetas()
                         .OrderBy(item => item.FingerprintId)
                         .ThenBy(item => item.FingerIndex))
            {
                byte[] bytes = BusinessDatabase.ReadFpTemplateBytes(
                    meta.FingerprintId, meta.FingerIndex) ?? Array.Empty<byte>();
                builder.Append("fingerprint:")
                    .Append(meta.FingerprintId).Append('|')
                    .Append(meta.UserId ?? "").Append('|')
                    .Append(meta.UserName ?? "").Append('|')
                    .Append(meta.FingerIndex).Append('|')
                    .Append(meta.EnrollTime.ToUniversalTime().ToString("O")).Append('|')
                    .Append(meta.TemplateSize).Append('|')
                    .Append(meta.SourceDevice ?? "").Append('|')
                    .Append(meta.Note ?? "").Append('|')
                    .Append(Convert.ToHexString(SHA256.HashData(bytes)))
                    .Append('\n');
            }

            return Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(builder.ToString())));
        }

        private static JToken Canonicalize(JToken token)
        {
            return token switch
            {
                JObject obj => new JObject(obj.Properties()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property => new JProperty(
                        property.Name, Canonicalize(property.Value)))),
                JArray array => new JArray(array.Select(Canonicalize)),
                _ => token.DeepClone()
            };
        }
    }
}
