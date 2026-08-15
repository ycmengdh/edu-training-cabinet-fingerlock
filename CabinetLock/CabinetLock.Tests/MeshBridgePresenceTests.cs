using System.Reflection;
using System.Text;

namespace CabinetLock.Tests;

public class MeshBridgePresenceTests
{
    [Fact]
    public void CabinetWithoutHeartbeat_PastConfiguredTimeout_IsExpired_AndReconnects()
    {
        var bridge = new MeshBridge();
        int connected = 0;
        int disconnected = 0;
        bridge.DeviceConnected += _ => connected++;
        bridge.DeviceDisconnected += _ => disconnected++;

        Receive(bridge, "{\"cmd\":\"HEARTBEAT\",\"device_id\":\"CABINET_001\",\"data\":{}}");
        DeviceClient cabinet = Assert.Single(bridge.Devices);
        int configuredTimeout = Math.Clamp(
            ConfigHelper.Current.OfflineTimeoutSeconds, 10, 3600);
        cabinet.LastSeen = DateTime.Now - TimeSpan.FromSeconds(configuredTimeout + 1);

        Assert.Empty(bridge.GetOnlineDevices());
        Assert.False(cabinet.IsOnline);
        Assert.Equal(1, disconnected);

        Receive(bridge, "{\"cmd\":\"HEARTBEAT\",\"device_id\":\"CABINET_001\",\"data\":{}}");

        Assert.True(cabinet.IsOnline);
        Assert.Equal(2, connected);
    }

    [Fact]
    public void RootUsesStatusReportTimeout_NotCabinetHeartbeatTimeout()
    {
        var bridge = new MeshBridge();
        Receive(bridge, "{\"cmd\":\"REGISTER\",\"device_id\":\"ROOT_001\",\"data\":{\"is_root\":true}}");
        DeviceClient root = Assert.Single(bridge.Devices);
        root.IsRoot = true;
        root.LastSeen = DateTime.Now - TimeSpan.FromSeconds(4);

        Assert.Single(bridge.GetOnlineDevices());

        root.LastSeen = DateTime.Now - TimeSpan.FromSeconds(6);
        Assert.Empty(bridge.GetOnlineDevices());
    }

    [Fact]
    public void SameLogicalId_FromDifferentMacs_RemainsTwoPhysicalDevices()
    {
        var bridge = new MeshBridge();

        Receive(bridge,
            "{\"cmd\":\"HEARTBEAT\",\"device_id\":\"CAB_DUP\",\"data\":{\"mesh_mac\":\"AA:00:00:00:00:01\"}}");
        Receive(bridge,
            "{\"cmd\":\"HEARTBEAT\",\"device_id\":\"CAB_DUP\",\"data\":{\"mesh_mac\":\"AA:00:00:00:00:02\"}}");

        Assert.Equal(2, bridge.GetOnlineDevices().Count);
        Assert.Equal(2, bridge.Devices.Select(d => d.MeshMac).Distinct().Count());
    }

    [Fact]
    public void CabinetIdAndReportedMac_AreOnePhysicalDevice_AndKeepStatus()
    {
        var bridge = new MeshBridge();
        Receive(bridge,
            "{\"cmd\":\"STATUS_REPORT\",\"device_id\":\"CAB_14C19F394688\"," +
            "\"source_device_id\":\"CAB_14C19F394688\",\"data\":{" +
            "\"fingerprint_count\":3,\"perm_count\":2,\"perm_version\":42}}");

        DeviceClient statusDevice = Assert.Single(bridge.Devices);
        Assert.Equal("14:C1:9F:39:46:88", statusDevice.MeshMac);
        Assert.Equal(3, statusDevice.Status.FingerprintCount);

        Receive(bridge,
            "{\"cmd\":\"REGISTER\",\"device_id\":\"CAB_14C19F394688\"," +
            "\"source_device_id\":\"CAB_14C19F394688\",\"data\":{" +
            "\"device_id\":\"CAB_14C19F394688\",\"mesh_mac\":\"14C19F394688\"," +
            "\"is_root\":false}}");

        DeviceClient registeredDevice = Assert.Single(bridge.Devices);
        Assert.Same(statusDevice, registeredDevice);
        Assert.Equal(3, registeredDevice.Status.FingerprintCount);
        Assert.Equal(2, registeredDevice.Status.PermissionCount);
        Assert.Equal(42U, registeredDevice.Status.PermissionVersion);
    }

    [Fact]
    public void RegisterWithMac_ClaimsEarlierLogicalIdPlaceholder()
    {
        var bridge = new MeshBridge();
        Receive(bridge,
            "{\"cmd\":\"HEARTBEAT\",\"device_id\":\"LEGACY_CABINET_01\",\"data\":{}}");
        DeviceClient placeholder = Assert.Single(bridge.Devices);

        Receive(bridge,
            "{\"cmd\":\"REGISTER\",\"device_id\":\"LEGACY_CABINET_01\",\"data\":{" +
            "\"mesh_mac\":\"AA:00:00:00:00:01\",\"is_root\":false}}");

        DeviceClient registered = Assert.Single(bridge.Devices);
        Assert.Same(placeholder, registered);
        Assert.Equal("AA:00:00:00:00:01", registered.MeshMac);
    }

    [Fact]
    public void ConfigResponse_FillsMissingVersions_WithoutClearingReportedValues()
    {
        var bridge = new MeshBridge();
        Receive(bridge,
            "{\"cmd\":\"HEARTBEAT\",\"device_id\":\"CABINET_001\",\"data\":{}}");

        Receive(bridge,
            "{\"cmd\":\"CONFIG_RESPONSE\",\"device_id\":\"CABINET_001\",\"data\":{" +
            "\"firmware_version\":\"3.4.0-idf\",\"hardware_version\":\"cabinet-v1\"}}");
        DeviceClient cabinet = Assert.Single(bridge.Devices);
        Assert.Equal("3.4.0-idf", cabinet.FirmwareVersion);
        Assert.Equal("cabinet-v1", cabinet.HardwareVersion);

        Receive(bridge,
            "{\"cmd\":\"CONFIG_RESPONSE\",\"device_id\":\"CABINET_001\",\"data\":{" +
            "\"firmware_version\":\"\",\"hardware_version\":null}}");
        Assert.Equal("3.4.0-idf", cabinet.FirmwareVersion);
        Assert.Equal("cabinet-v1", cabinet.HardwareVersion);
    }

    [Fact]
    public void EspBootLog_IsMarkedAsDeviceRestart()
    {
        var bridge = new MeshBridge();
        CommunicationTraceEntry? trace = null;
        bridge.TraceAdded += item => trace = item;
        MethodInfo method = typeof(MeshBridge).GetMethod(
            "OnUnframedDataReceived", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MeshBridge.OnUnframedDataReceived not found");

        method.Invoke(bridge, new object[]
        {
            Encoding.UTF8.GetBytes("ESP-ROM:esp32s3\r\nrst:0x1 (POWERON_RESET)\r\n")
        });

        Assert.NotNull(trace);
        Assert.Equal("设备启动/重启", trace.Category);
        Assert.Equal(CommunicationDirection.System, trace.Direction);
    }

    /// <summary>
    /// 注入应用层负载。优先二进制信封；测试用整包 JSON 走 OnPayloadReceived 兼容分支。
    /// （OnLineReceived 已废弃为空，避免与 PayloadReceived 双处理。）
    /// </summary>
    private static void Receive(MeshBridge bridge, string json)
    {
        MethodInfo method = typeof(MeshBridge).GetMethod(
            "OnPayloadReceived", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MeshBridge.OnPayloadReceived not found");
        byte[] payload = Encoding.UTF8.GetBytes(json);
        method.Invoke(bridge, new object[] { payload });
    }
}
