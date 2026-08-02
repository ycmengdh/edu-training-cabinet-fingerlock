namespace CabinetLock.Tests;

public class BinaryMessageCodecTests
{
    [Fact]
    public void EncodeDecode_RoundTripsEnvelope()
    {
        var original = new AppMessage
        {
            Flags = AppMessageFlags.NeedsAck | AppMessageFlags.Broadcast,
            CmdId = CmdIds.Register,
            MsgId = 0x1234,
            CorrId = 0x5678,
            DeviceId = "CABINET_001",
            SourceDeviceId = "ROOT_001",
            TimestampUnix = 1_700_000_000,
            Payload = new byte[] { 0x01, 0x02, 0x03 },
        };

        byte[] encoded = BinaryMessageCodec.Encode(original);
        Assert.Equal(BinaryMessageCodec.AppMagicLo, encoded[0]);
        Assert.Equal(BinaryMessageCodec.AppMagicHi, encoded[1]);
        Assert.Equal(BinaryMessageCodec.ProtoVer, encoded[2]);

        Assert.True(BinaryMessageCodec.TryDecode(encoded, out AppMessage? decoded));
        Assert.NotNull(decoded);
        Assert.Equal(original.Flags, decoded!.Flags);
        Assert.Equal(original.CmdId, decoded.CmdId);
        Assert.Equal(original.MsgId, decoded.MsgId);
        Assert.Equal(original.CorrId, decoded.CorrId);
        Assert.Equal(original.DeviceId, decoded.DeviceId);
        Assert.Equal(original.SourceDeviceId, decoded.SourceDeviceId);
        Assert.Equal(original.TimestampUnix, decoded.TimestampUnix);
        Assert.Equal(original.Payload, decoded.Payload);
    }

    [Fact]
    public void EncodeDecode_WithHmac_RoundTrips()
    {
        var original = new AppMessage
        {
            Flags = AppMessageFlags.NeedsAck | AppMessageFlags.HasHmac,
            CmdId = CmdIds.ControlLock,
            MsgId = 42,
            DeviceId = "CABINET_002",
            TimestampUnix = 1_700_000_100,
            Payload = BinaryMessageCodec.ControlLockPayload.Pack(1, 1),
            HmacTs = 1_700_000_100,
            HmacNonce = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            HmacSig = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray(),
        };

        byte[] encoded = BinaryMessageCodec.Encode(original);
        Assert.True(BinaryMessageCodec.TryDecode(encoded, out AppMessage? decoded));
        Assert.NotNull(decoded);
        Assert.True(decoded!.HasHmac);
        Assert.Equal(original.HmacTs, decoded.HmacTs);
        Assert.Equal(original.HmacNonce, decoded.HmacNonce);
        Assert.Equal(original.HmacSig, decoded.HmacSig);
        Assert.Equal(original.Payload, decoded.Payload);
    }

    [Fact]
    public void TryDecode_RejectsInvalidMagic()
    {
        var msg = new AppMessage
        {
            CmdId = CmdIds.Heartbeat,
            DeviceId = "ROOT_001",
            TimestampUnix = 1,
            Payload = Array.Empty<byte>(),
        };
        byte[] encoded = BinaryMessageCodec.Encode(msg);
        encoded[0] = 0x00; // corrupt magic

        Assert.False(BinaryMessageCodec.TryDecode(encoded, out AppMessage? decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void TryDecode_RejectsTruncatedBuffer()
    {
        var msg = new AppMessage
        {
            CmdId = CmdIds.Ack,
            DeviceId = "CABINET_001",
            TimestampUnix = 1,
            Payload = new byte[10],
        };
        byte[] encoded = BinaryMessageCodec.Encode(msg);
        byte[] truncated = encoded.Take(encoded.Length - 3).ToArray();

        Assert.False(BinaryMessageCodec.TryDecode(truncated, out _));
    }

    [Fact]
    public void HeartbeatPayload_RoundTrips()
    {
        byte[] packed = BinaryMessageCodec.HeartbeatPayload.Pack(
            freeHeap: 120_000,
            freePsram: 6_000_000,
            minFreeHeap: 40_000,
            meshLayer: 2,
            flags: 0x03,
            sendFail: 1,
            queueFull: 2,
            recoveries: 3);

        Assert.Equal(BinaryMessageCodec.HeartbeatPayload.Size, packed.Length);
        Assert.True(BinaryMessageCodec.HeartbeatPayload.TryUnpack(
            packed,
            out uint freeHeap, out uint freePsram, out ushort minFreeHeap,
            out byte meshLayer, out byte flags,
            out ushort sendFail, out ushort queueFull, out ushort recoveries));

        Assert.Equal(120_000u, freeHeap);
        Assert.Equal(6_000_000u, freePsram);
        Assert.Equal((ushort)40_000, minFreeHeap);
        Assert.Equal(2, meshLayer);
        Assert.Equal(0x03, flags);
        Assert.Equal(1, sendFail);
        Assert.Equal(2, queueFull);
        Assert.Equal(3, recoveries);

        var envelope = new AppMessage
        {
            CmdId = CmdIds.Heartbeat,
            MsgId = 7,
            DeviceId = "ROOT_001",
            TimestampUnix = 99,
            Payload = packed,
        };
        byte[] wire = BinaryMessageCodec.Encode(envelope);
        Assert.True(BinaryMessageCodec.TryDecode(wire, out AppMessage? decoded));
        Assert.Equal(CmdIds.Heartbeat, decoded!.CmdId);
        Assert.Equal(packed, decoded.Payload);
    }

    [Fact]
    public void CabinetStatusPayload_MapsToExistingStatusFields()
    {
        byte[] payload = new byte[BinaryMessageCodec.CabinetStatusPayload.Size];
        payload[0] = BinaryMessageCodec.CabinetStatusPayload.Version;
        payload[1] = 0x05;
        payload[2] = 2;
        payload[3] = 0x03;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4, 4), 1234);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(8, 2), 7);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(10, 2), 42);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(12, 4), 60);
        payload[20] = unchecked((byte)-55);
        payload[21] = 120;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(payload.AsSpan(22, 2), 22);

        Message message = AppMessageMapper.ToMessage(new AppMessage
        {
            CmdId = CmdIds.StatusResponse,
            MsgId = 9,
            DeviceId = "CABINET_001",
            Payload = payload,
        });

        var data = Assert.IsType<Newtonsoft.Json.Linq.JObject>(message.Data);
        Assert.Equal(1234u, data.Value<uint>("uptime"));
        Assert.Equal(7, data.Value<int>("fingerprint_count"));
        Assert.Equal(60u, data.Value<uint>("perm_version"));
        Assert.Equal(-55, data.Value<int>("mesh_link_rssi"));
        Assert.Equal("mesh", data.Value<string>("work_mode"));
        Assert.True(data.Value<bool>("time_synced"));
        Assert.Equal(new[] { 1, 0, 1, 0 }, data["lock_status"]!.Values<int>());
    }

    [Fact]
    public void ControlLockPayload_RoundTrips()
    {
        byte[] packed = BinaryMessageCodec.ControlLockPayload.Pack(3, 1);
        Assert.True(BinaryMessageCodec.ControlLockPayload.TryUnpack(
            packed, out byte lockId, out byte action));
        Assert.Equal(3, lockId);
        Assert.Equal(1, action);

        var envelope = new AppMessage
        {
            Flags = AppMessageFlags.NeedsAck,
            CmdId = CmdIds.ControlLock,
            MsgId = 100,
            DeviceId = "CABINET_003",
            TimestampUnix = 1_700_000_200,
            Payload = packed,
        };
        byte[] wire = BinaryMessageCodec.Encode(envelope);
        Assert.True(BinaryMessageCodec.TryDecode(wire, out AppMessage? decoded));
        Assert.Equal(CmdIds.ControlLock, decoded!.CmdId);
        Assert.True(BinaryMessageCodec.ControlLockPayload.TryUnpack(
            decoded.Payload, out byte lockId2, out byte action2));
        Assert.Equal(3, lockId2);
        Assert.Equal(1, action2);
    }

    [Fact]
    public void SyncPermissionPayload_RoundTrips()
    {
        byte[] packed = BinaryMessageCodec.SyncPermissionPayload.Pack(
            version: 12,
            total: 50,
            sequence: 7,
            fingerprintId: 42,
            role: 1,
            lockMask: 0x0F,
            expireDays: 365,
            name: "张三",
            userId: "U001");

        Assert.True(BinaryMessageCodec.SyncPermissionPayload.TryUnpack(
            packed,
            out uint version, out ushort total, out ushort sequence,
            out ushort fingerprintId, out byte role, out byte lockMask, out uint expireDays,
            out string name, out string userId));

        Assert.Equal(12u, version);
        Assert.Equal(50, total);
        Assert.Equal(7, sequence);
        Assert.Equal(42, fingerprintId);
        Assert.Equal(1, role);
        Assert.Equal(0x0F, lockMask);
        Assert.Equal(365u, expireDays);
        Assert.Equal("张三", name);
        Assert.Equal("U001", userId);

        var envelope = new AppMessage
        {
            Flags = AppMessageFlags.NeedsAck,
            CmdId = CmdIds.SyncPermission,
            MsgId = 200,
            CorrId = 1,
            DeviceId = "CABINET_001",
            SourceDeviceId = "PC",
            TimestampUnix = 1_700_000_300,
            Payload = packed,
        };
        byte[] wire = BinaryMessageCodec.Encode(envelope);
        Assert.True(BinaryMessageCodec.TryDecode(wire, out AppMessage? decoded));
        Assert.Equal(CmdIds.SyncPermission, decoded!.CmdId);
        Assert.Equal(packed, decoded.Payload);
    }

    [Fact]
    public void TemplatePayload_RoundTrips()
    {
        byte[] template = Enumerable.Range(0, 512).Select(i => (byte)(i & 0xFF)).ToArray();
        byte[] packed = BinaryMessageCodec.TemplatePayload.Pack(
            fingerprintId: 9, flags: 0x01, userId: "U009", template: template);

        Assert.True(BinaryMessageCodec.TemplatePayload.TryUnpack(
            packed, out ushort fpId, out byte flags, out string userId, out byte[] tmpl));
        Assert.Equal(9, fpId);
        Assert.Equal(0x01, flags);
        Assert.Equal("U009", userId);
        Assert.Equal(template, tmpl);

        var envelope = new AppMessage
        {
            Flags = AppMessageFlags.NeedsAck,
            CmdId = CmdIds.UploadFpTemplate,
            MsgId = 300,
            DeviceId = "ROOT_001",
            TimestampUnix = 1_700_000_400,
            Payload = packed,
        };
        byte[] wire = BinaryMessageCodec.Encode(envelope);
        Assert.True(BinaryMessageCodec.TryDecode(wire, out AppMessage? decoded));
        Assert.Equal(CmdIds.UploadFpTemplate, decoded!.CmdId);
        Assert.True(BinaryMessageCodec.TemplatePayload.TryUnpack(
            decoded.Payload, out _, out _, out _, out byte[] tmpl2));
        Assert.Equal(template, tmpl2);
    }

    [Fact]
    public void AckAndErrorPayload_RoundTrip()
    {
        byte[] ack = BinaryMessageCodec.AckPayload.Pack(0xABCD, 0, "success");
        Assert.True(BinaryMessageCodec.AckPayload.TryUnpack(
            ack, out ushort refId, out ushort code, out string tag));
        Assert.Equal(0xABCD, refId);
        Assert.Equal(0, code);
        Assert.Equal("success", tag);

        byte[] err = BinaryMessageCodec.ErrorPayload.Pack(0x1111, 2, "bad param");
        Assert.True(BinaryMessageCodec.ErrorPayload.TryUnpack(
            err, out ushort ref2, out ushort errCode, out string message));
        Assert.Equal(0x1111, ref2);
        Assert.Equal(2, errCode);
        Assert.Equal("bad param", message);
    }

    [Fact]
    public void CmdIds_MapsNamesBidirectionally()
    {
        Assert.Equal(Protocol.CmdHeartbeat, CmdIds.ToCmdName(CmdIds.Heartbeat));
        Assert.Equal(CmdIds.ControlLock, CmdIds.ToCmdId(Protocol.CmdControlLock));
        Assert.Equal(CmdIds.SyncPermission, CmdIds.ToCmdId(Protocol.CmdSyncPermission));
        Assert.Null(CmdIds.ToCmdId("NOT_A_REAL_CMD"));
        Assert.Null(CmdIds.ToCmdName(0xFFFF));
    }

    [Fact]
    public void FrameCodec_EncodeBytes_RoundTrips()
    {
        var app = new AppMessage
        {
            CmdId = CmdIds.TimeSync,
            MsgId = 1,
            DeviceId = "ROOT_001",
            TimestampUnix = 1_700_000_500,
            Payload = BinaryMessageCodec.TimeSyncPayload.Pack(1_700_000_500),
        };
        byte[] appBytes = BinaryMessageCodec.Encode(app);
        byte[] frame = Assert.IsType<byte[]>(FrameCodec.Encode(appBytes));

        Assert.True(FrameCodec.TryDecodeBytes(frame, out byte[]? payload));
        Assert.Equal(appBytes, payload);

        Assert.True(BinaryMessageCodec.TryDecode(payload, out AppMessage? decoded));
        Assert.Equal(CmdIds.TimeSync, decoded!.CmdId);
        Assert.True(BinaryMessageCodec.TimeSyncPayload.TryUnpack(
            decoded.Payload, out uint ts));
        Assert.Equal(1_700_000_500u, ts);
    }

    [Fact]
    public void FrameStreamDecoder_AppendBytes_EmitsRawPayload()
    {
        byte[] payload = BinaryMessageCodec.Encode(new AppMessage
        {
            CmdId = CmdIds.SdQuery,
            DeviceId = "ROOT_001",
            TimestampUnix = 1,
            Payload = BinaryMessageCodec.SdQueryPayload.Pack(2),
        });
        byte[] frame = Assert.IsType<byte[]>(FrameCodec.Encode(payload));
        var messages = new List<byte[]>();

        new FrameStreamDecoder().AppendBytes(frame, 0, frame.Length, messages.Add);

        Assert.Equal([payload], messages);
    }
}
