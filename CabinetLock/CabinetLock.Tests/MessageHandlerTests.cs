using Newtonsoft.Json.Linq;

namespace CabinetLock.Tests;

public class MessageHandlerTests
{
    [Fact]
    public void EnrollmentResult_IsNotDroppedAfterAckWithSameRequestId()
    {
        var handler = new MessageHandler();
        var device = new DeviceClient { DeviceId = "CABINET_001" };
        int ackCount = 0;
        FingerprintEnrollmentResult? enrollment = null;
        handler.OnAckReceived += (_, _) => ackCount++;
        handler.OnFingerprintEnrollmentResult += (_, result) => enrollment = result;

        handler.HandleMessage(device, new Message
        {
            MsgId = "request-1",
            Cmd = Protocol.CmdAck,
            DeviceId = device.DeviceId,
            Data = JObject.FromObject(new { result = "enrolling" })
        });
        handler.HandleMessage(device, new Message
        {
            MsgId = "request-1",
            Cmd = Protocol.CmdAddFingerprintResult,
            DeviceId = device.DeviceId,
            Data = JObject.FromObject(new
            {
                result = "success",
                user_id = "student_1",
                fingerprint_id = 7,
                template_hex = "0102A0FF"
            })
        });

        Assert.Equal(1, ackCount);
        Assert.NotNull(enrollment);
        Assert.True(enrollment.Success);
        Assert.Equal(7, enrollment.FingerprintId);
        Assert.Equal([0x01, 0x02, 0xA0, 0xFF], enrollment.TemplateBytes);
    }

    [Fact]
    public void DuplicateEnrollmentResult_FromSameDevice_IsHandledOnce()
    {
        var handler = new MessageHandler();
        var device = new DeviceClient { DeviceId = "CABINET_001" };
        int count = 0;
        handler.OnFingerprintEnrollmentResult += (_, _) => count++;
        var message = new Message
        {
            MsgId = "request-2",
            Cmd = Protocol.CmdAddFingerprintResult,
            DeviceId = device.DeviceId,
            Data = JObject.FromObject(new { result = "success", fingerprint_id = 8 })
        };

        handler.HandleMessage(device, message);
        handler.HandleMessage(device, message);

        Assert.Equal(1, count);
    }

    [Fact]
    public void SyncAck_WithSharedBroadcastId_IsHandledForEachDevice()
    {
        var handler = new MessageHandler();
        var devices = new[]
        {
            new DeviceClient { DeviceId = "CABINET_001" },
            new DeviceClient { DeviceId = "CABINET_002" }
        };
        var confirmed = new List<string>();
        handler.OnPermissionSyncResult += (deviceId, _, _) => confirmed.Add(deviceId);

        foreach (var device in devices)
        {
            handler.HandleMessage(device, new Message
            {
                MsgId = "broadcast-commit-1",
                Cmd = Protocol.CmdSyncAck,
                DeviceId = device.DeviceId,
                Data = JObject.FromObject(new { result = "success" })
            });
        }

        Assert.Equal(["CABINET_001", "CABINET_002"], confirmed);
    }

    [Fact]
    public void RootRegister_ReportsProtocolAndSdStateSeparately()
    {
        var handler = new MessageHandler();
        var root = new DeviceClient { DeviceId = "ROOT_001" };
        string? registeredId = null;
        bool? sdReady = null;
        handler.OnRootDeviceRegistered += (deviceId, ready) =>
        {
            registeredId = deviceId;
            sdReady = ready;
        };

        handler.HandleMessage(root, new Message
        {
            Cmd = Protocol.CmdRegister,
            DeviceId = root.DeviceId,
            Data = JObject.FromObject(new { is_root = true, sd_ready = false })
        });

        Assert.Equal("ROOT_001", registeredId);
        Assert.False(sdReady);
        Assert.True(root.IsRoot);
    }

    [Theory]
    [InlineData(Protocol.CmdAddFingerprintResult, CommandType.AddFingerprintResult)]
    [InlineData(Protocol.CmdSyncAck, CommandType.SyncAck)]
    public void Protocol_MapsResultCommands(string command, CommandType expected)
    {
        Assert.Equal(expected, Protocol.ToCommandType(command));
    }
}
