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

        /// <summary>旧版指纹验证命令（仅协议兼容，当前不走上位机鉴权）</summary>
        public const string CmdFingerVerify = "FINGER_VERIFY";

        /// <summary>旧版验证成功命令（柜子仅兼容应答，不授予权限）</summary>
        public const string CmdAuthOk = "AUTH_OK";

        /// <summary>旧版验证失败命令（柜子仅兼容应答，不参与本地验证）</summary>
        public const string CmdAuthFail = "AUTH_FAIL";

        /// <summary>同步权限（上位机 -> 下位机）</summary>
        public const string CmdSyncPermissions = "SYNC_PERMISSIONS";
        public const string CmdBeginPermissionSync = "BEGIN_PERMISSION_SYNC";
        public const string CmdSyncPermission = "SYNC_PERMISSION";
        public const string CmdCommitPermissionSync = "COMMIT_PERMISSION_SYNC";
        public const string CmdClearPermissions = "CLEAR_PERMISSIONS";
        public const string CmdSyncAck = "SYNC_ACK";

        /// <summary>添加指纹（上位机 -> 下位机）</summary>
        public const string CmdAddFingerprint = "ADD_FINGERPRINT";

        /// <summary>指纹录入最终结果（下位机 -> 上位机）</summary>
        public const string CmdAddFingerprintResult = "ADD_FINGERPRINT_RESULT";

        /// <summary>指纹录入过程提示（下位机 -> 上位机：放指/抬指/验证）</summary>
        public const string CmdEnrollProgress = "ENROLL_PROGRESS";

        /// <summary>删除指纹（上位机 -> 下位机）</summary>
        public const string CmdDeleteFingerprint = "DELETE_FINGERPRINT";

        /// <summary>从备份恢复指纹模板到柜子传感器（上位机 -> 下位机）</summary>
        public const string CmdRestoreFingerprint = "RESTORE_FINGERPRINT";

        /// <summary>指纹恢复结果（下位机 -> 上位机）</summary>
        public const string CmdRestoreFingerprintResult = "RESTORE_FINGERPRINT_RESULT";

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
        public const string CmdRebootAck = "REBOOT_ACK";

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

        /// <summary>Unix 时间同步（上位机 -> 根节点 -> 柜子）</summary>
        public const string CmdTimeSync = "TIME_SYNC";

        /// <summary>应答（下位机 -> 上位机，对下发命令的确认）</summary>
        public const string CmdAck = "ACK";

        /// <summary>命令处理失败响应</summary>
        public const string CmdError = "ERROR";

        // ===== SD 卡集中存储命令（上位机 <-> 根节点） =====

        /// <summary>查询 SD 卡表（上位机 -> 根节点）</summary>
        public const string CmdSdQuery = "SD_QUERY";

        /// <summary>查询 SD 卡表响应（根节点 -> 上位机）</summary>
        public const string CmdSdQueryResponse = "SD_QUERY_RESPONSE";

        /// <summary>查询 SD 卡表分片（根节点 -> 上位机，大表分批）</summary>
        public const string CmdSdQueryPart = "SD_QUERY_PART";

        /// <summary>保存 SD 卡表（上位机 -> 根节点，带乐观锁）</summary>
        public const string CmdSdSave = "SD_SAVE";

        /// <summary>保存 SD 卡表响应（根节点 -> 上位机）</summary>
        public const string CmdSdSaveResponse = "SD_SAVE_RESPONSE";

        /// <summary>查询 SD 卡版本号（上位机 -> 根节点）</summary>
        public const string CmdSdQueryVersion = "SD_QUERY_VERSION";

        /// <summary>查询 SD 卡版本号响应（根节点 -> 上位机）</summary>
        public const string CmdSdVersionResponse = "SD_VERSION_RESPONSE";

        /// <summary>上传指纹模板到 SD 卡（上位机 -> 根节点）</summary>
        public const string CmdUploadFpTemplate = "UPLOAD_FP_TEMPLATE";

        /// <summary>上传指纹模板响应（根节点 -> 上位机）</summary>
        public const string CmdFpTemplateUploadResponse = "FP_TEMPLATE_UPLOAD_RESPONSE";

        /// <summary>从 SD 卡下载指纹模板（上位机 -> 根节点）</summary>
        public const string CmdDownloadFpTemplate = "DOWNLOAD_FP_TEMPLATE";

        /// <summary>下载指纹模板响应（根节点 -> 上位机）</summary>
        public const string CmdFpTemplateDownloadResponse = "FP_TEMPLATE_DOWNLOAD_RESPONSE";

        /// <summary>删除 SD 卡指纹模板（上位机 -> 根节点）</summary>
        public const string CmdDeleteFpTemplate = "DELETE_FP_TEMPLATE";

        /// <summary>删除指纹模板响应（根节点 -> 上位机）</summary>
        public const string CmdFpTemplateDeleteResponse = "FP_TEMPLATE_DELETE_RESPONSE";

        // ===== V2.7 设备专属副指纹命令（上位机 <-> 柜子，不经 SD 卡） =====

        /// <summary>录入本机副指纹（上位机 -> 柜子）</summary>
        public const string CmdAddBackupFingerprint = "ADD_BACKUP_FINGERPRINT";

        /// <summary>请求本机副指纹清单（上位机 -> 柜子）</summary>
        public const string CmdBackupFpListRequest = "BACKUP_FP_LIST_REQUEST";

        /// <summary>本机副指纹清单响应（柜子 -> 上位机）</summary>
        public const string CmdBackupFpList = "BACKUP_FP_LIST";

        /// <summary>删除本机副指纹（上位机 -> 柜子）</summary>
        public const string CmdDeleteBackupFingerprint = "DELETE_BACKUP_FINGERPRINT";

        /// <summary>删除本机副指纹结果（柜子 -> 上位机）</summary>
        public const string CmdDeleteBackupFingerprintResult = "DELETE_BACKUP_FINGERPRINT_RESULT";

        /// <summary>验证窗口事件（柜子 -> 上位机：enter/timeout/cancel/unlocked）</summary>
        public const string CmdVerifyWindowEvent = "VERIFY_WINDOW_EVENT";

        // ===== 错误码常量（ACK 中 result 字段使用） =====

        /// <summary>成功</summary>
        public const string ErrOk = "OK";

        /// <summary>未知错误</summary>
        public const string ErrUnknown = "ERR_UNKNOWN";

        /// <summary>参数错误</summary>
        public const string ErrBadParam = "ERR_BAD_PARAM";

        /// <summary>指纹未注册</summary>
        public const string ErrFingerprintNotFound = "ERR_FP_NOT_FOUND";

        /// <summary>权限不足</summary>
        public const string ErrPermissionDenied = "ERR_PERMISSION_DENIED";

        /// <summary>设备繁忙</summary>
        public const string ErrDeviceBusy = "ERR_DEVICE_BUSY";

        /// <summary>硬件故障（如指纹模块通信失败）</summary>
        public const string ErrHardware = "ERR_HARDWARE";

        /// <summary>存储失败（如写 Flash 失败）</summary>
        public const string ErrStorage = "ERR_STORAGE";

        /// <summary>超时</summary>
        public const string ErrTimeout = "ERR_TIMEOUT";

        /// <summary>
        /// CommandType 枚举转换为命令字符串
        /// </summary>
        /// <param name="type">命令类型枚举</param>
        /// <returns>对应的命令字符串；未知类型返回 null</returns>
        public static string? ToCmdString(CommandType type)
        {
            switch (type)
            {
                case CommandType.Register: return CmdRegister;
                case CommandType.FingerVerify: return CmdFingerVerify;
                case CommandType.AuthOk: return CmdAuthOk;
                case CommandType.AuthFail: return CmdAuthFail;
                case CommandType.SyncPermissions: return CmdSyncPermissions;
                case CommandType.AddFingerprint: return CmdAddFingerprint;
                case CommandType.AddFingerprintResult: return CmdAddFingerprintResult;
                case CommandType.EnrollProgress: return CmdEnrollProgress;
                case CommandType.SyncAck: return CmdSyncAck;
                case CommandType.DeleteFingerprint: return CmdDeleteFingerprint;
                case CommandType.RestoreFingerprint: return CmdRestoreFingerprint;
                case CommandType.RestoreFingerprintResult: return CmdRestoreFingerprintResult;
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
                case CommandType.TimeSync: return CmdTimeSync;
                case CommandType.Ack: return CmdAck;
                case CommandType.Error: return CmdError;
                case CommandType.SdQuery: return CmdSdQuery;
                case CommandType.SdQueryResponse: return CmdSdQueryResponse;
                case CommandType.SdQueryPart: return CmdSdQueryPart;
                case CommandType.SdSave: return CmdSdSave;
                case CommandType.SdSaveResponse: return CmdSdSaveResponse;
                case CommandType.SdQueryVersion: return CmdSdQueryVersion;
                case CommandType.SdVersionResponse: return CmdSdVersionResponse;
                case CommandType.UploadFpTemplate: return CmdUploadFpTemplate;
                case CommandType.FpTemplateUploadResponse: return CmdFpTemplateUploadResponse;
                case CommandType.DownloadFpTemplate: return CmdDownloadFpTemplate;
                case CommandType.FpTemplateDownloadResponse: return CmdFpTemplateDownloadResponse;
                case CommandType.DeleteFpTemplate: return CmdDeleteFpTemplate;
                case CommandType.FpTemplateDeleteResponse: return CmdFpTemplateDeleteResponse;
                case CommandType.AddBackupFingerprint: return CmdAddBackupFingerprint;
                case CommandType.BackupFpListRequest: return CmdBackupFpListRequest;
                case CommandType.BackupFpList: return CmdBackupFpList;
                case CommandType.DeleteBackupFingerprint: return CmdDeleteBackupFingerprint;
                case CommandType.DeleteBackupFingerprintResult: return CmdDeleteBackupFingerprintResult;
                case CommandType.VerifyWindowEvent: return CmdVerifyWindowEvent;
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
                case CmdAddFingerprintResult: return CommandType.AddFingerprintResult;
                case CmdEnrollProgress: return CommandType.EnrollProgress;
                case CmdSyncAck: return CommandType.SyncAck;
                case CmdDeleteFingerprint: return CommandType.DeleteFingerprint;
                case CmdRestoreFingerprint: return CommandType.RestoreFingerprint;
                case CmdRestoreFingerprintResult: return CommandType.RestoreFingerprintResult;
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
                case CmdTimeSync: return CommandType.TimeSync;
                case CmdAck: return CommandType.Ack;
                case CmdError: return CommandType.Error;
                case CmdSdQuery: return CommandType.SdQuery;
                case CmdSdQueryResponse: return CommandType.SdQueryResponse;
                case CmdSdQueryPart: return CommandType.SdQueryPart;
                case CmdSdSave: return CommandType.SdSave;
                case CmdSdSaveResponse: return CommandType.SdSaveResponse;
                case CmdSdQueryVersion: return CommandType.SdQueryVersion;
                case CmdSdVersionResponse: return CommandType.SdVersionResponse;
                case CmdUploadFpTemplate: return CommandType.UploadFpTemplate;
                case CmdFpTemplateUploadResponse: return CommandType.FpTemplateUploadResponse;
                case CmdDownloadFpTemplate: return CommandType.DownloadFpTemplate;
                case CmdFpTemplateDownloadResponse: return CommandType.FpTemplateDownloadResponse;
                case CmdDeleteFpTemplate: return CommandType.DeleteFpTemplate;
                case CmdFpTemplateDeleteResponse: return CommandType.FpTemplateDeleteResponse;
                case CmdAddBackupFingerprint: return CommandType.AddBackupFingerprint;
                case CmdBackupFpListRequest: return CommandType.BackupFpListRequest;
                case CmdBackupFpList: return CommandType.BackupFpList;
                case CmdDeleteBackupFingerprint: return CommandType.DeleteBackupFingerprint;
                case CmdDeleteBackupFingerprintResult: return CommandType.DeleteBackupFingerprintResult;
                case CmdVerifyWindowEvent: return CommandType.VerifyWindowEvent;
                default: return null;
            }
        }
    }
}
