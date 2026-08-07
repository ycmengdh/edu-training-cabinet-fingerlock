using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace CabinetLock
{
    public sealed class CabinetFirmwareInfo
    {
        public string FilePath { get; init; } = "";
        public string ProjectName { get; init; } = "";
        public string Version { get; init; } = "";
        public string HardwareVersion { get; init; } = "";
        public string Sha256 { get; init; } = "";
        public int ImageSize { get; init; }
    }

    public sealed class CabinetOtaStatus
    {
        public string Operation { get; init; } = "";
        public string Phase { get; init; } = "";
        public string UploadId { get; init; } = "";
        public string Version { get; init; } = "";
        public string HardwareVersion { get; init; } = "";
        public string Sha256 { get; init; } = "";
        public string Error { get; init; } = "";
        public uint ImageSize { get; init; }
        public uint ReceivedBytes { get; init; }
        public uint NextOffset { get; init; }
        public uint ExpectedNodes { get; init; }
        public uint CompletedNodes { get; init; }
        public int MeshProgress { get; init; }
        public int FinishReason { get; init; } = -1;
        public bool Active { get; init; }
        public uint KnownNodes { get; init; }
        public uint CompatibleNodes { get; init; }
        public uint PendingNodes { get; init; }
        public uint IncompatibleNodes { get; init; }
        public uint UnknownHardwareNodes { get; init; }
        public long PublishedAt { get; init; }
    }

    public sealed class CabinetOtaProgress
    {
        public string Stage { get; init; } = "";
        public string Detail { get; init; } = "";
        public int Percent { get; init; }
        public uint CompletedNodes { get; init; }
        public uint ExpectedNodes { get; init; }
        public long BytesTransferred { get; init; }
        public long TotalBytes { get; init; }
        public double BytesPerSecond { get; init; }
        public TimeSpan? EstimatedRemaining { get; init; }
        public bool IsImportant { get; init; }
    }

    public sealed class CabinetOtaNodeStatus
    {
        public string DeviceId { get; init; } = "";
        public string ParentDeviceId { get; init; } = "";
        public string Version { get; init; } = "";
        public string Phase { get; init; } = "";
        public string Error { get; init; } = "";
        public int MeshLayer { get; init; }
        public int Progress { get; init; }
        public int RetryCount { get; init; }
        public uint UpdatedAgoSeconds { get; init; }
        public bool Online { get; init; }
        public bool Compatible { get; init; }
    }

    public sealed class CabinetOtaNodePage
    {
        public int Offset { get; init; }
        public int Total { get; init; }
        public IReadOnlyList<CabinetOtaNodeStatus> Nodes { get; init; } =
            Array.Empty<CabinetOtaNodeStatus>();
    }

    public sealed class CabinetOtaSnapshot
    {
        public CabinetOtaStatus Status { get; init; } = new();
        public IReadOnlyList<CabinetOtaNodeStatus> Nodes { get; init; } =
            Array.Empty<CabinetOtaNodeStatus>();
    }

    public sealed class CabinetOtaService
    {
        // 2880 bytes becomes 3840 Base64 bytes. Including JSON metadata it
        // remains below the firmware's 4000-byte application payload limit.
        public const int UploadChunkSize = 2880;
        private const int MinimumImageSize = 128 * 1024;
        private const int MaximumImageSize = 0x300000;
        private const int RequestTimeoutMs = 8000;
        private const int CommitTimeoutMs = 20000;

        private readonly ConcurrentDictionary<string, TaskCompletionSource<Message>> _pending = new();
        private readonly SemaphoreSlim _deploymentLock = new(1, 1);

        public bool IsBusy => _deploymentLock.CurrentCount == 0;

        public CabinetFirmwareInfo InspectFirmware(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                throw new FileNotFoundException("固件文件不存在", filePath);

            byte[] image = File.ReadAllBytes(filePath);
            return InspectFirmware(image, Path.GetFullPath(filePath));
        }

        public static CabinetFirmwareInfo InspectFirmware(byte[] image, string filePath = "")
        {
            ArgumentNullException.ThrowIfNull(image);
            if (image.Length < MinimumImageSize || image.Length > MaximumImageSize)
                throw new InvalidDataException("固件大小不在柜机 OTA 分区允许范围内");
            if (image[0] != 0xE9)
                throw new InvalidDataException("不是有效的 ESP-IDF 应用镜像");

            ushort chipId = (ushort)(image[12] | (image[13] << 8));
            if (chipId != 0x0009)
                throw new InvalidDataException("固件目标芯片不是 ESP32-S3");

            uint descriptorMagic = (uint)(image[32] | (image[33] << 8) |
                (image[34] << 16) | (image[35] << 24));
            if (descriptorMagic != 0xABCD5432)
                throw new InvalidDataException("ESP-IDF 应用描述信息无效");

            string version = ReadCString(image, 48, 32);
            string projectName = ReadCString(image, 80, 32);
            if (!string.Equals(projectName, "cabinet_node_idf", StringComparison.Ordinal))
                throw new InvalidDataException("所选镜像不是 cabinet_node_idf 柜机固件");
            if (string.IsNullOrWhiteSpace(version))
                throw new InvalidDataException("固件版本号为空");

            return new CabinetFirmwareInfo
            {
                FilePath = filePath,
                ProjectName = projectName,
                Version = version,
                HardwareVersion = "cabinet-v1",
                ImageSize = image.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant()
            };
        }

        public void HandleResponse(Message msg)
        {
            if (msg == null || string.IsNullOrEmpty(msg.MsgId)) return;
            if (msg.CorrId != 0 && msg.CorrId != AppMessageMapper.SessionId) return;
            if (_pending.TryRemove(msg.MsgId, out var pending))
                pending.TrySetResult(msg);
        }

        public async Task<CabinetOtaStatus> DeployAsync(
            string filePath, bool restrictHardware,
            IProgress<CabinetOtaProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            await _deploymentLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                progress?.Report(new CabinetOtaProgress
                {
                    Stage = "校验固件",
                    Detail = "正在读取镜像",
                    Percent = 0
                });
                byte[] image = await File.ReadAllBytesAsync(filePath, cancellationToken)
                    .ConfigureAwait(false);
                CabinetFirmwareInfo firmware = InspectFirmware(image, Path.GetFullPath(filePath));
                string targetHardware = restrictHardware
                    ? firmware.HardwareVersion : "";
                EnsureRootAvailable();

                CabinetOtaStatus? stagedStatus = null;
                try
                {
                    stagedStatus = await SendOtaRequestAsync(
                        Protocol.CmdCabinetOtaStatus, new { }, RequestTimeoutMs, 1,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                }

                CabinetOtaStatus status;
                if (CanReuseStagedImage(stagedStatus, firmware, targetHardware))
                {
                    status = stagedStatus!;
                    progress?.Report(new CabinetOtaProgress
                    {
                        Stage = "复用根节点镜像",
                        Detail = "版本、大小和 SHA-256 一致，跳过串口上传",
                        Percent = 50,
                        BytesTransferred = image.Length,
                        TotalBytes = image.Length,
                        IsImportant = true
                    });
                }
                else
                {
                    string uploadId = Guid.NewGuid().ToString("N");
                    status = await SendOtaRequestAsync(
                        Protocol.CmdCabinetOtaBegin,
                        new
                        {
                            upload_id = uploadId,
                            version = firmware.Version,
                            hardware_version = targetHardware,
                            sha256 = firmware.Sha256,
                            image_size = firmware.ImageSize,
                            published_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
                        }, RequestTimeoutMs, 3, cancellationToken).ConfigureAwait(false);

                    int offset = 0;
                    var uploadTimer = Stopwatch.StartNew();
                    while (offset < image.Length)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int length = Math.Min(UploadChunkSize, image.Length - offset);
                        string chunk = Convert.ToBase64String(image, offset, length);
                        status = await SendOtaRequestAsync(
                            Protocol.CmdCabinetOtaChunk,
                            new { upload_id = uploadId, offset, chunk_base64 = chunk },
                            RequestTimeoutMs, 3, cancellationToken,
                            retryAttempt => progress?.Report(new CabinetOtaProgress
                            {
                                Stage = "上传重试",
                                Detail = $"偏移 {offset:N0} 字节，第 {retryAttempt} 次重试",
                                Percent = Math.Clamp(offset * 50 / image.Length, 0, 50),
                                BytesTransferred = offset,
                                TotalBytes = image.Length,
                                IsImportant = true
                            })).ConfigureAwait(false);

                        uint expectedOffset = (uint)(offset + length);
                        if (status.NextOffset < expectedOffset || status.NextOffset > image.Length)
                            throw new InvalidDataException("根节点返回了无效的固件上传偏移");
                        offset = (int)status.NextOffset;
                        double seconds = Math.Max(uploadTimer.Elapsed.TotalSeconds, 0.001);
                        double bytesPerSecond = offset / seconds;
                        TimeSpan? remaining = bytesPerSecond > 0
                            ? TimeSpan.FromSeconds((image.Length - offset) / bytesPerSecond)
                            : null;
                        progress?.Report(new CabinetOtaProgress
                        {
                            Stage = "上传到根节点",
                            Detail = FormatUploadDetail(offset, image.Length,
                                bytesPerSecond, remaining),
                            Percent = Math.Clamp(offset * 50 / image.Length, 1, 50),
                            BytesTransferred = offset,
                            TotalBytes = image.Length,
                            BytesPerSecond = bytesPerSecond,
                            EstimatedRemaining = remaining
                        });
                    }

                    progress?.Report(new CabinetOtaProgress
                    {
                        Stage = "校验镜像",
                        Detail = "根节点正在核对 SHA-256",
                        Percent = 50
                    });
                    status = await SendOtaRequestAsync(
                        Protocol.CmdCabinetOtaCommit,
                        new { upload_id = uploadId }, CommitTimeoutMs, 2,
                        cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    status = await SendOtaRequestAsync(
                        Protocol.CmdCabinetOtaStart,
                        new { }, RequestTimeoutMs, 2,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception startError) when (startError is not OperationCanceledException)
                {
                    string rootDetail = "";
                    CabinetOtaStatus? failedStatus = null;
                    try
                    {
                        failedStatus = await SendOtaRequestAsync(
                            Protocol.CmdCabinetOtaStatus, new { }, RequestTimeoutMs, 1,
                            cancellationToken).ConfigureAwait(false);
                        rootDetail = failedStatus.Error;
                    }
                    catch
                    {
                    }

                    if (failedStatus != null &&
                        (failedStatus.Active || string.Equals(
                            failedStatus.Phase, "published",
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        status = failedStatus;
                        progress?.Report(new CabinetOtaProgress
                        {
                            Stage = "等待自动分发",
                            Detail = string.IsNullOrWhiteSpace(rootDetail)
                                ? "目标版本已保存，根节点将在柜机注册后自动重试"
                                : $"目标版本已保存，根节点将自动重试：{rootDetail}",
                            Percent = 100,
                            IsImportant = true
                        });
                    }
                    else
                    {
                        if (!string.IsNullOrWhiteSpace(rootDetail) &&
                            !startError.Message.Contains(rootDetail,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidOperationException(
                                $"{startError.Message}；根节点详情：{rootDetail}", startError);
                        }
                        throw;
                    }
                }

                progress?.Report(new CabinetOtaProgress
                {
                    Stage = "发布完成",
                    Detail = status.PendingNodes > 0
                        ? $"目标版本已保存，{status.PendingNodes} 台在线柜机正在升级"
                        : "目标版本已保存，后续接入的兼容柜机会自动升级",
                    Percent = 100,
                    CompletedNodes = status.CompletedNodes,
                    ExpectedNodes = status.CompatibleNodes,
                    IsImportant = true
                });
                return status;
            }
            finally
            {
                _deploymentLock.Release();
            }
        }

        public async Task<CabinetOtaStatus> QueryStatusAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureRootAvailable();
            return await SendOtaRequestAsync(
                Protocol.CmdCabinetOtaStatus, new { }, RequestTimeoutMs, 2,
                cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<CabinetOtaNodeStatus>> QueryNodesAsync(
            CancellationToken cancellationToken = default)
        {
            EnsureRootAvailable();
            const int pageSize = 10;
            const int maximumNodes = 100;
            var nodes = new List<CabinetOtaNodeStatus>();
            int offset = 0;
            int total;
            do
            {
                Message response = await SendRequestAsync(
                    Protocol.CmdCabinetOtaNodes,
                    new { offset, limit = pageSize }, RequestTimeoutMs, 2,
                    cancellationToken).ConfigureAwait(false);
                CabinetOtaNodePage page = ParseNodesResponse(response);
                if (page.Offset != offset)
                    throw new InvalidDataException("根节点返回了无效的 OTA 设备分页偏移");
                if (page.Total < 0 || page.Total > maximumNodes)
                    throw new InvalidDataException("根节点返回的 OTA 设备总数无效");

                nodes.AddRange(page.Nodes);
                total = page.Total;
                if (page.Nodes.Count == 0) break;
                offset += page.Nodes.Count;
            }
            while (offset < total);

            return nodes;
        }

        public async Task<CabinetOtaSnapshot> QuerySnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            CabinetOtaStatus status = await QueryStatusAsync(cancellationToken)
                .ConfigureAwait(false);
            IReadOnlyList<CabinetOtaNodeStatus> nodes =
                await QueryNodesAsync(cancellationToken).ConfigureAwait(false);
            return new CabinetOtaSnapshot { Status = status, Nodes = nodes };
        }

        private async Task<CabinetOtaStatus> SendOtaRequestAsync(
            string command, object data, int timeoutMs, int attempts,
            CancellationToken cancellationToken, Action<int>? retrying = null)
        {
            Message response = await SendRequestAsync(
                command, data, timeoutMs, attempts, cancellationToken, retrying)
                .ConfigureAwait(false);
            return ParseResponse(response);
        }

        private async Task<Message> SendRequestAsync(
            string command, object data, int timeoutMs, int attempts,
            CancellationToken cancellationToken, Action<int>? retrying = null)
        {
            EnsureRootAvailable();
            string rootId = App.SdStorageService.RootDeviceId;
            Message request = Message.Create(command, rootId, data);
            var responseSource = new TaskCompletionSource<Message>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_pending.TryAdd(request.MsgId, responseSource))
                throw new InvalidOperationException("OTA 请求消息号冲突");

            try
            {
                for (int attempt = 0; attempt < attempts; attempt++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!App.MeshBridge.SendToDevice(rootId, request))
                    {
                        if (attempt + 1 == attempts)
                            throw new IOException("向根节点发送 OTA 请求失败");
                    }
                    else
                    {
                        Task delay = Task.Delay(timeoutMs, cancellationToken);
                        Task completed = await Task.WhenAny(responseSource.Task, delay)
                            .ConfigureAwait(false);
                        if (completed == responseSource.Task)
                            return await responseSource.Task.ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
                    if (attempt + 1 < attempts)
                        retrying?.Invoke(attempt + 1);
                }
                if (responseSource.Task.IsCompleted)
                    return await responseSource.Task.ConfigureAwait(false);
                throw new TimeoutException("根节点 OTA 请求响应超时");
            }
            finally
            {
                _pending.TryRemove(request.MsgId, out _);
            }
        }

        private static CabinetOtaStatus ParseResponse(Message response)
        {
            JObject? data = response.Data as JObject;
            if (string.Equals(response.Cmd, Protocol.CmdError,
                              StringComparison.OrdinalIgnoreCase))
            {
                string code = data?["error_code"]?.ToString() ?? "";
                string message = data?["message"]?.ToString() ?? "根节点 OTA 处理失败";
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(code) ? message : $"{message}（{code}）");
            }
            if (!string.Equals(response.Cmd, Protocol.CmdCabinetOtaResponse,
                               StringComparison.OrdinalIgnoreCase) || data == null)
                throw new InvalidDataException("根节点 OTA 响应格式无效");

            string result = data["result"]?.ToString() ?? "";
            string error = data["error"]?.ToString() ?? "";
            if (!result.Equals("ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(error) ? "根节点 OTA 操作失败" : error);

            return new CabinetOtaStatus
            {
                Operation = data["operation"]?.ToString() ?? "",
                Phase = data["phase"]?.ToString() ?? "",
                UploadId = data["upload_id"]?.ToString() ?? "",
                Version = data["version"]?.ToString() ?? "",
                HardwareVersion = data["hardware_version"]?.ToString() ?? "",
                Sha256 = data["sha256"]?.ToString() ?? "",
                Error = error,
                ImageSize = data["image_size"]?.Value<uint>() ?? 0,
                ReceivedBytes = data["received_bytes"]?.Value<uint>() ?? 0,
                NextOffset = data["next_offset"]?.Value<uint>() ?? 0,
                ExpectedNodes = data["expected_nodes"]?.Value<uint>() ?? 0,
                CompletedNodes = data["completed_nodes"]?.Value<uint>() ?? 0,
                MeshProgress = data["mesh_progress"]?.Value<int>() ?? 0,
                FinishReason = data["finish_reason"]?.Value<int>() ?? -1,
                Active = data["active"]?.Value<bool>() ?? false,
                KnownNodes = data["known_nodes"]?.Value<uint>() ?? 0,
                CompatibleNodes = data["compatible_nodes"]?.Value<uint>() ?? 0,
                PendingNodes = data["pending_nodes"]?.Value<uint>() ?? 0,
                IncompatibleNodes = data["incompatible_nodes"]?.Value<uint>() ?? 0,
                UnknownHardwareNodes = data["unknown_hardware_nodes"]?.Value<uint>() ?? 0,
                PublishedAt = data["published_at"]?.Value<long>() ?? 0
            };
        }

        public static CabinetOtaNodePage ParseNodesResponse(Message response)
        {
            ArgumentNullException.ThrowIfNull(response);
            JObject? data = response.Data as JObject;
            ThrowIfErrorResponse(response, data);
            if (!string.Equals(response.Cmd, Protocol.CmdCabinetOtaNodesResponse,
                               StringComparison.OrdinalIgnoreCase) || data == null)
                throw new InvalidDataException("根节点 OTA 设备响应格式无效");

            int offset = data["offset"]?.Value<int>() ?? -1;
            int total = data["total"]?.Value<int>() ?? -1;
            JArray? items = data["nodes"] as JArray;
            if (offset < 0 || total < 0 || items == null)
                throw new InvalidDataException("根节点 OTA 设备分页字段无效");

            var nodes = new List<CabinetOtaNodeStatus>(items.Count);
            foreach (JToken token in items)
            {
                if (token is not JObject item)
                    throw new InvalidDataException("根节点 OTA 设备条目格式无效");
                string deviceId = item["device_id"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(deviceId))
                    throw new InvalidDataException("根节点 OTA 设备编号为空");
                nodes.Add(new CabinetOtaNodeStatus
                {
                    DeviceId = deviceId,
                    ParentDeviceId = item["parent_device_id"]?.ToString() ?? "",
                    Version = item["version"]?.ToString() ?? "",
                    Phase = item["phase"]?.ToString() ?? "",
                    Error = item["error"]?.ToString() ?? "",
                    MeshLayer = item["mesh_layer"]?.Value<int>() ?? 0,
                    Progress = Math.Clamp(item["progress"]?.Value<int>() ?? 0, 0, 100),
                    RetryCount = Math.Max(item["retry_count"]?.Value<int>() ?? 0, 0),
                    UpdatedAgoSeconds = item["updated_ago"]?.Value<uint>() ?? 0,
                    Online = item["online"]?.Value<bool>() ?? false,
                    Compatible = item["compatible"]?.Value<bool>() ?? false
                });
            }
            if (offset + nodes.Count > total)
                throw new InvalidDataException("根节点 OTA 设备分页数量无效");

            return new CabinetOtaNodePage
            {
                Offset = offset,
                Total = total,
                Nodes = nodes
            };
        }

        private static void ThrowIfErrorResponse(Message response, JObject? data)
        {
            if (!string.Equals(response.Cmd, Protocol.CmdError,
                               StringComparison.OrdinalIgnoreCase)) return;
            string code = data?["error_code"]?.ToString() ?? "";
            string message = data?["message"]?.ToString() ?? "根节点 OTA 处理失败";
            throw new InvalidOperationException(
                string.IsNullOrEmpty(code) ? message : $"{message}（{code}）");
        }

        private static string ReadCString(byte[] data, int offset, int length)
        {
            int end = offset;
            int limit = Math.Min(data.Length, offset + length);
            while (end < limit && data[end] != 0) end++;
            return Encoding.UTF8.GetString(data, offset, end - offset).Trim();
        }

        private static string FormatUploadDetail(
            long transferred, long total, double bytesPerSecond,
            TimeSpan? remaining)
        {
            string speed = bytesPerSecond >= 1024
                ? $"{bytesPerSecond / 1024:N1} KB/s"
                : $"{bytesPerSecond:N0} B/s";
            string eta = remaining.HasValue
                ? remaining.Value.TotalMinutes >= 1
                    ? $"约 {Math.Ceiling(remaining.Value.TotalMinutes):N0} 分钟"
                    : $"约 {Math.Max(1, Math.Ceiling(remaining.Value.TotalSeconds)):N0} 秒"
                : "正在估算";
            return $"{transferred:N0} / {total:N0} 字节 · {speed} · 剩余 {eta}";
        }

        public static bool CanReuseStagedImage(
            CabinetOtaStatus? status, CabinetFirmwareInfo firmware,
            string hardwareVersion = "")
        {
            if (status == null) return false;
            bool reusablePhase =
                string.Equals(status.Phase, "ready", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status.Phase, "complete", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status.Phase, "published", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(status.Phase, "distributing", StringComparison.OrdinalIgnoreCase);
            return reusablePhase &&
                string.Equals(status.Version, firmware.Version,
                    StringComparison.Ordinal) &&
                string.Equals(status.Sha256, firmware.Sha256,
                    StringComparison.OrdinalIgnoreCase) &&
                status.ImageSize == firmware.ImageSize &&
                status.ReceivedBytes == firmware.ImageSize &&
                string.Equals(status.HardwareVersion, hardwareVersion,
                    StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureRootAvailable()
        {
            if (!App.MeshBridge.IsConnected ||
                string.IsNullOrWhiteSpace(App.SdStorageService.RootDeviceId))
                throw new InvalidOperationException("根节点未连接");
            if (App.SdStorageService.IsStorageReady == false)
                throw new InvalidOperationException("根节点 SD 卡未就绪");
        }
    }
}
