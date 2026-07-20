namespace FingerprintLockManager
{
    /// <summary>
    /// 应用层二进制协议命令 ID（uint16，与固件 common/cmd_ids.h 对齐）。
    /// 字符串命令名仍用于日志/UI，通过 <see cref="ToCmdId"/> / <see cref="ToCmdName"/> 互转。
    /// </summary>
    public static class CmdIds
    {
        public const ushort Register = 0x0001;
        public const ushort Heartbeat = 0x0002;
        public const ushort HeartbeatAck = 0x0003;
        public const ushort Ack = 0x0004;
        public const ushort Error = 0x0005;

        public const ushort ControlLock = 0x0010;
        public const ushort AddFingerprint = 0x0011;
        public const ushort AddFingerprintResult = 0x0012;
        public const ushort EnrollProgress = 0x0017;
        public const ushort DeleteFingerprint = 0x0013;
        public const ushort RestoreFingerprint = 0x0014;
        public const ushort RestoreFingerprintResult = 0x0015;
        public const ushort DeleteAllFingerprints = 0x0016;
        // V2.7 设备专属副指纹
        public const ushort AddBackupFingerprint = 0x0018;
        public const ushort BackupFpList = 0x0019;
        public const ushort BackupFpListRequest = 0x001A;
        public const ushort DeleteBackupFingerprint = 0x001B;
        public const ushort VerifyWindowEvent = 0x001C;

        public const ushort BeginPermissionSync = 0x0020;
        public const ushort SyncPermission = 0x0021;
        public const ushort CommitPermissionSync = 0x0022;
        public const ushort ClearPermissions = 0x0023;
        public const ushort SyncAck = 0x0024;
        public const ushort SyncPermissions = 0x0025;
        public const ushort ReadPermissions = 0x0026;

        public const ushort ReadConfig = 0x0030;
        public const ushort WriteConfig = 0x0031;
        public const ushort ConfigResponse = 0x0032;
        public const ushort ConfigSaved = 0x0033;
        public const ushort ReadStatus = 0x0034;
        public const ushort StatusResponse = 0x0035;
        public const ushort StatusReport = 0x0036;
        public const ushort TimeSync = 0x0037;
        public const ushort Reboot = 0x0038;
        public const ushort RebootAck = 0x0039;
        public const ushort ClearLogs = 0x003A;

        public const ushort SdQuery = 0x0040;
        public const ushort SdQueryResponse = 0x0041;
        public const ushort SdQueryPart = 0x0042;
        public const ushort SdQueryPartAck = 0x0043;
        public const ushort SdSave = 0x0044;
        public const ushort SdSaveResponse = 0x0045;
        public const ushort SdQueryVersion = 0x0046;
        public const ushort SdVersionResponse = 0x0047;

        public const ushort UploadFpTemplate = 0x0050;
        public const ushort FpTemplateUploadResponse = 0x0051;
        public const ushort DownloadFpTemplate = 0x0052;
        public const ushort FpTemplateDownloadResponse = 0x0053;
        public const ushort DeleteFpTemplate = 0x0054;
        public const ushort FpTemplateDeleteResponse = 0x0055;

        public const ushort LogReport = 0x0060;
        public const ushort LogReportAck = 0x0061;
        public const ushort PermLost = 0x0062;
        public const ushort PermLostAck = 0x0063;

        // 字符串命令名（与 Protocol.Cmd* 及固件日志一致；未在 Protocol 中的补充于此）
        public const string NameHeartbeatAck = "HEARTBEAT_ACK";
        public const string NameDeleteAllFingerprints = "DELETE_ALL_FINGERPRINTS";
        public const string NameReadPermissions = "READ_PERMISSIONS";
        public const string NameSdQueryPartAck = "SD_QUERY_PART_ACK";
        public const string NameLogReportAck = "LOG_REPORT_ACK";
        public const string NamePermLost = "PERM_LOST";
        public const string NamePermLostAck = "PERM_LOST_ACK";

        private static readonly Dictionary<ushort, string> IdToName = new()
        {
            { Register, Protocol.CmdRegister },
            { Heartbeat, Protocol.CmdHeartbeat },
            { HeartbeatAck, NameHeartbeatAck },
            { Ack, Protocol.CmdAck },
            { Error, Protocol.CmdError },
            { ControlLock, Protocol.CmdControlLock },
            { AddFingerprint, Protocol.CmdAddFingerprint },
            { AddFingerprintResult, Protocol.CmdAddFingerprintResult },
            { EnrollProgress, Protocol.CmdEnrollProgress },
            { DeleteFingerprint, Protocol.CmdDeleteFingerprint },
            { RestoreFingerprint, Protocol.CmdRestoreFingerprint },
            { RestoreFingerprintResult, Protocol.CmdRestoreFingerprintResult },
            { DeleteAllFingerprints, NameDeleteAllFingerprints },
            { AddBackupFingerprint, Protocol.CmdAddBackupFingerprint },
            { BackupFpList, Protocol.CmdBackupFpList },
            { BackupFpListRequest, Protocol.CmdBackupFpListRequest },
            { DeleteBackupFingerprint, Protocol.CmdDeleteBackupFingerprint },
            { VerifyWindowEvent, Protocol.CmdVerifyWindowEvent },
            { BeginPermissionSync, Protocol.CmdBeginPermissionSync },
            { SyncPermission, Protocol.CmdSyncPermission },
            { CommitPermissionSync, Protocol.CmdCommitPermissionSync },
            { ClearPermissions, Protocol.CmdClearPermissions },
            { SyncAck, Protocol.CmdSyncAck },
            { SyncPermissions, Protocol.CmdSyncPermissions },
            { ReadPermissions, NameReadPermissions },
            { ReadConfig, Protocol.CmdReadConfig },
            { WriteConfig, Protocol.CmdWriteConfig },
            { ConfigResponse, Protocol.CmdConfigResponse },
            { ConfigSaved, Protocol.CmdConfigSaved },
            { ReadStatus, Protocol.CmdReadStatus },
            { StatusResponse, Protocol.CmdStatusResponse },
            { StatusReport, Protocol.CmdStatusReport },
            { TimeSync, Protocol.CmdTimeSync },
            { Reboot, Protocol.CmdReboot },
            { RebootAck, Protocol.CmdRebootAck },
            { ClearLogs, Protocol.CmdClearLogs },
            { SdQuery, Protocol.CmdSdQuery },
            { SdQueryResponse, Protocol.CmdSdQueryResponse },
            { SdQueryPart, Protocol.CmdSdQueryPart },
            { SdQueryPartAck, NameSdQueryPartAck },
            { SdSave, Protocol.CmdSdSave },
            { SdSaveResponse, Protocol.CmdSdSaveResponse },
            { SdQueryVersion, Protocol.CmdSdQueryVersion },
            { SdVersionResponse, Protocol.CmdSdVersionResponse },
            { UploadFpTemplate, Protocol.CmdUploadFpTemplate },
            { FpTemplateUploadResponse, Protocol.CmdFpTemplateUploadResponse },
            { DownloadFpTemplate, Protocol.CmdDownloadFpTemplate },
            { FpTemplateDownloadResponse, Protocol.CmdFpTemplateDownloadResponse },
            { DeleteFpTemplate, Protocol.CmdDeleteFpTemplate },
            { FpTemplateDeleteResponse, Protocol.CmdFpTemplateDeleteResponse },
            { LogReport, Protocol.CmdLogReport },
            { LogReportAck, NameLogReportAck },
            { PermLost, NamePermLost },
            { PermLostAck, NamePermLostAck },
        };

        private static readonly Dictionary<string, ushort> NameToId =
            IdToName.ToDictionary(
                pair => pair.Value,
                pair => pair.Key,
                StringComparer.OrdinalIgnoreCase);

        /// <summary>命令 ID → 字符串名；未知返回 null。</summary>
        public static string? ToCmdName(ushort cmdId) =>
            IdToName.TryGetValue(cmdId, out string? name) ? name : null;

        /// <summary>字符串名 → 命令 ID；未知返回 null。</summary>
        public static ushort? ToCmdId(string? cmdName)
        {
            if (string.IsNullOrEmpty(cmdName)) return null;
            return NameToId.TryGetValue(cmdName, out ushort id) ? id : null;
        }
    }
}
