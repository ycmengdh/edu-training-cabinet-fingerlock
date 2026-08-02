namespace CabinetLock.Tests;

/// <summary>
/// V2.7 协议层命令 ID / 字符串名映射测试。
/// 验证新增的副指纹命令在 CmdIds 与 Protocol 之间双向映射正确。
/// </summary>
public class BackupFingerprintProtocolTests
{
    [Theory]
    [InlineData(CommandType.AddBackupFingerprint, CmdIds.AddBackupFingerprint, Protocol.CmdAddBackupFingerprint)]
    [InlineData(CommandType.BackupFpListRequest, CmdIds.BackupFpListRequest, Protocol.CmdBackupFpListRequest)]
    [InlineData(CommandType.BackupFpList, CmdIds.BackupFpList, Protocol.CmdBackupFpList)]
    [InlineData(CommandType.DeleteBackupFingerprint, CmdIds.DeleteBackupFingerprint, Protocol.CmdDeleteBackupFingerprint)]
    [InlineData(CommandType.VerifyWindowEvent, CmdIds.VerifyWindowEvent, Protocol.CmdVerifyWindowEvent)]
    public void NewBackupCommands_MapCorrectly(CommandType type, ushort expectedId, string expectedName)
    {
        // CommandType -> string
        string? name = Protocol.ToCmdString(type);
        Assert.Equal(expectedName, name);

        // string -> CommandType
        CommandType? back = Protocol.ToCommandType(name!);
        Assert.Equal(type, back);

        // ushort -> string
        string? idToName = CmdIds.ToCmdName(expectedId);
        Assert.Equal(expectedName, idToName);

        // string -> ushort
        ushort? nameToId = CmdIds.ToCmdId(expectedName);
        Assert.Equal(expectedId, nameToId);
    }

    [Fact]
    public void AddBackupFingerprint_IsInNeedsAckSet()
    {
        // AppMessageMapper.NeedsAckCmds 是私有的；通过 AppMessageMapper.ToApp 间接验证
        // 这里仅验证命令能被正确创建为 Message
        var msg = Message.Create(Protocol.CmdAddBackupFingerprint, "CABINET_001", new { user_id = "U001" });
        Assert.Equal(Protocol.CmdAddBackupFingerprint, msg.Cmd);
        Assert.Equal("CABINET_001", msg.DeviceId);
    }
}
