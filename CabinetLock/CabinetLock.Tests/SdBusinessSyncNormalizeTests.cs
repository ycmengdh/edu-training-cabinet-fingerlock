using Newtonsoft.Json.Linq;

namespace CabinetLock.Tests;

public class SdBusinessSyncNormalizeTests
{
    [Fact]
    public void TableArraysEqual_IgnoresRowAndPropertyOrder()
    {
        var left = JArray.Parse("[{\"user_id\":\"2\",\"enabled\":true},{\"user_id\":\"1\",\"enabled\":false}]");
        var right = JArray.Parse("[{\"enabled\":false,\"user_id\":\"1\"},{\"enabled\":true,\"user_id\":\"2\"}]");

        Assert.True(SdBusinessSyncService.TableArraysEqual(left, right));
    }

    [Fact]
    public void TableArraysEqual_DetectsChangedBusinessValue()
    {
        var left = JArray.Parse("[{\"user_id\":\"1\",\"enabled\":true}]");
        var right = JArray.Parse("[{\"user_id\":\"1\",\"enabled\":false}]");

        Assert.False(SdBusinessSyncService.TableArraysEqual(left, right));
    }

    [Fact]
    public void NormalizeTableArray_AcceptsArray()
    {
        var arr = SdBusinessSyncService.NormalizeTableArray(
            """[{"user_id":"a"}]""", "users", out string? err);
        Assert.Null(err);
        Assert.NotNull(arr);
        Assert.Single(arr!);
        Assert.Equal("a", arr[0]!["user_id"]!.ToString());
    }

    [Fact]
    public void NormalizeTableArray_EmptyString_IsEmptyArray()
    {
        var arr = SdBusinessSyncService.NormalizeTableArray("", "classes", out string? err);
        Assert.Null(err);
        Assert.NotNull(arr);
        Assert.Empty(arr!);
    }

    [Fact]
    public void NormalizeTableArray_ItemsWrapper()
    {
        var arr = SdBusinessSyncService.NormalizeTableArray(
            """{"items":[{"class_id":"c1"}]}""", "classes", out string? err);
        Assert.Null(err);
        Assert.NotNull(arr);
        Assert.Single(arr!);
    }

    [Fact]
    public void NormalizeTableArray_SingleObjectWrapped()
    {
        var arr = SdBusinessSyncService.NormalizeTableArray(
            """{"role":"admin","lock_0":true}""", "role_permissions", out string? err);
        Assert.Null(err);
        Assert.NotNull(arr);
        Assert.Single(arr!);
        Assert.Equal("admin", arr[0]!["role"]!.ToString());
    }

    [Fact]
    public void NormalizeTableArray_InvalidJson_ReturnsNull()
    {
        var arr = SdBusinessSyncService.NormalizeTableArray("{bad", "users", out string? err);
        Assert.Null(arr);
        Assert.False(string.IsNullOrWhiteSpace(err));
    }
}
