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
        public string RootDeviceId { get; set; } = "";

        /// <summary>SD 卡是否可用（根节点在线且 SD 卡就绪）</summary>
        public bool IsAvailable => App.MeshBridge.IsConnected && !string.IsNullOrEmpty(RootDeviceId);

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
            var msg = Message.Create(Protocol.CmdSdQuery, RootDeviceId, new { table });
            var resp = await SendRequestAsync(msg, timeoutMs);
            if (resp?.Cmd == Protocol.CmdSdQueryResponse)
            {
                // data: {table, json:...}
                var data = resp.Data as JObject;
                return data?["json"]?.ToString(Formatting.None);
            }
            return null;
        }

        /// <summary>
        /// 保存整张表到 SD 卡（带乐观锁版本检测）
        /// </summary>
        /// <param name="table">表名</param>
        /// <param name="json">表 JSON 内容</param>
        /// <param name="baseVersion">基础版本号（乐观锁），0 表示不检测</param>
        /// <returns>成功返回 true；版本冲突返回 false</returns>
        public async Task<bool> SaveTableAsync(string table, string json, uint baseVersion = 0,
            int timeoutMs = DefaultTimeoutMs)
        {
            var msg = Message.Create(Protocol.CmdSdSave, RootDeviceId, new
            {
                table,
                json,
                base_version = baseVersion
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
                    SdTotalBytes = data["sd_total_bytes"]?.Value<ulong>() ?? 0,
                    SdUsedBytes = data["sd_used_bytes"]?.Value<ulong>() ?? 0
                };
            }
            return null;
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

        // ====== 内部实现 ======

        /// <summary>发送请求并等待响应</summary>
        private async Task<Message?> SendRequestAsync(Message msg, int timeoutMs)
        {
            if (!IsAvailable)
            {
                return null;
            }

            var tcs = new TaskCompletionSource<Message>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[msg.MsgId] = new PendingRequest { Tcs = tcs, Cmd = msg.Cmd };

            bool sent = App.MeshBridge.SendToDevice(RootDeviceId, msg);
            if (!sent)
            {
                _pending.TryRemove(msg.MsgId, out _);
                return null;
            }

            // 超时处理
            using var cts = new CancellationTokenSource(timeoutMs);
            cts.Token.Register(() =>
            {
                _pending.TryRemove(msg.MsgId, out _);
                tcs.TrySetResult(null!);
            });

            return await tcs.Task;
        }

        /// <summary>处理分片消息：累积重组，完成后触发 pending</summary>
        private void HandleFragment(Message msg)
        {
            var data = msg.Data as JObject;
            if (data == null) return;

            int part = data["part"]?.Value<int>() ?? 0;
            int total = data["total"]?.Value<int>() ?? 0;
            string? chunk = data["data"]?.ToString();
            if (part <= 0 || total <= 0 || chunk == null) return;

            var buf = _fragments.GetOrAdd(msg.MsgId, _ => new FragmentBuffer { Total = total });
            lock (buf)
            {
                buf.Parts[part] = chunk;
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
            var merged = new Message
            {
                MsgId = msg.MsgId,
                Cmd = Protocol.CmdSdQueryResponse,
                Data = JObject.Parse(fullJson)
            };

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
            public TaskCompletionSource<Message> Tcs { get; set; } = null!;
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
        public ulong SdTotalBytes { get; set; }
        public ulong SdUsedBytes { get; set; }

        public override string ToString()
        {
            return $"全局版本={GlobalVersion}, 用户={UsersVersion}, 班级={ClassesVersion}, " +
                   $"权限={PermissionsVersion}, 设备={DevicesVersion}, 指纹={FpVersion}, " +
                   $"SD卡={SdUsedBytes / 1024 / 1024}MB/{SdTotalBytes / 1024 / 1024}MB";
        }
    }
}
