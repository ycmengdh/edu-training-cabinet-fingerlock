using Microsoft.Data.Sqlite;

namespace CabinetLock
{
    public sealed class CabinetSyncQueueService
    {
        // 自动流水线每次只认领一个任务，确保 OTA 可以在下一柜边界立即获得优先权。
        private const int MaxJobsPerPass = 1;
        private readonly SemaphoreSlim _processor = new(1, 1);

        public void EnqueueUser(string userId, IEnumerable<string> deviceIds, string reason)
        {
            if (string.IsNullOrWhiteSpace(userId) || deviceIds == null) return;
            foreach (string deviceId in deviceIds.Where(id =>
                             !string.IsNullOrWhiteSpace(id) && !IsRootTarget(id))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                Upsert("user", userId.Trim(), deviceId.Trim(), reason);
        }

        public void EnqueueUserDeletion(
            string userId, IEnumerable<string> deviceIds, string reason)
        {
            if (string.IsNullOrWhiteSpace(userId) || deviceIds == null) return;
            foreach (string deviceId in deviceIds.Where(id =>
                             !string.IsNullOrWhiteSpace(id) && !IsRootTarget(id))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                Upsert("delete_user", userId.Trim(), deviceId.Trim(), reason);
        }

        public void EnqueueCabinet(string deviceId, string reason)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || IsRootTarget(deviceId)) return;
            Upsert("cabinet", "", deviceId.Trim(), reason);
        }

        public void EnqueueMaintenance(IEnumerable<string> deviceIds, string reason)
        {
            if (deviceIds == null) return;
            foreach (string deviceId in deviceIds.Where(id =>
                             !string.IsNullOrWhiteSpace(id) && !IsRootTarget(id))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                Upsert("maintenance", "", deviceId.Trim(), reason);
        }

        public IReadOnlyList<CabinetSyncJob> GetAll()
        {
            BusinessDatabase.Initialize();
            using SqliteConnection connection = BusinessDatabase.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT job_key,job_kind,user_id,device_id,reason,state,
attempt_count,next_attempt_time,last_error,update_time,complete_time
FROM cabinet_sync_queue WHERE UPPER(TRIM(device_id)) NOT LIKE 'ROOT\_%' ESCAPE '\'
ORDER BY update_time DESC";
            using SqliteDataReader reader = command.ExecuteReader();
            var jobs = new List<CabinetSyncJob>();
            while (reader.Read()) jobs.Add(Map(reader));
            return jobs;
        }

        public IReadOnlyList<CabinetSyncJob> GetRelevant(
            IEnumerable<string>? userIds, IEnumerable<string>? deviceIds)
        {
            string[] users = NormalizeIds(userIds);
            string[] devices = NormalizeIds(deviceIds);
            if (users.Length == 0 && devices.Length == 0)
                return Array.Empty<CabinetSyncJob>();

            BusinessDatabase.Initialize();
            using SqliteConnection connection = BusinessDatabase.Open();
            using SqliteCommand command = connection.CreateCommand();
            var conditions = new List<string>();
            if (users.Length > 0)
            {
                string[] names = users.Select((_, index) => $"$user_{index}").ToArray();
                for (int index = 0; index < users.Length; index++)
                    command.Parameters.AddWithValue(names[index], users[index]);
                conditions.Add($"(job_kind='user' AND user_id COLLATE NOCASE IN ({string.Join(',', names)}))");
            }
            if (devices.Length > 0)
            {
                string[] names = devices.Select((_, index) => $"$device_{index}").ToArray();
                for (int index = 0; index < devices.Length; index++)
                    command.Parameters.AddWithValue(names[index], devices[index]);
                conditions.Add($"(job_kind='cabinet' AND device_id COLLATE NOCASE IN ({string.Join(',', names)}))");
            }
            command.CommandText = $@"SELECT job_key,job_kind,user_id,device_id,reason,state,
attempt_count,next_attempt_time,last_error,update_time,complete_time
FROM cabinet_sync_queue WHERE UPPER(TRIM(device_id)) NOT LIKE 'ROOT\_%' ESCAPE '\'
AND ({string.Join(" OR ", conditions)})
ORDER BY update_time DESC";
            using SqliteDataReader reader = command.ExecuteReader();
            var jobs = new List<CabinetSyncJob>();
            while (reader.Read()) jobs.Add(Map(reader));
            return jobs;
        }

        /// <summary>未完成任务（pending/running/failed 待重试）。</summary>
        public IReadOnlyList<CabinetSyncJob> GetOpen() =>
            GetAll().Where(job => !string.Equals(job.State, "completed", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(job => job.UpdateTime)
                .ToList();

        public (int Open, int Failed) CountOpenAndFailed()
        {
            BusinessDatabase.Initialize();
            using SqliteConnection connection = BusinessDatabase.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"
SELECT COUNT(1),COALESCE(SUM(CASE WHEN state='failed' THEN 1 ELSE 0 END),0)
FROM cabinet_sync_queue WHERE state<>'completed'
AND UPPER(TRIM(device_id)) NOT LIKE 'ROOT\_%' ESCAPE '\'";
            using SqliteDataReader reader = command.ExecuteReader();
            return reader.Read()
                ? (reader.GetInt32(0), reader.GetInt32(1))
                : (0, 0);
        }

        public int CountOpen() => CountOpenAndFailed().Open;

        public int CountFailed() => CountOpenAndFailed().Failed;

        public CabinetSyncJob? GetUserJob(string userId, string deviceId) => GetAll()
            .FirstOrDefault(job => job.JobKind == "user" &&
                string.Equals(job.UserId, userId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(job.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

        public CabinetSyncJob? GetCabinetJob(string deviceId) => GetAll()
            .FirstOrDefault(job => job.JobKind == "cabinet" &&
                string.Equals(job.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

        public CabinetSyncJob? GetMaintenanceJob(string deviceId) => GetAll()
            .FirstOrDefault(job => job.JobKind == "maintenance" &&
                string.Equals(job.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

        public void RemoveDeviceJobs(string deviceId)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            BusinessDatabase.Initialize();
            using SqliteConnection connection = BusinessDatabase.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM cabinet_sync_queue WHERE device_id=$device_id";
            command.Parameters.AddWithValue("$device_id", deviceId.Trim());
            command.ExecuteNonQuery();
        }

        /// <summary>删除旧版本误写入的根节点同步任务。</summary>
        public int RemoveInvalidRootJobs()
        {
            BusinessDatabase.Initialize();
            using SqliteConnection connection = BusinessDatabase.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"DELETE FROM cabinet_sync_queue
WHERE UPPER(TRIM(device_id)) LIKE 'ROOT\_%' ESCAPE '\'";
            return command.ExecuteNonQuery();
        }

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try { Trigger(); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch { }
                await Task.Delay(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
            }
        }

        public void Trigger()
        {
            if (System.Windows.Application.Current is App app &&
                app.CabinetBackgroundServicesStarted)
                app.QueueAutomaticCommunicationPipeline();
        }

        public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
        {
            if (!await _processor.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return 0;
            try
            {
                HashSet<string> online = App.MeshBridge.GetOnlineDevices()
                    .Where(device => device.IsOnline && !device.IsRoot &&
                        !string.IsNullOrWhiteSpace(device.DeviceId) &&
                        !IsRootTarget(device.DeviceId))
                    .Select(device => device.DeviceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (online.Count == 0) return 0;

                DateTime now = DateTime.Now;
                List<CabinetSyncJob> due = SelectAutomaticPass(
                    GetAll(), online, now, MaxJobsPerPass).ToList();
                int processed = 0;
                foreach (CabinetSyncJob job in due)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string lease = "";
                    try
                    {
                        (bool claimed, bool success, string error) =
                            await App.CommunicationCoordinator
                            .RunExclusiveAsync(
                                CommunicationOperationKind.CabinetSync,
                                DescribeJob(job),
                                job.DeviceId,
                                async token =>
                                {
                                    lease = MarkRunning(job);
                                    if (string.IsNullOrEmpty(lease))
                                        return (false, false, "");
                                    (bool jobSuccess, string jobError) =
                                        await ExecuteJobAsync(job, token).ConfigureAwait(false);
                                    return (true, jobSuccess, jobError);
                                },
                                cancellationToken)
                            .ConfigureAwait(false);
                        if (!claimed) continue;
                        processed++;
                        if (success)
                        {
                            MarkCompleted(job.JobKey, lease);
                        }
                        else MarkFailed(job, error, lease);
                    }
                    catch (Exception ex)
                    {
                        if (!string.IsNullOrEmpty(lease))
                            MarkFailed(job, ex.Message, lease);
                    }
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                return processed;
            }
            finally
            {
                _processor.Release();
            }
        }

        private static async Task<(bool Success, string Error)> ExecuteJobAsync(
            CabinetSyncJob job, CancellationToken cancellationToken)
        {
            if (job.JobKind == "cabinet")
            {
                CabinetDataSyncResult result = await App.CabinetSyncService
                    .SyncCabinetDataAsync(job.DeviceId,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return (result.Success,
                    result.Success ? "" : result.FormatForDisplay());
            }

            if (job.JobKind == "delete_user")
            {
                CommandResult result = await App.CommandService
                    .DeleteUserPermissionAsync(job.DeviceId, job.UserId,
                        CabinetSyncService.GetExpectedPermissionVersion())
                    .ConfigureAwait(false);
                return (result.Success, result.Success ? "" : result.ErrorMessage);
            }

            if (job.JobKind == "maintenance")
            {
                bool success = await App.MaintenanceService
                    .SyncDeviceAsync(job.DeviceId, cancellationToken)
                    .ConfigureAwait(false);
                return (success, success ? "" : "维护配置同步失败");
            }

            User? user = App.UserService.GetUser(job.UserId);
            if (user == null) return (true, "");

            IReadOnlyList<UserCabinetSyncResult> userResult = await App.CabinetSyncService
                .VerifyAndSyncUserAsync(user, new[] { job.DeviceId },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            UserCabinetSyncResult? item = userResult.FirstOrDefault();
            return (item?.Success == true,
                item?.ErrorMessage ?? "柜机未返回同步结果");
        }

        public static IReadOnlyList<CabinetSyncJob> SelectAutomaticPass(
            IEnumerable<CabinetSyncJob> jobs,
            IReadOnlySet<string> onlineDeviceIds,
            DateTime now,
            int maximumJobs = 1)
        {
            if (jobs == null || onlineDeviceIds == null || maximumJobs <= 0)
                return Array.Empty<CabinetSyncJob>();

            return jobs.Where(job =>
                    !string.Equals(job.State, "completed", StringComparison.OrdinalIgnoreCase) &&
                    !IsRootTarget(job.DeviceId) &&
                    onlineDeviceIds.Contains(job.DeviceId) &&
                    (!job.NextAttemptTime.HasValue || job.NextAttemptTime.Value <= now))
                .OrderBy(AutomaticPhasePriority)
                .ThenBy(job => job.UpdateTime)
                .ThenBy(job => job.DeviceId, StringComparer.OrdinalIgnoreCase)
                .Take(maximumJobs)
                .ToList();
        }

        private static int AutomaticPhasePriority(CabinetSyncJob job) =>
            string.Equals(job.JobKind, "maintenance", StringComparison.OrdinalIgnoreCase)
                ? 1 : 0;

        private static string DescribeJob(CabinetSyncJob job) => job.JobKind switch
        {
            "cabinet" => $"自动流程 2/3 · 同步柜机 {job.DeviceId} 权限与指纹",
            "delete_user" => $"自动流程 2/3 · 清理柜机 {job.DeviceId} 用户权限",
            "maintenance" => $"自动流程 3/3 · 同步柜机 {job.DeviceId} 维护配置",
            _ => $"自动流程 2/3 · 同步柜机 {job.DeviceId} 用户 {job.UserId}"
        };

        public void RecordUserOutcome(
            string userId, string deviceId, bool success, string? error = null)
        {
            CabinetSyncJob? job = GetUserJob(userId, deviceId);
            if (job == null) return;
            if (success) MarkCompleted(job.JobKey);
            else MarkFailed(job, error ?? "同步失败");
        }

        public void RecordCabinetOutcome(string deviceId, bool success, string? error = null)
        {
            CabinetSyncJob? job = GetCabinetJob(deviceId);
            if (job == null) return;
            if (success) MarkCompleted(job.JobKey);
            else MarkFailed(job, error ?? "柜机同步失败");
        }

        public void RecordMaintenanceOutcome(
            string deviceId, bool success, string? error = null)
        {
            CabinetSyncJob? job = GetMaintenanceJob(deviceId);
            if (job == null) return;
            if (success) MarkCompleted(job.JobKey);
            else MarkFailed(job, error ?? "维护配置同步失败");
        }

        private static void Upsert(string kind, string userId, string deviceId, string reason)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || IsRootTarget(deviceId)) return;
            BusinessDatabase.Initialize();
            using SqliteConnection connection = BusinessDatabase.Open();
            using SqliteCommand command = connection.CreateCommand();
            // upsert/delete 共用一个用户级键，离线期间反复操作只保留最终意图。
            string keyKind = kind == "delete_user" ? "user" : kind;
            string key = $"{keyKind}:{deviceId}:{userId}".ToUpperInvariant();
            command.CommandText = @"
INSERT INTO cabinet_sync_queue(job_key,job_kind,user_id,device_id,reason,state,
attempt_count,next_attempt_time,last_error,update_time,complete_time)
VALUES($key,$kind,$user,$device,$reason,'pending',0,NULL,'',$now,NULL)
ON CONFLICT(job_key) DO UPDATE SET job_kind=excluded.job_kind,
reason=excluded.reason,state='pending',
next_attempt_time=NULL,last_error='',update_time=excluded.update_time,complete_time=NULL;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$user", userId);
            command.Parameters.AddWithValue("$device", deviceId);
            command.Parameters.AddWithValue("$reason", reason ?? "");
            command.Parameters.AddWithValue("$now", DateTime.Now.ToString("o"));
            command.ExecuteNonQuery();
        }

        internal static bool IsRootTarget(string? deviceId) =>
            !string.IsNullOrWhiteSpace(deviceId) &&
            deviceId.Trim().StartsWith("ROOT_", StringComparison.OrdinalIgnoreCase);

        private static string MarkRunning(CabinetSyncJob job)
        {
            string lease = DateTime.Now.ToString("o");
            BusinessDatabase.Initialize();
            using SqliteConnection connection = BusinessDatabase.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"UPDATE cabinet_sync_queue SET state='running',
attempt_count=$attempts,next_attempt_time=NULL,last_error='',update_time=$lease,
complete_time=NULL WHERE job_key=$key AND update_time=$expected";
            command.Parameters.AddWithValue("$attempts", job.AttemptCount + 1);
            command.Parameters.AddWithValue("$lease", lease);
            command.Parameters.AddWithValue("$key", job.JobKey);
            command.Parameters.AddWithValue("$expected", job.UpdateTime.ToString("o"));
            return command.ExecuteNonQuery() == 1 ? lease : "";
        }

        private static bool MarkCompleted(string jobKey, string? expectedUpdateTime = null) =>
            ExecuteStateUpdate(jobKey, "completed", null, null, "", true,
                expectedUpdateTime);

        private static void MarkFailed(
            CabinetSyncJob job, string error, string? expectedUpdateTime = null)
        {
            int attempt = job.AttemptCount + 1;
            double seconds = Math.Min(300, 5 * Math.Pow(2, Math.Min(6, attempt - 1)));
            ExecuteStateUpdate(job.JobKey, "failed", attempt,
                DateTime.Now.AddSeconds(seconds), error, false, expectedUpdateTime);
        }

        private static bool ExecuteStateUpdate(string key, string state, int? attempts,
            DateTime? nextAttempt, string error, bool completed,
            string? expectedUpdateTime = null)
        {
            BusinessDatabase.Initialize();
            using SqliteConnection connection = BusinessDatabase.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"UPDATE cabinet_sync_queue SET state=$state,
attempt_count=COALESCE($attempts,attempt_count),next_attempt_time=$next,
last_error=$error,update_time=$now,complete_time=$complete WHERE job_key=$key
AND ($expected IS NULL OR update_time=$expected)";
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$attempts", attempts.HasValue ? attempts.Value : DBNull.Value);
            command.Parameters.AddWithValue("$next", nextAttempt.HasValue
                ? nextAttempt.Value.ToString("o") : DBNull.Value);
            command.Parameters.AddWithValue("$error", error ?? "");
            command.Parameters.AddWithValue("$now", DateTime.Now.ToString("o"));
            command.Parameters.AddWithValue("$complete", completed
                ? DateTime.Now.ToString("o") : DBNull.Value);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$expected",
                (object?)expectedUpdateTime ?? DBNull.Value);
            return command.ExecuteNonQuery() == 1;
        }

        private static CabinetSyncJob Map(SqliteDataReader reader) => new()
        {
            JobKey = reader.GetString(0),
            JobKind = reader.GetString(1),
            UserId = reader.GetString(2),
            DeviceId = reader.GetString(3),
            Reason = reader.GetString(4),
            State = reader.GetString(5),
            AttemptCount = reader.GetInt32(6),
            NextAttemptTime = Parse(reader, 7),
            LastError = reader.IsDBNull(8) ? "" : reader.GetString(8),
            UpdateTime = Parse(reader, 9) ?? DateTime.MinValue,
            CompleteTime = Parse(reader, 10)
        };

        private static DateTime? Parse(SqliteDataReader reader, int ordinal) =>
            reader.IsDBNull(ordinal) ? null : DateTime.TryParse(reader.GetString(ordinal), out DateTime value)
                ? value : null;

        private static string[] NormalizeIds(IEnumerable<string>? ids) =>
            (ids ?? Array.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
    }

    public sealed class CabinetSyncJob
    {
        public string JobKey { get; init; } = "";
        public string JobKind { get; init; } = "";
        public string UserId { get; init; } = "";
        public string DeviceId { get; init; } = "";
        public string Reason { get; init; } = "";
        public string State { get; init; } = "pending";
        public int AttemptCount { get; init; }
        public DateTime? NextAttemptTime { get; init; }
        public string LastError { get; init; } = "";
        public DateTime UpdateTime { get; init; }
        public DateTime? CompleteTime { get; init; }

        public string StatusText => State switch
        {
            "completed" => "已同步",
            "running" => "同步中",
            "failed" => "待重试",
            _ => "待同步"
        };
    }
}
