namespace FingerprintLockManager.Tests;

public class CabinetDataSyncResultTests
{
    [Fact]
    public void Result_WithMissingTemplate_IsPartialEvenWhenPermissionsCommit()
    {
        var result = new CabinetDataSyncResult
        {
            DeviceId = "CAB_01",
            ExpectedFingerprintCount = 2,
            CurrentFingerprintCount = 0,
            RestoredFingerprintCount = 1,
            FingerprintFailures = new[] { "管理员（admin，ID 1）：本机和 SD 均无模板" },
            PermissionResult = BroadcastCommandResult.Succeeded(new[] { "CAB_01" })
        };

        Assert.False(result.Success);
        Assert.Equal(1, result.ConfirmedFingerprintCount);
        string summary = result.FormatForDisplay();
        Assert.Contains("权限已确认：2 条", summary);
        Assert.Contains("用户指纹已确认：1/2", summary);
        Assert.Contains("管理员", summary);
    }

    [Fact]
    public void Result_WithConfirmedTemplatesAndPermissions_IsSuccessful()
    {
        var result = new CabinetDataSyncResult
        {
            DeviceId = "CAB_01",
            ExpectedFingerprintCount = 2,
            CurrentFingerprintCount = 1,
            RestoredFingerprintCount = 1,
            PermissionResult = BroadcastCommandResult.Succeeded(new[] { "CAB_01" })
        };

        Assert.True(result.Success);
        Assert.Equal(2, result.ConfirmedFingerprintCount);
        Assert.DoesNotContain("未完成项", result.FormatForDisplay());
    }

    [Fact]
    public void FingerprintListResult_ExposesCompleteReportedStatus()
    {
        var status = new DeviceRuntimeStatus
        {
            FingerprintCount = 2,
            PermissionCount = 2,
            PermissionVersion = 1234
        };

        var result = new DeviceFingerprintListResult(
            new List<DeviceFingerprintInfo>(), status);

        Assert.Equal(2, result.ReportedFingerprintCount);
        Assert.Equal(2, result.ReportedStatus!.PermissionCount);
        Assert.Equal(1234u, result.ReportedStatus.PermissionVersion);
    }
}
