using System.IO;
using System.Linq;

namespace CabinetLock
{
    /// <summary>
    /// 业务库 business.db 的备份 / 历史版本 / 同步前后替换管理。
    ///
    /// 同步 SD 前不应直接覆盖本地库：先备份当前主库到带时间戳的快照，
    /// 再让 SD 同步写入临时库 business_sync.db；同步成功才用临时库替换主库，
    /// 失败则丢弃临时库、主库保持原样。历史快照供“使用本地历史数据继续”使用。
    /// </summary>
    public static class BusinessDatabaseBackupService
    {
        private const string MainFileName = SqlitePaths.BusinessFileName; // business.db
        private const string TempFileName = "business_sync.db";
        private const string BackupPrefix = "business_";
        private const string BackupSuffix = ".bak.db";

        public static string MainDbPath => SqlitePaths.BusinessDbPath;
        public static string TempDbPath => Path.Combine(SqlitePaths.GetDataDirectory(), TempFileName);

        /// <summary>历史备份目录：data\backups\</summary>
        public static string BackupDirectory
        {
            get
            {
                string dir = Path.Combine(SqlitePaths.GetDataDirectory(), "backups");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return dir;
            }
        }

        // ===== 主库 <-> 临时库 同步生命周期 =====

        /// <summary>
        /// 开始一次 SD 同步：
        /// 1) 若主库存在且有数据，先备份成带时间戳的快照（失败不阻塞）。
        /// 2) 复制主库为 business_sync.db，再将 BusinessDatabase 指向临时副本。
        /// 同步期间所有 ReplaceTable 都会写进临时库，主库 business.db 不受影响。
        /// 未参与启动同步的指纹模板等本地表会保留在临时副本中。
        /// </summary>
        public static void BeginSyncToTemp()
        {
            BusinessDatabase.SetActivePath(MainDbPath);
            BusinessDatabase.Initialize();
            BusinessDatabase.Checkpoint();

            // 备份当前主库（带时间戳）。失败不阻塞同步流程。
            try { BackupCurrent(TimeStampNow()); }
            catch { /* 备份失败不影响同步 */ }

            // 清理残留临时库
            DeleteFileSafe(TempDbPath);
            DeleteFileSafe(TempDbPath + "-wal");
            DeleteFileSafe(TempDbPath + "-shm");

            File.Copy(MainDbPath, TempDbPath, overwrite: true);

            // 切到临时副本；五张 SD 业务表随后逐表覆盖。
            BusinessDatabase.SetActivePath(TempDbPath);
            BusinessDatabase.Initialize();
        }

        /// <summary>
        /// SD 同步成功：把临时库替换为主库。
        /// 先 checkpoint 临时库落盘，再删除主库相关文件，最后把临时库重命名成 business.db。
        /// 替换完成后把 ActiveDbPath 切回主库并重新初始化。
        /// </summary>
        public static void CommitTempAsMain()
        {
            // 临时库 WAL 落盘
            try { BusinessDatabase.Checkpoint(); }
            catch { /* 尽力而为 */ }

            BusinessDatabase.SetActivePath(MainDbPath); // 先切走，释放对临时库文件的占用

            ReplaceTempAsMainWithRetry();
            DeleteFileSafe(TempDbPath + "-wal");
            DeleteFileSafe(TempDbPath + "-shm");
            DeleteFileSafe(MainDbPath + "-wal");
            DeleteFileSafe(MainDbPath + "-shm");

            BusinessDatabase.Initialize();
        }

        /// <summary>
        /// SD 同步失败或用户取消：丢弃临时库，主库保持原样，切回主库。
        /// </summary>
        public static void AbortTemp()
        {
            BusinessDatabase.SetActivePath(MainDbPath);
            try { BusinessDatabase.Initialize(); }
            catch { /* 主库可能尚不存在 */ }

            DeleteFileSafe(TempDbPath);
            DeleteFileSafe(TempDbPath + "-wal");
            DeleteFileSafe(TempDbPath + "-shm");
        }

        // ===== 备份与历史快照 =====

        /// <summary>把当前主库复制为一份带时间戳的备份快照。返回备份文件路径（失败返回 null）。</summary>
        public static string? BackupCurrent(string tag)
        {
            if (!File.Exists(MainDbPath)) return null;

            // checkpoint 主库，确保 WAL 数据写入主文件
            try
            {
                BusinessDatabase.SetActivePath(MainDbPath);
                BusinessDatabase.Checkpoint();
            }
            catch { /* 尽力而为 */ }

            string safeTag = string.IsNullOrWhiteSpace(tag) ? TimeStampNow() : SanitizeTag(tag);
            string path = Path.Combine(BackupDirectory, $"{BackupPrefix}{safeTag}{BackupSuffix}");
            File.Copy(MainDbPath, path, overwrite: true);
            return path;
        }

        /// <summary>列出所有历史备份快照，按时间倒序（最新的在前）。</summary>
        public static List<BackupEntry> ListBackups()
        {
            var dir = BackupDirectory;
            return Directory.GetFiles(dir, $"{BackupPrefix}*{BackupSuffix}")
                .Select(p => new BackupEntry(p, File.GetLastWriteTime(p)))
                .OrderByDescending(b => b.Time)
                .ToList();
        }

        /// <summary>最近的一份历史备份；没有则返回 null。</summary>
        public static BackupEntry? GetLatestBackup()
        {
            var list = ListBackups();
            return list.Count == 0 ? null : list[0];
        }

        /// <summary>
        /// 把最近一份（或指定）历史备份还原为主库并切换过去。
        /// 用于“使用本地历史数据继续”：替换主库 business.db 为历史快照。
        /// </summary>
        public static bool RestoreLatestBackup()
        {
            var entry = GetLatestBackup();
            if (entry == null) return false;
            return RestoreBackup(entry);
        }

        public static bool RestoreBackup(BackupEntry entry)
        {
            if (!File.Exists(entry.Path)) return false;

            // 切走并清理主库文件
            try { BusinessDatabase.SetActivePath(TempDbPath); }
            catch { /* 已切 */ }
            DeleteFileSafe(MainDbPath);
            DeleteFileSafe(MainDbPath + "-wal");
            DeleteFileSafe(MainDbPath + "-shm");

            File.Copy(entry.Path, MainDbPath, overwrite: true);

            BusinessDatabase.SetActivePath(MainDbPath);
            BusinessDatabase.Initialize();
            return true;
        }

        // ===== helpers =====

        private static string TimeStampNow() =>
            DateTime.Now.ToString("yyyyMMdd_HHmmss");

        private static string SanitizeTag(string tag)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(tag.Length);
            foreach (char c in tag)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            return sb.ToString();
        }

        private static void DeleteFileSafe(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch { /* 被占用等，忽略；调用方会重试或继续 */ }
        }

        private static void ReplaceTempAsMainWithRetry()
        {
            if (!File.Exists(TempDbPath))
                throw new FileNotFoundException("同步临时业务库不存在", TempDbPath);

            Exception? lastError = null;
            for (int attempt = 1; attempt <= 5; attempt++)
            {
                try
                {
                    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                    if (File.Exists(MainDbPath))
                        File.Replace(TempDbPath, MainDbPath, null, ignoreMetadataErrors: true);
                    else
                        File.Move(TempDbPath, MainDbPath);
                    return;
                }
                catch (IOException ex)
                {
                    lastError = ex;
                    if (attempt < 5)
                        Thread.Sleep(attempt * 100);
                }
            }

            throw new IOException("应用同步业务库失败：数据库文件仍被占用", lastError);
        }

        public sealed class BackupEntry
        {
            public string Path { get; }
            public DateTime Time { get; }
            public string Name => System.IO.Path.GetFileName(Path);
            public long SizeBytes { get; }

            public BackupEntry(string path, DateTime time)
            {
                Path = path;
                Time = time;
                try { SizeBytes = new FileInfo(path).Length; } catch { SizeBytes = 0; }
            }
        }
    }
}
