using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CabinetLock
{
    /// <summary>
    /// 本机业务库 business.db：users / classes / permissions / role_permissions / devices。
    /// 运行期读写本库；启动从 SD 覆盖导入，关闭回写 SD。
    /// </summary>
    public static partial class BusinessDatabase
    {
        private static readonly object Sync = new();
        private static bool _initialized;

        public static readonly string[] BusinessTables =
        {
            "users", "classes", "permissions", "role_permissions", "devices", "fingerprints",
            "system_settings"
        };

        // Routine synchronization intentionally excludes fingerprint metadata/blob data.
        // Fingerprints are written to SD at enrollment time and are handled by full backup.
        public static readonly string[] DailySyncTables =
        {
            "users", "classes", "permissions", "role_permissions", "devices", "system_settings"
        };

        /// <summary>
        /// 当前实际使用的 SQLite 文件路径。默认指向 business.db；
        /// 启动同步期间可临时切换到 business_sync.db，确认无误后再提交替换。
        /// </summary>
        public static string ActiveDbPath { get; private set; } = SqlitePaths.BusinessDbPath;

        public static void Initialize()
        {
            lock (Sync)
            {
                if (_initialized) return;
                using var conn = Open();
                EnsureSchema(conn);
                _initialized = true;
            }
        }

        /// <summary>
        /// 切换 ActiveDbPath 到临时同步库并强制重新初始化。
        /// 仅用于启动同步：先写入临时库，成功后由备份服务替换主库文件。
        /// </summary>
        public static void SetActivePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("path 不能为空", nameof(path));
            lock (Sync)
            {
                SqliteConnection.ClearAllPools();
                ActiveDbPath = path;
                _initialized = false;
            }
        }

        /// <summary>
        /// 将当前 ActiveDbPath 的 WAL 合并回主库文件，确保备份/替换前数据已落盘。
        /// </summary>
        public static void Checkpoint()
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
            }
        }

        public static SqliteConnection Open()
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = ActiveDbPath,
                Pooling = false
            }.ToString();
            var conn = new SqliteConnection(connectionString);
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;";
                pragma.ExecuteNonQuery();
            }
            return conn;
        }

        private static void EnsureSchema(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS table_meta (
  table_name TEXT PRIMARY KEY,
  version INTEGER NOT NULL DEFAULT 0,
  updated_at TEXT
);
CREATE TABLE IF NOT EXISTS users (
  user_id TEXT PRIMARY KEY,
  user_code TEXT,
  name TEXT NOT NULL,
  gender TEXT NOT NULL DEFAULT '',
  role TEXT NOT NULL,
  class_id TEXT,
  class_ids_json TEXT,
  assigned_device_ids_json TEXT,
  cabinet_assignments_json TEXT,
  fingerprint_id INTEGER,
  password_salt TEXT NOT NULL DEFAULT '',
  password_hash TEXT NOT NULL DEFAULT '',
  enabled INTEGER NOT NULL DEFAULT 1,
  create_time TEXT NOT NULL,
  update_time TEXT
);
CREATE TABLE IF NOT EXISTS classes (
  class_id TEXT PRIMARY KEY,
  name TEXT NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  create_time TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS permissions (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  user_id TEXT NOT NULL,
  lock_id INTEGER NOT NULL,
  has_access INTEGER NOT NULL,
  update_time TEXT NOT NULL,
  UNIQUE(user_id, lock_id)
);
CREATE TABLE IF NOT EXISTS role_permissions (
  role TEXT PRIMARY KEY,
  lock_0 INTEGER NOT NULL DEFAULT 0,
  lock_1 INTEGER NOT NULL DEFAULT 0,
  lock_2 INTEGER NOT NULL DEFAULT 0,
  lock_3 INTEGER NOT NULL DEFAULT 0,
  update_time TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS devices (
  device_id TEXT PRIMARY KEY,
  device_name TEXT,
  device_number TEXT,
  ip_address TEXT,
  online INTEGER NOT NULL DEFAULT 0,
  register_time TEXT,
  last_online_time TEXT,
  last_seen INTEGER DEFAULT 0,
  offline_time INTEGER DEFAULT 0,
  mesh_mac TEXT,
  is_root INTEGER NOT NULL DEFAULT 0,
  firmware_version TEXT,
  hardware_version TEXT,
  status_json TEXT
);
CREATE TABLE IF NOT EXISTS system_settings (
  setting_key TEXT PRIMARY KEY,
  setting_value TEXT NOT NULL,
  config_version INTEGER NOT NULL DEFAULT 1,
  update_time TEXT NOT NULL
);
INSERT OR IGNORE INTO system_settings(setting_key,setting_value,config_version,update_time)
VALUES('maintenance_pin','112233',1,datetime('now'));
INSERT OR IGNORE INTO table_meta(table_name,version,updated_at)
VALUES('system_settings',1,datetime('now'));
CREATE TABLE IF NOT EXISTS fingerprints (
  fingerprint_id INTEGER PRIMARY KEY,
  user_id TEXT,
  user_name TEXT,
  finger_index INTEGER NOT NULL DEFAULT 1,
  finger_name TEXT NOT NULL DEFAULT '',
  quality INTEGER NOT NULL DEFAULT 0,
  enabled INTEGER NOT NULL DEFAULT 1,
  enroll_time TEXT,
  template_size INTEGER NOT NULL DEFAULT 0,
  source_device TEXT,
  backup_status TEXT,
  note TEXT,
  template_blob BLOB
);
CREATE INDEX IF NOT EXISTS ix_fp_user ON fingerprints(user_id);
CREATE TABLE IF NOT EXISTS cabinet_sync_queue (
  job_key TEXT PRIMARY KEY,
  job_kind TEXT NOT NULL,
  user_id TEXT NOT NULL DEFAULT '',
  device_id TEXT NOT NULL,
  reason TEXT NOT NULL DEFAULT '',
  state TEXT NOT NULL DEFAULT 'pending',
  attempt_count INTEGER NOT NULL DEFAULT 0,
  next_attempt_time TEXT,
  last_error TEXT NOT NULL DEFAULT '',
  update_time TEXT NOT NULL,
  complete_time TEXT
);
CREATE INDEX IF NOT EXISTS ix_sync_queue_due
ON cabinet_sync_queue(state, next_attempt_time);
CREATE INDEX IF NOT EXISTS ix_sync_queue_user
ON cabinet_sync_queue(job_kind, user_id);
CREATE INDEX IF NOT EXISTS ix_sync_queue_device
ON cabinet_sync_queue(job_kind, device_id);
CREATE INDEX IF NOT EXISTS ix_users_role_id
ON users(role, user_id);
CREATE INDEX IF NOT EXISTS ix_users_class_role
ON users(class_id, role);
CREATE INDEX IF NOT EXISTS ix_classes_name
ON classes(name);
CREATE INDEX IF NOT EXISTS ix_permissions_user_update
ON permissions(user_id, update_time);
";
            cmd.ExecuteNonQuery();
            EnsureColumn(conn, "users", "gender", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "users", "user_code", "TEXT");
            EnsureColumn(conn, "users", "class_ids_json", "TEXT");
            EnsureColumn(conn, "users", "assigned_device_ids_json", "TEXT");
            EnsureColumn(conn, "users", "cabinet_assignments_json", "TEXT");
            EnsureColumn(conn, "devices", "device_number", "TEXT");
            EnsureColumn(conn, "devices", "hardware_version", "TEXT");
            EnsureColumn(conn, "fingerprints", "finger_name", "TEXT NOT NULL DEFAULT ''");
            EnsureColumn(conn, "fingerprints", "quality", "INTEGER NOT NULL DEFAULT 0");
            EnsureColumn(conn, "fingerprints", "enabled", "INTEGER NOT NULL DEFAULT 1");
            using var index = conn.CreateCommand();
            index.CommandText = @"
UPDATE devices SET device_number=NULL
WHERE device_number IS NOT NULL AND device_number <> ''
  AND rowid NOT IN (
    SELECT MIN(rowid) FROM devices
    WHERE device_number IS NOT NULL AND device_number <> ''
    GROUP BY device_number COLLATE NOCASE
  );
DROP INDEX IF EXISTS ux_devices_number;
CREATE UNIQUE INDEX ux_devices_number
ON devices(device_number COLLATE NOCASE)
WHERE device_number IS NOT NULL AND device_number <> '';";
            index.ExecuteNonQuery();
        }

        private static void EnsureColumn(
            SqliteConnection conn, string table, string column, string definition)
        {
            using var check = conn.CreateCommand();
            check.CommandText = $"PRAGMA table_info({table});";
            using var reader = check.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                    return;
            }
            reader.Close();
            using var alter = conn.CreateCommand();
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition};";
            alter.ExecuteNonQuery();
        }

        public static uint GetTableVersion(string table)
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT version FROM table_meta WHERE table_name=$t";
                cmd.Parameters.AddWithValue("$t", table);
                var o = cmd.ExecuteScalar();
                if (o == null || o is DBNull) return 0;
                return Convert.ToUInt32(o, CultureInfo.InvariantCulture);
            }
        }

        public static void SetTableVersion(string table, uint version)
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO table_meta(table_name, version, updated_at) VALUES($t,$v,$u)
ON CONFLICT(table_name) DO UPDATE SET version=$v, updated_at=$u;";
                cmd.Parameters.AddWithValue("$t", table);
                cmd.Parameters.AddWithValue("$v", (long)version);
                cmd.Parameters.AddWithValue("$u", DateTime.Now.ToString("o"));
                cmd.ExecuteNonQuery();
            }
        }

        public static bool HasAnyBusinessData()
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                foreach (string table in BusinessTables)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = $"SELECT COUNT(1) FROM {table}";
                    long n = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                    if (n > 0) return true;
                }
                return false;
            }
        }

        public static JArray ReadArray(string table)
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                return table switch
                {
                    "users" => ReadUsers(conn),
                    "classes" => ReadClasses(conn),
                    "permissions" => ReadPermissions(conn),
                    "role_permissions" => ReadRolePermissions(conn),
                    "devices" => ReadDevices(conn),
                    "system_settings" => ReadSystemSettings(conn),
                    "fingerprints" => ReadFingerprintMetadata(conn),
                    _ => throw new ArgumentException($"未知业务表: {table}", nameof(table))
                };
            }
        }

        /// <summary>整表替换并更新版本号（启动从 SD 导入 / 运行期 Save）。</summary>
        public static void ReplaceTable(string table, JArray array, uint version)
        {
            if (array == null) array = new JArray();
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                using var tx = conn.BeginTransaction();
                switch (table)
                {
                    case "users": WriteUsers(conn, tx, array); break;
                    case "classes": WriteClasses(conn, tx, array); break;
                    case "permissions": WritePermissions(conn, tx, array); break;
                    case "role_permissions": WriteRolePermissions(conn, tx, array); break;
                    case "devices": WriteDevices(conn, tx, array); break;
                    case "system_settings": WriteSystemSettings(conn, tx, array); break;
                    case "fingerprints": WriteFingerprintMetadata(conn, tx, array); break;
                    default: throw new ArgumentException($"未知业务表: {table}", nameof(table));
                }
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT INTO table_meta(table_name, version, updated_at) VALUES($t,$v,$u)
ON CONFLICT(table_name) DO UPDATE SET version=$v, updated_at=$u;";
                    cmd.Parameters.AddWithValue("$t", table);
                    cmd.Parameters.AddWithValue("$v", (long)version);
                    cmd.Parameters.AddWithValue("$u", DateTime.Now.ToString("o"));
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        /// <summary>
        /// Atomically replaces every table carried by a daily business snapshot.
        /// No caller can observe a mixture of old and new table generations.
        /// </summary>
        public static void ReplaceBusinessSnapshot(
            IReadOnlyDictionary<string, JArray> tables,
            IReadOnlyDictionary<string, uint> versions)
        {
            if (tables == null) throw new ArgumentNullException(nameof(tables));
            if (versions == null) throw new ArgumentNullException(nameof(versions));
            foreach (string table in DailySyncTables)
            {
                if (!tables.ContainsKey(table) &&
                    !string.Equals(table, "system_settings", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"Snapshot table is missing: {table}");
            }

            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                JArray snapshotUsers = EnsureSystemAdministrator(
                    tables["users"] ?? new JArray(), ReadUsers(conn));
                using var tx = conn.BeginTransaction();
                foreach (string table in DailySyncTables)
                {
                    JArray array = string.Equals(table, "users",
                        StringComparison.OrdinalIgnoreCase)
                        ? snapshotUsers
                        : tables.TryGetValue(table, out JArray? value)
                            ? value ?? new JArray()
                            : new JArray();
                    switch (table)
                    {
                        case "users": WriteUsers(conn, tx, array); break;
                        case "classes": WriteClasses(conn, tx, array); break;
                        case "permissions": WritePermissions(conn, tx, array); break;
                        case "role_permissions": WriteRolePermissions(conn, tx, array); break;
                        case "devices": WriteDevices(conn, tx, array); break;
                        case "system_settings": WriteSystemSettings(conn, tx, array); break;
                    }

                    using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
INSERT INTO table_meta(table_name, version, updated_at) VALUES($t,$v,$u)
ON CONFLICT(table_name) DO UPDATE SET version=$v, updated_at=$u;";
                    cmd.Parameters.AddWithValue("$t", table);
                    cmd.Parameters.AddWithValue("$v",
                        (long)(versions.TryGetValue(table, out uint version) ? version : 0));
                    cmd.Parameters.AddWithValue("$u", DateTime.Now.ToString("o"));
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        private static JArray EnsureSystemAdministrator(
            JArray incomingUsers, JArray existingUsers)
        {
            JObject? incomingAdministrator = incomingUsers.OfType<JObject>()
                .FirstOrDefault(row => SystemAdministratorPolicy.IsReservedId(
                                           row.Value<string>("user_id")) ||
                                       SystemAdministratorPolicy.IsReservedId(
                                           row.Value<string>("user_code")));
            JObject? existingAdministrator = existingUsers.OfType<JObject>()
                .FirstOrDefault(row => SystemAdministratorPolicy.IsReservedId(
                                           row.Value<string>("user_id")) ||
                                       SystemAdministratorPolicy.IsReservedId(
                                           row.Value<string>("user_code")));

            User administrator = (incomingAdministrator ?? existingAdministrator)
                ?.ToObject<User>() ?? SystemAdministratorPolicy.CreateDefault();
            SystemAdministratorPolicy.Normalize(administrator);

            var normalized = new JArray(incomingUsers.OfType<JObject>()
                .Where(row => !SystemAdministratorPolicy.IsReservedId(
                                  row.Value<string>("user_id")) &&
                              !SystemAdministratorPolicy.IsReservedId(
                                  row.Value<string>("user_code")))
                .Select(row => row.DeepClone()));
            normalized.Add(JObject.FromObject(administrator));
            return normalized;
        }

        /// <summary>从旧 JSON 缓存迁移（仅当业务库为空时）。</summary>
        public static void MigrateFromLocalCacheIfEmpty()
        {
            lock (Sync)
            {
                Initialize();
                if (HasAnyBusinessData()) return;
                foreach (string table in BusinessTables)
                {
                    try
                    {
                        var arr = LocalCacheService.ReadTable(table);
                        if (arr == null || arr.Count == 0) continue;
                        uint v = LocalCacheService.ReadTableVersion(table);
                        ReplaceTable(table, arr, v);
                    }
                    catch
                    {
                        // 迁移失败不阻塞启动
                    }
                }
            }
        }

        /// <summary>从旧 fp_templates.json + .bin 迁移指纹到 business.db（仅当表为空）。</summary>
        public static void MigrateFingerprintsFromLocalCacheIfEmpty()
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = "SELECT COUNT(1) FROM fingerprints";
                    long n = Convert.ToInt64(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                    if (n > 0) return;
                }

                try
                {
                    var metas = LocalCacheService.ReadAllFpTemplateMetas();
                    foreach (var meta in metas)
                    {
                        if (meta == null || meta.FingerprintId <= 0) continue;
                        byte[]? blob = LocalCacheService.ReadFpTemplateByFingerprintId(
                            meta.FingerprintId, meta.FingerIndex);
                        if (blob == null || blob.Length == 0)
                        {
                            if (!string.IsNullOrWhiteSpace(meta.UserId))
                                blob = LocalCacheService.ReadFpTemplate(meta.UserId, meta.FingerIndex);
                        }
                        UpsertFingerprintTemplateUnlocked(conn, meta, blob);
                    }
                }
                catch
                {
                    // ignore
                }
            }
        }

        // ===== 指纹模板（业务库内） =====

        public static List<FingerprintTemplate> ReadAllFpTemplateMetas()
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                return ReadAllFpMetasUnlocked(conn);
            }
        }

        public static FingerprintTemplate? ReadFpTemplateMeta(int fingerprintId)
        {
            if (fingerprintId <= 0) return null;
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT fingerprint_id,user_id,user_name,finger_index,finger_name,
quality,enabled,enroll_time,template_size,source_device,backup_status,note FROM fingerprints
WHERE fingerprint_id=$id LIMIT 1";
                cmd.Parameters.AddWithValue("$id", fingerprintId);
                using var reader = cmd.ExecuteReader();
                return reader.Read() ? MapFpMeta(reader) : null;
            }
        }

        public static void WriteFpTemplateMeta(FingerprintTemplate meta)
        {
            if (meta == null || meta.FingerprintId <= 0) return;
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                byte[]? existingBlob = ReadFpBlobUnlocked(conn, meta.FingerprintId);
                UpsertFingerprintTemplateUnlocked(conn, meta, existingBlob);
                BumpTableVersion(conn, "fingerprints");
            }
        }

        public static void SaveFpTemplateWithMeta(int fingerprintId, string? userId, int fingerIndex,
            byte[] template, string sourceDevice)
        {
            if (fingerprintId <= 0 || template == null || template.Length == 0) return;
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                var existing = ReadAllFpMetasUnlocked(conn)
                    .FirstOrDefault(m => m.FingerprintId == fingerprintId);
                var meta = new FingerprintTemplate
                {
                    FingerprintId = fingerprintId,
                    UserId = userId,
                    UserName = existing?.UserName,
                    FingerIndex = fingerIndex <= 0 ? 1 : fingerIndex,
                    FingerName = existing?.FingerName ?? "",
                    Quality = existing?.Quality ?? 0,
                    Enabled = existing?.Enabled ?? true,
                    EnrollTime = DateTime.Now,
                    TemplateSize = template.Length,
                    SourceDevice = sourceDevice ?? "",
                    BackupStatus = existing?.BackupStatus ?? "local",
                    Note = existing?.Note
                };
                UpsertFingerprintTemplateUnlocked(conn, meta, template);
                BumpTableVersion(conn, "fingerprints");
            }
        }

        public static bool BindFpTemplateToUser(int fingerprintId, string userId, string? userName)
        {
            if (fingerprintId <= 0 || string.IsNullOrWhiteSpace(userId)) return false;
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                var meta = ReadAllFpMetasUnlocked(conn)
                    .FirstOrDefault(m => m.FingerprintId == fingerprintId);
                if (meta == null) return false;
                meta.UserId = userId;
                meta.UserName = userName;
                byte[]? blob = ReadFpBlobUnlocked(conn, fingerprintId);
                UpsertFingerprintTemplateUnlocked(conn, meta, blob);
                BumpTableVersion(conn, "fingerprints");
                return true;
            }
        }

        public static bool UpdateFpTemplateBackupStatus(int fingerprintId, string backupStatus)
        {
            if (fingerprintId <= 0) return false;
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                var meta = ReadAllFpMetasUnlocked(conn)
                    .FirstOrDefault(m => m.FingerprintId == fingerprintId);
                if (meta == null) return false;
                meta.BackupStatus = backupStatus;
                byte[]? blob = ReadFpBlobUnlocked(conn, fingerprintId);
                UpsertFingerprintTemplateUnlocked(conn, meta, blob);
                BumpTableVersion(conn, "fingerprints");
                return true;
            }
        }

        public static byte[]? ReadFpTemplateBytes(int fingerprintId, int fingerIndex = 1)
        {
            if (fingerprintId <= 0) return null;
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                return ReadFpBlobUnlocked(conn, fingerprintId);
            }
        }

        public static bool DeleteFpTemplateByFingerprintId(int fingerprintId)
        {
            if (fingerprintId <= 0) return false;
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM fingerprints WHERE fingerprint_id=$id";
                cmd.Parameters.AddWithValue("$id", fingerprintId);
                bool deleted = cmd.ExecuteNonQuery() > 0;
                if (deleted) BumpTableVersion(conn, "fingerprints");
                return deleted;
            }
        }

        public static List<(FingerprintTemplate Meta, byte[] Bytes)> ListFpTemplatesWithBytes()
        {
            var result = new List<(FingerprintTemplate, byte[])>();
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT fingerprint_id,user_id,user_name,finger_index,finger_name,
quality,enabled,enroll_time,template_size,source_device,backup_status,note,template_blob
FROM fingerprints";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var meta = MapFpMeta(r);
                    byte[]? blob = r.IsDBNull(12) ? null : (byte[])r.GetValue(12);
                    if (blob == null || blob.Length == 0) continue;
                    result.Add((meta, blob));
                }
            }
            return result;
        }

        private static List<FingerprintTemplate> ReadAllFpMetasUnlocked(SqliteConnection conn)
        {
            var list = new List<FingerprintTemplate>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT fingerprint_id,user_id,user_name,finger_index,finger_name,
quality,enabled,enroll_time,template_size,source_device,backup_status,note FROM fingerprints
ORDER BY enroll_time DESC, fingerprint_id DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(MapFpMeta(r));
            return list;
        }

        private static FingerprintTemplate MapFpMeta(SqliteDataReader r)
        {
            return new FingerprintTemplate
            {
                FingerprintId = r.GetInt32(0),
                UserId = r.IsDBNull(1) ? null : r.GetString(1),
                UserName = r.IsDBNull(2) ? null : r.GetString(2),
                FingerIndex = r.IsDBNull(3) ? 1 : r.GetInt32(3),
                FingerName = r.IsDBNull(4) ? "" : r.GetString(4),
                Quality = r.IsDBNull(5) ? 0 : r.GetInt32(5),
                Enabled = r.IsDBNull(6) || r.GetInt64(6) != 0,
                EnrollTime = ParseTime(r.IsDBNull(7) ? null : r.GetString(7)) ?? DateTime.MinValue,
                TemplateSize = r.IsDBNull(8) ? 0 : r.GetInt32(8),
                SourceDevice = r.IsDBNull(9) ? "" : r.GetString(9),
                BackupStatus = r.IsDBNull(10) ? "local" : r.GetString(10),
                Note = r.IsDBNull(11) ? null : r.GetString(11)
            };
        }

        private static byte[]? ReadFpBlobUnlocked(
            SqliteConnection conn, int fingerprintId, SqliteTransaction? transaction = null)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT template_blob FROM fingerprints WHERE fingerprint_id=$id";
            cmd.Parameters.AddWithValue("$id", fingerprintId);
            var o = cmd.ExecuteScalar();
            if (o == null || o is DBNull) return null;
            return o as byte[];
        }

        private static void UpsertFingerprintTemplateUnlocked(
            SqliteConnection conn, FingerprintTemplate meta, byte[]? blob,
            SqliteTransaction? transaction = null)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = @"
INSERT INTO fingerprints(
  fingerprint_id,user_id,user_name,finger_index,finger_name,quality,enabled,enroll_time,
  template_size,source_device,backup_status,note,template_blob)
VALUES($id,$uid,$un,$fi,$fn,$quality,$enabled,$et,$sz,$sd,$bs,$note,$blob)
ON CONFLICT(fingerprint_id) DO UPDATE SET
  user_id=excluded.user_id,
  user_name=excluded.user_name,
  finger_index=excluded.finger_index,
  finger_name=excluded.finger_name,
  quality=excluded.quality,
  enabled=excluded.enabled,
  enroll_time=excluded.enroll_time,
  template_size=excluded.template_size,
  source_device=excluded.source_device,
  backup_status=excluded.backup_status,
  note=excluded.note,
  template_blob=COALESCE(excluded.template_blob, fingerprints.template_blob);";
            cmd.Parameters.AddWithValue("$id", meta.FingerprintId);
            cmd.Parameters.AddWithValue("$uid", (object?)meta.UserId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$un", (object?)meta.UserName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$fi", meta.FingerIndex <= 0 ? 1 : meta.FingerIndex);
            cmd.Parameters.AddWithValue("$fn", meta.FingerName ?? "");
            cmd.Parameters.AddWithValue("$quality", Math.Max(0, meta.Quality));
            cmd.Parameters.AddWithValue("$enabled", meta.Enabled ? 1 : 0);
            cmd.Parameters.AddWithValue("$et",
                meta.EnrollTime == default ? DateTime.Now.ToString("o") : meta.EnrollTime.ToString("o"));
            int size = meta.TemplateSize;
            if (blob != null && blob.Length > 0) size = blob.Length;
            cmd.Parameters.AddWithValue("$sz", size);
            cmd.Parameters.AddWithValue("$sd", meta.SourceDevice ?? "");
            cmd.Parameters.AddWithValue("$bs", meta.BackupStatus ?? "local");
            cmd.Parameters.AddWithValue("$note", (object?)meta.Note ?? DBNull.Value);
            if (blob != null && blob.Length > 0)
                cmd.Parameters.AddWithValue("$blob", blob);
            else
                cmd.Parameters.AddWithValue("$blob", DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        private static void WriteFingerprintMetadata(
            SqliteConnection conn, SqliteTransaction transaction, JArray array)
        {
            Dictionary<int, FingerprintTemplate> existingById = ReadAllFpMetasUnlocked(conn)
                .ToDictionary(item => item.FingerprintId);
            foreach (JObject token in array.OfType<JObject>())
            {
                FingerprintTemplate? meta = token.ToObject<FingerprintTemplate>();
                if (meta == null || meta.FingerprintId <= 0) continue;
                byte[]? blob = ReadFpBlobUnlocked(conn, meta.FingerprintId, transaction);
                existingById.TryGetValue(meta.FingerprintId, out FingerprintTemplate? existing);
                meta.BackupStatus = existing?.BackupStatus ?? "sd";
                UpsertFingerprintTemplateUnlocked(conn, meta, blob, transaction);
            }
        }

        private static JArray ReadFingerprintMetadata(SqliteConnection conn)
        {
            var array = new JArray();
            foreach (FingerprintTemplate meta in ReadAllFpMetasUnlocked(conn))
            {
                array.Add(new JObject
                {
                    ["fingerprint_id"] = meta.FingerprintId,
                    ["user_id"] = meta.UserId,
                    ["user_name"] = meta.UserName,
                    ["finger_index"] = meta.FingerIndex,
                    ["finger_name"] = meta.FingerName,
                    ["quality"] = meta.Quality,
                    ["enabled"] = meta.Enabled,
                    ["enroll_time"] = meta.EnrollTime == default ? null : meta.EnrollTime.ToString("o"),
                    ["template_size"] = meta.TemplateSize,
                    ["source_device"] = meta.SourceDevice,
                    ["note"] = meta.Note
                });
            }
            return array;
        }

        private static void BumpTableVersion(SqliteConnection conn, string table)
        {
            using var command = conn.CreateCommand();
            command.CommandText = @"
INSERT INTO table_meta(table_name,version,updated_at) VALUES($table,1,$time)
ON CONFLICT(table_name) DO UPDATE SET version=version+1,updated_at=$time;";
            command.Parameters.AddWithValue("$table", table);
            command.Parameters.AddWithValue("$time", DateTime.Now.ToString("o"));
            command.ExecuteNonQuery();
        }

        private static DateTime? ParseTime(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var dt)) return dt;
            if (DateTime.TryParse(s, out dt)) return dt;
            return null;
        }

        // ===== readers =====

        private static JArray ReadUsers(SqliteConnection conn)
        {
            var arr = new JArray();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT user_id,user_code,name,gender,role,class_id,class_ids_json,assigned_device_ids_json,
cabinet_assignments_json,fingerprint_id,password_salt,password_hash,enabled,create_time,update_time FROM users";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var user = new JObject
                {
                    ["user_id"] = r.GetString(0),
                    ["user_code"] = r.IsDBNull(1) ? r.GetString(0) : r.GetString(1),
                    ["name"] = r.IsDBNull(2) ? "" : r.GetString(2),
                    ["gender"] = r.IsDBNull(3) ? "" : r.GetString(3),
                    ["role"] = r.IsDBNull(4) ? "" : r.GetString(4),
                    ["class_id"] = r.IsDBNull(5) ? null : r.GetString(5),
                    ["class_ids"] = null,
                    ["assigned_device_ids"] = null,
                    ["cabinet_assignments"] = null,
                    ["fingerprint_id"] = r.IsDBNull(9) ? null : r.GetInt32(9),
                    ["password_salt"] = r.IsDBNull(10) ? "" : r.GetString(10),
                    ["password_hash"] = r.IsDBNull(11) ? "" : r.GetString(11),
                    ["enabled"] = !r.IsDBNull(12) && r.GetInt64(12) != 0,
                    ["create_time"] = r.IsDBNull(13) ? null : r.GetString(13),
                    ["update_time"] = r.IsDBNull(14) ? null : r.GetString(14)
                };
                if (!r.IsDBNull(6))
                {
                    try { user["class_ids"] = JArray.Parse(r.GetString(6)); }
                    catch { user["class_ids"] = new JArray(); }
                }
                if (!r.IsDBNull(7))
                {
                    try { user["assigned_device_ids"] = JArray.Parse(r.GetString(7)); }
                    catch { user["assigned_device_ids"] = new JArray(); }
                }
                if (!r.IsDBNull(8))
                {
                    try { user["cabinet_assignments"] = JArray.Parse(r.GetString(8)); }
                    catch { user["cabinet_assignments"] = new JArray(); }
                }
                arr.Add(user);
            }
            return arr;
        }

        private static JArray ReadClasses(SqliteConnection conn)
        {
            var arr = new JArray();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT class_id,name,enabled,create_time FROM classes";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                arr.Add(new JObject
                {
                    ["class_id"] = r.GetString(0),
                    ["name"] = r.IsDBNull(1) ? "" : r.GetString(1),
                    ["enabled"] = !r.IsDBNull(2) && r.GetInt64(2) != 0,
                    ["create_time"] = r.IsDBNull(3) ? null : r.GetString(3)
                });
            }
            return arr;
        }

        private static JArray ReadPermissions(SqliteConnection conn)
        {
            var arr = new JArray();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,user_id,lock_id,has_access,update_time FROM permissions";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                arr.Add(new JObject
                {
                    ["id"] = r.GetInt64(0),
                    ["user_id"] = r.IsDBNull(1) ? "" : r.GetString(1),
                    ["lock_id"] = r.IsDBNull(2) ? 0 : r.GetInt32(2),
                    ["has_access"] = !r.IsDBNull(3) && r.GetInt64(3) != 0,
                    ["update_time"] = r.IsDBNull(4) ? null : r.GetString(4)
                });
            }
            return arr;
        }

        private static JArray ReadRolePermissions(SqliteConnection conn)
        {
            var arr = new JArray();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT role,lock_0,lock_1,lock_2,lock_3,update_time FROM role_permissions";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                arr.Add(new JObject
                {
                    ["role"] = r.GetString(0),
                    ["lock_0"] = !r.IsDBNull(1) && r.GetInt64(1) != 0,
                    ["lock_1"] = !r.IsDBNull(2) && r.GetInt64(2) != 0,
                    ["lock_2"] = !r.IsDBNull(3) && r.GetInt64(3) != 0,
                    ["lock_3"] = !r.IsDBNull(4) && r.GetInt64(4) != 0,
                    ["update_time"] = r.IsDBNull(5) ? null : r.GetString(5)
                });
            }
            return arr;
        }

        private static JArray ReadDevices(SqliteConnection conn)
        {
            var arr = new JArray();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT device_id,device_name,device_number,ip_address,online,register_time,last_online_time,
last_seen,offline_time,mesh_mac,is_root,firmware_version,hardware_version,status_json FROM devices";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var obj = new JObject
                {
                    ["device_id"] = r.GetString(0),
                    ["device_name"] = r.IsDBNull(1) ? "" : r.GetString(1),
                    ["device_number"] = r.IsDBNull(2) ? "" : r.GetString(2),
                    ["ip_address"] = r.IsDBNull(3) ? "" : r.GetString(3),
                    ["online"] = !r.IsDBNull(4) && r.GetInt64(4) != 0,
                    ["register_time"] = r.IsDBNull(5)
                        ? DateTime.MinValue.ToString("o")
                        : r.GetString(5),
                    ["last_online_time"] = r.IsDBNull(6) ? null : r.GetString(6),
                    ["last_seen"] = r.IsDBNull(7) ? 0 : r.GetInt64(7),
                    ["offline_time"] = r.IsDBNull(8) ? 0 : r.GetInt64(8),
                    ["mesh_mac"] = r.IsDBNull(9) ? "" : r.GetString(9),
                    ["is_root"] = !r.IsDBNull(10) && r.GetInt64(10) != 0,
                    ["firmware_version"] = r.IsDBNull(11) ? "" : r.GetString(11),
                    ["hardware_version"] = r.IsDBNull(12) ? "" : r.GetString(12)
                };
                if (!r.IsDBNull(13))
                {
                    try
                    {
                        var status = JToken.Parse(r.GetString(13));
                        obj["status"] = status;
                    }
                    catch
                    {
                        obj["status"] = new JObject();
                    }
                }
                else
                {
                    obj["status"] = new JObject();
                }
                arr.Add(obj);
            }
            return arr;
        }

        // ===== writers =====

        private static void WriteUsers(SqliteConnection conn, SqliteTransaction tx, JArray array)
        {
            Exec(conn, tx, "DELETE FROM users");
            foreach (var token in array.OfType<JObject>())
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO users(user_id,user_code,name,gender,role,class_id,class_ids_json,assigned_device_ids_json,
cabinet_assignments_json,fingerprint_id,password_salt,password_hash,enabled,create_time,update_time)
VALUES($id,$code,$n,$g,$r,$c,$classes,$d,$bindings,$f,$s,$h,$e,$ct,$ut)";
                cmd.Parameters.AddWithValue("$id", token.Value<string>("user_id") ?? "");
                cmd.Parameters.AddWithValue("$code", token.Value<string>("user_code") ??
                    token.Value<string>("user_id") ?? "");
                cmd.Parameters.AddWithValue("$n", token.Value<string>("name") ?? "");
                cmd.Parameters.AddWithValue("$g", token.Value<string>("gender") ?? "");
                cmd.Parameters.AddWithValue("$r", token.Value<string>("role") ?? "");
                cmd.Parameters.AddWithValue("$c", (object?)token.Value<string>("class_id") ?? DBNull.Value);
                JToken? classIds = token["class_ids"];
                cmd.Parameters.AddWithValue("$classes", classIds is JArray
                    ? classIds.ToString(Formatting.None)
                    : DBNull.Value);
                JToken? assignments = token["assigned_device_ids"];
                cmd.Parameters.AddWithValue("$d", assignments is JArray
                    ? assignments.ToString(Formatting.None)
                    : DBNull.Value);
                JToken? bindings = token["cabinet_assignments"];
                cmd.Parameters.AddWithValue("$bindings", bindings is JArray
                    ? bindings.ToString(Formatting.None)
                    : DBNull.Value);
                int? fp = token.Value<int?>("fingerprint_id");
                cmd.Parameters.AddWithValue("$f", fp.HasValue ? fp.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("$s", token.Value<string>("password_salt") ?? "");
                cmd.Parameters.AddWithValue("$h", token.Value<string>("password_hash") ?? "");
                cmd.Parameters.AddWithValue("$e", (token.Value<bool?>("enabled") ?? true) ? 1 : 0);
                cmd.Parameters.AddWithValue("$ct", FormatTime(token["create_time"]));
                cmd.Parameters.AddWithValue("$ut", (object?)FormatTimeOrNull(token["update_time"]) ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        private static void WriteClasses(SqliteConnection conn, SqliteTransaction tx, JArray array)
        {
            Exec(conn, tx, "DELETE FROM classes");
            foreach (var token in array.OfType<JObject>())
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "INSERT INTO classes(class_id,name,enabled,create_time) VALUES($id,$n,$e,$ct)";
                cmd.Parameters.AddWithValue("$id", token.Value<string>("class_id") ?? "");
                cmd.Parameters.AddWithValue("$n", token.Value<string>("name") ?? "");
                cmd.Parameters.AddWithValue("$e", (token.Value<bool?>("enabled") ?? true) ? 1 : 0);
                cmd.Parameters.AddWithValue("$ct", FormatTime(token["create_time"]));
                cmd.ExecuteNonQuery();
            }
        }

        private static void WritePermissions(SqliteConnection conn, SqliteTransaction tx, JArray array)
        {
            Exec(conn, tx, "DELETE FROM permissions");
            foreach (var token in array.OfType<JObject>())
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                long id = token.Value<long?>("id") ?? 0;
                if (id > 0)
                {
                    cmd.CommandText =
                        "INSERT INTO permissions(id,user_id,lock_id,has_access,update_time) VALUES($id,$u,$l,$a,$t)";
                    cmd.Parameters.AddWithValue("$id", id);
                }
                else
                {
                    cmd.CommandText =
                        "INSERT INTO permissions(user_id,lock_id,has_access,update_time) VALUES($u,$l,$a,$t)";
                }
                cmd.Parameters.AddWithValue("$u", token.Value<string>("user_id") ?? "");
                cmd.Parameters.AddWithValue("$l", token.Value<int?>("lock_id") ?? 0);
                cmd.Parameters.AddWithValue("$a", (token.Value<bool?>("has_access") ?? false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$t", FormatTime(token["update_time"]));
                cmd.ExecuteNonQuery();
            }
        }

        private static void WriteRolePermissions(SqliteConnection conn, SqliteTransaction tx, JArray array)
        {
            Exec(conn, tx, "DELETE FROM role_permissions");
            foreach (var token in array.OfType<JObject>())
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO role_permissions(role,lock_0,lock_1,lock_2,lock_3,update_time)
VALUES($r,$0,$1,$2,$3,$t)";
                cmd.Parameters.AddWithValue("$r", token.Value<string>("role") ?? "");
                cmd.Parameters.AddWithValue("$0", (token.Value<bool?>("lock_0") ?? false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$1", (token.Value<bool?>("lock_1") ?? false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$2", (token.Value<bool?>("lock_2") ?? false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$3", (token.Value<bool?>("lock_3") ?? false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$t", FormatTime(token["update_time"]));
                cmd.ExecuteNonQuery();
            }
        }

        private static void WriteDevices(SqliteConnection conn, SqliteTransaction tx, JArray array)
        {
            Exec(conn, tx, "DELETE FROM devices");
            var usedNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var token in array.OfType<JObject>())
            {
                using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = @"INSERT INTO devices(device_id,device_name,device_number,ip_address,online,register_time,last_online_time,
last_seen,offline_time,mesh_mac,is_root,firmware_version,hardware_version,status_json)
VALUES($id,$n,$number,$ip,$on,$rt,$lo,$ls,$of,$mac,$root,$fw,$hw,$st)";
                cmd.Parameters.AddWithValue("$id", token.Value<string>("device_id") ?? "");
                cmd.Parameters.AddWithValue("$n", token.Value<string>("device_name") ?? "");
                string number = token.Value<string>("device_number")?.Trim() ?? "";
                if (!string.IsNullOrEmpty(number) && !usedNumbers.Add(number)) number = "";
                cmd.Parameters.AddWithValue("$number", string.IsNullOrEmpty(number) ? DBNull.Value : number);
                cmd.Parameters.AddWithValue("$ip", token.Value<string>("ip_address") ?? "");
                cmd.Parameters.AddWithValue("$on", (token.Value<bool?>("online") ?? false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$rt", (object?)FormatTimeOrNull(token["register_time"]) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$lo", (object?)FormatTimeOrNull(token["last_online_time"]) ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$ls", token.Value<long?>("last_seen") ?? 0);
                cmd.Parameters.AddWithValue("$of", token.Value<long?>("offline_time") ?? 0);
                cmd.Parameters.AddWithValue("$mac", token.Value<string>("mesh_mac") ?? "");
                cmd.Parameters.AddWithValue("$root", (token.Value<bool?>("is_root") ?? false) ? 1 : 0);
                cmd.Parameters.AddWithValue("$fw", token.Value<string>("firmware_version") ?? "");
                cmd.Parameters.AddWithValue("$hw", token.Value<string>("hardware_version") ?? "");
                string statusJson = token["status"]?.ToString(Formatting.None) ?? "{}";
                cmd.Parameters.AddWithValue("$st", statusJson);
                cmd.ExecuteNonQuery();
            }
        }

        private static void Exec(SqliteConnection conn, SqliteTransaction tx, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }

        private static string FormatTime(JToken? token)
        {
            string? s = FormatTimeOrNull(token);
            return s ?? DateTime.Now.ToString("o");
        }

        private static string? FormatTimeOrNull(JToken? token)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            if (token.Type == JTokenType.Date)
                return token.Value<DateTime>().ToString("o");
            string? s = token.ToString();
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, out var dt)) return dt.ToString("o");
            return s;
        }
    }
}
