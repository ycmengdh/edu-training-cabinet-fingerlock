namespace CabinetLock.Tests;

public class FingerprintTemplateSummaryTests
{
    [Fact]
    public void BuildEnabledTemplateCounts_CountsDistinctEnabledTemplatesPerUser()
    {
        var templates = new[]
        {
            new FingerprintTemplate { FingerprintId = 11, UserId = "A", Enabled = true },
            new FingerprintTemplate { FingerprintId = 11, UserId = "A", Enabled = true },
            new FingerprintTemplate { FingerprintId = 12, UserId = "A", Enabled = false },
            new FingerprintTemplate { FingerprintId = 21, UserId = "B", Enabled = true },
            new FingerprintTemplate { FingerprintId = 22, UserId = "B", Enabled = true },
            new FingerprintTemplate { FingerprintId = 31, UserId = "", Enabled = true }
        };

        Dictionary<string, int> counts = FingerprintTemplateService
            .BuildEnabledTemplateCounts(templates);

        Assert.Equal(1, counts["A"]);
        Assert.Equal(2, counts["B"]);
        Assert.Equal(2, counts.Count);
    }

    [Fact]
    public void FingerprintSummary_ShowsCountAndDefaultTemplate()
    {
        var user = new User { FingerprintId = 21, FingerprintCount = 2 };

        Assert.Equal("2 枚 · 默认 #21", user.FingerprintSummary);

        user.FingerprintCount = 0;
        Assert.Equal("未录入", user.FingerprintSummary);
    }

    [Fact]
    public void FingerprintSummary_UsesAutomaticEffectiveDefault()
    {
        var user = new User { FingerprintCount = 1, EffectiveFingerprintId = 31 };

        Assert.Equal("1 枚 · 默认 #31", user.FingerprintSummary);
    }
}
