namespace FingerprintLockManager
{
    /// <summary>
    /// 通信协议常量与辅助方法
    /// 命令字符串与 ESP32 端定义保持一致，统一使用大写加下划线格式
    /// </summary>
    public static class Protocol
    {
        /// <summary>默认通信端口</summary>
        public const int DefaultPort = 8888;

        /// <summary>AP 模式下 ESP32 默认 IP 地址</summary>
        public const string DefaultApIp = "192.168.4.1";

        // ===== 命令字符串常量（与 ESP32 端对应） =====

        /// <summary>设备注册（下位机 -> 上位机）</summary>
        public const string CmdRegister = "REGISTER";

        /// <summary>指纹验证请求（下位机 -> 上位机）</summary>
        public const string CmdFingerVerify = "FINGER_VERIFY";

        /// <summary>验证成功（上位机 -> 下位机）</summary>
        public const string CmdAuthOk = "AUTH_OK";

        /// <summary>验证失败（上位机 -> 下位机）</summary>
        public const string CmdAuthFail = "AUTH_FAIL";

        /// <summary>同步权限（上位机 -> 下位机）</summary>
        public const string CmdSyncPermissions = "SYNC_PERMISSIONS";

        /// <summary>添加指纹（上位机 -> 下位机）</summary>
        public const string CmdAddFingerprint = "ADD_FINGERPRINT";

        /// <summary>删除指纹（上位机 -> 下位机）</summary>
        public const string CmdDeleteFingerprint = "DELETE_FINGERPRINT";

        /// <summary>控制锁（上位机 -> 下位机）</summary>
        public const string CmdControlLock = "CONTROL_LOCK";

        /// <summary>读取设备配置（上位机 -> 下位机）</summary>
        public const string CmdReadConfig = "READ_CONFIG";

        /// <summary>写入设备配置（上位机 -> 下位机）</summary>
        public const string CmdWriteConfig = "WRITE_CONFIG";

        /// <summary>读取设备状态（上位机 -> 下位机）</summary>
        public const string CmdReadStatus = "READ_STATUS";

        /// <summary>清除本地日志（上位机 -> 下位机）</summary>
        public const string CmdClearLogs = "CLEAR_LOGS";

        /// <summary>重启设备（上位机 -> 下位机）</summary>
        public const string CmdReboot = "REBOOT";

        /// <summary>状态上报（下位机 -> 上位机）</summary>
        public const string CmdStatusReport = "STATUS_REPORT";

        /// <summary>日志上报（下位机 -> 上位机）</summary>
        public const string CmdLogReport = "LOG_REPORT";

        /// <summary>配置读取响应（下位机 -> 上位机）</summary>
        public const string CmdConfigResponse = "CONFIG_RESPONSE";

        /// <summary>状态读取响应（下位机 -> 上位机）</summary>
        public const string CmdStatusResponse = "STATUS_RESPONSE";

        /// <summary>配置保存成功（下位机 -> 上位机）</summary>
        public const string CmdConfigSaved = "CONFIG_SAVED";

        /// <summary>心跳（双向，用于保活检测）</summary>
        public const string CmdHeartbeat = "HEARTBEAT";

        /// <summary>
        /// CommandType 枚举转换为命令字符串
        /// </summary>
        /// <param name="type">命令类型枚举</param>
        /// <returns>对应的命令字符串；未知类型返回 null</returns>
        public static string ToCmdString(CommandType type)
        {
            switch (type)
            {
                case CommandType.Register: return CmdRegister;
                case CommandType.FingerVerify: return CmdFingerVerify;
                case CommandType.AuthOk: return CmdAuthOk;
                case CommandType.AuthFail: return CmdAuthFail;
                case CommandType.SyncPermissions: return CmdSyncPermissions;
                case CommandType.AddFingerprint: return CmdAddFingerprint;
                case CommandType.DeleteFingerprint: return CmdDeleteFingerprint;
                case CommandType.ControlLock: return CmdControlLock;
                case CommandType.ReadConfig: return CmdReadConfig;
                case CommandType.WriteConfig: return CmdWriteConfig;
                case CommandType.ReadStatus: return CmdReadStatus;
                case CommandType.ClearLogs: return CmdClearLogs;
                case CommandType.Reboot: return CmdReboot;
                case CommandType.StatusReport: return CmdStatusReport;
                case CommandType.LogReport: return CmdLogReport;
                case CommandType.ConfigResponse: return CmdConfigResponse;
                case CommandType.StatusResponse: return CmdStatusResponse;
                case CommandType.ConfigSaved: return CmdConfigSaved;
                case CommandType.Heartbeat: return CmdHeartbeat;
                default: return null;
            }
        }

        /// <summary>
        /// 命令字符串转换为 CommandType 枚举
        /// </summary>
        /// <param name="cmd">命令字符串（大小写不敏感）</param>
        /// <returns>对应的 CommandType 枚举；未知或空字符串返回 null</returns>
        public static CommandType? ToCommandType(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return null;
            switch (cmd.ToUpperInvariant())
            {
                case CmdRegister: return CommandType.Register;
                case CmdFingerVerify: return CommandType.FingerVerify;
                case CmdAuthOk: return CommandType.AuthOk;
                case CmdAuthFail: return CommandType.AuthFail;
                case CmdSyncPermissions: return CommandType.SyncPermissions;
                case CmdAddFingerprint: return CommandType.AddFingerprint;
                case CmdDeleteFingerprint: return CommandType.DeleteFingerprint;
                case CmdControlLock: return CommandType.ControlLock;
                case CmdReadConfig: return CommandType.ReadConfig;
                case CmdWriteConfig: return CommandType.WriteConfig;
                case CmdReadStatus: return CommandType.ReadStatus;
                case CmdClearLogs: return CommandType.ClearLogs;
                case CmdReboot: return CommandType.Reboot;
                case CmdStatusReport: return CommandType.StatusReport;
                case CmdLogReport: return CommandType.LogReport;
                case CmdConfigResponse: return CommandType.ConfigResponse;
                case CmdStatusResponse: return CommandType.StatusResponse;
                case CmdConfigSaved: return CommandType.ConfigSaved;
                case CmdHeartbeat: return CommandType.Heartbeat;
                default: return null;
            }
        }
    }
}
