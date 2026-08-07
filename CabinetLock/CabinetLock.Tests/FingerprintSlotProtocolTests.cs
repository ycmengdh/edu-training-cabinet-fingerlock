using Newtonsoft.Json.Linq;

namespace CabinetLock.Tests;

public class FingerprintSlotProtocolTests
{
    [Theory]
    [InlineData(CommandType.FingerprintListRequest, CmdIds.FingerprintListRequest,
        Protocol.CmdFingerprintListRequest)]
    [InlineData(CommandType.FingerprintListResponse, CmdIds.FingerprintListResponse,
        Protocol.CmdFingerprintListResponse)]
    public void FingerprintListCommands_RoundTrip(
        CommandType type, ushort id, string name)
    {
        Assert.Equal(name, Protocol.ToCmdString(type));
        Assert.Equal(type, Protocol.ToCommandType(name));
        Assert.Equal(name, CmdIds.ToCmdName(id));
        Assert.Equal(id, CmdIds.ToCmdId(name));
    }

    [Fact]
    public void FingerprintListRequest_RequiresAcknowledgement()
    {
        Message request = Message.Create(
            Protocol.CmdFingerprintListRequest, "CAB_01",
            new { page = 0, page_size = 20 });

        AppMessage app = AppMessageMapper.ToApp(request);

        Assert.Equal(CmdIds.FingerprintListRequest, app.CmdId);
        Assert.True(app.Flags.HasFlag(AppMessageFlags.NeedsAck));
    }

    [Fact]
    public async Task NormalEnrollment_RejectsMissingLogicalTemplateId()
    {
        FingerprintEnrollmentResult result = await new CommandService()
            .EnrollFingerprintAsync("CAB_01", "S001", fingerprintId: 0);

        Assert.False(result.Success);
        Assert.Contains("有效的用户模板 ID", result.ErrorMessage);
    }

    [Fact]
    public void MessageHandler_ForwardsSlotListWithRequestMessageId()
    {
        var handler = new MessageHandler();
        var device = new DeviceClient { DeviceId = "CAB_01" };
        string? receivedDevice = null;
        string? receivedMessageId = null;
        JObject? received = null;
        handler.OnFingerprintListResponse += (deviceId, messageId, json) =>
        {
            receivedDevice = deviceId;
            receivedMessageId = messageId;
            received = JObject.Parse(json);
        };

        handler.HandleMessage(device, new Message
        {
            Cmd = Protocol.CmdFingerprintListResponse,
            MsgId = "42",
            DeviceId = device.DeviceId,
            Data = JObject.FromObject(new
            {
                total = 1,
                items = new[]
                {
                    new { slot = 17, fingerprint_id = 81, bound = true }
                }
            })
        });

        Assert.Equal("CAB_01", receivedDevice);
        Assert.Equal("42", receivedMessageId);
        Assert.Equal(17, received?["items"]?[0]?["slot"]?.Value<int>());
        Assert.Equal(81, received?["items"]?[0]?["fingerprint_id"]?.Value<int>());
    }

    [Theory]
    [InlineData(0, false, false, "临时槽")]
    [InlineData(17, false, false, "未绑定残留")]
    [InlineData(18, true, false, "正式绑定")]
    [InlineData(19, true, true, "本机副指纹")]
    public void SlotRows_ExposeOperationalBindingState(
        int slot, bool bound, bool backup, string expected)
    {
        var row = new DeviceFingerprintInfo
        {
            SlotId = slot,
            IsBound = bound,
            IsBackup = backup
        };

        Assert.Equal(expected, row.BindingStatusText);
    }
}
