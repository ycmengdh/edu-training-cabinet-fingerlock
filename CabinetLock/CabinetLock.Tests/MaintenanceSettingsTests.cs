using Newtonsoft.Json.Linq;

namespace CabinetLock.Tests;

public class MaintenanceSettingsTests
{
    [Theory]
    [InlineData("112233", true)]
    [InlineData("123412", true)]
    [InlineData("11223", false)]
    [InlineData("1122334", false)]
    [InlineData("112235", false)]
    [InlineData("abcdef", false)]
    public void PinValidation_RequiresSixKeysFromOneToFour(string pin, bool expected)
    {
        Assert.Equal(expected, MaintenanceSettings.IsValidPin(pin));
    }

    [Theory]
    [InlineData("112233", "223344")]
    [InlineData("123412", "234523")]
    [InlineData("444444", "555555")]
    public void DevicePinEncoding_AddsOneToEveryDigit(string pin, string expected)
    {
        Assert.Equal(expected, MaintenanceSettings.EncodeForDevice(pin));
    }

    [Theory]
    [InlineData("26081006-cab", false)]
    [InlineData("26081007-cab", true)]
    [InlineData("26081101-cab", true)]
    [InlineData("", false)]
    public void DevicePinEncoding_RequiresCompatibleFirmware(
        string firmwareVersion, bool expected)
    {
        Assert.Equal(expected,
            MaintenanceSettings.SupportsDevicePinEncoding(firmwareVersion));
    }

    [Fact]
    public void MaintenanceSyncPayload_SendsEncodedPinAndEncodingMarker()
    {
        Message message = Message.Create(Protocol.CmdSyncMaintenanceConfig, "CAB_TEST", new
        {
            pin = MaintenanceSettings.EncodeForDevice("112233"),
            pin_encoding = MaintenanceSettings.DevicePinEncoding,
            version = 1
        });

        AppMessage mapped = AppMessageMapper.ToApp(message);
        JObject payload = JObject.Parse(System.Text.Encoding.UTF8.GetString(mapped.Payload));

        Assert.Equal("223344", payload.Value<string>("pin"));
        Assert.Equal("digit_plus_one", payload.Value<string>("pin_encoding"));
    }

    [Fact]
    public void Database_DefaultsMaintenancePinTo112233()
    {
        MaintenanceSettings settings = BusinessDatabase.GetMaintenanceSettings();

        Assert.Equal("112233", settings.Pin);
        Assert.True(settings.Version >= 1);
    }

    [Fact]
    public void Protocol_MapsMaintenanceCommands()
    {
        Assert.Equal(CmdIds.SyncMaintenanceConfig,
            CmdIds.ToCmdId(Protocol.CmdSyncMaintenanceConfig));
        Assert.Equal(Protocol.CmdEnterMaintenance,
            CmdIds.ToCmdName(CmdIds.EnterMaintenance));
        Assert.Equal(CommandType.MaintenanceEvent,
            Protocol.ToCommandType(Protocol.CmdMaintenanceEvent));
    }

    [Fact]
    public void SnapshotCodec_IncludesSystemSettings()
    {
        BusinessSnapshot snapshot = BusinessSnapshotCodec.Create(
            BusinessDatabase.DailySyncTables.ToDictionary(
                table => table,
                table => table == "system_settings"
                    ? new JArray(new JObject
                    {
                        ["setting_key"] = "maintenance_pin",
                        ["setting_value"] = "112233",
                        ["config_version"] = 1,
                        ["update_time"] = DateTime.Now.ToString("o")
                    })
                    : new JArray()));

        BusinessSnapshotPackage package = BusinessSnapshotCodec.ReadPackage(snapshot);

        Assert.True(package.Tables.ContainsKey("system_settings"));
        Assert.Equal("112233", package.Tables["system_settings"][0]!["setting_value"]);
    }
}
