using System.IO.Compression;
using Newtonsoft.Json;

namespace FingerprintLockManager
{
    /// <summary>
    /// 备份还原服务（需求 11）
    ///
    /// 需求 11：每次系统用户操作前，自动备份数据到上位机数据目录。
    /// 备份仅用于还原，数据以根节点为主。
    /// 分配设备/权限成功才更新根节点，保证根节点数据准确。
    ///
    /// 备份内容：
    ///   - 业务表 JSON（users/classes/role_permissions/user_permissions/device_authorizations/devices/fingerprint_templates）
    ///   - 指纹模板二进制文件（FP_&lt;userId&gt;.bin，仅完整备份包含）
    ///
    /// 两种备份模式：
    ///   1. BackupBeforeAction（同步，自动备份）：仅业务表 JSON，快速（&lt;1秒），不阻塞用户操作
    ///   2. CreateFullBackupAsync（异步，手动备份）：业务表 JSON + 所有指纹模板二进制文件，完整恢复点
    ///
    /// 还原逻辑：
    ///   - 还原前自动备份当前状态
    ///   - 业务表 JSON 写回根节点 SD 卡
    ///   - 如果 zip 中含 templates/ 目录，把指纹模板文件上传回根节点
    ///   - 重新加载 DataStore 内存数据
    /// </summary>
    public class BackupService
    {
        /// <summary>备份文件目录</summary>
        private static string BackupDir => Path.Combine(AppContext.BaseDirectory, "Backups");

        /// <summary>业务表清单（顺序固定）</summary>
        private static readonly string[] TableNames = new[]
        {
            "users", "classes", "role_permissions", "user_permissions",
            "device_authorizations", "devices", "fingerprint_templates"
        };

        /// <summary>获取备份列表</summary>
        public List<BackupRecord> GetBackupRecords()
        {
            return LogDbService.Current.GetBackupRecords();
        }

        /// <summary>
        /// 在用户操作前创建自动备份（仅业务表 JSON，快速）
        ///
        /// 此方法为同步，供各业务操作前调用，保证不阻塞主流程。
        /// 不下载指纹模板二进制文件（耗时较长），如需完整备份请用 CreateFullBackupAsync。
        /// </summary>
        /// <param name="triggerAction">触发备份的操作描述</param>
        /// <param name="operatorUserId">操作人</param>
        /// <returns>备份记录；失败返回 null</returns>
        public BackupRecord? BackupBeforeAction(string triggerAction, string? operatorUserId)
        {
            try
            {
                Directory.CreateDirectory(BackupDir);

                // 生成备份 ID（时间戳，精确到秒）
                string backupId = DateTime.Now.ToString("yyyyMMddHHmmss");
                string zipPath = Path.Combine(BackupDir, $"backup_{backupId}.zip");

                // 拉取 DataStore 业务表全量快照
                var snapshot = TakeSnapshot();

                // 打包为 zip
                using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    WriteTablesToZip(zip, snapshot);
                }

                return SaveBackupRecord(zipPath, backupId, triggerAction, operatorUserId, snapshot.Keys, 0);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Backup] 自动备份失败：{ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 创建完整备份（业务表 JSON + 所有指纹模板二进制文件）
        ///
        /// 用于手动备份场景，可作为完整的系统恢复点。
        /// 异步方法，因为需要逐个下载指纹模板文件（每个 512B，网络往返耗时）。
        /// </summary>
        /// <param name="triggerAction">触发备份的操作描述</param>
        /// <param name="operatorUserId">操作人</param>
        /// <returns>备份记录；失败返回 null</returns>
        public async Task<BackupRecord?> CreateFullBackupAsync(string triggerAction, string? operatorUserId)
        {
            try
            {
                Directory.CreateDirectory(BackupDir);

                string backupId = DateTime.Now.ToString("yyyyMMddHHmmss");
                string zipPath = Path.Combine(BackupDir, $"backup_{backupId}.zip");

                var snapshot = TakeSnapshot();
                int templateCount = 0;

                using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
                {
                    // 1. 写入业务表 JSON
                    WriteTablesToZip(zip, snapshot);

                    // 2. 下载并写入指纹模板二进制文件
                    templateCount = await WriteTemplatesToZipAsync(zip);
                }

                string action = triggerAction;
                if (templateCount > 0)
                {
                    action += $"（含 {templateCount} 枚指纹模板）";
                }

                var record = SaveBackupRecord(zipPath, backupId, action, operatorUserId,
                    snapshot.Keys, 0);

                if (templateCount > 0)
                {
                    record.Tables = record.Tables + ",templates";
                    // 更新 SQLite 中的记录（重新写入）
                    LogDbService.Current.AddBackupRecord(record);
                }

                System.Diagnostics.Debug.WriteLine(
                    $"[Backup] 完整备份完成：{backupId}，模板 {templateCount} 枚");

                return record;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Backup] 完整备份失败：{ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从备份还原数据
        ///
        /// 还原流程：
        ///   1. 还原前先备份当前状态（防止误操作）
        ///   2. 读取 zip 中的业务表 JSON，写回根节点 SD 卡
        ///   3. 如果 zip 中含 templates/ 目录，把指纹模板文件上传回根节点
        ///   4. 重新加载 DataStore 内存数据
        /// </summary>
        /// <param name="backupId">备份 ID</param>
        /// <param name="operatorUserId">操作人</param>
        /// <returns>成功返回 null；失败返回错误信息</returns>
        public async Task<RestoreResult?> RestoreFromBackupAsync(string backupId, string? operatorUserId)
        {
            try
            {
                var records = LogDbService.Current.GetBackupRecords(500);
                var target = records.FirstOrDefault(r => r.BackupId == backupId);
                if (target == null) return new RestoreResult { ErrorMessage = "备份记录不存在" };
                if (!File.Exists(target.FilePath)) return new RestoreResult { ErrorMessage = "备份文件已丢失" };

                // 1. 还原前先备份当前状态
                BackupBeforeAction($"还原前自动备份（目标：{backupId}）", operatorUserId);

                // 2. 读取 zip 中的所有条目
                var tableJsons = new Dictionary<string, string>();
                var templateEntries = new List<(string userId, byte[] bytes)>();

                using (var fs = File.OpenRead(target.FilePath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        // 业务表 JSON
                        if (entry.FullName.EndsWith(".json") && !entry.FullName.Contains("/"))
                        {
                            string tableName = Path.GetFileNameWithoutExtension(entry.Name);
                            using var stream = entry.Open();
                            using var reader = new StreamReader(stream);
                            tableJsons[tableName] = reader.ReadToEnd();
                        }
                        // 指纹模板二进制文件：templates/FP_<userId>.bin
                        else if (entry.FullName.StartsWith("templates/") && entry.FullName.EndsWith(".bin"))
                        {
                            string fileName = Path.GetFileNameWithoutExtension(entry.Name);
                            if (fileName.StartsWith("FP_"))
                            {
                                string userId = fileName.Substring(3);
                                using var stream = entry.Open();
                                using var ms = new MemoryStream();
                                stream.CopyTo(ms);
                                templateEntries.Add((userId, ms.ToArray()));
                            }
                        }
                    }
                }

                // 3. 业务表写回根节点 SD 卡
                var sd = App.SdStorageService;
                if (!sd.IsAvailable) return new RestoreResult { ErrorMessage = "根节点 SD 卡不可用，无法还原" };

                int tableCount = 0;
                foreach (var name in TableNames)
                {
                    if (tableJsons.TryGetValue(name, out var json))
                    {
                        bool ok = await sd.SaveTableAsync(name, json);
                        if (ok) tableCount++;
                    }
                }

                // 4. 指纹模板文件上传回根节点
                int templateRestored = 0;
                int templateFailed = 0;
                foreach (var (userId, bytes) in templateEntries)
                {
                    try
                    {
                        bool ok = await sd.UploadTemplateAsync(userId, 1, bytes);
                        if (ok) templateRestored++;
                        else templateFailed++;
                    }
                    catch
                    {
                        templateFailed++;
                    }
                }

                // 5. 重新加载 DataStore
                await DataStore.Current.LoadFromSdCardAsync();

                System.Diagnostics.Debug.WriteLine(
                    $"[Backup] 还原完成：{backupId}，表 {tableCount} 张，模板 {templateRestored}/{templateEntries.Count}");

                return new RestoreResult
                {
                    TableCount = tableCount,
                    TemplateTotal = templateEntries.Count,
                    TemplateRestored = templateRestored,
                    TemplateFailed = templateFailed
                };
            }
            catch (Exception ex)
            {
                return new RestoreResult { ErrorMessage = $"还原失败：{ex.Message}" };
            }
        }

        /// <summary>清理过期的备份文件（保留最近 N 个）</summary>
        public void CleanupOldBackups(int keepCount = 30)
        {
            try
            {
                var records = LogDbService.Current.GetBackupRecords(keepCount);
                var keepIds = new HashSet<string>(records.Select(r => r.BackupId));

                if (Directory.Exists(BackupDir))
                {
                    foreach (var file in Directory.GetFiles(BackupDir, "backup_*.zip"))
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        string id = name.StartsWith("backup_") ? name.Substring(7) : name;
                        if (!keepIds.Contains(id))
                        {
                            try { File.Delete(file); } catch { }
                        }
                    }
                }
            }
            catch
            {
                // 清理失败不影响主流程
            }
        }

        // ====== 内部辅助方法 ======

        /// <summary>从 DataStore 拉取业务表全量快照</summary>
        private static Dictionary<string, object> TakeSnapshot()
        {
            return new Dictionary<string, object>
            {
                ["users"] = DataStore.Current.GetUsers(),
                ["classes"] = DataStore.Current.GetClasses(),
                ["role_permissions"] = DataStore.Current.GetRolePermissions(),
                ["user_permissions"] = DataStore.Current.GetUserPermissions(),
                ["device_authorizations"] = DataStore.Current.GetDeviceAuthorizations(),
                ["devices"] = DataStore.Current.GetDevices(),
                ["fingerprint_templates"] = DataStore.Current.GetFingerprintTemplates()
            };
        }

        /// <summary>把业务表快照写入 zip（每个表一个 JSON 条目）</summary>
        private static void WriteTablesToZip(ZipArchive zip, Dictionary<string, object> snapshot)
        {
            foreach (var kv in snapshot)
            {
                var entry = zip.CreateEntry(kv.Key + ".json");
                using var stream = entry.Open();
                string json = JsonConvert.SerializeObject(kv.Value, Formatting.Indented);
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        /// <summary>
        /// 下载所有指纹模板并写入 zip 的 templates/ 目录
        /// </summary>
        /// <returns>成功写入的模板数量</returns>
        private static async Task<int> WriteTemplatesToZipAsync(ZipArchive zip)
        {
            if (!App.SdStorageService.IsAvailable) return 0;

            var templates = DataStore.Current.GetFingerprintTemplates();
            int count = 0;

            foreach (var t in templates)
            {
                try
                {
                    var bytes = await App.SdStorageService.DownloadTemplateAsync(t.UserId, 1);
                    if (bytes != null && bytes.Length > 0)
                    {
                        var entry = zip.CreateEntry($"templates/FP_{t.UserId}.bin");
                        using var stream = entry.Open();
                        stream.Write(bytes, 0, bytes.Length);
                        count++;
                    }
                }
                catch
                {
                    // 单个模板下载失败忽略，继续下一个
                }
            }

            return count;
        }

        /// <summary>保存备份记录到 SQLite</summary>
        private static BackupRecord SaveBackupRecord(string zipPath, string backupId,
            string triggerAction, string? operatorUserId,
            Dictionary<string, object>.KeyCollection tableKeys, long globalVersion)
        {
            var fileInfo = new FileInfo(zipPath);
            var record = new BackupRecord
            {
                BackupId = backupId,
                TriggerAction = triggerAction,
                OperatorUserId = operatorUserId,
                FilePath = zipPath,
                FileSize = fileInfo.Length,
                Tables = string.Join(",", tableKeys),
                GlobalVersion = globalVersion,
                CreateTime = DateTime.Now
            };
            LogDbService.Current.AddBackupRecord(record);
            return record;
        }
    }

    /// <summary>
    /// 还原结果
    /// </summary>
    public class RestoreResult
    {
        /// <summary>失败时返回错误信息；成功时为 null</summary>
        public string? ErrorMessage { get; set; }

        /// <summary>成功还原的业务表数量</summary>
        public int TableCount { get; set; }

        /// <summary>备份中包含的指纹模板总数</summary>
        public int TemplateTotal { get; set; }

        /// <summary>成功还原的指纹模板数量</summary>
        public int TemplateRestored { get; set; }

        /// <summary>还原失败的指纹模板数量</summary>
        public int TemplateFailed { get; set; }

        /// <summary>是否成功</summary>
        public bool Success => string.IsNullOrEmpty(ErrorMessage);
    }
}
