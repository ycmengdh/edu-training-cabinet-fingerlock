namespace CabinetLock.Tests;

public sealed class CabinetIncrementalSyncPlannerTests
{
    [Fact]
    public void OneNewBinding_KeepsTenExistingRowsUntouched()
    {
        CabinetPermissionDescriptor[] expected = Enumerable.Range(1, 11)
            .Select(id => Permission(id, $"U{id:000}"))
            .ToArray();
        FingerprintSlotRecord[] actual = Enumerable.Range(1, 10)
            .Select(id => Slot(id, $"U{id:000}"))
            .ToArray();

        CabinetIncrementalSyncPlan plan = CabinetIncrementalSyncPlanner.Build(
            expected, actual, new[] { "U011" }, reportedPermissionCount: 10);

        Assert.Equal(10, plan.TrustedFingerprintCount);
        Assert.Equal(10, plan.UnchangedPermissionCount);
        Assert.Equal(new[] { 11 }, plan.MissingFingerprintIds);
        Assert.Equal(new[] { 11 }, plan.PermissionUpsertFingerprintIds);
        Assert.Empty(plan.FingerprintIdsToVerify);
        Assert.Empty(plan.StaleFingerprintIds);
        Assert.False(plan.UseFullPermissionTransaction);
    }

    [Fact]
    public void ExistingChangedUser_VerifiesAndUpdatesOnlyThatUser()
    {
        CabinetPermissionDescriptor[] expected = Enumerable.Range(1, 10)
            .Select(id => Permission(id, $"U{id:000}", lockMask: id == 6 ? 0x0E : 0x02))
            .ToArray();
        FingerprintSlotRecord[] actual = Enumerable.Range(1, 10)
            .Select(id => Slot(id, $"U{id:000}", lockMask: 0x02))
            .ToArray();

        CabinetIncrementalSyncPlan plan = CabinetIncrementalSyncPlanner.Build(
            expected, actual, new[] { "U006" }, reportedPermissionCount: 10);

        Assert.Equal(new[] { 6 }, plan.FingerprintIdsToVerify);
        Assert.Equal(new[] { 6 }, plan.PermissionUpsertFingerprintIds);
        Assert.Equal(9, plan.TrustedFingerprintCount);
        Assert.Equal(9, plan.UnchangedPermissionCount);
        Assert.False(plan.UseFullPermissionTransaction);
    }

    [Fact]
    public void EmptyCabinetWithManyExpectedRows_UsesFullPermissionTransaction()
    {
        CabinetPermissionDescriptor[] expected = Enumerable.Range(1, 10)
            .Select(id => Permission(id, $"U{id:000}"))
            .ToArray();

        CabinetIncrementalSyncPlan plan = CabinetIncrementalSyncPlanner.Build(
            expected, Array.Empty<FingerprintSlotRecord>(), reportedPermissionCount: 0);

        Assert.Equal(10, plan.MissingFingerprintIds.Length);
        Assert.Equal(10, plan.PermissionUpsertFingerprintIds.Length);
        Assert.True(plan.UseFullPermissionTransaction);
    }

    [Fact]
    public void ReportedPermissionWithoutVisibleSlot_UsesRepairTransaction()
    {
        CabinetIncrementalSyncPlan plan = CabinetIncrementalSyncPlanner.Build(
            new[] { Permission(1, "U001") },
            new[] { Slot(1, "U001") },
            reportedPermissionCount: 2);

        Assert.Equal(1, plan.OrphanPermissionCount);
        Assert.True(plan.UseFullPermissionTransaction);
    }

    private static CabinetPermissionDescriptor Permission(
        int fingerprintId, string userId, int lockMask = 0x02) =>
        new(fingerprintId, userId, $"用户{fingerprintId}", 2, lockMask);

    private static FingerprintSlotRecord Slot(
        int fingerprintId, string userId, int lockMask = 0x02) => new()
        {
            Slot = fingerprintId,
            Bound = true,
            FingerprintId = fingerprintId,
            UserId = userId,
            Name = $"用户{fingerprintId}",
            Role = 2,
            LockMask = lockMask
        };
}
