namespace CabinetLock.Tests;

public class LockNamingTests
{
    [Theory]
    [InlineData(0, "Lock 1")]
    [InlineData(1, "Lock 2")]
    [InlineData(2, "Lock 3")]
    [InlineData(3, "Lock 4")]
    [InlineData(-1, "-")]
    [InlineData(4, "-")]
    public void ToDisplayName_ConvertsProtocolIndexToUserFacingName(
        int lockId,
        string expected)
    {
        Assert.Equal(expected, LockNaming.ToDisplayName(lockId));
    }
}
