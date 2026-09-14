using Confast.Web.Features.Parts;
using Confast.Web.Features.ProductionScheduling;

namespace Confast.Web.Tests;

public sealed class ProductionSchedulerTests
{
    private static readonly DateOnly Monday = new(2026, 9, 7);
    internal static ProductionSnapshot Sample(params decimal[] quantities)
    {
        var part = new Part { Id = 1, PartNumber = "SORT-1", BoxQuantity = 3000 };
        var machine = new SortingMachine { Id = 1, Name = "Sorter", Parts = [new() { PartId = 1, TargetPph = 10000 }],
            WorkingDays = Enumerable.Range(1, 5).Select(i => new MachineWorkingDay { Day = (DayOfWeek)i, Hours = 8 }).ToList() };
        var jobs = quantities.Select((q, i) => new ProductionJob { Id = i + 1, PartId = 1, Part = part, Quantity = q,
            Segments = [new() { Id = i + 1, JobId = i + 1, MachineId = 1, Sequence = i + 1, Quantity = q,
                OriginalHours = q / 9100m, OriginalTargetPph = 10000, OriginalEfficiencyPercent = 91 }] }).ToList();
        return new(new(), [machine], [], [], [], jobs, [], [part], [], Monday, true, true);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(100.001)]
    public void InvalidEfficiencyRejected(decimal value) => Assert.Throws<SchedulingException>(() => ProductionScheduler.EffectivePph(10000, value));

    [Fact]
    public void EfficiencyAppliedExactlyOnceAndPartialDaysAreShared()
    {
        var data = Sample(36400, 36400);
        var f = ProductionScheduler.Forecast(data);
        Assert.All(f, x => { Assert.Equal(9100, x.EffectivePph); Assert.Equal(4, x.RequiredHours); Assert.Equal(Monday, x.Finish); });
        Assert.Equal(8, f.Sum(x => x.Capacity.Sum(c => c.Hours)));
    }

    [Fact]
    public void VariableCalendarsHolidaysAndOverlappingInclusiveDowntime()
    {
        var data = Sample(91000);
        data.Machines[0].WorkingDays.Single(x => x.Day == DayOfWeek.Tuesday).Hours = 2;
        data.Holidays.Add(new() { Date = Monday });
        data.Downtime.AddRange([new() { MachineId = 1, Start = Monday.AddDays(2), End = Monday.AddDays(3) }, new() { MachineId = 1, Start = Monday.AddDays(3), End = Monday.AddDays(3) }]);
        var f = Assert.Single(ProductionScheduler.Forecast(data));
        Assert.Equal(Monday.AddDays(1), f.Start); Assert.Equal(Monday.AddDays(4), f.Finish);
        Assert.Equal(new[] { 2m, 8m }, f.Capacity.Select(x => x.Hours));
    }

    [Fact]
    public void ZeroCapacityAndMissingRateProduceErrorsNotInventedDates()
    {
        var data = Sample(100);
        data.Machines[0].WorkingDays.Clear();
        var f = Assert.Single(ProductionScheduler.Forecast(data));
        Assert.NotNull(f.Error); Assert.Null(f.Finish);
        data = Sample(100); data.Machines[0].Parts.Clear();
        Assert.NotNull(Assert.Single(ProductionScheduler.Forecast(data)).Error);
        Assert.Null(ProductionScheduler.Boxes(100, 0)); Assert.Null(ProductionScheduler.Boxes(100, null));
        Assert.Equal(2, ProductionScheduler.Boxes(3001, 3000));
    }

    [Theory]
    [InlineData(72800, 4)] [InlineData(291200, 1)]
    public void CumulativeCheckpointReforecastsRemainingWork(decimal completed, int remainingDays)
    {
        var data = Sample(364000);
        var s = data.Jobs[0].Segments[0];
        s.State = ProductionState.Running; s.ActualStart = Monday; s.CompletedQuantity = completed; s.ProgressAsOf = Monday.AddDays(2);
        var f = Assert.Single(ProductionScheduler.Forecast(data with { Horizon = Monday.AddDays(3) }));
        Assert.Equal(remainingDays * 8m, f.RemainingHours);
        Assert.Equal(Monday.AddDays(3), f.Start); Assert.Equal(3, f.ElapsedDays);
        Assert.Equal(40, s.OriginalHours);
    }

    [Fact]
    public void EfficiencyChangesForecastButNotOriginalOrCompletedHistory()
    {
        var data = Sample(72800, 72800);
        var s = data.Jobs[0].Segments[0]; s.State = ProductionState.Completed; s.CompletedQuantity = s.Quantity;
        s.ActualStart = Monday.AddDays(-3); s.ActualCompletion = Monday.AddDays(-3); s.ActualElapsedWorkingDays = 1;
        data.Settings.EfficiencyPercent = 50;
        var f = ProductionScheduler.Forecast(data);
        Assert.Equal(8, f[0].RequiredHours); Assert.Equal(s.ActualCompletion, f[0].Finish);
        Assert.Equal(14.56m, f[1].RequiredHours); Assert.Equal(8, data.Jobs[1].Segments[0].OriginalHours);
    }

    [Fact]
    public void PartialRequirementCanBeMetBeforeWholeJobFinishAndTargetsRemainStable()
    {
        var data = Sample(145600);
        data.Requirements.Add(new() { Id = 1, PartId = 1, Date = Monday, CumulativeTarget = 72800 });
        var f = ProductionScheduler.Forecast(data);
        Assert.Equal(Monday.AddDays(1), f[0].Finish);
        Assert.True(Assert.Single(ProductionScheduler.Requirements(data, f)).Met);
        data.Jobs[0].Segments[0].CompletedQuantity = 10000;
        _ = ProductionScheduler.Requirements(data, ProductionScheduler.Forecast(data));
        Assert.Equal(72800, data.Requirements[0].CumulativeTarget);
    }

    [Fact]
    public void ConstraintsShiftDownstreamAndOptimizationPreservesPinsChainsAndRunning()
    {
        var data = Sample(36400, 36400, 36400, 36400, 36400);
        data.Jobs[0].Segments[0].State = ProductionState.Running;
        data.Jobs[1].Segments[0].IsPinned = true;
        data.Jobs[2].Segments[0].PredecessorId = 2;
        data.Jobs[3].Segments[0].NotBefore = Monday.AddDays(2);
        AssignPart(data, data.Jobs[4], 2);
        data.Requirements.Add(new() { Id = 1, PartId = 2, Date = Monday, CumulativeTarget = 1000 });
        var plan = ProductionScheduler.Optimize(data, 1);
        Assert.Equal(new long[] { 1, 2, 3, 5, 4 }, plan.Order);
        Assert.Equal(plan.Order, ProductionScheduler.Optimize(data, 1).Order);
        Assert.Equal(Monday.AddDays(2), plan.Forecasts.Single(x => x.SegmentId == 4).Start);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, data.Segments.Select(x => x.Id));
    }

    [Fact]
    public void OptimizationUsesRequiredQuantityButDoesNotStopUnsplitProduction()
    {
        var data = Sample(145600, 72800);
        data.Requirements.Add(new() { Id = 1, PartId = 1, Date = Monday, CumulativeTarget = 1000 });
        AssignPart(data, data.Jobs[1], 2);
        data.Requirements.Add(new() { Id = 2, PartId = 2, Date = Monday.AddDays(1), CumulativeTarget = 72800 });
        var preview = ProductionScheduler.Optimize(data, 1);
        Assert.Equal(new long[] { 1, 2 }, preview.Order);
        Assert.Equal(Monday.AddDays(2), preview.Forecasts.Single(x => x.SegmentId == 2).Finish);
        Assert.False(preview.Requirements.Single(x => x.RequirementId == 2).Met);
    }

    [Fact]
    public void MachinesHaveIndependentCalendarsAndCombinedRequirementForecasts()
    {
        var data = Sample(72800);
        var machine = new SortingMachine { Id = 2, Name = "Manual", Parts = [new() { PartId = 1, TargetPph = 10000 }],
            WorkingDays = [new() { Day = DayOfWeek.Tuesday, Hours = 4 }] };
        data.Machines.Add(machine);
        var job = data.Jobs[0]; job.Segments[0].Quantity = 36400;
        job.Segments.Add(new() { Id = 2, JobId = job.Id, MachineId = 2, Quantity = 36400 });
        data.Requirements.Add(new() { Id = 1, PartId = 1, Date = Monday.AddDays(1), CumulativeTarget = 72800 });
        var forecasts = ProductionScheduler.Forecast(data);
        Assert.Equal(Monday, forecasts.Single(x => x.SegmentId == 1).Finish);
        Assert.Equal(Monday.AddDays(1), forecasts.Single(x => x.SegmentId == 2).Finish);
        Assert.True(Assert.Single(ProductionScheduler.Requirements(data, forecasts)).Met);
    }

    [Fact]
    public void ExtremeStartConstraintAndAncientRequirementAreBounded()
    {
        var data = Sample(100);
        data.Jobs[0].Segments[0].NotBefore = DateOnly.MaxValue;
        data.Requirements.Add(new() { PartId = 1, Date = DateOnly.MinValue, CumulativeTarget = 100 });
        var preview = ProductionScheduler.Optimize(data, 1);
        Assert.NotNull(preview.Forecasts[0].Error);
        Assert.Null(preview.Forecasts[0].Finish);
    }

    [Fact]
    public void CompletionCheckpointReservesWholeDayAndStaleProgressDoesNotProduceInPast()
    {
        var data = Sample(100, 100);
        var first = data.Jobs[0].Segments[0]; first.State = ProductionState.Completed; first.CompletedQuantity = 100;
        first.ActualCompletion = Monday;
        Assert.Equal(Monday.AddDays(1), ProductionScheduler.Forecast(data).Single(x => x.SegmentId == 2).Start);
        first.State = ProductionState.Running; first.CompletedQuantity = 0; first.ActualStart = Monday.AddDays(-10);
        var f = ProductionScheduler.Forecast(data)[0]; Assert.True(f.StaleProgress); Assert.Equal(Monday, f.Start);
    }

    private static void AssignPart(ProductionSnapshot data, ProductionJob job, long partId)
    {
        var part = new Part { Id = partId, PartNumber = $"SORT-{partId}", BoxQuantity = 3000 };
        data.Parts.Add(part);
        data.Machines[0].Parts.Add(new() { MachineId = data.Machines[0].Id, PartId = partId, Part = part, TargetPph = 10000 });
        job.PartId = partId;
        job.Part = part;
    }
}
