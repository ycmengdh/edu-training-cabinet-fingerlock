using Newtonsoft.Json.Linq;

namespace FingerprintLockManager.Tests;

public class FingerprintTestProtocolTests
{
    [Theory]
    [InlineData(CommandType.StartFingerprintTest, CmdIds.StartFingerprintTest, Protocol.CmdStartFingerprintTest)]
    [InlineData(CommandType.StopFingerprintTest, CmdIds.StopFingerprintTest, Protocol.CmdStopFingerprintTest)]
    [InlineData(CommandType.FingerprintTestEvent, CmdIds.FingerprintTestEvent, Protocol.CmdFingerprintTestEvent)]
    [InlineData(CommandType.ReadPermissions, CmdIds.ReadPermissions, Protocol.CmdReadPermissions)]
    [InlineData(CommandType.PermissionsResponse, CmdIds.PermissionsResponse, Protocol.CmdPermissionsResponse)]
    [InlineData(CommandType.CheckFingerprint, CmdIds.CheckFingerprint, Protocol.CmdCheckFingerprint)]
    [InlineData(CommandType.FingerprintCheckResponse, CmdIds.FingerprintCheckResponse, Protocol.CmdFingerprintCheckResponse)]
    public void Commands_MapBidirectionally(
        CommandType type, ushort commandId, string commandName)
    {
        Assert.Equal(commandName, Protocol.ToCmdString(type));
        Assert.Equal(type, Protocol.ToCommandType(commandName));
        Assert.Equal(commandName, CmdIds.ToCmdName(commandId));
        Assert.Equal(commandId, CmdIds.ToCmdId(commandName));
    }

    [Fact]
    public void StartFingerprintTest_UsesAckFlagAndPreservesTemplate()
    {
        string templateHex = new('A', 1024);
        Message message = Message.Create(Protocol.CmdStartFingerprintTest, "CABINET_001", new
        {
            fingerprint_id = 17,
            template_hex = templateHex,
            test_token = "token-1"
        });

        AppMessage app = AppMessageMapper.ToApp(message);
        Message roundTrip = AppMessageMapper.ToMessage(app);

        Assert.True(app.Flags.HasFlag(AppMessageFlags.NeedsAck));
        Assert.Equal(templateHex, ((JObject)roundTrip.Data!)["template_hex"]?.ToString());
    }

    [Fact]
    public void FingerprintTestEvent_IsParsed()
    {
        var handler = new MessageHandler();
        FingerprintTestEvent? received = null;
        handler.OnFingerprintTestEvent += value => received = value;

        handler.HandleMessage(new DeviceClient { DeviceId = "CABINET_001" }, new Message
        {
            MsgId = "91",
            Cmd = Protocol.CmdFingerprintTestEvent,
            Data = JObject.FromObject(new
            {
                event_name = "ignored",
                @event = "matched",
                test_token = "abc",
                fingerprint_id = 17,
                confidence = 86,
                idle_timeout_seconds = 60
            })
        });

        Assert.NotNull(received);
        Assert.Equal("matched", received.Event);
        Assert.Equal("abc", received.TestToken);
        Assert.Equal(17, received.FingerprintId);
        Assert.Equal(86, received.Confidence);
    }

    [Fact]
    public void ProbeResponses_AreParsed()
    {
        var handler = new MessageHandler();
        PermissionProbeResult? permission = null;
        FingerprintProbeResult? fingerprint = null;
        handler.OnPermissionsResponse += (_, _, value) => permission = value;
        handler.OnFingerprintCheckResponse += (_, _, value) => fingerprint = value;
        var device = new DeviceClient { DeviceId = "CABINET_001" };

        handler.HandleMessage(device, new Message
        {
            MsgId = "92",
            Cmd = Protocol.CmdPermissionsResponse,
            Data = JObject.FromObject(new
            {
                found = true,
                user_id = "T001",
                fingerprint_id = 9,
                role = 1,
                lock_0 = false,
                lock_1 = true,
                lock_2 = true,
                lock_3 = false,
                version = 7
            })
        });
        handler.HandleMessage(device, new Message
        {
            MsgId = "93",
            Cmd = Protocol.CmdFingerprintCheckResponse,
            Data = JObject.FromObject(new
            {
                fingerprint_id = 9,
                exists = true,
                readable = true,
                matches = true,
                expected_crc32 = 12U,
                actual_crc32 = 12U
            })
        });

        Assert.NotNull(permission);
        Assert.True(permission.Found);
        Assert.Equal([false, true, true, false], permission.Permissions);
        Assert.NotNull(fingerprint);
        Assert.True(fingerprint.Matches);
        Assert.Equal(12U, fingerprint.ActualCrc32);
    }

    [Fact]
    public void TemplateCrc32_UsesStandardPolynomial()
    {
        Assert.Equal(0xCBF43926U,
            CommandService.ComputeTemplateCrc32("123456789"u8.ToArray()));
    }
}
