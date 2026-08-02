namespace CabinetLock.Tests;

public sealed class SingleInstanceGuardTests
{
    [Fact]
    public void Acquire_AllowsOnlyOnePrimaryInstance()
    {
        string mutexName = $@"Local\CabinetLock.Tests.{Guid.NewGuid():N}";

        using var primary = SingleInstanceGuard.Acquire(mutexName);
        using var secondary = SingleInstanceGuard.Acquire(mutexName);

        Assert.True(primary.IsPrimaryInstance);
        Assert.False(secondary.IsPrimaryInstance);
    }

    [Fact]
    public void Dispose_ReleasesMutexForNextInstance()
    {
        string mutexName = $@"Local\CabinetLock.Tests.{Guid.NewGuid():N}";

        using (var primary = SingleInstanceGuard.Acquire(mutexName))
        {
            Assert.True(primary.IsPrimaryInstance);
        }

        using var next = SingleInstanceGuard.Acquire(mutexName);
        Assert.True(next.IsPrimaryInstance);
    }
}
