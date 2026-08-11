namespace CabinetLock.Tests;

public sealed class FingerprintEnrollmentPromptTests
{
    [Theory]
    [InlineData("verify_lift_1", "先松开手指")]
    [InlineData("verify_place_1", "第一次验证")]
    [InlineData("verify_retry_lift_1", "第一次验证未识别")]
    [InlineData("verify_lift_2", "请松开手指")]
    [InlineData("verify_place_2", "第二次验证")]
    [InlineData("verify_retry_lift_2", "第二次验证未识别")]
    public void VerificationPrompts_FollowReleaseAndPressSequence(
        string phase, string expectedText)
    {
        string hint = FingerprintEnrollmentPrompts.GetHint(phase);

        Assert.Contains(expectedText, hint);
        Assert.DoesNotContain("finger", hint, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("fingerprints did not match", "指纹不一致")]
    [InlineData("fingerprint verification failed", "验证未通过")]
    [InlineData("fingerprint enrollment timeout", "操作超时")]
    [InlineData("device busy", "其他操作")]
    public void FirmwareErrors_AreLocalizedForUsers(string error, string expectedText)
    {
        Assert.Contains(expectedText, FingerprintEnrollmentPrompts.LocalizeError(error));
    }

    [Fact]
    public void GenericFailure_DuringVerification_UsesVerificationSpecificMessage()
    {
        string message = FingerprintEnrollmentPrompts.EnhanceFailureForPhase(
            "unknown enrollment failure", "verify_place_1");

        Assert.Contains("调整手指按压位置", message);
    }

    [Fact]
    public void VerificationFailureAfterRetries_ExplainsAttemptLimit()
    {
        string message = FingerprintEnrollmentPrompts.EnhanceFailureForPhase(
            "fingerprint verification failed after retries", "verify_place_1");

        Assert.Contains("连续 3 次验证未通过", message);
    }

    [Theory]
    [InlineData("place_2", 300)]
    [InlineData("verify_place_1", 350)]
    [InlineData("verify_lift_1", 0)]
    public void RepeatPressPrompts_UseFriendlyDisplayDelay(string phase, int expectedDelay)
    {
        Assert.Equal(expectedDelay,
            FingerprintEnrollmentPrompts.GetDisplayDelayMilliseconds(phase));
    }

    [Theory]
    [InlineData("verify_lift_2", 6, 6, 5)]
    [InlineData("verify_place_2", 6, 6, 5)]
    [InlineData("verify_retry_lift_2", 6, 6, 5)]
    [InlineData("verify_2", 6, 6, 5)]
    [InlineData("verify_place_1", 5, 6, 5)]
    [InlineData("success", 6, 6, 6)]
    public void FinalVerification_ReachesFullProgressOnlyAfterSuccess(
        string phase, int step, int total, int expectedProgress)
    {
        Assert.Equal(expectedProgress,
            FingerprintEnrollmentPrompts.GetProgressValue(phase, step, total));
    }
}
