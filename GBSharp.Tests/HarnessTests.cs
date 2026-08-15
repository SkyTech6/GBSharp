namespace GBSharp.Tests;

/// <summary>
/// Tests for the harness itself.
/// </summary>
/// <remarks>
/// The integration layer returns early when GBDK is absent, so the mechanism
/// that decides "absent" is load-bearing: if it silently answered "absent" on a
/// machine that has the toolchain, every ROM test would pass without building a
/// ROM. These pin the part of that decision which does not need a toolchain.
/// </remarks>
public sealed class HarnessTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("false")]
    [InlineData("False")]
    public void UnsetAndFalseValuesDoNotRequireGbdk(string? value) =>
        Assert.False(TestHarness.IsRequireValue(value));

    [Theory]
    [InlineData("1")]
    [InlineData("true")]
    [InlineData("yes")]
    public void AnythingElseRequiresGbdk(string value) =>
        Assert.True(TestHarness.IsRequireValue(value));

    /// <summary>
    /// CI sets the variable after fetching GBDK, so on that machine the property
    /// must answer true rather than throw. Locally it simply reports what it found.
    /// </summary>
    [Fact]
    public void GbdkAvailabilityIsAnsweredWithoutThrowingWhenNotRequired()
    {
        if (TestHarness.IsRequireValue(
                Environment.GetEnvironmentVariable(TestHarness.RequireGbdkVariable)))
        {
            Assert.True(TestHarness.GbdkAvailable);
            return;
        }

        // Either answer is legitimate on a developer machine; not throwing is the assertion.
        _ = TestHarness.GbdkAvailable;
    }
}
