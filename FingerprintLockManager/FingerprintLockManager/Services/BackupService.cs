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
    /// 备份内容：业务表 JSON + 指纹模板二进制文件（全量备份）
    ///
    /// 模板下载为串行（USB 串口是单条物理链路，无法真正并发；
    /// 根节点固件单线程顺序处理请求，并发只会让响应交织、增加风险无收益）。
    /// 2Mbps 波特率下，单次往返约 15-20ms，200 枚模板串行下载约 3-4 秒。
    /// 根节点不在线时跳过模板下载，仅备份业务表（从内存快照，&lt;100ms）。
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

        /// <summary>单枚模板下载超时（毫秒，配合 2Mbps 波特率）</summary>
        private const int TemplateDownloadTimeoutMs = 3000;

        /// <summary>获取备份列表</summary>
        public List<BackupRecord> GetBackupRecords()
        {
            return LogDbService.Current.GetBackupRecords();
        }

        /// <summary>
        /// 在用户操作前创建自动备份（全量：业务表 JSON + 指纹模板二进制文件）
        ///
        /// 同步方法，供各业务操作前调用。
        /// 内部在 ThreadPool 线程执行异步全量备份，避免 UI 死锁。
        /// 模板串行下载（USB 串口单链路，无法并发），200 枚约 3-4 秒。
        /// 根节点不在线时跳过模板下载，仅备份业务表（&lt;100ms）。
        /// </summary>
        /// <param name="triggerAction">触发备份的操作描述</param>
        /// <param name="operatorUserId">操作人</param>
        /// <returns>备份记录；失败返回 null</returns>
        public BackupRecord? BackupBeforeAction(string triggerAction, string? operatorUserId)
        {
            try
            {
                // 在 ThreadPool 线程执行异步全量备份，同步等待结果
                // 避免在 UI 线程直接 .Result 导致死锁
                return Task.Run(() =>
                    ExecuteFullBackupAsync(triggerAction, operatorUserId)).GetAwaiter().GetResult();
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
        /// </summary>
        /// <param name="triggerAction">触发备份的操作描述</param>
        /// <param name="operatorUserId">操作人</param>
        /// <returns>备份记录；失败返回 null</returns>
        public async Task<BackupRecord?> CreateFullBackupAsync(string triggerAction, string? operatorUserId)
        {
            try
            {
                return await ExecuteFullBackupAsync(triggerAction, operatorUserId);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Backup] 完整备份失败：{ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 执行全量备份的核心逻辑（业务表 + 指纹模板并行下载）
        /// </summary>
        private static async Task<BackupRecord?> ExecuteFullBackupAsync(string triggerAction, string? operatorUserId)
        {
            Directory.CreateDirectory(BackupDir);

            // 生成备份 ID（时间戳，精确到秒）
            string backupId = DateTime.Now.ToString("yyyyMMddHHmmss");
            string zipPath = Path.Combine(BackupDir, $"backup_{backupId}.zip");

            // 拉取 DataStore 业务表全量快照
            var snapshot = TakeSnapshot();

            // 串行下载指纹模板二进制文件
            // USB 串口是单条物理链路，无法真正并发；
            // 根节点固件单线程顺序处理请求，并发只会让响应交织、增加风险无收益。
            var templates = snapshot["fingerprint_templates"] as List<FingerprintTemplate> ?? new List<FingerprintTemplate>();
            var templateData = await DownloadTemplatesSerialAsync(templates);
            int templateCount = templateData.Count;

            // 打包为 zip
            using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                // 1. 写入业务表 JSON
                WriteTablesToZip(zip, snapshot);

                // 2. 写入指纹模板二进制文件
                foreach (var (userId, bytes) in templateData)
                {
                    var entry = zip.CreateEntry($"templates/FP_{userId}.bin");
                    using var stream = entry.Open();
                    stream.Write(bytes, 0, bytes.Length);
                }
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
                // 更新 SQLite 中的记录
                LogDbService.Current.AddBackupRecord(record);
            }

            System.Diagnostics.Debug.WriteLine(
                $"[Backup] 全量备份完成：{backupId}，模板 {templateCount} 枚");

            return record;
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
        /// <returns>还原结果；失败时 ErrorMessage 非空</returns>
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
                        // 业务表 JSON（根目录下的 .json 文件）
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
        /// 串行下载所有指纹模板二进制文件
        ///
        /// 为什么不能并发：
        ///   - USB 串口是单条物理链路，所有数据必须按位串行传输，无法真正并行
        ///   - 根节点固件单线程顺序处理请求，并发请求会在串口缓冲区排队，总耗时不变
        ///   - 多个请求的响应可能交织，虽然 msg_id 匹配能处理，但增加复杂度无收益
        ///
        /// 2Mbps 波特率下，单次往返约 15-20ms：
        ///   - 请求 ~100B JSON → 0.4ms
        ///   - 根节点读 SD 卡 → 5-10ms
        ///   - 响应 512B 模板 hex 编码 ~1.2KB → 5ms
        /// 200 枚 × 20ms ≈ 3-4 秒。
        /// 单枚失败不影响其他，最终返回成功下载的列表。
        /// </summary>
        /// <param name="templates">指纹模板元数据列表</param>
        /// <returns>成功下载的 (userId, bytes) 列表</returns>
        private static async Task<List<(string userId, byte[] bytes)>> DownloadTemplatesSerialAsync(
            List<FingerprintTemplate> templates)
        {
            if (!App.SdStorageService.IsAvailable || templates.Count == 0)
            {
                return new List<(string, byte[])>();
            }

            var result = new List<(string userId, byte[] bytes)>();

            foreach (var t in templates)
            {
                try
                {
                    var bytes = await App.SdStorageService.DownloadTemplateAsync(
                        t.UserId, 1, TemplateDownloadTimeoutMs);
                    if (bytes != null && bytes.Length > 0)
                    {
                        result.Add((t.UserId, bytes));
                    }
                }
                catch
                {
                    // 单个模板下载失败忽略，不影响整体备份
                }
            }

            return result;
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
