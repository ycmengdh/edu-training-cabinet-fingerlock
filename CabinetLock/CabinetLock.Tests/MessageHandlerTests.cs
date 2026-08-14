using Newtonsoft.Json.Linq;

namespace CabinetLock.Tests;

public class MessageHandlerTests
{
    [Theory]
    [InlineData(Protocol.ErrMeshForwardFailed, true)]
    [InlineData(Protocol.ErrDeviceNotRegistered, true)]
    [InlineData("9103", false)]
    public void CommandService_ClassifiesRouteRecoveryErrorsAsTransient(
        string errorCode, bool expected)
    {
        Assert.Equal(expected, CommandService.IsTransientError(errorCode));
    }

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
    public void AckFromPreviousProcessSession_DoesNotConsumeCurrentAck()
    {
        var handler = new MessageHandler();
        var device = new DeviceClient { DeviceId = "CABINET_001" };
        int count = 0;
        handler.OnAckReceived += (_, _) => count++;

        handler.HandleMessage(device, new Message
        {
            MsgId = "7",
            CorrId = (ushort)(AppMessageMapper.SessionId == ushort.MaxValue
                ? AppMessageMapper.SessionId - 1
                : AppMessageMapper.SessionId + 1),
            Cmd = Protocol.CmdAck,
            DeviceId = device.DeviceId,
            Data = JObject.FromObject(new { result = "stale" })
        });
        handler.HandleMessage(device, new Message
        {
            MsgId = "7",
            CorrId = AppMessageMapper.SessionId,
            Cmd = Protocol.CmdAck,
            DeviceId = device.DeviceId,
            Data = JObject.FromObject(new { result = "open" })
        });

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

    [Fact]
    public void Register_ReportsMaintenanceVersionBeforeDeviceRegistration()
    {
        var handler = new MessageHandler();
        var device = new DeviceClient { DeviceId = "CABINET_001" };
        var order = new List<string>();
        uint reportedVersion = 0;
        handler.OnMaintenanceStatus += (_, data) =>
        {
            reportedVersion = data.Value<uint>("maintenance_config_version");
            order.Add("maintenance");
        };
        handler.OnDeviceRegistered += (_, _) => order.Add("registered");

        handler.HandleMessage(device, new Message
        {
            MsgId = "register-maintenance-version",
            Cmd = Protocol.CmdRegister,
            DeviceId = device.DeviceId,
            Data = JObject.FromObject(new
            {
                device_id = device.DeviceId,
                is_root = false,
                maintenance_config_version = 7
            })
        });

        Assert.Equal(7U, reportedVersion);
        Assert.Equal(new[] { "maintenance", "registered" }, order);
    }

    [Fact]
    public void ConfigResponse_MergesNonEmptyReportedVersions()
    {
        var handler = new MessageHandler();
        var device = new DeviceClient
        {
            DeviceId = "CABINET_001",
            FirmwareVersion = "3.3.0-idf",
            HardwareVersion = "cabinet-v1"
        };

        handler.HandleMessage(device, new Message
        {
            MsgId = "config-1",
            Cmd = Protocol.CmdConfigResponse,
            DeviceId = device.DeviceId,
            Data = JObject.FromObject(new
            {
                firmware_version = "3.4.0-idf",
                hardware_version = ""
            })
        });

        Assert.Equal("3.4.0-idf", device.FirmwareVersion);
        Assert.Equal("cabinet-v1", device.HardwareVersion);
    }

    [Theory]
    [InlineData(Protocol.CmdAddFingerprintResult, CommandType.AddFingerprintResult)]
    [InlineData(Protocol.CmdSyncAck, CommandType.SyncAck)]
    public void Protocol_MapsResultCommands(string command, CommandType expected)
    {
        Assert.Equal(expected, Protocol.ToCommandType(command));
    }
}
