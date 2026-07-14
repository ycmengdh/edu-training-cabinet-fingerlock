using Microsoft.Data.Sqlite;

namespace FingerprintLockManager
{
    /// <summary>
    /// 上位机本地 SQLite 数据服务（需求 9/11）
    ///
    /// 仅存储三类辅助数据（业务主数据走根节点 SD 卡）：
    ///   logs             - 开锁/关锁操作日志（柜子发出，上位机在线则记录）
    ///   deploy_tasks     - 下发任务记录（老师广播/学生按需/删除用户/按班级删）
    ///   deploy_statuses  - 下发状态明细（每台柜子的接收状态，需求 7）
    ///   backup_records   - 备份记录（需求 11）
    ///
    /// 数据库文件：./Data/system.db（上位机数据目录）
    /// 线程安全：所有操作经 _lock 串行化，SQLite 使用单连接持久模式。
    /// </summary>
    public class LogDbService
    {
        /// <summary>全局单例</summary>
        public static LogDbService Current { get; } = new LogDbService();

        private readonly object _lock = new();
        private readonly string _connStr;
        private bool _initialized;

        private LogDbService()
        {
            string dbPath = Path.Combine(AppContext.BaseDirectory, "Data", "system.db");
            string dir = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            _connStr = $"Data Source={dbPath}";
        }

        /// <summary>初始化数据库（建表）。App 启动时调用一次。</summary>
        public void Init()
        {
            if (_initialized) return;
            lock (_lock)
            {
                if (_initialized) return;
                using var conn = new SqliteConnection(_connStr);
                conn.Open();

                // 开锁日志表
                conn.ExecuteNonQuery(@"
                    CREATE TABLE IF NOT EXISTS logs (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        device_id TEXT NOT NULL,
                        user_id TEXT,
                        lock_id INTEGER NOT NULL,
                        action TEXT NOT NULL,
                        result TEXT NOT NULL,
                        reason TEXT,
                        create_time TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_logs_time ON logs(create_time DESC);
                    CREATE INDEX IF NOT EXISTS idx_logs_device ON logs(device_id);
                ");

                // 下发任务表
                conn.ExecuteNonQuery(@"
                    CREATE TABLE IF NOT EXISTS deploy_tasks (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        task_type TEXT NOT NULL,
                        user_id TEXT,
                        device_id TEXT,
                        class_id TEXT,
                        payload TEXT,
                        operator_user_id TEXT,
                        status TEXT NOT NULL DEFAULT 'pending',
                        total_devices INTEGER NOT NULL DEFAULT 0,
                        acked_devices INTEGER NOT NULL DEFAULT 0,
                        create_time TEXT NOT NULL,
                        complete_time TEXT
                    );
                    CREATE INDEX IF NOT EXISTS idx_tasks_status ON deploy_tasks(status);
                    CREATE INDEX IF NOT EXISTS idx_tasks_time ON deploy_tasks(create_time DESC);
                ");

                // 下发状态明细表
                conn.ExecuteNonQuery(@"
                    CREATE TABLE IF NOT EXISTS deploy_statuses (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        task_id INTEGER NOT NULL,
                        device_id TEXT NOT NULL,
                        status TEXT NOT NULL DEFAULT 'pending',
                        error_message TEXT,
                        retry_count INTEGER NOT NULL DEFAULT 0,
                        last_retry_time TEXT,
                        ack_time TEXT,
                        FOREIGN KEY(task_id) REFERENCES deploy_tasks(id)
                    );
                    CREATE INDEX IF NOT EXISTS idx_statuses_task ON deploy_statuses(task_id);
                ");

                // 备份记录表
                conn.ExecuteNonQuery(@"
                    CREATE TABLE IF NOT EXISTS backup_records (
                        backup_id TEXT PRIMARY KEY,
                        trigger_action TEXT NOT NULL,
                        operator_user_id TEXT,
                        file_path TEXT NOT NULL,
                        file_size INTEGER NOT NULL DEFAULT 0,
                        tables TEXT,
                        global_version INTEGER NOT NULL DEFAULT 0,
                        create_time TEXT NOT NULL
                    );
                    CREATE INDEX IF NOT EXISTS idx_backups_time ON backup_records(create_time DESC);
                ");

                _initialized = true;
            }
        }

        // ====== 日志 ======

        /// <summary>追加一条开锁/关锁日志</summary>
        public void AddLog(LogEntry log)
        {
            if (log == null) return;
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO logs (device_id, user_id, lock_id, action, result, reason, create_time)
                    VALUES (@dev, @uid, @lid, @act, @res, @rsn, @ct)";
                cmd.Parameters.AddWithValue("@dev", log.DeviceId ?? "");
                cmd.Parameters.AddWithValue("@uid", (object?)log.UserId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@lid", log.LockId);
                cmd.Parameters.AddWithValue("@act", log.Action ?? "");
                cmd.Parameters.AddWithValue("@res", log.Result ?? "");
                cmd.Parameters.AddWithValue("@rsn", (object?)log.Reason ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ct", (log.CreateTime == default ? DateTime.Now : log.CreateTime).ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>批量追加日志</summary>
        public void AddLogs(List<LogEntry> logs)
        {
            if (logs == null || logs.Count == 0) return;
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connStr);
                conn.Open();
                using var tx = conn.BeginTransaction();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO logs (device_id, user_id, lock_id, action, result, reason, create_time)
                    VALUES (@dev, @uid, @lid, @act, @res, @rsn, @ct)";
                foreach (var log in logs)
                {
                    cmd.Parameters.Clear();
                    cmd.Parameters.AddWithValue("@dev", log.DeviceId ?? "");
                    cmd.Parameters.AddWithValue("@uid", (object?)log.UserId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@lid", log.LockId);
                    cmd.Parameters.AddWithValue("@act", log.Action ?? "");
                    cmd.Parameters.AddWithValue("@res", log.Result ?? "");
                    cmd.Parameters.AddWithValue("@rsn", (object?)log.Reason ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ct", (log.CreateTime == default ? DateTime.Now : log.CreateTime).ToString("O"));
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }

        /// <summary>查询日志（支持按设备/用户/时间范围筛选，返回最近的 limit 条）</summary>
        public List<LogEntry> QueryLogs(string? deviceId = null, string? userId = null,
            DateTime? startTime = null, DateTime? endTime = null, int limit = 500)
        {
            var result = new List<LogEntry>();
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                var sql = "SELECT id, device_id, user_id, lock_id, action, result, reason, create_time FROM logs WHERE 1=1";
                if (!string.IsNullOrEmpty(deviceId)) { sql += " AND device_id = @dev"; cmd.Parameters.AddWithValue("@dev", deviceId); }
                if (!string.IsNullOrEmpty(userId)) { sql += " AND user_id = @uid"; cmd.Parameters.AddWithValue("@uid", userId); }
                if (startTime.HasValue) { sql += " AND create_time >= @st"; cmd.Parameters.AddWithValue("@st", startTime.Value.ToString("O")); }
                if (endTime.HasValue) { sql += " AND create_time <= @et"; cmd.Parameters.AddWithValue("@et", endTime.Value.ToString("O")); }
                sql += " ORDER BY create_time DESC LIMIT @lim";
                cmd.Parameters.AddWithValue("@lim", limit);
                cmd.CommandText = sql;
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new LogEntry
                    {
                        Id = reader.GetInt64(0),
                        DeviceId = reader.GetString(1),
                        UserId = reader.IsDBNull(2) ? null : reader.GetString(2),
                        LockId = reader.GetInt32(3),
                        Action = reader.GetString(4),
                        Result = reader.GetString(5),
                        Reason = reader.IsDBNull(6) ? null : reader.GetString(6),
                        CreateTime = DateTime.Parse(reader.GetString(7))
                    });
                }
            }
            return result;
        }

        /// <summary>日志总数</summary>
        public int GetLogCount()
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM logs";
                return Convert.ToInt32(cmd.ExecuteScalar());
            }
        }

        /// <summary>清除所有日志</summary>
        public void ClearLogs()
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connStr);
                conn.Open();
                conn.ExecuteNonQuery("DELETE FROM logs");
            }
        }

        // ====== 下发任务 ======

        /// <summary>创建下发任务，返回任务 ID</summary>
        public long CreateDeployTask(DeployTask task)
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO deploy_tasks
                    (task_type, user_id, device_id, class_id, payload, operator_user_id, status, total_devices, acked_devices, create_time)
                    VALUES (@tt, @uid, @did, @cid, @pl, @op, @st, @td, @ad, @ct);
                    SELECT last_insert_rowid();";
                cmd.Parameters.AddWithValue("@tt", task.TaskType ?? "");
                cmd.Parameters.AddWithValue("@uid", (object?)task.UserId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@did", (object?)task.DeviceId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@cid", (object?)task.ClassId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@pl", (object?)task.Payload ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@op", (object?)task.OperatorUserId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@st", task.Status ?? "pending");
                cmd.Parameters.AddWithValue("@td", task.TotalDevices);
                cmd.Parameters.AddWithValue("@ad", task.AckedDevices);
                cmd.Parameters.AddWithValue("@ct", DateTime.Now.ToString("O"));
                return (long)cmd.ExecuteScalar();
            }
        }

        /// <summary>更新下发任务状态</summary>
        public void UpdateDeployTask(long taskId, string status, int ackedDevices, DateTime? completeTime = null)
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"UPDATE deploy_tasks SET status = @st, acked_devices = @ad,
                    complete_time = @ct WHERE id = @id";
                cmd.Parameters.AddWithValue("@st", status);
                cmd.Parameters.AddWithValue("@ad", ackedDevices);
                cmd.Parameters.AddWithValue("@ct", (object?)completeTime?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@id", taskId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>获取最近的下发任务列表</summary>
        public List<DeployTask> GetRecentDeployTasks(int limit = 50)
        {
            var result = new List<DeployTask>();
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT id, task_type, user_id, device_id, class_id, payload, operator_user_id,
                    status, total_devices, acked_devices, create_time, complete_time
                    FROM deploy_tasks ORDER BY create_time DESC LIMIT @lim";
                cmd.Parameters.AddWithValue("@lim", limit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new DeployTask
                    {
                        Id = reader.GetInt64(0),
                        TaskType = reader.GetString(1),
                        UserId = reader.IsDBNull(2) ? null : reader.GetString(2),
                        DeviceId = reader.IsDBNull(3) ? null : reader.GetString(3),
                        ClassId = reader.IsDBNull(4) ? null : reader.GetString(4),
                        Payload = reader.IsDBNull(5) ? null : reader.GetString(5),
                        OperatorUserId = reader.IsDBNull(6) ? null : reader.GetString(6),
                        Status = reader.GetString(7),
                        TotalDevices = reader.GetInt32(8),
                        AckedDevices = reader.GetInt32(9),
                        CreateTime = DateTime.Parse(reader.GetString(10)),
                        CompleteTime = reader.IsDBNull(11) ? null : DateTime.Parse(reader.GetString(11))
                    });
                }
            }
            return result;
        }

        // ====== 下发状态明细 ======

        /// <summary>创建下发状态记录</summary>
        public void CreateDeployStatus(DeployStatus status)
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT INTO deploy_statuses (task_id, device_id, status, error_message, retry_count, last_retry_time, ack_time)
                    VALUES (@tid, @did, @st, @em, @rc, @lrt, @at)";
                cmd.Parameters.AddWithValue("@tid", status.TaskId);
                cmd.Parameters.AddWithValue("@did", status.DeviceId ?? "");
                cmd.Parameters.AddWithValue("@st", status.Status ?? "pending");
                cmd.Parameters.AddWithValue("@em", (object?)status.ErrorMessage ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@rc", status.RetryCount);
                cmd.Parameters.AddWithValue("@lrt", (object?)status.LastRetryTime?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@at", (object?)status.AckTime?.ToString("O") ?? DBNull.Value);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>更新下发状态（收到 ACK 或重试时）</summary>
        public void UpdateDeployStatus(long statusId, string status, DateTime? ackTime, string? errorMsg, int retryCount)
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"UPDATE deploy_statuses SET status = @st, ack_time = @at,
                    error_message = @em, retry_count = @rc, last_retry_time = @lrt WHERE id = @id";
                cmd.Parameters.AddWithValue("@st", status);
                cmd.Parameters.AddWithValue("@at", (object?)ackTime?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@em", (object?)errorMsg ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@rc", retryCount);
                cmd.Parameters.AddWithValue("@lrt", DateTime.Now.ToString("O"));
                cmd.Parameters.AddWithValue("@id", statusId);
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>获取某任务的下发状态明细</summary>
        public List<DeployStatus> GetDeployStatuses(long taskId)
        {
            var result = new List<DeployStatus>();
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT id, task_id, device_id, status, error_message, retry_count, last_retry_time, ack_time
                    FROM deploy_statuses WHERE task_id = @tid ORDER BY device_id";
                cmd.Parameters.AddWithValue("@tid", taskId);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new DeployStatus
                    {
                        Id = reader.GetInt64(0),
                        TaskId = reader.GetInt64(1),
                        DeviceId = reader.GetString(2),
                        Status = reader.GetString(3),
                        ErrorMessage = reader.IsDBNull(4) ? null : reader.GetString(4),
                        RetryCount = reader.GetInt32(5),
                        LastRetryTime = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6)),
                        AckTime = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7))
                    });
                }
            }
            return result;
        }

        // ====== 备份记录 ======

        /// <summary>添加备份记录</summary>
        public void AddBackupRecord(BackupRecord record)
        {
            if (record == null) return;
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"INSERT OR REPLACE INTO backup_records
                    (backup_id, trigger_action, operator_user_id, file_path, file_size, tables, global_version, create_time)
                    VALUES (@bid, @ta, @op, @fp, @fs, @tb, @gv, @ct)";
                cmd.Parameters.AddWithValue("@bid", record.BackupId);
                cmd.Parameters.AddWithValue("@ta", record.TriggerAction ?? "");
                cmd.Parameters.AddWithValue("@op", (object?)record.OperatorUserId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@fp", record.FilePath ?? "");
                cmd.Parameters.AddWithValue("@fs", record.FileSize);
                cmd.Parameters.AddWithValue("@tb", (object?)record.Tables ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@gv", record.GlobalVersion);
                cmd.Parameters.AddWithValue("@ct", (record.CreateTime == default ? DateTime.Now : record.CreateTime).ToString("O"));
                cmd.ExecuteNonQuery();
            }
        }

        /// <summary>获取备份记录列表</summary>
        public List<BackupRecord> GetBackupRecords(int limit = 50)
        {
            var result = new List<BackupRecord>();
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connStr);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT backup_id, trigger_action, operator_user_id, file_path, file_size, tables, global_version, create_time
                    FROM backup_records ORDER BY create_time DESC LIMIT @lim";
                cmd.Parameters.AddWithValue("@lim", limit);
                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    result.Add(new BackupRecord
                    {
                        BackupId = reader.GetString(0),
                        TriggerAction = reader.GetString(1),
                        OperatorUserId = reader.IsDBNull(2) ? null : reader.GetString(2),
                        FilePath = reader.GetString(3),
                        FileSize = reader.GetInt64(4),
                        Tables = reader.IsDBNull(5) ? null : reader.GetString(5),
                        GlobalVersion = reader.GetInt64(6),
                        CreateTime = DateTime.Parse(reader.GetString(7))
                    });
                }
            }
            return result;
        }
    }

    /// <summary>SqliteConnection 扩展：便捷执行非查询 SQL</summary>
    internal static class SqliteExtensions
    {
        public static void ExecuteNonQuery(this SqliteConnection conn, string sql)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
    }
}
