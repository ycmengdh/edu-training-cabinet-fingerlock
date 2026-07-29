using Microsoft.Data.Sqlite;

namespace FingerprintLockManager
{
    public sealed class CabinetSyncQueueService
    {
        private const int MaxJobsPerPass = 20;
        private readonly SemaphoreSlim _processor = new(1, 1);

        public void EnqueueUser(string userId, IEnumerable<string> deviceIds, string reason)
        {
            if (string.IsNullOrWhiteSpace(userId) || deviceIds == null) return;
            foreach (string deviceId in deviceIds.Where(id => !string.IsNullOrWhiteSpace(id))
                         .Distinct(StringComparer.OrdinalIgnoreCase))
                Upsert("user", userId.Trim(), deviceId.Trim(), reason);
        }

        public void EnqueueCabinet(string deviceId, string reason)
        {
            if (string.IsNullOrWhiteSpace(deviceId)) return;
            Upsert("cabinet", "", deviceId.Trim(), reason);
        }

        public IReadOnlyList<CabinetSyncJob> GetAll()
        {
            BusinessDatabase.Initialize();
            using SqliteConnection connection = BusinessDatabase.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"SELECT job_key,job_kind,user_id,device_id,reason,state,
attempt_count,next_attempt_time,last_error,update_time,complete_time
FROM cabinet_sync_queue ORDER BY update_time DESC";
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

        public int CountOpen() => GetOpen().Count;

        public int CountFailed() => GetOpen().Count(job =>
            string.Equals(job.State, "failed", StringComparison.OrdinalIgnoreCase));

        public CabinetSyncJob? GetUserJob(string userId, string deviceId) => GetAll()
            .FirstOrDefault(job => job.JobKind == "user" &&
                string.Equals(job.UserId, userId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(job.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

        public CabinetSyncJob? GetCabinetJob(string deviceId) => GetAll()
            .FirstOrDefault(job => job.JobKind == "cabinet" &&
                string.Equals(job.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

        public async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try { await ProcessPendingAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
                catch { }
                await Task.Delay(TimeSpan.FromSeconds(12), cancellationToken).ConfigureAwait(false);
            }
        }

        public void Trigger()
        {
            _ = Task.Run(async () =>
            {
                try { await ProcessPendingAsync(CancellationToken.None).ConfigureAwait(false); }
                catch { }
            });
        }

        public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken)
        {
            if (!await _processor.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return 0;
            try
            {
                HashSet<string> online = App.MeshBridge.GetOnlineDevices()
                    .Where(device => device.IsOnline && !device.IsRoot &&
                        !string.IsNullOrWhiteSpace(device.DeviceId))
                    .Select(device => device.DeviceId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (online.Count == 0) return 0;

                DateTime now = DateTime.Now;
                List<CabinetSyncJob> due = GetAll().Where(job =>
                        job.State != "completed" && online.Contains(job.DeviceId) &&
                        (!job.NextAttemptTime.HasValue || job.NextAttemptTime.Value <= now))
                    .Take(MaxJobsPerPass).ToList();
                int completed = 0;
                foreach (CabinetSyncJob job in due)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    MarkRunning(job);
                    try
                    {
                        bool success;
                        string error;
                        if (job.JobKind == "cabinet")
                        {
                            CabinetDataSyncResult result = await App.CabinetSyncService
                                .SyncCabinetDataAsync(job.DeviceId, cancellationToken: cancellationToken)
                                .ConfigureAwait(false);
                            success = result.Success;
                            error = success ? "" : result.FormatForDisplay();
                        }
                        else
                        {
                            User? user = App.UserService.GetUser(job.UserId);
                            if (user == null)
                            {
                                success = true;
                                error = "";
                            }
                            else
                            {
                                IReadOnlyList<UserCabinetSyncResult> result = await App.CabinetSyncService
                                    .VerifyAndSyncUserAsync(user, new[] { job.DeviceId },
                                        cancellationToken: cancellationToken).ConfigureAwait(false);
                                UserCabinetSyncResult? item = result.FirstOrDefault();
                                success = item?.Success == true;
                                error = item?.ErrorMessage ?? "柜机未返回同步结果";
                            }
                        }
                        if (success)
                        {
                            MarkCompleted(job.JobKey);
                            completed++;
                        }
                        else MarkFailed(job, error);
                    }
                    catch (Exception ex)
                    {
                        MarkFailed(job, ex.Message);
                    }
                    await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                }
                return completed;
            }
            finally
            {
                _processor.Release();
            }
        }

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

        private static void Upsert(string kind, string userId, string deviceId, string reason)
        {
            BusinessDatabase.Initialize();
            using SqliteConnection connection = BusinessDatabase.Open();
            using SqliteCommand command = connection.CreateCommand();
            string key = $"{kind}:{deviceId}:{userId}".ToUpperInvariant();
            command.CommandText = @"
INSERT INTO cabinet_sync_queue(job_key,job_kind,user_id,device_id,reason,state,
attempt_count,next_attempt_time,last_error,update_time,complete_time)
VALUES($key,$kind,$user,$device,$reason,'pending',0,NULL,'',$now,NULL)
ON CONFLICT(job_key) DO UPDATE SET reason=excluded.reason,state='pending',
next_attempt_time=NULL,last_error='',update_time=excluded.update_time,complete_time=NULL;";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$user", userId);
            command.Parameters.AddWithValue("$device", deviceId);
            command.Parameters.AddWithValue("$reason", reason ?? "");
            command.Parameters.AddWithValue("$now", DateTime.Now.ToString("o"));
            command.ExecuteNonQuery();
        }

        private static void MarkRunning(CabinetSyncJob job) => ExecuteStateUpdate(
            job.JobKey, "running", job.AttemptCount + 1, null, "", false);

        private static void MarkCompleted(string jobKey) => ExecuteStateUpdate(
            jobKey, "completed", null, null, "", true);

        private static void MarkFailed(CabinetSyncJob job, string error)
        {
            int attempt = job.AttemptCount + 1;
            double seconds = Math.Min(300, 5 * Math.Pow(2, Math.Min(6, attempt - 1)));
            ExecuteStateUpdate(job.JobKey, "failed", attempt,
                DateTime.Now.AddSeconds(seconds), error, false);
        }

        private static void ExecuteStateUpdate(string key, string state, int? attempts,
            DateTime? nextAttempt, string error, bool completed)
        {
            BusinessDatabase.Initialize();
            using SqliteConnection connection = BusinessDatabase.Open();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = @"UPDATE cabinet_sync_queue SET state=$state,
attempt_count=COALESCE($attempts,attempt_count),next_attempt_time=$next,
last_error=$error,update_time=$now,complete_time=$complete WHERE job_key=$key";
            command.Parameters.AddWithValue("$state", state);
            command.Parameters.AddWithValue("$attempts", attempts.HasValue ? attempts.Value : DBNull.Value);
            command.Parameters.AddWithValue("$next", nextAttempt.HasValue
                ? nextAttempt.Value.ToString("o") : DBNull.Value);
            command.Parameters.AddWithValue("$error", error ?? "");
            command.Parameters.AddWithValue("$now", DateTime.Now.ToString("o"));
            command.Parameters.AddWithValue("$complete", completed
                ? DateTime.Now.ToString("o") : DBNull.Value);
            command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
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
