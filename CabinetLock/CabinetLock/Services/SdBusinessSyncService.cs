using Newtonsoft.Json.Linq;

namespace CabinetLock
{
    /// <summary>
    /// SD 业务同步：启动 SD→business.db；显式同步或链路恢复时 business.db→SD。
    /// 开锁日志可从 SD 合并进 logs.db（非强制）。
    /// </summary>
    public class SdBusinessSyncService
    {
        private readonly SemaphoreSlim _pushGate = new(1, 1);

        public sealed class SyncResult
        {
            public bool Success { get; set; }
            public List<string> PulledTables { get; } = new();
            public List<string> UnchangedTables { get; } = new();
            public List<string> FailedTables { get; } = new();
            public List<string> FailureDetails { get; } = new();
            public List<string> EmptyTables { get; } = new();
            public int UploadedFingerprintCount { get; set; }
            public int FailedFingerprintCount { get; set; }
            public string Message { get; set; } = "";
        }

        /// <summary>
        /// 从 SD 覆盖导入业务表到本机 business.db。
        /// 空表（[]）视为成功同步；仅网络/解析失败计入 FailedTables。
        /// 对失败表做有限重试，并支持部分成功继续登录。
        /// </summary>
        public async Task<SyncResult> PullBusinessFromSdAsync(
            IProgress<string>? progress = null,
            int timeoutMs = 10000,
            CancellationToken cancellationToken = default)
        {
            var result = new SyncResult();
            if (!App.SdStorageService.IsAvailable)
            {
                result.Success = false;
                result.Message = string.IsNullOrWhiteSpace(App.SdStorageService.LastError)
                    ? "根节点 SD 不可用，无法拉取业务数据"
                    : App.SdStorageService.LastError;
                return result;
            }

            BusinessDatabase.Initialize();

            // 先拉版本快照，便于空表也能写对 version。
            SdVersionInfo? versions = null;
            try
            {
                progress?.Report("正在读取 SD 版本…");
                versions = await App.SdStorageService.QueryVersionAsync(timeoutMs)
                    .ConfigureAwait(false);
            }
            catch
            {
                versions = null;
            }
            if (versions != null)
                BusinessDatabase.SetTableVersion("fingerprints", versions.FpVersion);

            foreach (string table in BusinessDatabase.BusinessTables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"正在读取 SD 表：{table}…");

                bool imported = false;
                string? lastFail = null;
                for (int attempt = 1; attempt <= 2 && !imported; attempt++)
                {
                    try
                    {
                        var snapshot = await App.SdStorageService.QueryTableSnapshotAsync(table, timeoutMs)
                            .ConfigureAwait(false);

                        JArray array;
                        uint version = RootDataService.GetTableVersion(table, versions);

                        if (snapshot == null || string.IsNullOrWhiteSpace(snapshot.Json))
                        {
                            // 固件新版本会回 []；旧固件可能返回 ERROR。空表回退为 []。
                            array = new JArray();
                            lastFail = App.SdStorageService.LastError;
                            // 仅当根节点明确报错且不是 not found/empty 时才算失败
                            if (!string.IsNullOrWhiteSpace(lastFail) &&
                                !lastFail.Contains("not found", StringComparison.OrdinalIgnoreCase) &&
                                !lastFail.Contains("empty", StringComparison.OrdinalIgnoreCase) &&
                                !lastFail.Contains("table not found", StringComparison.OrdinalIgnoreCase))
                            {
                                if (attempt < 2)
                                {
                                    await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                                    continue;
                                }
                                result.FailedTables.Add(table);
                                break;
                            }
                        }
                        else
                        {
                            JArray? parsed = NormalizeTableArray(
                                snapshot.Json, table, out string? parseError);
                            if (parsed == null)
                            {
                                lastFail = parseError ?? "JSON 解析失败";
                                if (attempt < 2)
                                {
                                    await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                                    continue;
                                }
                                result.FailedTables.Add(table);
                                break;
                            }
                            array = parsed;
                            version = snapshot.Version > 0 ? snapshot.Version : version;
                        }

                        BusinessDatabase.ReplaceTable(table, array, version);
                        result.PulledTables.Add(table);
                        if (array.Count == 0)
                            result.EmptyTables.Add(table);
                        imported = true;
                    }
                    catch (Exception ex)
                    {
                        lastFail = ex.Message;
                        if (attempt < 2)
                        {
                            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                            continue;
                        }
                        result.FailedTables.Add(table);
                    }
                }

                if (!imported && !result.FailedTables.Contains(table))
                    result.FailedTables.Add(table);
            }

            // 尽力合并开锁日志
            try
            {
                progress?.Report("正在合并 SD 开锁日志（可选）…");
                var logSnap = await App.SdStorageService.QueryTableSnapshotAsync("logs", timeoutMs)
                    .ConfigureAwait(false);
                if (logSnap != null && !string.IsNullOrWhiteSpace(logSnap.Json))
                {
                    var array = NormalizeTableArray(logSnap.Json, "logs", out _);
                    if (array != null)
                        LogDatabase.MergeUnlockFromArray(array);
                }
                progress?.Report("开锁日志合并完成，正在校验业务表…");
            }
            catch
            {
                // 开锁日志合并失败不影响业务
                progress?.Report("开锁日志暂未合并，不影响业务同步…");
            }

            // 成功条件：核心表 users 必须成功；其余允许空表。
            bool usersOk = result.PulledTables.Contains("users");
            result.Success = usersOk && result.FailedTables.Count == 0;
            if (result.PulledTables.Count > 0 && result.FailedTables.Count > 0)
            {
                result.Success = usersOk; // 用户表成功即可登录使用已同步数据
                result.Message =
                    $"部分表同步成功：{string.Join(",", result.PulledTables)}；失败：{string.Join(",", result.FailedTables)}";
            }
            else if (result.PulledTables.Count == 0)
            {
                result.Success = false;
                result.Message = string.IsNullOrWhiteSpace(App.SdStorageService.LastError)
                    ? "未能从 SD 读取任何业务表"
                    : App.SdStorageService.LastError;
            }
            else
            {
                string emptyHint = result.EmptyTables.Count > 0
                    ? $"（空表 {result.EmptyTables.Count} 张）"
                    : "";
                result.Message = $"已同步 {result.PulledTables.Count} 张业务表{emptyHint}";
            }

            return result;
        }

        /// <summary>
        /// 将 SD 返回的 JSON 规范为表数组。
        /// 支持 []、{items:[]}、单对象包一层、嵌套 json 字符串。
        /// </summary>
        public static JArray? NormalizeTableArray(string json, string table, out string? error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                return new JArray();
            }

            try
            {
                var token = JToken.Parse(json);
                if (token is JArray arr)
                    return arr;

                if (token is JObject obj)
                {
                    if (obj["items"] is JArray items)
                        return items;
                    if (obj["data"] is JArray dataArr)
                        return dataArr;
                    if (obj["json"] != null)
                    {
                        // response envelope accidentally passed through
                        var inner = obj["json"];
                        if (inner is JArray innerArr) return innerArr;
                        if (inner?.Type == JTokenType.String)
                        {
                            var nested = JToken.Parse(inner.Value<string>() ?? "[]");
                            if (nested is JArray nestedArr) return nestedArr;
                        }
                    }

                    // 单记录对象：包装为数组（容错）
                    if (obj.Properties().Any())
                        return new JArray(obj);
                }

                error = $"表 {table} 不是 JSON 数组";
                return null;
            }
            catch (Exception ex)
            {
                error = $"表 {table} JSON 无效：{ex.Message}";
                return null;
            }
        }

        /// <summary>将本机 business.db 回写到 SD（显式同步或链路恢复时调用）。</summary>
        public async Task<SyncResult> PushBusinessToSdAsync(
            IProgress<string>? progress = null,
            int timeoutMs = 10000,
            CancellationToken cancellationToken = default)
        {
            await _pushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                string uploadStartHash = BusinessUploadStateService.CaptureCurrentDataHash();
                SyncResult result = await PushBusinessToSdCoreAsync(
                    progress, timeoutMs, cancellationToken).ConfigureAwait(false);
                if (result.Success)
                {
                    try
                    {
                        if (!BusinessUploadStateService.TryMarkUploadedIfUnchanged(uploadStartHash))
                        {
                            result.Success = false;
                            result.Message = "上传过程中本机业务数据发生变化，需要重新上传";
                        }
                        else
                        {
                            DirectMaintenanceStateService.CompleteSession();
                        }
                    }
                    catch (Exception ex)
                    {
                        result.Success = false;
                        result.Message = $"业务数据已发送，但无法记录上传状态：{ex.Message}";
                    }
                }
                return result;
            }
            finally
            {
                _pushGate.Release();
            }
        }

        private async Task<SyncResult> PushBusinessToSdCoreAsync(
            IProgress<string>? progress,
            int timeoutMs,
            CancellationToken cancellationToken)
        {
            var result = new SyncResult();
            if (!App.SdStorageService.IsAvailable)
            {
                result.Success = false;
                result.Message = "SD 不可用，跳过业务库上传";
                return result;
            }

            BusinessDatabase.Initialize();
            SdVersionInfo? versions = null;
            try
            {
                progress?.Report("正在读取 SD 版本…");
                versions = await App.SdStorageService.QueryVersionAsync(timeoutMs).ConfigureAwait(false);
            }
            catch
            {
                versions = null;
            }

            foreach (string table in BusinessDatabase.BusinessTables)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report($"正在上传业务表：{table}…");
                try
                {
                    JArray array = BusinessDatabase.ReadArray(table) ?? new JArray();
                    // 始终写合法 JSON 数组（含空表），避免固件侧 missing json。
                    string json = array.ToString(Newtonsoft.Json.Formatting.None);
                    if (string.IsNullOrWhiteSpace(json)) json = "[]";

                    SdTableSnapshot? remoteSnapshot = null;
                    try
                    {
                        remoteSnapshot = await App.SdStorageService
                            .QueryTableSnapshotAsync(table, timeoutMs).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                    JArray? remoteArray = remoteSnapshot == null
                        ? null
                        : NormalizeTableArray(remoteSnapshot.Json, table, out _);
                    if (remoteArray != null && TableArraysEqual(array, remoteArray))
                    {
                        uint currentVersion = remoteSnapshot!.Version > 0
                            ? remoteSnapshot.Version
                            : RootDataService.GetTableVersion(table, versions);
                        BusinessDatabase.SetTableVersion(table, currentVersion);
                        result.UnchangedTables.Add(table);
                        continue;
                    }

                    uint sdVersion = RootDataService.GetTableVersion(table, versions);
                    bool ok = false;
                    for (int attempt = 1; attempt <= 2 && !ok; attempt++)
                    {
                        ok = await App.SdStorageService.SaveTableAsync(
                            table, json, sdVersion, timeoutMs).ConfigureAwait(false);
                        if (ok) break;

                        // 版本冲突 / 瞬时失败：刷新版本后重试一次
                        try
                        {
                            versions = await App.SdStorageService.QueryVersionAsync(timeoutMs)
                                .ConfigureAwait(false);
                            sdVersion = RootDataService.GetTableVersion(table, versions);
                        }
                        catch
                        {
                            // keep previous sdVersion
                        }
                        await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                    }

                    if (ok)
                    {
                        uint localNext = sdVersion + 1;
                        BusinessDatabase.SetTableVersion(table, localNext);
                        versions ??= new SdVersionInfo();
                        versions.AdvanceAfterSuccessfulSave(table);
                        result.PulledTables.Add(table);
                        if (array == null || array.Count == 0)
                            result.EmptyTables.Add(table);
                    }
                    else
                    {
                        AddTableFailure(result, table, App.SdStorageService.LastError);
                    }
                }
                catch (Exception ex)
                {
                    AddTableFailure(result, table, ex.Message);
                }
            }

            // 尽力回传开锁日志到 SD
            try
            {
                progress?.Report("正在上传开锁日志…");
                var unlock = LogDatabase.ReadAllUnlock();
                if (unlock.Count > 0)
                {
                    var arr = JArray.FromObject(unlock);
                    uint logBase = versions?.LogsVersion ?? 0;
                    await App.SdStorageService.SaveTableWithFallbackAsync(
                        "logs", arr.ToString(Newtonsoft.Json.Formatting.None), logBase, timeoutMs)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                // ignore
            }

            int fingerprintTotal = 0;
            bool fingerprintUploadCompleted = true;
            try
            {
                progress?.Report("正在上传指纹模板…");
                fingerprintTotal = BusinessDatabase.ListFpTemplatesWithBytes().Count;
                int fpOk = await UploadFingerprintsToSdAsync(timeoutMs).ConfigureAwait(false);
                result.UploadedFingerprintCount = fpOk;
                result.FailedFingerprintCount = Math.Max(0, fingerprintTotal - fpOk);
                if (result.FailedFingerprintCount == 0)
                {
                    try
                    {
                        SdVersionInfo? refreshed = await App.SdStorageService
                            .QueryVersionAsync(timeoutMs).ConfigureAwait(false);
                        if (refreshed != null)
                            BusinessDatabase.SetTableVersion("fingerprints", refreshed.FpVersion);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
                fingerprintUploadCompleted = false;
                result.FailedFingerprintCount = Math.Max(1, fingerprintTotal);
            }

            int processedTableCount = result.PulledTables.Count + result.UnchangedTables.Count;
            result.Success = result.FailedTables.Count == 0 &&
                processedTableCount > 0 &&
                fingerprintUploadCompleted &&
                result.FailedFingerprintCount == 0;
            string fingerprintSummary = fingerprintTotal > 0
                ? $"；指纹模板 {result.UploadedFingerprintCount}/{fingerprintTotal} 条"
                : "";
            if (processedTableCount == 0)
            {
                result.Success = false;
                result.Message = "业务表上传失败" + fingerprintSummary;
            }
            else if (result.FailedTables.Count > 0)
            {
                result.Success = false;
                result.Message =
                    $"部分上传成功：{string.Join(",", result.PulledTables)}；失败：{string.Join("；", result.FailureDetails)}" +
                    fingerprintSummary;
            }
            else if (!fingerprintUploadCompleted || result.FailedFingerprintCount > 0)
            {
                result.Success = false;
                result.Message = $"业务表已上传，但有 {result.FailedFingerprintCount} 条指纹模板上传失败" +
                    fingerprintSummary;
            }
            else
            {
                result.Message = result.PulledTables.Count == 0
                    ? "业务表无变化，无需重复上传" + fingerprintSummary
                    : $"已上传 {result.PulledTables.Count} 张业务表到 SD" +
                      (result.UnchangedTables.Count > 0
                          ? $"，跳过 {result.UnchangedTables.Count} 张未变化表"
                          : "") + fingerprintSummary;
            }

            return result;
        }

        public static bool TableArraysEqual(JArray left, JArray right)
        {
            static string Canonicalize(JToken token)
            {
                if (token is JObject obj)
                {
                    var sorted = new JObject(obj.Properties()
                        .OrderBy(property => property.Name, StringComparer.Ordinal)
                        .Select(property => new JProperty(
                            property.Name, JToken.Parse(Canonicalize(property.Value)))));
                    return sorted.ToString(Newtonsoft.Json.Formatting.None);
                }
                if (token is JArray array)
                {
                    var normalized = new JArray(array.Select(item =>
                        JToken.Parse(Canonicalize(item))));
                    return normalized.ToString(Newtonsoft.Json.Formatting.None);
                }
                return token.ToString(Newtonsoft.Json.Formatting.None);
            }

            string[] leftRows = left.Select(Canonicalize)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            string[] rightRows = right.Select(Canonicalize)
                .OrderBy(value => value, StringComparer.Ordinal).ToArray();
            return leftRows.SequenceEqual(rightRows, StringComparer.Ordinal);
        }

        private static void AddTableFailure(SyncResult result, string table, string? detail)
        {
            if (!result.FailedTables.Contains(table, StringComparer.OrdinalIgnoreCase))
                result.FailedTables.Add(table);
            string reason = string.IsNullOrWhiteSpace(detail) ? "未收到根节点写入确认" : detail.Trim();
            result.FailureDetails.Add($"{table}：{reason}");
        }

        /// <summary>同步封装，供非异步调用方使用。</summary>
        public SyncResult PushBusinessToSd(int timeoutMs = 8000)
        {
            try
            {
                return PushBusinessToSdAsync(null, timeoutMs).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                return new SyncResult { Success = false, Message = ex.Message };
            }
        }

        /// <summary>将 business.db 中指纹模板上传到 SD，返回成功条数。</summary>
        public async Task<int> UploadFingerprintsToSdAsync(int timeoutMs = 8000)
        {
            if (!App.SdStorageService.IsAvailable) return 0;
            int success = 0;
            foreach (var (meta, bytes) in BusinessDatabase.ListFpTemplatesWithBytes())
            {
                if (meta == null || bytes == null || bytes.Length == 0) continue;
                string userId = string.IsNullOrWhiteSpace(meta.UserId)
                    ? $"fp_{meta.FingerprintId}" : meta.UserId!;
                try
                {
                    bool ok = await App.SdStorageService.UploadFpTemplateWithFallbackAsync(
                        userId, meta.FingerIndex <= 0 ? 1 : meta.FingerIndex, bytes, timeoutMs)
                        .ConfigureAwait(false);
                    if (ok)
                    {
                        BusinessDatabase.UpdateFpTemplateBackupStatus(meta.FingerprintId, "sd");
                        success++;
                    }
                }
                catch
                {
                }
            }
            return success;
        }
    }
}
