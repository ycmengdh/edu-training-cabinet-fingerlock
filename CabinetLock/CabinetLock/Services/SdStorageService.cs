using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CabinetLock
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
    ///   SD_SNAPSHOT_*                                  压缩业务快照流式传输
    ///   UPLOAD_FP_TEMPLATE / *_RESPONSE                上传指纹模板
    ///   DOWNLOAD_FP_TEMPLATE / *_RESPONSE              下载指纹模板
    ///   DELETE_FP_TEMPLATE / *_RESPONSE                删除指纹模板
    /// </summary>
    public class SdStorageService
    {
        /// <summary>默认请求超时（毫秒）</summary>
        private const int DefaultTimeoutMs = 8000;

        // Root firmware accepts at most 4000 payload bytes. Keep direct writes
        // comfortably below that boundary and stream larger tables in 2KB chunks.
        private const int DirectSavePayloadLimit = 3000;
        private const int UploadChunkSize = 2048;
        private const int SnapshotChunkSize = 3000;
        private const int SnapshotAckWindow = 4;
        private const byte SnapshotOperationBegin = 1;
        private const byte SnapshotOperationChunk = 2;
        private const byte SnapshotOperationCommit = 3;
        private const byte SnapshotStatusOk = 0;
        private const byte SnapshotStatusNotFound = 1;
        private const byte SnapshotStatusInvalid = 2;
        private const byte SnapshotStatusIoError = 3;
        private const byte SnapshotStatusHashMismatch = 4;
        private const byte SnapshotStatusOutOfOrder = 5;

        /// <summary>大表分片重组缓冲：msg_id -> (已收集分片, 总分片数)</summary>
        private readonly ConcurrentDictionary<string, FragmentBuffer> _fragments = new();

        /// <summary>待响应的请求：msg_id -> TaskCompletionSource</summary>
        private readonly ConcurrentDictionary<string, PendingRequest> _pending = new();
        private readonly ConcurrentDictionary<string, SnapshotDownloadBuffer> _snapshotDownloads = new();

        /// <summary>根节点设备 ID（SD 卡命令发往根节点）</summary>
        public string RootDeviceId { get; private set; } = "";

        /// <summary>null=旧固件未报告，true=就绪，false=已确认故障/未挂载。</summary>
        public bool? IsStorageReady { get; private set; }

        public string LastError { get; private set; } = "";

        public SdVersionInfo? LastVersion { get; private set; }

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
            foreach (var pair in _snapshotDownloads)
            {
                if (_snapshotDownloads.TryRemove(pair.Key, out var download))
                    download.Tcs.TrySetResult(null);
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

            if (msg.Cmd == Protocol.CmdSdSnapshotDownloadPart)
            {
                HandleSnapshotDownloadPart(msg);
                return;
            }

            if (msg.Cmd == Protocol.CmdError &&
                _snapshotDownloads.TryRemove(msg.MsgId, out var failedDownload))
            {
                CaptureResponseError(msg);
                failedDownload.Tcs.TrySetResult(null);
                return;
            }

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
            LastError = "";
            var msg = Message.Create(Protocol.CmdSdSave, RootDeviceId, new
            {
                table,
                json,
                base_version = baseVersion,
                enforce_version = true
            });

            int payloadLength = AppMessageMapper.ToApp(msg).Payload.Length;
            if (payloadLength > DirectSavePayloadLimit)
                return await SaveTableChunkedAsync(table, json, baseVersion, timeoutMs);

            var resp = await SendRequestAsync(msg, timeoutMs);
            return ParseSaveResponse(resp, table, baseVersion, finalPart: true);
        }

        private async Task<bool> SaveTableChunkedAsync(
            string table, string json, uint baseVersion, int timeoutMs)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            int partTotal = Math.Max(1, (bytes.Length + UploadChunkSize - 1) / UploadChunkSize);
            string uploadId = Guid.NewGuid().ToString("N");

            for (int partIndex = 0; partIndex < partTotal; partIndex++)
            {
                int offset = partIndex * UploadChunkSize;
                int count = Math.Min(UploadChunkSize, bytes.Length - offset);
                string chunkBase64 = Convert.ToBase64String(bytes, offset, count);
                var partMessage = Message.Create(Protocol.CmdSdSave, RootDeviceId, new
                {
                    table,
                    upload_id = uploadId,
                    part_index = partIndex,
                    part_total = partTotal,
                    total_bytes = bytes.Length,
                    chunk_base64 = chunkBase64,
                    base_version = baseVersion,
                    enforce_version = true
                });

                var response = await SendRequestAsync(partMessage, timeoutMs, attemptsOverride: 2);
                bool finalPart = partIndex == partTotal - 1;
                if (!ParseSaveResponse(response, table, baseVersion, finalPart))
                {
                    if (!string.IsNullOrWhiteSpace(LastError))
                        LastError += $"（分块 {partIndex + 1}/{partTotal}）";
                    return false;
                }
            }

            return true;
        }

        private bool ParseSaveResponse(
            Message? response, string table, uint baseVersion, bool finalPart)
        {
            if (response?.Cmd == Protocol.CmdSdSaveResponse)
            {
                var data = response.Data as JObject;
                string? result = data?["result"]?.ToString();
                if (result == "success" || (!finalPart && result == "part_ok"))
                    return true;

                string? error = data?["error"]?.ToString();
                if (string.Equals(error, "version_conflict", StringComparison.OrdinalIgnoreCase))
                {
                    uint current = data?["current_version"]?.Value<uint>() ?? 0;
                    LastError = $"表 {table} 版本冲突：本地基于 {baseVersion}，SD 当前 {current}";
                }
                else if (string.Equals(error, "out_of_order", StringComparison.OrdinalIgnoreCase))
                {
                    int expected = data?["expected_part"]?.Value<int>() ?? 0;
                    LastError = $"表 {table} 分块顺序异常，根节点等待第 {expected + 1} 块";
                }
                else
                {
                    string detail = data?["message"]?.ToString() ?? error ?? "根节点未确认写入";
                    LastError = $"表 {table} 保存失败：{detail}";
                }
                return false;
            }

            CaptureResponseError(response);
            if (string.IsNullOrWhiteSpace(LastError))
                LastError = $"表 {table} 保存超时，未收到根节点响应";
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
                var version = new SdVersionInfo
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
                LastVersion = version;
                return version;
            }
            return null;
        }

        /// <summary>同步版本查询包装。</summary>
        public SdVersionInfo? QueryVersion(int timeoutMs = DefaultTimeoutMs)
        {
            return QueryVersionAsync(timeoutMs).GetAwaiter().GetResult();
        }

        // ====== Compressed business snapshot ======

        public async Task<SnapshotTransferResult> UploadBusinessSnapshotAsync(
            BusinessSnapshot snapshot,
            IProgress<SnapshotTransferProgress>? progress = null,
            int timeoutMs = 10000,
            CancellationToken cancellationToken = default)
        {
            if (snapshot == null) throw new ArgumentNullException(nameof(snapshot));
            var manifest = await QueryBusinessSnapshotManifestAsync(timeoutMs)
                .ConfigureAwait(false);
            if (manifest.Unsupported)
                return SnapshotTransferResult.UnsupportedResult(LastError);
            if (manifest.Exists && manifest.Header != null &&
                BusinessSnapshotCodec.TryReadHeader(manifest.Header, out _, out _,
                    out byte[] remoteHash) &&
                System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                    remoteHash, snapshot.ContentSha256))
            {
                progress?.Report(new SnapshotTransferProgress(0,
                    snapshot.CompressedPayload.Length, "unchanged"));
                return new SnapshotTransferResult
                {
                    Success = true,
                    Unchanged = true,
                    TotalBytes = snapshot.CompressedPayload.Length
                };
            }

            LastError = "";
            SnapshotResponse begin = await BeginSnapshotAsync(snapshot, timeoutMs)
                .ConfigureAwait(false);
            if (!begin.Valid || begin.Status != SnapshotStatusOk)
                return SnapshotTransferResult.Failed(LastError, begin.Unsupported);

            uint offset = Math.Min(begin.NextOffset,
                (uint)snapshot.CompressedPayload.Length);
            int resumeAttempts = 0;
            while (offset < snapshot.CompressedPayload.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint groupStart = offset;
                SnapshotResponse acknowledgement = default;
                bool groupSent = true;
                for (int index = 0; index < SnapshotAckWindow &&
                     offset < snapshot.CompressedPayload.Length; index++)
                {
                    int length = Math.Min(SnapshotChunkSize,
                        snapshot.CompressedPayload.Length - (int)offset);
                    bool requestAck = index == SnapshotAckWindow - 1 ||
                        offset + length >= snapshot.CompressedPayload.Length;
                    byte[] payload = PackSnapshotChunk(snapshot.UploadId, offset,
                        snapshot.CompressedPayload.AsSpan((int)offset, length), requestAck);
                    var message = Message.Create(Protocol.CmdSdSnapshotChunk,
                        RootDeviceId, payload);
                    if (requestAck)
                    {
                        Message? response = await SendRequestAsync(message, timeoutMs,
                            attemptsOverride: 1).ConfigureAwait(false);
                        acknowledgement = ParseSnapshotResponse(response,
                            SnapshotOperationChunk);
                        if (!acknowledgement.Valid) groupSent = false;
                    }
                    else if (!App.MeshBridge.SendToDevice(RootDeviceId, message))
                    {
                        groupSent = false;
                        break;
                    }
                    offset += (uint)length;
                    if (requestAck) break;
                }

                if (!groupSent || acknowledgement.Status != SnapshotStatusOk)
                {
                    if (++resumeAttempts > 3)
                        return SnapshotTransferResult.Failed(
                            string.IsNullOrWhiteSpace(LastError)
                                ? "业务快照分块上传未收到确认"
                                : LastError);
                    begin = await BeginSnapshotAsync(snapshot, timeoutMs)
                        .ConfigureAwait(false);
                    if (!begin.Valid || begin.Status != SnapshotStatusOk)
                        return SnapshotTransferResult.Failed(LastError, begin.Unsupported);
                    offset = Math.Min(begin.NextOffset,
                        (uint)snapshot.CompressedPayload.Length);
                    if (offset < groupStart)
                        progress?.Report(new SnapshotTransferProgress(offset,
                            snapshot.CompressedPayload.Length, "resume"));
                    continue;
                }

                resumeAttempts = 0;
                offset = Math.Min(acknowledgement.NextOffset,
                    (uint)snapshot.CompressedPayload.Length);
                progress?.Report(new SnapshotTransferProgress(offset,
                    snapshot.CompressedPayload.Length, "upload"));
            }

            byte[] commitPayload = new byte[20];
            commitPayload[0] = BusinessSnapshotCodec.FormatVersion;
            snapshot.UploadId.CopyTo(commitPayload, 4);
            Message? commitMessage = await SendRequestAsync(
                Message.Create(Protocol.CmdSdSnapshotCommit, RootDeviceId, commitPayload),
                timeoutMs, attemptsOverride: 1).ConfigureAwait(false);
            SnapshotResponse commit = ParseSnapshotResponse(commitMessage,
                SnapshotOperationCommit);
            if (!commit.Valid || commit.Status != SnapshotStatusOk)
            {
                // A lost commit response is indistinguishable from a failed
                // commit on the wire. Re-read the promoted manifest before
                // reporting failure so an already durable snapshot succeeds.
                SnapshotManifestResult committedManifest =
                    await QueryBusinessSnapshotManifestAsync(timeoutMs)
                        .ConfigureAwait(false);
                bool committed = committedManifest.Exists &&
                    committedManifest.Header != null &&
                    BusinessSnapshotCodec.TryReadHeader(committedManifest.Header,
                        out _, out _, out byte[] committedHash) &&
                    System.Security.Cryptography.CryptographicOperations
                        .FixedTimeEquals(committedHash, snapshot.ContentSha256);
                if (!committed)
                {
                    return SnapshotTransferResult.Failed(
                        string.IsNullOrWhiteSpace(LastError)
                            ? "根节点未能校验并提交业务快照"
                            : LastError,
                        commit.Unsupported || committedManifest.Unsupported);
                }
            }

            LastError = "";
            progress?.Report(new SnapshotTransferProgress(
                snapshot.CompressedPayload.Length,
                snapshot.CompressedPayload.Length, "commit"));
            return new SnapshotTransferResult
            {
                Success = true,
                TransferredBytes = snapshot.CompressedPayload.Length,
                TotalBytes = snapshot.CompressedPayload.Length
            };
        }

        public async Task<SnapshotTransferResult> DownloadBusinessSnapshotAsync(
            IProgress<SnapshotTransferProgress>? progress = null,
            int timeoutMs = 15000,
            CancellationToken cancellationToken = default)
        {
            var manifest = await QueryBusinessSnapshotManifestAsync(timeoutMs)
                .ConfigureAwait(false);
            if (manifest.Unsupported)
                return SnapshotTransferResult.UnsupportedResult(LastError);
            if (!manifest.Exists || manifest.Header == null ||
                !BusinessSnapshotCodec.TryReadHeader(manifest.Header,
                    out uint compressedSize, out _, out _))
            {
                return new SnapshotTransferResult
                {
                    NotFound = true,
                    Error = "根节点尚无业务快照"
                };
            }

            int expectedSize = checked(BusinessSnapshotCodec.HeaderSize +
                (int)compressedSize);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = Message.Create(Protocol.CmdSdSnapshotDownload,
                    RootDeviceId, PackSnapshotDownloadRequest(0));
                var buffer = new SnapshotDownloadBuffer(expectedSize, progress);
                _snapshotDownloads[request.MsgId] = buffer;
                if (!App.MeshBridge.SendToDevice(RootDeviceId, request))
                {
                    _snapshotDownloads.TryRemove(request.MsgId, out _);
                    continue;
                }

                Task completed = await Task.WhenAny(buffer.Tcs.Task,
                    Task.Delay(timeoutMs, cancellationToken)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (completed == buffer.Tcs.Task)
                {
                    byte[]? container = await buffer.Tcs.Task.ConfigureAwait(false);
                    if (container != null)
                    {
                        return new SnapshotTransferResult
                        {
                            Success = true,
                            ContainerBytes = container,
                            TransferredBytes = container.Length,
                            TotalBytes = container.Length
                        };
                    }
                }
                _snapshotDownloads.TryRemove(request.MsgId, out _);
            }

            return SnapshotTransferResult.Failed(
                string.IsNullOrWhiteSpace(LastError)
                    ? "业务快照下载超时"
                    : LastError);
        }

        private async Task<SnapshotManifestResult> QueryBusinessSnapshotManifestAsync(
            int timeoutMs)
        {
            var request = Message.Create(Protocol.CmdSdSnapshotManifest,
                RootDeviceId, new byte[] { BusinessSnapshotCodec.FormatVersion });
            Message? response = await SendRequestAsync(request, timeoutMs,
                attemptsOverride: 1).ConfigureAwait(false);
            if (response?.Cmd == Protocol.CmdError)
            {
                CaptureResponseError(response);
                return new SnapshotManifestResult
                {
                    Unsupported = IsUnsupportedSnapshotError(response)
                };
            }
            if (response?.Cmd != Protocol.CmdSdSnapshotManifestResponse ||
                response.Data is not byte[] payload || payload.Length < 4 ||
                payload[0] != BusinessSnapshotCodec.FormatVersion)
            {
                LastError = "根节点未返回有效的业务快照清单";
                return new SnapshotManifestResult();
            }
            if (payload[1] == SnapshotStatusNotFound)
                return new SnapshotManifestResult { Exists = false };
            if (payload[1] != SnapshotStatusOk ||
                payload.Length != 4 + BusinessSnapshotCodec.HeaderSize)
            {
                LastError = "根节点业务快照清单无效";
                return new SnapshotManifestResult();
            }
            return new SnapshotManifestResult
            {
                Exists = true,
                Header = payload.AsSpan(4, BusinessSnapshotCodec.HeaderSize).ToArray()
            };
        }

        private async Task<SnapshotResponse> BeginSnapshotAsync(
            BusinessSnapshot snapshot, int timeoutMs)
        {
            Message? response = await SendRequestAsync(
                Message.Create(Protocol.CmdSdSnapshotBegin, RootDeviceId,
                    snapshot.Header), timeoutMs, attemptsOverride: 1)
                .ConfigureAwait(false);
            return ParseSnapshotResponse(response, SnapshotOperationBegin);
        }

        private SnapshotResponse ParseSnapshotResponse(
            Message? message, byte expectedOperation)
        {
            if (message?.Cmd == Protocol.CmdError)
            {
                CaptureResponseError(message);
                return new SnapshotResponse
                {
                    Unsupported = IsUnsupportedSnapshotError(message)
                };
            }
            if (message?.Cmd != Protocol.CmdSdSnapshotResponse ||
                message.Data is not byte[] data || data.Length < 28 ||
                data[0] != BusinessSnapshotCodec.FormatVersion ||
                data[1] != expectedOperation)
            {
                LastError = "根节点业务快照响应无效";
                return default;
            }
            byte status = data[2];
            uint nextOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4, 4));
            uint totalSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8, 4));
            if (status != SnapshotStatusOk)
                LastError = SnapshotStatusMessage(status);
            return new SnapshotResponse
            {
                Valid = true,
                Status = status,
                NextOffset = nextOffset,
                TotalSize = totalSize
            };
        }

        private static byte[] PackSnapshotChunk(byte[] uploadId, uint offset,
            ReadOnlySpan<byte> data, bool requestAck)
        {
            byte[] payload = new byte[24 + data.Length];
            payload[0] = BusinessSnapshotCodec.FormatVersion;
            payload[1] = requestAck ? (byte)1 : (byte)0;
            uploadId.CopyTo(payload, 4);
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(20, 4), offset);
            data.CopyTo(payload.AsSpan(24));
            return payload;
        }

        private static byte[] PackSnapshotDownloadRequest(uint offset)
        {
            byte[] payload = new byte[8];
            payload[0] = BusinessSnapshotCodec.FormatVersion;
            BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), offset);
            return payload;
        }

        private static bool IsUnsupportedSnapshotError(Message response)
        {
            if (response.Data is not JObject data) return false;
            int code = data["error_code"]?.Value<int>() ?? 0;
            return code is 9001 or 9002;
        }

        private static string SnapshotStatusMessage(byte status) => status switch
        {
            SnapshotStatusNotFound => "根节点业务快照不存在",
            SnapshotStatusInvalid => "根节点拒绝了无效的业务快照",
            SnapshotStatusIoError => "根节点写入业务快照失败",
            SnapshotStatusHashMismatch => "根节点校验业务快照 SHA-256 失败",
            SnapshotStatusOutOfOrder => "业务快照分块偏移不连续",
            _ => $"根节点业务快照错误：{status}"
        };

        private void HandleSnapshotDownloadPart(Message message)
        {
            if (!_snapshotDownloads.TryGetValue(message.MsgId, out var target) ||
                message.Data is not byte[] payload || payload.Length < 12 ||
                payload[0] != BusinessSnapshotCodec.FormatVersion)
                return;

            bool complete = false;
            bool failed = false;
            lock (target)
            {
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(4, 4));
                uint total = BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(8, 4));
                int dataLength = payload.Length - 12;
                if (total != target.Buffer.Length || offset > total ||
                    (uint)dataLength > total - offset)
                {
                    failed = true;
                }
                else if (offset == target.NextOffset)
                {
                    Buffer.BlockCopy(payload, 12, target.Buffer, (int)offset, dataLength);
                    target.NextOffset += (uint)dataLength;
                    target.Progress?.Report(new SnapshotTransferProgress(
                        target.NextOffset, target.Buffer.Length, "download"));
                }
                else if (offset + dataLength > target.NextOffset)
                {
                    failed = true;
                }

                bool last = (payload[1] & 1) != 0;
                complete = last && target.NextOffset == total;
                if (last && !complete) failed = true;
            }

            if (complete && _snapshotDownloads.TryRemove(message.MsgId, out target))
                target.Tcs.TrySetResult(target.Buffer);
            else if (failed && _snapshotDownloads.TryRemove(message.MsgId, out target))
                target.Tcs.TrySetResult(null);
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

        /// <summary>只删除用户的一枚指纹模板，不影响该用户其他手指。</summary>
        public async Task<bool> DeleteFingerTemplateAsync(
            string userId, int fingerIndex, int timeoutMs = DefaultTimeoutMs)
        {
            if (string.IsNullOrWhiteSpace(userId) || fingerIndex is < 1 or > 10) return false;
            if (!SupportsIndexedTemplateDelete()) return false;
            var msg = Message.Create(Protocol.CmdDeleteFpTemplate, RootDeviceId, new
            {
                user_id = userId,
                finger_index = fingerIndex
            });
            var resp = await SendRequestAsync(msg, timeoutMs);
            if (resp?.Cmd != Protocol.CmdFpTemplateDeleteResponse) return false;
            var data = resp.Data as JObject;
            return data?["result"]?.ToString() == "success";
        }

        private static bool SupportsIndexedTemplateDelete()
        {
            try
            {
                Device? root = App.DeviceService.GetAllDevices()
                    .FirstOrDefault(DeviceService.IsTrueRoot);
                string value = root?.FirmwareVersion ?? "";
                int suffix = value.IndexOf('-');
                if (suffix >= 0) value = value[..suffix];
                return Version.TryParse(value, out Version? version) &&
                    version >= new Version(2, 7, 2);
            }
            catch
            {
                return false;
            }
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
        private async Task<Message?> SendRequestAsync(
            Message msg, int timeoutMs, int? attemptsOverride = null)
        {
            if (!IsAvailable)
            {
                return null;
            }

            var tcs = new TaskCompletionSource<Message?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[msg.MsgId] = new PendingRequest { Tcs = tcs, Cmd = msg.Cmd };

            int attempts = attemptsOverride ?? (IsReadOnlyRequest(msg.Cmd) ? 2 : 1);
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

        private sealed class SnapshotDownloadBuffer
        {
            public SnapshotDownloadBuffer(int size,
                IProgress<SnapshotTransferProgress>? progress)
            {
                Buffer = new byte[size];
                Progress = progress;
            }

            public byte[] Buffer { get; }
            public uint NextOffset { get; set; }
            public IProgress<SnapshotTransferProgress>? Progress { get; }
            public TaskCompletionSource<byte[]?> Tcs { get; } =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        private sealed class SnapshotManifestResult
        {
            public bool Exists { get; set; }
            public bool Unsupported { get; set; }
            public byte[]? Header { get; set; }
        }

        private struct SnapshotResponse
        {
            public bool Valid { get; set; }
            public bool Unsupported { get; set; }
            public byte Status { get; set; }
            public uint NextOffset { get; set; }
            public uint TotalSize { get; set; }
        }
    }

    public sealed class SnapshotTransferProgress
    {
        public SnapshotTransferProgress(long transferredBytes, long totalBytes,
            string phase)
        {
            TransferredBytes = transferredBytes;
            TotalBytes = totalBytes;
            Phase = phase ?? "";
        }

        public long TransferredBytes { get; }
        public long TotalBytes { get; }
        public string Phase { get; }
        public int Percent => TotalBytes <= 0 ? 0 :
            (int)Math.Clamp(TransferredBytes * 100 / TotalBytes, 0, 100);
    }

    public sealed class SnapshotTransferResult
    {
        public bool Success { get; set; }
        public bool Unsupported { get; set; }
        public bool NotFound { get; set; }
        public bool Unchanged { get; set; }
        public long TransferredBytes { get; set; }
        public long TotalBytes { get; set; }
        public byte[]? ContainerBytes { get; set; }
        public string Error { get; set; } = "";

        internal static SnapshotTransferResult Failed(string? error,
            bool unsupported = false) => new()
        {
            Unsupported = unsupported,
            Error = error ?? ""
        };

        internal static SnapshotTransferResult UnsupportedResult(string? error) =>
            Failed(error, unsupported: true);
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

        public void AdvanceAfterSuccessfulSave(string table)
        {
            GlobalVersion++;
            switch (table)
            {
                case "users":
                    UsersVersion++;
                    PermissionsVersion++;
                    break;
                case "classes":
                    ClassesVersion++;
                    break;
                case "permissions":
                case "role_permissions":
                    PermissionsVersion++;
                    break;
                case "devices":
                    DevicesVersion++;
                    break;
                case "fingerprints":
                case "fp":
                    FpVersion++;
                    break;
                case "logs":
                    LogsVersion++;
                    break;
            }
        }

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
