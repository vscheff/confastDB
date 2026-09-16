using Confast.Web.Features.ProductionTracking;

namespace Confast.Web.Tests;

public sealed class SortLogMetricsCalculatorTests
{
    [Fact]
    public void CalculatesGoodOutputFeedRateFailureRateAndTargetEfficiencySeparately()
    {
        var start = new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);
        var lines = new[]
        {
            Line(1, start, start.AddHours(1), 9100, 100),
            Line(2, start.AddHours(1).AddMinutes(15), start.AddHours(2).AddMinutes(15), 9100, 0)
        };

        var metrics = SortLogMetricsCalculator.Calculate(lines, 10000);

        Assert.Equal(18200, metrics.TotalPassed);
        Assert.Equal(100, metrics.TotalFailed);
        Assert.Equal(TimeSpan.FromHours(2), metrics.TotalRunTime);
        Assert.Equal(TimeSpan.FromMinutes(15), metrics.TotalDowntime);
        Assert.Equal(100m / 18300m, metrics.FailureRate);
        Assert.Equal(9100, metrics.Pph);
        Assert.Equal(9150, metrics.FeedRate);
        Assert.Equal(.91m, metrics.Efficiency);
    }

    [Fact]
    public void IgnoresActiveLineForOutputAndRunTimeButCountsGapLeadingToIt()
    {
        var start = new DateTimeOffset(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);
        var lines = new[]
        {
            Line(1, start, start.AddHours(1), 1000, 25),
            Line(2, start.AddHours(1).AddMinutes(20), null, 9999, 9999)
        };

        var metrics = SortLogMetricsCalculator.Calculate(lines, 2000);

        Assert.Equal(1000, metrics.TotalPassed);
        Assert.Equal(25, metrics.TotalFailed);
        Assert.Equal(TimeSpan.FromHours(1), metrics.TotalRunTime);
        Assert.Equal(TimeSpan.FromMinutes(20), metrics.TotalDowntime);
        Assert.Equal(1000, metrics.Pph);
    }

    [Fact]
    public void EmptyRuntimeProducesZeroRatesInsteadOfDividingByZero()
    {
        var metrics = SortLogMetricsCalculator.Calculate([], 10000);

        Assert.Equal(0, metrics.FailureRate);
        Assert.Equal(0, metrics.Pph);
        Assert.Equal(0, metrics.FeedRate);
        Assert.Equal(0, metrics.Efficiency);
    }

    private static SortLogLineItem Line(int sequence, DateTimeOffset start, DateTimeOffset? stop,
        long passed, long failed) => new(sequence, sequence, start, stop, -1, "End of Day",
        passed, failed, 0, 0, null, null, null, null, null);
}
