namespace CabinetLock.Tests;

public class ClassLifecycleResultTests
{
    [Fact]
    public void SkippedResult_PreservesDistinctOutcomeAndDetails()
    {
        ClassLifecycleResult result = ClassLifecycleResult.Skipped(
            "柜机离线，保留班级数据", new[] { "CAB_01：柜机离线" });

        Assert.False(result.Success);
        Assert.True(result.WasSkipped);
        Assert.False(result.IsPartial);
        Assert.Equal("柜机离线，保留班级数据", result.Message);
        Assert.Single(result.Failures);
    }

    [Fact]
    public void PartialResult_ReportsDataChangeAndSkippedItems()
    {
        ClassLifecycleResult result = ClassLifecycleResult.Partial(
            "已删除 2 名，跳过 1 名", new[] { "学生 A：柜机离线" });

        Assert.True(result.Success);
        Assert.False(result.WasSkipped);
        Assert.True(result.IsPartial);
        Assert.Single(result.Failures);
    }
}
