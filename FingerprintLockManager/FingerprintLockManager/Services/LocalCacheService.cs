using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FingerprintLockManager
{
    /// <summary>
    /// 本地磁盘缓存服务
    /// 当根节点 SD 卡不可用时，作为降级数据源使用；SD 卡恢复后由调用方将本地缓存回传到 SD 卡。
    /// 缓存目录：%APPDATA%\FingerprintLockManager\cache\
    ///   {table}.json        业务表内容（JArray）
    ///   {table}.version     表版本号（uint，纯文本）
    ///   fp_templates\*.bin  指纹模板二进制
    ///   logs.json           降级期间缓存的日志
    /// 线程安全：所有公共方法均通过 lock 保护文件读写。
    /// </summary>
    public static class LocalCacheService
    {
        private static readonly object _lock = new object();

        private const string AppDataFolderName = "FingerprintLockManager";
        private const string CacheFolderName = "cache";
        private const string FpFolderName = "fp_templates";

        private static string _cacheDirectory = "";

        /// <summary>缓存目录绝对路径（首次调用时计算并缓存）</summary>
        public static string GetCacheDirectory()
        {
            if (!string.IsNullOrEmpty(_cacheDirectory) && Directory.Exists(_cacheDirectory))
                return _cacheDirectory;
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            _cacheDirectory = Path.Combine(appData, AppDataFolderName, CacheFolderName);
            return _cacheDirectory;
        }

        /// <summary>初始化缓存目录（目录不存在则创建，可重复调用）</summary>
        public static void Initialize()
        {
            try
            {
                lock (_lock)
                {
                    EnsureCacheDirectory();
                    string fpDir = Path.Combine(GetCacheDirectory(), FpFolderName);
                    if (!Directory.Exists(fpDir))
                        Directory.CreateDirectory(fpDir);
                }
            }
            catch
            {
                // 初始化失败不抛异常，后续读写时会再次尝试
            }
        }

        // ===== 业务表读写 =====

        /// <summary>读取表数据；不存在返回 null</summary>
        public static JArray? ReadTable(string table)
        {
            if (string.IsNullOrWhiteSpace(table)) return null;
            try
            {
                lock (_lock)
                {
                    string path = GetTablePath(table);
                    if (!File.Exists(path)) return null;
                    string json = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(json)) return null;
                    var token = JToken.Parse(json);
                    return token as JArray;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>写入表数据（写入失败不抛异常，避免影响主流程）</summary>
        public static void WriteTable(string table, JArray data)
        {
            if (string.IsNullOrWhiteSpace(table) || data == null) return;
            try
            {
                lock (_lock)
                {
                    EnsureCacheDirectory();
                    string path = GetTablePath(table);
                    string json = data.ToString(Formatting.None);
                    File.WriteAllText(path, json);
                }
            }
            catch
            {
                // 缓存写入失败不能让程序崩溃
            }
        }

        /// <summary>读取表的版本号；不存在返回 0</summary>
        public static uint ReadTableVersion(string table)
        {
            if (string.IsNullOrWhiteSpace(table)) return 0;
            try
            {
                lock (_lock)
                {
                    string path = GetTableVersionPath(table);
                    if (!File.Exists(path)) return 0;
                    string text = File.ReadAllText(path);
                    return uint.TryParse(text.Trim(), out uint v) ? v : 0;
                }
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>写入表版本号</summary>
        public static void WriteTableVersion(string table, uint version)
        {
            if (string.IsNullOrWhiteSpace(table)) return;
            try
            {
                lock (_lock)
                {
                    EnsureCacheDirectory();
                    string path = GetTableVersionPath(table);
                    File.WriteAllText(path, version.ToString());
                }
            }
            catch
            {
                // 忽略
            }
        }

        // ===== 指纹模板 =====

        /// <summary>指纹模板元数据文件名（List<FingerprintTemplate> 序列化为 JSON）</summary>
        private const string FpMetaFileName = "fp_templates.json";

        /// <summary>保存指纹模板到本地缓存（按 userId + fingerIndex 命名）</summary>
        public static void SaveFpTemplate(string userId, int fingerIndex, byte[] template)
        {
            if (string.IsNullOrWhiteSpace(userId) || template == null || template.Length == 0) return;
            try
            {
                lock (_lock)
                {
                    EnsureFpDirectory();
                    string path = GetFpTemplatePath(userId, fingerIndex);
                    File.WriteAllBytes(path, template);
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>读取本地缓存的指纹模板；不存在返回 null</summary>
        public static byte[]? ReadFpTemplate(string userId, int fingerIndex)
        {
            if (string.IsNullOrWhiteSpace(userId)) return null;
            try
            {
                lock (_lock)
                {
                    string path = GetFpTemplatePath(userId, fingerIndex);
                    if (!File.Exists(path)) return null;
                    return File.ReadAllBytes(path);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>列出本地缓存中的所有指纹模板</summary>
        public static List<(string userId, int fingerIndex)> ListFpTemplates()
        {
            var result = new List<(string userId, int fingerIndex)>();
            try
            {
                lock (_lock)
                {
                    string fpDir = Path.Combine(GetCacheDirectory(), FpFolderName);
                    if (!Directory.Exists(fpDir)) return result;
                    foreach (string file in Directory.GetFiles(fpDir, "*.bin"))
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        int sep = name.LastIndexOf('_');
                        if (sep <= 0) continue;
                        string userId = name.Substring(0, sep);
                        if (!int.TryParse(name.Substring(sep + 1), out int idx)) continue;
                        result.Add((userId, idx));
                    }
                }
            }
            catch
            {
                // 忽略
            }
            return result;
        }

        /// <summary>删除指定用户的所有本地缓存指纹模板</summary>
        public static void DeleteFpTemplate(string userId)
        {
            if (string.IsNullOrWhiteSpace(userId)) return;
            try
            {
                lock (_lock)
                {
                    string fpDir = Path.Combine(GetCacheDirectory(), FpFolderName);
                    if (!Directory.Exists(fpDir)) return;
                    string prefix = userId + "_";
                    foreach (string file in Directory.GetFiles(fpDir, prefix + "*.bin"))
                    {
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            catch
            {
                // 忽略
            }
        }

        // ===== 指纹模板元数据（按 fingerprintId 管理） =====

        /// <summary>读取所有指纹模板元数据；文件不存在或解析失败返回空列表</summary>
        public static List<FingerprintTemplate> ReadAllFpTemplateMetas()
        {
            try
            {
                lock (_lock)
                {
                    string path = GetFpMetaPath();
                    if (!File.Exists(path)) return new List<FingerprintTemplate>();
                    string json = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(json)) return new List<FingerprintTemplate>();
                    var list = JsonConvert.DeserializeObject<List<FingerprintTemplate>>(json);
                    return list ?? new List<FingerprintTemplate>();
                }
            }
            catch
            {
                return new List<FingerprintTemplate>();
            }
        }

        /// <summary>读取指定指纹 ID 的元数据；不存在返回 null</summary>
        public static FingerprintTemplate? ReadFpTemplateMeta(int fingerprintId)
        {
            try
            {
                lock (_lock)
                {
                    return ReadAllFpTemplateMetas().FirstOrDefault(m => m.FingerprintId == fingerprintId);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>写入指纹模板元数据（fingerprintId 已存在则覆盖，否则追加）</summary>
        public static void WriteFpTemplateMeta(FingerprintTemplate meta)
        {
            if (meta == null || meta.FingerprintId <= 0) return;
            try
            {
                lock (_lock)
                {
                    EnsureCacheDirectory();
                    var list = ReadAllFpTemplateMetas();
                    int idx = list.FindIndex(m => m.FingerprintId == meta.FingerprintId);
                    if (idx >= 0) list[idx] = meta;
                    else list.Add(meta);
                    string json = JsonConvert.SerializeObject(list, Formatting.None);
                    File.WriteAllText(GetFpMetaPath(), json);
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 同时保存模板字节和元数据。
        /// 模板按 {fingerprintId}_{fingerIndex}.bin 命名（保持与旧格式 userId_fingerIndex.bin 兼容并存）。
        /// </summary>
        public static void SaveFpTemplateWithMeta(int fingerprintId, string? userId, int fingerIndex,
            byte[] template, string sourceDevice)
        {
            if (template == null || template.Length == 0) return;
            if (fingerprintId <= 0) return;
            try
            {
                lock (_lock)
                {
                    EnsureFpDirectory();
                    string path = GetFpTemplateByFpIdPath(fingerprintId, fingerIndex);
                    File.WriteAllBytes(path, template);

                    var meta = new FingerprintTemplate
                    {
                        FingerprintId = fingerprintId,
                        UserId = userId,
                        UserName = null,
                        FingerIndex = fingerIndex,
                        EnrollTime = DateTime.Now,
                        TemplateSize = template.Length,
                        SourceDevice = sourceDevice ?? "",
                        BackupStatus = "local"
                    };
                    // 保留旧的 userName（若已有元数据）
                    var existing = ReadAllFpTemplateMetas()
                        .FirstOrDefault(m => m.FingerprintId == fingerprintId);
                    if (existing != null)
                    {
                        meta.UserName = existing.UserName;
                        meta.Note = existing.Note;
                        if (!string.IsNullOrEmpty(existing.BackupStatus))
                            meta.BackupStatus = existing.BackupStatus;
                    }
                    WriteFpTemplateMeta(meta);
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>绑定指纹模板到指定用户（更新元数据的 UserId 和 UserName）</summary>
        public static bool BindFpTemplateToUser(int fingerprintId, string userId, string? userName)
        {
            if (fingerprintId <= 0 || string.IsNullOrWhiteSpace(userId)) return false;
            try
            {
                lock (_lock)
                {
                    var list = ReadAllFpTemplateMetas();
                    int idx = list.FindIndex(m => m.FingerprintId == fingerprintId);
                    if (idx < 0) return false;
                    list[idx].UserId = userId;
                    list[idx].UserName = userName;
                    string json = JsonConvert.SerializeObject(list, Formatting.None);
                    File.WriteAllText(GetFpMetaPath(), json);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>更新指纹模板的备份状态；不存在返回 false</summary>
        public static bool UpdateFpTemplateBackupStatus(int fingerprintId, string backupStatus)
        {
            if (fingerprintId <= 0) return false;
            try
            {
                lock (_lock)
                {
                    var list = ReadAllFpTemplateMetas();
                    int idx = list.FindIndex(m => m.FingerprintId == fingerprintId);
                    if (idx < 0) return false;
                    list[idx].BackupStatus = backupStatus;
                    string json = JsonConvert.SerializeObject(list, Formatting.None);
                    File.WriteAllText(GetFpMetaPath(), json);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>按指纹 ID 读取本地模板字节（同时支持新命名 fingerprintId_fingerIndex.bin）</summary>
        public static byte[]? ReadFpTemplateByFingerprintId(int fingerprintId, int fingerIndex)
        {
            if (fingerprintId <= 0) return null;
            try
            {
                lock (_lock)
                {
                    string path = GetFpTemplateByFpIdPath(fingerprintId, fingerIndex);
                    if (File.Exists(path)) return File.ReadAllBytes(path);

                    // 兼容：尝试通过元数据中的 userId 找到旧格式文件
                    var meta = ReadAllFpTemplateMetas()
                        .FirstOrDefault(m => m.FingerprintId == fingerprintId);
                    if (meta != null && !string.IsNullOrWhiteSpace(meta.UserId))
                    {
                        string legacyPath = GetFpTemplatePath(meta.UserId, fingerIndex);
                        if (File.Exists(legacyPath)) return File.ReadAllBytes(legacyPath);
                    }
                    return null;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>删除指定指纹 ID 的本地模板字节和元数据</summary>
        public static bool DeleteFpTemplateByFingerprintId(int fingerprintId)
        {
            if (fingerprintId <= 0) return false;
            bool removed = false;
            try
            {
                lock (_lock)
                {
                    // 删除元数据
                    var list = ReadAllFpTemplateMetas();
                    int idx = list.FindIndex(m => m.FingerprintId == fingerprintId);
                    if (idx >= 0)
                    {
                        var meta = list[idx];
                        list.RemoveAt(idx);
                        File.WriteAllText(GetFpMetaPath(),
                            JsonConvert.SerializeObject(list, Formatting.None));
                        removed = true;

                        // 删除模板字节文件（新命名 + 旧命名）
                        string fpDir = Path.Combine(GetCacheDirectory(), FpFolderName);
                        if (Directory.Exists(fpDir))
                        {
                            string newPath = GetFpTemplateByFpIdPath(fingerprintId, meta.FingerIndex);
                            if (File.Exists(newPath))
                            {
                                try { File.Delete(newPath); } catch { }
                            }
                            if (!string.IsNullOrWhiteSpace(meta.UserId))
                            {
                                string legacyPath = GetFpTemplatePath(meta.UserId, meta.FingerIndex);
                                if (File.Exists(legacyPath))
                                {
                                    try { File.Delete(legacyPath); } catch { }
                                }
                            }
                        }
                    }
                }
            }
            catch
            {
                // 忽略
            }
            return removed;
        }

        // ===== 日志 =====

        /// <summary>追加一条日志到本地缓存（自动分配 ID）</summary>
        public static void AppendLog(LogEntry log)
        {
            if (log == null) return;
            try
            {
                lock (_lock)
                {
                    EnsureCacheDirectory();
                    var logs = ReadLogsInternal();
                    if (log.Id <= 0)
                    {
                        long maxId = logs.Count > 0 ? logs.Max(l => l.Id) : 0;
                        log.Id = maxId + 1;
                    }
                    logs.Add(log);
                    string path = GetLogPath();
                    string json = JsonConvert.SerializeObject(logs, Formatting.None);
                    File.WriteAllText(path, json);
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>读取本地缓存的日志列表</summary>
        public static List<LogEntry> ReadLogs()
        {
            try
            {
                lock (_lock)
                {
                    return ReadLogsInternal();
                }
            }
            catch
            {
                return new List<LogEntry>();
            }
        }

        /// <summary>清空本地缓存的日志</summary>
        public static void ClearLogs()
        {
            try
            {
                lock (_lock)
                {
                    string path = GetLogPath();
                    if (File.Exists(path))
                        File.Delete(path);
                }
            }
            catch
            {
                // 忽略
            }
        }

        // ===== 内部辅助 =====

        private static List<LogEntry> ReadLogsInternal()
        {
            var result = new List<LogEntry>();
            string path = GetLogPath();
            if (!File.Exists(path)) return result;
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return result;
            var arr = JArray.Parse(json);
            foreach (var token in arr.OfType<JObject>())
            {
                var log = new LogEntry
                {
                    Id = token.Value<long?>("id") ?? 0,
                    DeviceId = token.Value<string>("device_id") ?? "",
                    UserId = token.Value<string>("user_id") ?? "",
                    LockId = token.Value<int?>("lock_id") ?? 0,
                    Action = token.Value<string>("action") ?? "",
                    Result = token.Value<string>("result") ?? "",
                    Reason = token.Value<string>("reason") ?? "",
                    CreateTime = token.Value<DateTime?>("create_time") ?? DateTime.MinValue
                };
                result.Add(log);
            }
            return result;
        }

        private static void EnsureCacheDirectory()
        {
            string dir = GetCacheDirectory();
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        private static void EnsureFpDirectory()
        {
            EnsureCacheDirectory();
            string fpDir = Path.Combine(GetCacheDirectory(), FpFolderName);
            if (!Directory.Exists(fpDir))
                Directory.CreateDirectory(fpDir);
        }

        private static string GetTablePath(string table) =>
            Path.Combine(GetCacheDirectory(), table + ".json");

        private static string GetTableVersionPath(string table) =>
            Path.Combine(GetCacheDirectory(), table + ".version");

        private static string GetFpTemplatePath(string userId, int fingerIndex) =>
            Path.Combine(GetCacheDirectory(), FpFolderName, $"{userId}_{fingerIndex}.bin");

        /// <summary>按指纹 ID 命名的模板文件路径</summary>
        private static string GetFpTemplateByFpIdPath(int fingerprintId, int fingerIndex) =>
            Path.Combine(GetCacheDirectory(), FpFolderName, $"{fingerprintId}_{fingerIndex}.bin");

        /// <summary>指纹模板元数据 JSON 文件路径</summary>
        private static string GetFpMetaPath() =>
            Path.Combine(GetCacheDirectory(), FpMetaFileName);

        private static string GetLogPath() =>
            Path.Combine(GetCacheDirectory(), "logs.json");
    }
}
