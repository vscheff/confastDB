using Confast.Web.Time;

namespace Confast.Web.Tests;

public sealed class DevelopmentDateOverrideTimeProviderTests
{
    [Fact]
    public void OverrideChangesTheLocalBusinessDateWithoutChangingTheSystemClock()
    {
        var clock = new DevelopmentDateOverrideTimeProvider();
        var utcBeforeOverride = TimeProvider.System.GetUtcNow();

        clock.SetDate(new DateOnly(2031, 2, 3));

        Assert.True(clock.HasOverride);
        Assert.Equal(new DateOnly(2031, 2, 3), clock.CurrentDate);
        Assert.InRange(
            TimeProvider.System.GetUtcNow() - utcBeforeOverride,
            TimeSpan.Zero,
            TimeSpan.FromSeconds(5));

        clock.UseSystemDate();

        Assert.False(clock.HasOverride);
        Assert.Equal(
            DateOnly.FromDateTime(TimeProvider.System.GetLocalNow().DateTime),
            clock.CurrentDate);
    }
}
