namespace Confast.Web.Features.ProductionTracking;

public sealed record SortLogMetrics(long TotalPassed, long TotalFailed, TimeSpan TotalRunTime,
    TimeSpan TotalDowntime, decimal FailureRate, decimal Pph, decimal FeedRate,
    decimal TargetPph, decimal Efficiency);

public static class SortLogMetricsCalculator
{
    public static SortLogMetrics Calculate(IEnumerable<SortLogLineItem> lines, decimal targetPph)
    {
        var ordered = lines.OrderBy(x => x.StartTimeUtc).ThenBy(x => x.Sequence).ToList();
        var completed = ordered.Where(x => x.StopTimeUtc != null).ToList();
        var totalPassed = completed.Sum(x => x.PassQuantity);
        var totalFailed = completed.Sum(x => x.FailQuantity);
        var runTime = completed.Aggregate(TimeSpan.Zero,
            (total, line) => total + (line.StopTimeUtc!.Value - line.StartTimeUtc));
        var downtime = TimeSpan.Zero;
        for (var index = 0; index + 1 < ordered.Count; index++)
        {
            if (ordered[index].StopTimeUtc is { } stop)
                downtime += ordered[index + 1].StartTimeUtc - stop;
        }

        var inspected = totalPassed + totalFailed;
        var runHours = (decimal)runTime.TotalHours;
        var failureRate = inspected == 0 ? 0 : (decimal)totalFailed / inspected;
        var pph = runHours <= 0 ? 0 : totalPassed / runHours;
        var feedRate = runHours <= 0 ? 0 : inspected / runHours;
        var efficiency = targetPph <= 0 ? 0 : pph / targetPph;
        return new(totalPassed, totalFailed, runTime, downtime, failureRate, pph, feedRate,
            targetPph, efficiency);
    }
}
