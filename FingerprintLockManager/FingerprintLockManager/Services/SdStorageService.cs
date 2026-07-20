using System.Collections.Concurrent;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace FingerprintLockManager
{
    /// <summary>
    /// SD 卡集中存储服务（上位机端）
    /// 通过 Mesh 网络与根节点 SD 卡通信，作为多上位机共享的单一权威数据源。
    /// 采用 msg_id 匹配请求-响应，支持超时与分片重组。
    ///
    /// 协议命令：
    ///   SD_QUERY / SD_QUERY_RESPONSE / SD_QUERY_PART   读取整张表
    ///   SD_SAVE / SD_SAVE_RESPONSE                     保存整张表（带乐观锁）
    ///   SD_QUERY_VERSION / SD_VERSION_RESPONSE         查询版本号
    ///   UPLOAD_FP_TEMPLATE / *_RESPONSE                上传指纹模板
    ///   DOWNLOAD_FP_TEMPLATE / *_RESPONSE              下载指纹模板
    ///   DELETE_FP_TEMPLATE / *_RESPONSE                删除指纹模板
    /// </summary>
    public class SdStorageService
    {
        /// <summary>默认请求超时（毫秒）</summary>
        private const int DefaultTimeoutMs = 8000;

        /// <summary>大表分片重组缓冲：msg_id -> (已收集分片, 总分片数)</summary>
        private readonly ConcurrentDictionary<string, FragmentBuffer> _fragments = new();

        /// <summary>待响应的请求：msg_id -> TaskCompletionSource</summary>
        private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();

        /// <summary>根节点设备 ID（SD 卡命令发往根节点）</summary>
        public string RootDeviceId { get; private set; } = "";

        /// <summary>null=旧固件未报告，true=就绪，false=已确认故障/未挂载。</summary>
        public bool? IsStorageReady { get; private set; }

        public string LastError { get; private set; } = "";

        public bool IsRootConnected => App.MeshBridge.IsConnected &&
            !string.IsNullOrEmpty(RootDeviceId);

        /// <summary>SD 卡是否可用（根节点在线且 SD 卡就绪）</summary>
        public bool IsAvailable => IsRootConnected && IsStorageReady != false;

        /// <summary>当前是否处于降级模式（SD 不可用、读写在本地缓存）</summary>
        public bool IsDegraded => !IsAvailable;

        public event Action? StatusChanged;

        /// <summary>SD 进入降级模式时触发（UI 可订阅以提示用户）</summary>
        public event Action? StorageDegraded;

        /// <summary>SD 从降级模式恢复时触发（App 可订阅以回传本地缓存到 SD）</summary>
        public event Action? StorageRecovered;

        public void RegisterRoot(string rootDeviceId, bool? storageReady)
        {
            RootDeviceId = rootDeviceId ?? "";
            if (storageReady.HasValue || IsStorageReady == null)
                IsStorageReady = storageReady;
            LastError = IsStorageReady == false ? "根节点 SD 卡未就绪" : "";
            UpdateDegradedState();
            StatusChanged?.Invoke();
        }

        /// <summary>链路断开时清理根节点定位和所有等待中的请求。</summary>
        public void HandleConnectionChanged(bool connected)
        {
            if (connected) return;

            RootDeviceId = "";
            IsStorageReady = null;
            LastError = "根节点物理链路已断开";
            _fragments.Clear();
            foreach (var pair in _pending)
            {
                if (_pending.TryRemove(pair.Key, out var pending))
                {
                    pending.Tcs.TrySetResult(null);
                }
            }
            UpdateDegradedState();
            StatusChanged?.Invoke();
        }

        /// <summary>根据 IsAvailable 变化触发 StorageDegraded / StorageRecovered 事件</summary>
        private void UpdateDegradedState()
        {
            bool degraded = !IsAvailable;
            // 用事件是否挂载判断“之前是否处于降级”——简单稳妥：直接看 IsAvailable
            // 这里通过比较私有字段记录的上一次状态来检测跳变
            if (degraded && !_wasDegraded)
            {
                _wasDegraded = true;
                try { StorageDegraded?.Invoke(); } catch { }
            }
            else if (!degraded && _wasDegraded)
            {
                _wasDegraded = false;
                try { StorageRecovered?.Invoke(); } catch { }
            }
        }

        private bool _wasDegraded;

        /// <summary>
        /// 处理收到的 SD 卡响应消息（由 MessageHandler 调用）
        /// </summary>
        public void HandleResponse(Message msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.MsgId)) return;

            // 分片消息：累积重组
            if (msg.Cmd == Protocol.CmdSdQueryPart)
            {
                HandleFragment(msg);
                return;
            }

            // 普通响应：匹配并完成 pending 请求
            if (_pending.TryRemove(msg.MsgId, out var pending))
            {
                _fragments.TryRemove(msg.MsgId, out _);
                try
                {
                    pending.Tcs.TrySetResult(msg);
                }
                catch
                {
                    // 忽略
                }
            }
        }

        // ====== 表读写 ======

        /// <summary>
        /// 查询 SD 卡整张表
        /// </summary>
        /// <param name="table">表名：users / classes / permissions / devices</param>
        /// <returns>表 JSON 字符串；失败返回 null</returns>
        public async Task<string?> QueryTableAsync(string table, int timeoutMs = DefaultTimeoutMs)
        {
            var snapshot = await QueryTableSnapshotAsync(table, timeoutMs);
            return snapshot?.Json;
        }

        /// <summary>查询表内容及根节点读取该内容时的版本号。</summary>
        public async Task<SdTableSnapshot?> QueryTableSnapshotAsync(
            string table, int timeoutMs = DefaultTimeoutMs)
        {
            var msg = Message.Create(Protocol.CmdSdQuery, RootDeviceId, new { table });
            var resp = await SendRequestAsync(msg, timeoutMs);
            if (resp?.Cmd == Protocol.CmdSdQueryResponse)
            {
                var data = resp.Data as JObject;
                var json = data?["json"]?.ToString(Formatting.None);
                if (string.IsNullOrWhiteSpace(json)) return null;
                return new SdTableSnapshot
                {
                    Table = data?["table"]?.ToString() ?? table,
                    Json = json,
                    Version = data?["version"]?.Value<uint>() ?? 0
                };
            }
            CaptureResponseError(resp);
            return null;
        }

        /// <summary>同步查询包装，供现有 WPF 页面使用；数据仍来自根节点 SD。</summary>
        public string? QueryTable(string table, int timeoutMs = DefaultTimeoutMs)
        {
            return QueryTableAsync(table, timeoutMs).GetAwaiter().GetResult();
        }

        public SdTableSnapshot? QueryTableSnapshot(string table, int timeoutMs = DefaultTimeoutMs)
        {
            return QueryTableSnapshotAsync(table, timeoutMs).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 保存整张表到 SD 卡（带乐观锁版本检测）
        /// </summary>
        /// <param name="table">表名</param>
        /// <param name="json">表 JSON 内容</param>
        /// <param name="baseVersion">读取该表时得到的基础版本号（包括初始版本 0）</param>
        /// <returns>成功返回 true；版本冲突返回 false</returns>
        public async Task<bool> SaveTableAsync(string table, string json, uint baseVersion = 0,
            int timeoutMs = DefaultTimeoutMs)
        {
            var msg = Message.Create(Protocol.CmdSdSave, RootDeviceId, new
            {
                table,
                json,
                base_version = baseVersion,
                enforce_version = true
            });
            var resp = await SendRequestAsync(msg, timeoutMs);
            if (resp?.Cmd == Protocol.CmdSdSaveResponse)
            {
                var data = resp.Data as JObject;
                string? result = data?["result"]?.ToString();
                return result == "success";
            }
            return false;
        }

        /// <summary>同步保存包装，供现有 WPF 页面使用。</summary>
        public bool SaveTable(string table, string json, uint baseVersion = 0,
            int timeoutMs = DefaultTimeoutMs)
        {
            return SaveTableAsync(table, json, baseVersion, timeoutMs).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 查询 SD 卡版本号信息
        /// </summary>
        /// <returns>版本信息对象；失败返回 null</returns>
        public async Task<SdVersionInfo?> QueryVersionAsync(int timeoutMs = DefaultTimeoutMs)
        {
            var msg = Message.Create(Protocol.CmdSdQueryVersion, RootDeviceId);
            var resp = await SendRequestAsync(msg, timeoutMs);
            if (resp?.Cmd == Protocol.CmdSdVersionResponse)
            {
                var data = resp.Data as JObject;
                if (data == null) return null;
                return new SdVersionInfo
                {
                    GlobalVersion = data["global_version"]?.Value<uint>() ?? 0,
                    UsersVersion = data["users_version"]?.Value<uint>() ?? 0,
                    ClassesVersion = data["classes_version"]?.Value<uint>() ?? 0,
                    PermissionsVersion = data["permissions_version"]?.Value<uint>() ?? 0,
                    DevicesVersion = data["devices_version"]?.Value<uint>() ?? 0,
                    FpVersion = data["fp_version"]?.Value<uint>() ?? 0,
                    LogsVersion = data["logs_version"]?.Value<uint>() ?? 0,
                    SdTotalBytes = data["sd_total_bytes"]?.Value<ulong>() ?? 0,
                    SdUsedBytes = data["sd_used_bytes"]?.Value<ulong>() ?? 0
                };
            }
            return null;
        }

        /// <summary>同步版本查询包装。</summary>
        public SdVersionInfo? QueryVersion(int timeoutMs = DefaultTimeoutMs)
        {
            return QueryVersionAsync(timeoutMs).GetAwaiter().GetResult();
        }

        // ====== 指纹模板 ======

        /// <summary>
        /// 上传指纹模板到 SD 卡
        /// </summary>
        /// <param name="userId">用户 ID</param>
        /// <param name="fingerIndex">模板序号（1 或 2）</param>
        /// <param name="templateBytes">模板二进制数据（512B）</param>
        public async Task<bool> UploadTemplateAsync(string userId, int fingerIndex,
            byte[] templateBytes, int timeoutMs = DefaultTimeoutMs)
        {
            string hex = BitConverter.ToString(templateBytes).Replace("-", "");
            var msg = Message.Create(Protocol.CmdUploadFpTemplate, RootDeviceId, new
            {
                user_id = userId,
                finger_index = fingerIndex,
                template_hex = hex
            });
            var resp = await SendRequestAsync(msg, timeoutMs);
            if (resp?.Cmd == Protocol.CmdFpTemplateUploadResponse)
            {
                var data = resp.Data as JObject;
                return data?["result"]?.ToString() == "success";
            }
            return false;
        }

        /// <summary>
        /// 从 SD 卡下载指纹模板
        /// </summary>
        /// <returns>模板二进制数据；失败返回 null</returns>
        public async Task<byte[]?> DownloadTemplateAsync(string userId, int fingerIndex,
            int timeoutMs = DefaultTimeoutMs)
        {
            var msg = Message.Create(Protocol.CmdDownloadFpTemplate, RootDeviceId, new
            {
                user_id = userId,
                finger_index = fingerIndex
            });
            var resp = await SendRequestAsync(msg, timeoutMs);
            if (resp?.Cmd == Protocol.CmdFpTemplateDownloadResponse)
            {
                var data = resp.Data as JObject;
                string? hex = data?["template_hex"]?.ToString();
                if (string.IsNullOrEmpty(hex)) return null;
                return HexToBytes(hex);
            }
            return null;
        }

        /// <summary>
        /// 删除用户在 SD 卡上的所有指纹模板
        /// </summary>
        public async Task<bool> DeleteTemplateAsync(string userId, int timeoutMs = DefaultTimeoutMs)
        {
            var msg = Message.Create(Protocol.CmdDeleteFpTemplate, RootDeviceId, new { user_id = userId });
            var resp = await SendRequestAsync(msg, timeoutMs);
            if (resp?.Cmd == Protocol.CmdFpTemplateDeleteResponse)
            {
                var data = resp.Data as JObject;
                return data?["result"]?.ToString() == "success";
            }
            return false;
        }

        // ====== 降级模式包装（SD 不可用时自动切换到本地缓存） ======

        /// <summary>
        /// 查询表快照（带降级）：SD 可用时优先 SD，失败或不可用时回落到本地缓存。
        /// </summary>
        public async Task<SdTableSnapshot?> QueryTableSnapshotWithFallbackAsync(
            string table, int timeoutMs = DefaultTimeoutMs)
        {
            if (IsAvailable)
            {
                var snap = await QueryTableSnapshotAsync(table, timeoutMs);
                if (snap != null && !string.IsNullOrWhiteSpace(snap.Json)) return snap;
            }

            // SD 不可用或读取失败：从本地缓存读
            var cached = LocalCacheService.ReadTable(table);
            if (cached == null) return null;
            return new SdTableSnapshot
            {
                Table = table,
                Json = cached.ToString(Formatting.None),
                Version = LocalCacheService.ReadTableVersion(table)
            };
        }

        /// <summary>同步包装</summary>
        public SdTableSnapshot? QueryTableSnapshotWithFallback(string table, int timeoutMs = DefaultTimeoutMs)
        {
            return QueryTableSnapshotWithFallbackAsync(table, timeoutMs).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 保存表（带降级）：SD 可用时同时写 SD 和本地缓存；
        /// SD 不可用或写失败时仅写本地缓存。
        /// </summary>
        public async Task<bool> SaveTableWithFallbackAsync(string table, string json,
            uint baseVersion = 0, int timeoutMs = DefaultTimeoutMs)
        {
            if (IsAvailable)
            {
                bool ok = await SaveTableAsync(table, json, baseVersion, timeoutMs);
                if (ok)
                {
                    try
                    {
                        var arr = JArray.Parse(json);
                        LocalCacheService.WriteTable(table, arr);
                        LocalCacheService.WriteTableVersion(table, baseVersion + 1);
                    }
                    catch { }
                    return true;
                }
            }

            // SD 不可用或保存失败：写本地缓存
            try
            {
                var arr = JArray.Parse(json);
                LocalCacheService.WriteTable(table, arr);
                uint v = baseVersion > 0 ? baseVersion + 1 : LocalCacheService.ReadTableVersion(table) + 1;
                LocalCacheService.WriteTableVersion(table, v);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>同步包装</summary>
        public bool SaveTableWithFallback(string table, string json, uint baseVersion = 0,
            int timeoutMs = DefaultTimeoutMs)
        {
            return SaveTableWithFallbackAsync(table, json, baseVersion, timeoutMs).GetAwaiter().GetResult();
        }

        /// <summary>
        /// 上传指纹模板（带降级）：SD 可用时优先上传 SD；
        /// SD 不可用或上传失败时保存到本地缓存。
        /// </summary>
        public async Task<bool> UploadFpTemplateWithFallbackAsync(string userId, int fingerIndex,
            byte[] templateBytes, int timeoutMs = DefaultTimeoutMs)
        {
            if (IsAvailable)
            {
                bool ok = await UploadTemplateAsync(userId, fingerIndex, templateBytes, timeoutMs);
                if (ok) return true;
            }

            // SD 不可用或上传失败：保存到本地
            try
            {
                LocalCacheService.SaveFpTemplate(userId, fingerIndex, templateBytes);
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ====== 内部实现 ======

        /// <summary>发送请求并等待响应</summary>
        private async Task<Message?> SendRequestAsync(Message msg, int timeoutMs)
        {
            if (!IsAvailable)
            {
                return null;
            }

            var tcs = new TaskCompletionSource<Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[msg.MsgId] = new PendingRequest { Tcs = tcs, Cmd = msg.Cmd };

            int attempts = IsReadOnlyRequest(msg.Cmd) ? 2 : 1;
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                if (!IsAvailable || !App.MeshBridge.SendToDevice(RootDeviceId, msg)) break;

                Task completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                if (completed == tcs.Task)
                {
                    return await tcs.Task;
                }
            }

            _pending.TryRemove(msg.MsgId, out _);
            _fragments.TryRemove(msg.MsgId, out _);
            tcs.TrySetResult(null);
            return null;
        }

        private void CaptureResponseError(Message? response)
        {
            if (response?.Cmd != Protocol.CmdError) return;
            var data = response.Data as JObject;
            string message = data?["message"]?.ToString() ?? "根节点返回错误";
            LastError = message.Equals("sd card not ready", StringComparison.OrdinalIgnoreCase)
                ? "根节点通讯正常，但 SD 卡未就绪"
                : message;
            if (message.Contains("sd card", StringComparison.OrdinalIgnoreCase))
                IsStorageReady = false;
            StatusChanged?.Invoke();
        }

        private static bool IsReadOnlyRequest(string cmd)
        {
            return cmd == Protocol.CmdSdQuery ||
                   cmd == Protocol.CmdSdQueryVersion ||
                   cmd == Protocol.CmdDownloadFpTemplate;
        }

        /// <summary>处理分片消息：累积重组，完成后触发 pending</summary>
        private void HandleFragment(Message msg)
        {
            var data = msg.Data as JObject;
            if (data == null) return;

            int part = data["part"]?.Value<int>() ?? 0;
            int total = data["total"]?.Value<int>() ?? 0;
            string? chunk = data["data"]?.ToString();
            if (part <= 0 || total <= 0 || total > 512 || part > total || chunk == null) return;
            if (!_pending.ContainsKey(msg.MsgId)) return;

            var buf = _fragments.GetOrAdd(msg.MsgId, _ => new FragmentBuffer { Total = total });
            lock (buf)
            {
                if (buf.Total != total)
                {
                    _fragments.TryRemove(msg.MsgId, out _);
                    return;
                }
                buf.Parts[part] = chunk;
                // 可选 PART_ACK：帮助根节点侧诊断丢片；根节点当前为 fire-and-forget 窗口发送。
                try
                {
                    if (!string.IsNullOrEmpty(RootDeviceId))
                    {
                        App.MeshBridge.Send(RootDeviceId, CmdIds.NameSdQueryPartAck,
                            new { part, total, msg_id = msg.MsgId });
                    }
                }
                catch { /* best-effort */ }

                if (buf.Parts.Count < buf.Total) return;  // 未收齐
            }

            // 全部分片收齐，重组
            _fragments.TryRemove(msg.MsgId, out _);
            var sb = new System.Text.StringBuilder();
            for (int i = 1; i <= buf.Total; i++)
            {
                if (buf.Parts.TryGetValue(i, out var p)) sb.Append(p);
            }
            string fullJson = sb.ToString();

            // 构造等效的 SD_QUERY_RESPONSE 消息
            Message? merged = null;
            try
            {
                merged = new Message
                {
                    MsgId = msg.MsgId,
                    Cmd = Protocol.CmdSdQueryResponse,
                    Data = JObject.Parse(fullJson)
                };
            }
            catch (JsonException)
            {
                // 完整分片不是有效 JSON，按请求失败处理。
            }

            if (_pending.TryRemove(msg.MsgId, out var pending))
            {
                pending.Tcs.TrySetResult(merged);
            }
        }

        /// <summary>hex 字符串转字节数组</summary>
        private static byte[] HexToBytes(string hex)
        {
            if (hex.Length % 2 != 0) return Array.Empty<byte>();
            byte[] bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            }
            return bytes;
        }

        // ====== 嵌套类型 ======

        private class PendingRequest
        {
            public TaskCompletionSource<Message?> Tcs { get; set; } = null!;
            public string Cmd { get; set; } = "";
        }

        private class FragmentBuffer
        {
            public int Total { get; set; }
            public Dictionary<int, string> Parts { get; } = new();
        }
    }

    /// <summary>SD 卡版本信息</summary>
    public class SdVersionInfo
    {
        public uint GlobalVersion { get; set; }
        public uint UsersVersion { get; set; }
        public uint ClassesVersion { get; set; }
        public uint PermissionsVersion { get; set; }
        public uint DevicesVersion { get; set; }
        public uint FpVersion { get; set; }
        public uint LogsVersion { get; set; }
        public ulong SdTotalBytes { get; set; }
        public ulong SdUsedBytes { get; set; }

        public override string ToString()
        {
            return $"全局版本={GlobalVersion}, 用户={UsersVersion}, 班级={ClassesVersion}, " +
                   $"权限={PermissionsVersion}, 设备={DevicesVersion}, 指纹={FpVersion}, " +
                   $"SD卡={SdUsedBytes / 1024 / 1024}MB/{SdTotalBytes / 1024 / 1024}MB";
        }
    }

    public class SdTableSnapshot
    {
        public string Table { get; set; } = "";
        public string Json { get; set; } = "";
        public uint Version { get; set; }
    }
}
