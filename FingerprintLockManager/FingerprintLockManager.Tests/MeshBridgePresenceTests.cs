using System.Reflection;

namespace FingerprintLockManager.Tests;

public class MeshBridgePresenceTests
{
    [Fact]
    public void CabinetWithoutHeartbeat_IsExpired_AndNextMessageReconnectsIt()
    {
        var bridge = new MeshBridge();
        int connected = 0;
        int disconnected = 0;
        bridge.DeviceConnected += _ => connected++;
        bridge.DeviceDisconnected += _ => disconnected++;

        Receive(bridge, "{\"cmd\":\"HEARTBEAT\",\"device_id\":\"CABINET_001\",\"data\":{}}");
        DeviceClient cabinet = Assert.Single(bridge.Devices);
        cabinet.LastSeen = DateTime.Now - TimeSpan.FromSeconds(36);

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
        root.LastSeen = DateTime.Now - TimeSpan.FromSeconds(60);

        Assert.Single(bridge.GetOnlineDevices());

        root.LastSeen = DateTime.Now - TimeSpan.FromSeconds(76);
        Assert.Empty(bridge.GetOnlineDevices());
    }

    private static void Receive(MeshBridge bridge, string json)
    {
        MethodInfo method = typeof(MeshBridge).GetMethod(
            "OnLineReceived", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("MeshBridge.OnLineReceived not found");
        method.Invoke(bridge, new object[] { json });
    }
}
