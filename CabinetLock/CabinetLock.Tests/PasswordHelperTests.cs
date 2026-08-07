namespace CabinetLock.Tests;

public class PasswordHelperTests
{
    private const string LegacySalt = "000102030405060708090a0b0c0d0e0f";
    private const string LegacyAdminHash =
        "eb427d2e310382de4e4bf02b93005681040294011a20356bb0348fc49ad70a8f";

    [Fact]
    public void HashPassword_UsesVersionedPbkdf2AndVerifies()
    {
        string salt = PasswordHelper.GenerateSalt();
        string hash = PasswordHelper.HashPassword("A-strong-password", salt);

        Assert.StartsWith("pbkdf2-sha256$", hash);
        Assert.True(PasswordHelper.VerifyPassword("A-strong-password", salt, hash));
        Assert.False(PasswordHelper.VerifyPassword("wrong-password", salt, hash));
        Assert.False(PasswordHelper.NeedsRehash(hash));
    }

    [Fact]
    public void VerifyPassword_AcceptsLegacyHashForMigration()
    {
        Assert.True(PasswordHelper.VerifyPassword("admin123", LegacySalt, LegacyAdminHash));
        Assert.False(PasswordHelper.VerifyPassword("Admin123", LegacySalt, LegacyAdminHash));
        Assert.True(PasswordHelper.NeedsRehash(LegacyAdminHash));
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    public void PasswordPolicy_RejectsValuesShorterThanSixCharacters(string password)
    {
        Assert.False(PasswordHelper.IsPasswordAcceptable(password));
    }

    [Fact]
    public void PasswordPolicy_AcceptsSixThroughOneHundredTwentyEightCharacters()
    {
        Assert.True(PasswordHelper.IsPasswordAcceptable("123456"));
        Assert.True(PasswordHelper.IsPasswordAcceptable(new string('a', 128)));
    }

    [Fact]
    public void PasswordPolicy_RejectsValuesLongerThanOneHundredTwentyEightCharacters()
    {
        Assert.False(PasswordHelper.IsPasswordAcceptable(new string('a', 129)));
    }
}
