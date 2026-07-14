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
    /// 备份内容：从 DataStore 内存快照拉取全量业务表（users/classes/permissions/authorizations/devices/fp_templates），
    /// 序列化为 JSON 打包成 zip 存于 ./Backups/。
    /// 备份记录写入 SQLite（经 LogDbService）。
    /// </summary>
    public class BackupService
    {
        /// <summary>备份文件目录</summary>
        private static string BackupDir => Path.Combine(AppContext.BaseDirectory, "Backups");

        /// <summary>获取备份列表</summary>
        public List<BackupRecord> GetBackupRecords()
        {
            return LogDbService.Current.GetBackupRecords();
        }

        /// <summary>
        /// 在用户操作前创建自动备份
        /// </summary>
        /// <param name="triggerAction">触发备份的操作描述</param>
        /// <param name="operatorUserId">操作人</param>
        /// <returns>备份记录；失败返回 null</returns>
        public BackupRecord? BackupBeforeAction(string triggerAction, string? operatorUserId)
        {
            try
            {
                Directory.CreateDirectory(BackupDir);

                // 生成备份 ID（时间戳）
                string backupId = DateTime.Now.ToString("yyyyMMddHHmmss");
                string zipPath = Path.Combine(BackupDir, $"backup_{backupId}.zip");

                // 拉取 DataStore 全量快照
                var snapshot = new Dictionary<string, object>
                {
                    ["users"] = DataStore.Current.GetUsers(),
                    ["classes"] = DataStore.Current.GetClasses(),
                    ["role_permissions"] = DataStore.Current.GetRolePermissions(),
                    ["user_permissions"] = DataStore.Current.GetUserPermissions(),
                    ["device_authorizations"] = DataStore.Current.GetDeviceAuthorizations(),
                    ["devices"] = DataStore.Current.GetDevices(),
                    ["fingerprint_templates"] = DataStore.Current.GetFingerprintTemplates()
                };

                // 打包为 zip
                using (var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
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

                var fileInfo = new FileInfo(zipPath);
                var record = new BackupRecord
                {
                    BackupId = backupId,
                    TriggerAction = triggerAction,
                    OperatorUserId = operatorUserId,
                    FilePath = zipPath,
                    FileSize = fileInfo.Length,
                    Tables = string.Join(",", snapshot.Keys),
                    GlobalVersion = 0, // TODO: 从 SdStorageService 查询版本号
                    CreateTime = DateTime.Now
                };
                LogDbService.Current.AddBackupRecord(record);

                System.Diagnostics.Debug.WriteLine($"[Backup] 备份完成：{backupId} ({fileInfo.Length} 字节) - {triggerAction}");
                return record;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Backup] 备份失败：{ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 从备份还原数据
        /// 注意：还原操作会将备份中的数据写回根节点 SD 卡（覆盖当前数据）。
        /// 还原前会自动创建一次当前状态的备份。
        /// </summary>
        /// <param name="backupId">备份 ID</param>
        /// <param name="operatorUserId">操作人</param>
        /// <returns>成功返回 null；失败返回错误信息</returns>
        public async Task<string?> RestoreFromBackupAsync(string backupId, string? operatorUserId)
        {
            try
            {
                var records = LogDbService.Current.GetBackupRecords(500);
                var target = records.FirstOrDefault(r => r.BackupId == backupId);
                if (target == null) return "备份记录不存在";
                if (!File.Exists(target.FilePath)) return "备份文件已丢失";

                // 还原前先备份当前状态
                BackupBeforeAction($"还原前自动备份（目标：{backupId}）", operatorUserId);

                // 读取 zip 中的各表 JSON
                var tableJsons = new Dictionary<string, string>();
                using (var fs = File.OpenRead(target.FilePath))
                using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    foreach (var entry in zip.Entries)
                    {
                        string tableName = Path.GetFileNameWithoutExtension(entry.Name);
                        using var stream = entry.Open();
                        using var reader = new StreamReader(stream);
                        tableJsons[tableName] = reader.ReadToEnd();
                    }
                }

                // 逐表写回根节点 SD 卡
                var sd = App.SdStorageService;
                if (!sd.IsAvailable) return "根节点 SD 卡不可用，无法还原";

                var tableNames = new[] { "users", "classes", "role_permissions", "user_permissions",
                    "device_authorizations", "devices", "fingerprint_templates" };
                foreach (var name in tableNames)
                {
                    if (tableJsons.TryGetValue(name, out var json))
                    {
                        await sd.SaveTableAsync(name, json);
                    }
                }

                // 重新加载 DataStore
                await DataStore.Current.LoadFromSdCardAsync();

                System.Diagnostics.Debug.WriteLine($"[Backup] 还原完成：{backupId}");
                return null;
            }
            catch (Exception ex)
            {
                return $"还原失败：{ex.Message}";
            }
        }

        /// <summary>清理过期的备份文件（保留最近 N 个）</summary>
        public void CleanupOldBackups(int keepCount = 30)
        {
            try
            {
                var records = LogDbService.Current.GetBackupRecords(keepCount);
                var keepIds = new HashSet<string>(records.Select(r => r.BackupId));

                // 扫描备份目录，删除不在保留列表中的文件
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
    }
}
