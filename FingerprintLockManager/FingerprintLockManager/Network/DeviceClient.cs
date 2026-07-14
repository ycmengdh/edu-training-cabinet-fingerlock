namespace FingerprintLockManager
{
    /// <summary>
    /// 逻辑设备表示（由 MeshBridge 按 device_id 维护）
    /// 在 Mesh 拓扑下，所有设备通讯均经 Root 转发，DeviceClient 不再直接持有 TCP 连接，
    /// 仅作为设备状态句柄（DeviceId/名称/在线状态/最后活跃时间），发送通过注入的回调经由 ITransport 发往 Root。
    /// </summary>
    public class DeviceClient
    {
        /// <summary>设备 ID（设备注册后由消息同步，或上层手动设置）</summary>
        public string DeviceId { get; set; }

        /// <summary>设备名称</summary>
        public string DeviceName { get; set; }

        /// <summary>是否在线</summary>
        public bool IsOnline { get; set; }

        /// <summary>连接/首次发现时间</summary>
        public DateTime ConnectTime { get; set; }

        /// <summary>最后活跃时间（收到该设备消息时更新）</summary>
        public DateTime LastSeen { get; set; }

        /// <summary>Mesh MAC 地址（Root 路由用）</summary>
        public string MeshMac { get; set; }

        /// <summary>是否为 Mesh 根节点</summary>
        public bool IsRoot { get; set; }

        /// <summary>
        /// MeshBridge 注入的发送回调：将消息经 ITransport 发往 Root（由 Root 转发到目标设备）
        /// </summary>
        internal Func<Message, bool>? SendCallback { get; set; }

        /// <summary>
        /// 发送消息到该设备（经 Root 转发）
        /// </summary>
        /// <param name="msg">待发送的消息（DeviceId 会被自动填充为本设备 ID）</param>
        /// <returns>发送成功返回 true；未注入回调或发送失败返回 false</returns>
        public bool Send(Message msg)
        {
            if (msg == null) return false;
            if (string.IsNullOrEmpty(msg.DeviceId))
            {
                msg.DeviceId = DeviceId;
            }
            return SendCallback?.Invoke(msg) ?? false;
        }

        /// <summary>
        /// 发送消息（便捷重载，自动构造 Message 并填充当前设备 ID）
        /// </summary>
        /// <param name="cmd">命令字符串</param>
        /// <param name="data">附加数据，可为 null</param>
        public void Send(string cmd, object data = null)
        {
            var msg = Message.Create(cmd, DeviceId, data);
            Send(msg);
        }
    }
}
