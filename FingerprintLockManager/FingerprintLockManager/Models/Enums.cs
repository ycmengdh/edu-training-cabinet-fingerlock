namespace FingerprintLockManager
{
    /// <summary>
    /// 用户角色枚举（与权限等级对应）
    /// </summary>
    public enum UserRole
    {
        /// <summary>系统管理员：可开启所有4个锁</summary>
        Admin,

        /// <summary>老师：可开启除系统锁外的所有锁</summary>
        Teacher,

        /// <summary>学生：由老师分配指定锁的权限</summary>
        Student
    }

    /// <summary>
    /// ESP32 通讯工作模式
    /// </summary>
    public enum WorkMode
    {
        /// <summary>STA 路由模式：ESP32 作为 TCP 客户端连接上位机</summary>
        STA,

        /// <summary>AP 热点模式：ESP32 开热点，上位机作为 TCP 客户端连接进来</summary>
        AP
    }

    /// <summary>
    /// 上位机与 Mesh 根节点之间的传输链路类型
    /// </summary>
    public enum TransportType
    {
        /// <summary>USB 串口直连根节点（SerialTransport）</summary>
        UsbSerial,

        /// <summary>TCP 客户端：上位机主动连接根节点 AP 热点（TcpClientTransport）</summary>
        TcpClient,

        /// <summary>TCP 服务端：上位机监听端口，等待根节点连接（TcpServerTransport）</summary>
        TcpServer
    }

    /// <summary>
    /// 通信命令类型枚举（对应协议中的 cmd 字段）
    /// </summary>
    public enum CommandType
    {
        /// <summary>设备注册（下位机 -> 上位机）</summary>
        Register,

        /// <summary>指纹验证请求（下位机 -> 上位机）</summary>
        FingerVerify,

        /// <summary>验证成功（上位机 -> 下位机）</summary>
        AuthOk,

        /// <summary>验证失败（上位机 -> 下位机）</summary>
        AuthFail,

        /// <summary>同步权限（上位机 -> 下位机）</summary>
        SyncPermissions,

        /// <summary>添加指纹（上位机 -> 下位机）</summary>
        AddFingerprint,

        /// <summary>指纹录入最终结果（下位机 -> 上位机）</summary>
        AddFingerprintResult,

        /// <summary>权限事务提交结果（下位机 -> 上位机）</summary>
        SyncAck,

        /// <summary>删除指纹（上位机 -> 下位机）</summary>
        DeleteFingerprint,

        /// <summary>从备份恢复指纹模板到柜子</summary>
        RestoreFingerprint,

        /// <summary>指纹恢复结果（下位机 -> 上位机）</summary>
        RestoreFingerprintResult,

        /// <summary>控制锁（上位机 -> 下位机）</summary>
        ControlLock,

        /// <summary>读取设备配置（上位机 -> 下位机）</summary>
        ReadConfig,

        /// <summary>写入设备配置（上位机 -> 下位机）</summary>
        WriteConfig,

        /// <summary>读取设备状态（上位机 -> 下位机）</summary>
        ReadStatus,

        /// <summary>清除本地日志（上位机 -> 下位机）</summary>
        ClearLogs,

        /// <summary>重启设备（上位机 -> 下位机）</summary>
        Reboot,

        /// <summary>状态上报（下位机 -> 上位机）</summary>
        StatusReport,

        /// <summary>日志上报（下位机 -> 上位机）</summary>
        LogReport,

        /// <summary>配置读取响应（下位机 -> 上位机）</summary>
        ConfigResponse,

        /// <summary>状态读取响应（下位机 -> 上位机）</summary>
        StatusResponse,

        /// <summary>配置保存成功（下位机 -> 上位机）</summary>
        ConfigSaved,

        /// <summary>心跳（双向，用于保活检测）</summary>
        Heartbeat,

        /// <summary>Unix 时间同步（上位机/根节点 -> 柜子）</summary>
        TimeSync,

        /// <summary>应答（下位机 -> 上位机，对下发命令的确认）</summary>
        Ack,

        /// <summary>命令处理失败响应</summary>
        Error,

        // ===== SD 卡集中存储命令（上位机 <-> 根节点） =====

        /// <summary>查询 SD 卡表（上位机 -> 根节点）</summary>
        SdQuery,

        /// <summary>查询 SD 卡表响应（根节点 -> 上位机）</summary>
        SdQueryResponse,

        /// <summary>查询 SD 卡表分片（根节点 -> 上位机，大表分批）</summary>
        SdQueryPart,

        /// <summary>保存 SD 卡表（上位机 -> 根节点，带乐观锁）</summary>
        SdSave,

        /// <summary>保存 SD 卡表响应（根节点 -> 上位机）</summary>
        SdSaveResponse,

        /// <summary>查询 SD 卡版本号（上位机 -> 根节点）</summary>
        SdQueryVersion,

        /// <summary>查询 SD 卡版本号响应（根节点 -> 上位机）</summary>
        SdVersionResponse,

        /// <summary>上传指纹模板到 SD 卡（上位机 -> 根节点）</summary>
        UploadFpTemplate,

        /// <summary>上传指纹模板响应（根节点 -> 上位机）</summary>
        FpTemplateUploadResponse,

        /// <summary>从 SD 卡下载指纹模板（上位机 -> 根节点）</summary>
        DownloadFpTemplate,

        /// <summary>下载指纹模板响应（根节点 -> 上位机）</summary>
        FpTemplateDownloadResponse,

        /// <summary>删除 SD 卡指纹模板（上位机 -> 根节点）</summary>
        DeleteFpTemplate,

        /// <summary>删除指纹模板响应（根节点 -> 上位机）</summary>
        FpTemplateDeleteResponse
    }
}
