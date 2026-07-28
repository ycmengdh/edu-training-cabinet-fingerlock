using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;

namespace FingerprintLockManager
{
    /// <summary>
    /// 本机日志库 logs.db：operation_logs（操作审计）+ unlock_logs（开锁日志）。
    /// </summary>
    public static class LogDatabase
    {
        private static readonly object Sync = new();
        private static bool _initialized;
        private const int MaxOperationLogs = 20000;
        private const int MaxUnlockLogs = 50000;

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

        public static SqliteConnection Open()
        {
            var conn = new SqliteConnection($"Data Source={SqlitePaths.LogsDbPath}");
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL;";
                pragma.ExecuteNonQuery();
            }
            return conn;
        }

        private static void EnsureSchema(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS operation_logs (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  time TEXT NOT NULL,
  operator_id TEXT,
  operator_name TEXT,
  module TEXT,
  action TEXT,
  target TEXT,
  result TEXT,
  detail TEXT
);
CREATE INDEX IF NOT EXISTS ix_op_time ON operation_logs(time);

CREATE TABLE IF NOT EXISTS unlock_logs (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  external_id INTEGER,
  device_id TEXT,
  user_id TEXT,
  lock_id INTEGER,
  action TEXT,
  result TEXT,
  reason TEXT,
  create_time TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_unlock_time ON unlock_logs(create_time);
CREATE INDEX IF NOT EXISTS ix_unlock_device ON unlock_logs(device_id);
CREATE INDEX IF NOT EXISTS ix_unlock_user ON unlock_logs(user_id);
CREATE UNIQUE INDEX IF NOT EXISTS ux_unlock_dedupe
  ON unlock_logs(device_id, create_time, action, lock_id, result, IFNULL(user_id,''));
";
            cmd.ExecuteNonQuery();
        }

        /// <summary>从旧 JSON 迁移操作日志（仅当表为空）。</summary>
        public static void MigrateOperationLogsFromJsonIfEmpty()
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = "SELECT COUNT(1) FROM operation_logs";
                    long n = Convert.ToInt64(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                    if (n > 0) return;
                }
                try
                {
                    string path = Path.Combine(LocalCacheService.GetCacheDirectory(), "operation_logs.json");
                    if (!File.Exists(path)) return;
                    string json = File.ReadAllText(path);
                    if (string.IsNullOrWhiteSpace(json)) return;
                    var arr = JToken.Parse(json) as JArray;
                    if (arr == null) return;
                    var list = arr.ToObject<List<OperationLogEntry>>() ?? new List<OperationLogEntry>();
                    foreach (var e in list)
                        AppendOperationUnlocked(conn, e);
                    TrimOperationUnlocked(conn);
                }
                catch
                {
                    // ignore
                }
            }
        }

        public static void MigrateUnlockLogsFromCacheIfEmpty()
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                using (var countCmd = conn.CreateCommand())
                {
                    countCmd.CommandText = "SELECT COUNT(1) FROM unlock_logs";
                    long n = Convert.ToInt64(countCmd.ExecuteScalar(), CultureInfo.InvariantCulture);
                    if (n > 0) return;
                }
                try
                {
                    var logs = LocalCacheService.ReadLogs();
                    if (logs == null || logs.Count == 0) return;
                    foreach (var log in logs)
                        AppendUnlockUnlocked(conn, log);
                    TrimUnlockUnlocked(conn);
                }
                catch
                {
                    // ignore
                }
            }
        }

        // ===== operation logs =====

        public static void AppendOperation(OperationLogEntry entry)
        {
            if (entry == null) return;
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                AppendOperationUnlocked(conn, entry);
                TrimOperationUnlocked(conn);
            }
        }

        private static void AppendOperationUnlocked(SqliteConnection conn, OperationLogEntry entry)
        {
            if (entry.Time == default) entry.Time = DateTime.Now;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO operation_logs(time,operator_id,operator_name,module,action,target,result,detail)
VALUES($t,$oid,$on,$m,$a,$tg,$r,$d);
SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$t", entry.Time.ToString("o"));
            cmd.Parameters.AddWithValue("$oid", entry.OperatorId ?? "");
            cmd.Parameters.AddWithValue("$on", entry.OperatorName ?? "");
            cmd.Parameters.AddWithValue("$m", entry.Module ?? "");
            cmd.Parameters.AddWithValue("$a", entry.Action ?? "");
            cmd.Parameters.AddWithValue("$tg", entry.Target ?? "");
            cmd.Parameters.AddWithValue("$r", entry.Result ?? "info");
            cmd.Parameters.AddWithValue("$d", entry.Detail ?? "");
            entry.Id = Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        private static void TrimOperationUnlocked(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
DELETE FROM operation_logs WHERE id NOT IN (
  SELECT id FROM operation_logs ORDER BY time DESC, id DESC LIMIT $max
);";
            cmd.Parameters.AddWithValue("$max", MaxOperationLogs);
            cmd.ExecuteNonQuery();
        }

        public static List<OperationLogEntry> QueryOperations(
            string? keyword = null,
            DateTime? startTime = null,
            DateTime? endTime = null,
            int limit = 100,
            int offset = 0)
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                var all = ReadAllOperationsUnlocked(conn);
                var q = FilterOperations(all, keyword, startTime, endTime);
                if (offset > 0) q = q.Skip(offset);
                return q.Take(limit > 0 ? limit : 100).ToList();
            }
        }

        public static int CountOperations(string? keyword = null, DateTime? startTime = null, DateTime? endTime = null)
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                return FilterOperations(ReadAllOperationsUnlocked(conn), keyword, startTime, endTime).Count();
            }
        }

        public static List<OperationLogEntry> QueryAllOperations(
            string? keyword = null,
            DateTime? startTime = null,
            DateTime? endTime = null,
            int max = 50000)
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                return FilterOperations(ReadAllOperationsUnlocked(conn), keyword, startTime, endTime)
                    .Take(max > 0 ? max : 50000).ToList();
            }
        }

        private static List<OperationLogEntry> ReadAllOperationsUnlocked(SqliteConnection conn)
        {
            var list = new List<OperationLogEntry>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT id,time,operator_id,operator_name,module,action,target,result,detail FROM operation_logs ORDER BY time DESC, id DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new OperationLogEntry
                {
                    Id = r.GetInt64(0),
                    Time = ParseTime(r.IsDBNull(1) ? null : r.GetString(1)) ?? DateTime.MinValue,
                    OperatorId = r.IsDBNull(2) ? "" : r.GetString(2),
                    OperatorName = r.IsDBNull(3) ? "" : r.GetString(3),
                    Module = r.IsDBNull(4) ? "" : r.GetString(4),
                    Action = r.IsDBNull(5) ? "" : r.GetString(5),
                    Target = r.IsDBNull(6) ? "" : r.GetString(6),
                    Result = r.IsDBNull(7) ? "info" : r.GetString(7),
                    Detail = r.IsDBNull(8) ? "" : r.GetString(8)
                });
            }
            return list;
        }

        private static IEnumerable<OperationLogEntry> FilterOperations(
            IEnumerable<OperationLogEntry> query, string? keyword, DateTime? startTime, DateTime? endTime)
        {
            if (startTime.HasValue) query = query.Where(x => x.Time >= startTime.Value);
            if (endTime.HasValue) query = query.Where(x => x.Time <= endTime.Value);
            if (!string.IsNullOrWhiteSpace(keyword))
            {
                string k = keyword.Trim();
                query = query.Where(x =>
                    Contains(x.OperatorId, k) || Contains(x.OperatorName, k) ||
                    Contains(x.Module, k) || Contains(x.Action, k) ||
                    Contains(x.Target, k) || Contains(x.Result, k) || Contains(x.Detail, k));
            }
            return query;
        }

        // ===== unlock logs =====

        public static void AppendUnlock(LogEntry log)
        {
            if (log == null) return;
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                AppendUnlockUnlocked(conn, log);
                TrimUnlockUnlocked(conn);
            }
        }

        public static void AppendUnlockMany(IEnumerable<LogEntry> logs)
        {
            if (logs == null) return;
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                using var tx = conn.BeginTransaction();
                foreach (var log in logs)
                {
                    if (log == null) continue;
                    AppendUnlockUnlocked(conn, log, tx);
                }
                tx.Commit();
                TrimUnlockUnlocked(conn);
            }
        }

        /// <summary>启动从 SD 合并开锁日志（不整表清空，按去重索引插入）。</summary>
        public static void MergeUnlockFromArray(JArray array)
        {
            if (array == null) return;
            var logs = new List<LogEntry>();
            foreach (var token in array.OfType<JObject>())
            {
                logs.Add(new LogEntry
                {
                    Id = token.Value<long?>("id") ?? token.Value<long?>("log_seq") ?? 0,
                    DeviceId = token.Value<string>("device_id") ?? "",
                    UserId = token.Value<string>("user_id") ?? "",
                    LockId = token.Value<int?>("lock_id") ?? 0,
                    Action = token.Value<string>("action") ?? "",
                    Result = token.Value<string>("result") ?? "",
                    Reason = token.Value<string>("reason") ?? "",
                    CreateTime = ReadLogTime(token)
                });
            }
            AppendUnlockMany(logs);
        }

        private static void AppendUnlockUnlocked(SqliteConnection conn, LogEntry log, SqliteTransaction? tx = null)
        {
            if (log.CreateTime == default) log.CreateTime = DateTime.Now;
            using var cmd = conn.CreateCommand();
            if (tx != null) cmd.Transaction = tx;
            cmd.CommandText = @"
INSERT OR IGNORE INTO unlock_logs(external_id,device_id,user_id,lock_id,action,result,reason,create_time)
VALUES($eid,$d,$u,$l,$a,$r,$reason,$t);";
            cmd.Parameters.AddWithValue("$eid", log.Id > 0 ? log.Id : DBNull.Value);
            cmd.Parameters.AddWithValue("$d", log.DeviceId ?? "");
            cmd.Parameters.AddWithValue("$u", log.UserId ?? "");
            cmd.Parameters.AddWithValue("$l", log.LockId);
            cmd.Parameters.AddWithValue("$a", log.Action ?? "");
            cmd.Parameters.AddWithValue("$r", log.Result ?? "");
            cmd.Parameters.AddWithValue("$reason", log.Reason ?? "");
            cmd.Parameters.AddWithValue("$t", log.CreateTime.ToString("o"));
            cmd.ExecuteNonQuery();
        }

        private static void TrimUnlockUnlocked(SqliteConnection conn)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
DELETE FROM unlock_logs WHERE id NOT IN (
  SELECT id FROM unlock_logs ORDER BY create_time DESC, id DESC LIMIT $max
);";
            cmd.Parameters.AddWithValue("$max", MaxUnlockLogs);
            cmd.ExecuteNonQuery();
        }

        public static List<LogEntry> QueryUnlock(
            string? deviceId = null, string? userId = null,
            DateTime? startTime = null, DateTime? endTime = null, string? result = null,
            int limit = 1000, int offset = 0)
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                var q = FilterUnlock(ReadAllUnlockUnlocked(conn), deviceId, userId, startTime, endTime, result);
                if (offset > 0) q = q.Skip(offset);
                return q.Take(limit > 0 ? limit : 1000).ToList();
            }
        }

        public static int CountUnlock(
            string? deviceId = null, string? userId = null,
            DateTime? startTime = null, DateTime? endTime = null, string? result = null)
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                return FilterUnlock(ReadAllUnlockUnlocked(conn), deviceId, userId, startTime, endTime, result).Count();
            }
        }

        public static List<LogEntry> ReadAllUnlock()
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                return ReadAllUnlockUnlocked(conn);
            }
        }

        public static void ClearUnlock()
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM unlock_logs";
                cmd.ExecuteNonQuery();
            }
        }

        public static long GetUnlockCount()
        {
            lock (Sync)
            {
                Initialize();
                using var conn = Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(1) FROM unlock_logs";
                return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
            }
        }

        private static List<LogEntry> ReadAllUnlockUnlocked(SqliteConnection conn)
        {
            var list = new List<LogEntry>();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT id,external_id,device_id,user_id,lock_id,action,result,reason,create_time
FROM unlock_logs ORDER BY create_time DESC, id DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                long rowId = r.GetInt64(0);
                long external = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                list.Add(new LogEntry
                {
                    Id = external > 0 ? external : rowId,
                    DeviceId = r.IsDBNull(2) ? "" : r.GetString(2),
                    UserId = r.IsDBNull(3) ? "" : r.GetString(3),
                    LockId = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    Action = r.IsDBNull(5) ? "" : r.GetString(5),
                    Result = r.IsDBNull(6) ? "" : r.GetString(6),
                    Reason = r.IsDBNull(7) ? "" : r.GetString(7),
                    CreateTime = ParseTime(r.IsDBNull(8) ? null : r.GetString(8)) ?? DateTime.MinValue
                });
            }
            return list;
        }

        private static IEnumerable<LogEntry> FilterUnlock(
            IEnumerable<LogEntry> query, string? deviceId, string? userId,
            DateTime? startTime, DateTime? endTime, string? result)
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
                query = query.Where(l => l.DeviceId == deviceId);
            if (!string.IsNullOrWhiteSpace(userId))
                query = query.Where(l => l.UserId == userId);
            if (startTime.HasValue) query = query.Where(l => l.CreateTime >= startTime.Value);
            if (endTime.HasValue) query = query.Where(l => l.CreateTime <= endTime.Value);
            if (!string.IsNullOrWhiteSpace(result))
            {
                query = query.Where(l =>
                    string.Equals(l.Result, result, StringComparison.OrdinalIgnoreCase));
            }
            return query;
        }

        private static DateTime ReadLogTime(JObject token)
        {
            if (DateTime.TryParse(token.Value<string>("create_time"), out var date)) return date;
            long unix = token.Value<long?>("time") ?? token.Value<long?>("timestamp") ?? 0;
            return unix > 0
                ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime
                : DateTime.MinValue;
        }

        private static DateTime? ParseTime(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (DateTime.TryParse(s, null, DateTimeStyles.RoundtripKind, out var dt)) return dt;
            if (DateTime.TryParse(s, out dt)) return dt;
            return null;
        }

        private static bool Contains(string? value, string keyword) =>
            !string.IsNullOrEmpty(value) &&
            value.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }
}
