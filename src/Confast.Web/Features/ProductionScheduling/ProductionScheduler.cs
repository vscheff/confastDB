namespace Confast.Web.Features.ProductionScheduling;

public static class ProductionScheduler
{
    private const int MaximumDays = 3660;

    public static decimal EffectivePph(decimal target, decimal efficiency)
    {
        if (target <= 0) throw new SchedulingException("Set a positive target PPH for this part and machine.");
        if (efficiency <= 0 || efficiency > 100) throw new SchedulingException("Production efficiency must be greater than 0% and at most 100%.");
        return target * efficiency / 100m;
    }

    public static decimal? Boxes(decimal quantity, decimal? boxQuantity) =>
        boxQuantity > 0 ? decimal.Ceiling(quantity / boxQuantity.Value) : null;

    public static decimal Capacity(ProductionSnapshot data, SortingMachine machine, DateOnly date) =>
        data.Holidays.Any(x => x.Date == date) || data.Downtime.Any(x => x.MachineId == machine.Id && x.Start <= date && x.End >= date)
            ? 0 : machine.WorkingDays.SingleOrDefault(x => x.Day == date.DayOfWeek)?.Hours ?? 0;

    public static List<SegmentForecast> Forecast(ProductionSnapshot data, long? optimizedMachine = null, IReadOnlyList<long>? order = null)
    {
        var result = new List<SegmentForecast>();
        foreach (var machine in data.Machines)
        {
            var segments = data.Segments.Where(x => x.MachineId == machine.Id)
                .OrderBy(x => x.State == ProductionState.Completed ? 0 : x.State == ProductionState.Running ? 1 : 2)
                .ThenBy(x => optimizedMachine == machine.Id && order != null && x.State == ProductionState.Pending
                    ? IndexOf(order, x.Id) : x.Sequence).ThenBy(x => x.Id).ToList();
            var date = data.Horizon;
            var used = 0m;
            string? blocked = null;
            var average = machine.WorkingDays.Where(x => x.Hours > 0).Select(x => x.Hours).DefaultIfEmpty(0).Average();
            foreach (var segment in segments)
            {
                var job = data.Jobs.Single(x => x.Id == segment.JobId);
                var rate = machine.Parts.SingleOrDefault(x => x.PartId == job.PartId)?.TargetPph ?? 0;
                if (segment.State == ProductionState.Completed)
                {
                    if (segment.ActualCompletion is { } completion && completion >= date)
                    { date = completion.AddDays(1); used = 0; }
                    result.Add(new(segment.Id, segment.ActualStart, segment.ActualCompletion,
                        segment.OriginalTargetPph, segment.OriginalTargetPph * segment.OriginalEfficiencyPercent / 100m,
                        segment.OriginalHours, 0, average, segment.ActualElapsedWorkingDays ?? Elapsed(data, machine, segment), false, null, []));
                    continue;
                }
                var error = blocked;
                if (rate <= 0) error = "Set a positive eligible part–machine PPH in scheduling settings.";
                if (average <= 0) error = "Configure at least one working weekday with positive hours.";
                if (data.Settings.EfficiencyPercent <= 0 || data.Settings.EfficiencyPercent > 100) error = "Set efficiency above 0% and at most 100%.";
                if (!machine.IsActive) error = "Machine is inactive; activate it or explicitly reassign pending work.";
                if (segment.PredecessorId is { } predecessor && !result.Any(x => x.SegmentId == predecessor && x.Error == null))
                    error = "Cut-in predecessor must be scheduled before this segment on the same machine.";
                var effective = rate > 0 && data.Settings.EfficiencyPercent > 0 ? rate * data.Settings.EfficiencyPercent / 100m : 0;
                var required = effective > 0 ? segment.Quantity / effective : 0;
                var remaining = effective > 0 ? (segment.Quantity - segment.CompletedQuantity) / effective : 0;
                var earliest = segment.NotBefore ?? data.Horizon;
                if (segment.ProgressAsOf is { } checkpoint && checkpoint.AddDays(1) > earliest) earliest = checkpoint.AddDays(1);
                if (earliest.DayNumber - data.Horizon.DayNumber >= MaximumDays)
                    error = "Start constraint exceeds the ten-year forecast horizon. Review the explicit constraint.";
                if (earliest > date) { date = earliest; used = 0; }
                var slices = new List<CapacitySlice>();
                DateOnly? start = null;
                DateOnly? finish = null;
                if (error == null)
                {
                    var hours = remaining;
                    for (var days = 0; days < MaximumDays && hours > 0 && date.DayNumber - data.Horizon.DayNumber < MaximumDays; days++)
                    {
                        var available = Math.Max(0, Capacity(data, machine, date) - used);
                        var take = Math.Min(hours, available);
                        if (take > 0)
                        {
                            start ??= date;
                            finish = date;
                            slices.Add(new(date, take, take * effective));
                            used += take;
                            hours -= take;
                        }
                        if (hours > 0) { date = date.AddDays(1); used = 0; }
                    }
                    if (hours > 0) error = "Work exceeds the ten-year forecast horizon. Review quantity, rate, calendar, and downtime.";
                    if (remaining == 0) start = finish = segment.ProgressAsOf ?? date;
                }
                if (error != null) { blocked = "Earlier work cannot be forecast: " + error; start = finish = null; slices.Clear(); }
                result.Add(new(segment.Id, start, finish, rate, effective, required, remaining, average,
                    Elapsed(data, machine, segment), segment.State == ProductionState.Running &&
                    (segment.ProgressAsOf ?? segment.ActualStart) < data.Horizon.AddDays(-1), error, slices));
            }
        }
        return result;
    }

    private static int IndexOf(IReadOnlyList<long> order, long id)
    {
        for (var i = 0; i < order.Count; i++) if (order[i] == id) return i;
        return int.MaxValue;
    }

    public static decimal Elapsed(ProductionSnapshot data, SortingMachine machine, ProductionSegment segment)
    {
        if (segment.ActualStart is not { } start) return 0;
        var end = segment.ActualCompletion ?? data.Horizon.AddDays(-1);
        var count = 0;
        // Elapsed calendar working days, not equivalent output days.
        for (var date = start; date <= end && date.DayNumber - start.DayNumber < MaximumDays; date = date.AddDays(1))
            if (Capacity(data, machine, date) > 0) count++;
        return count;
    }

    public static List<RequirementForecast> Requirements(ProductionSnapshot data, List<SegmentForecast> forecasts)
    {
        var result = new List<RequirementForecast>();
        foreach (var requirement in data.Requirements.OrderBy(x => x.PartId).ThenBy(x => x.Date))
        {
            var jobs = data.Jobs.Where(x => x.PartId == requirement.PartId).ToList();
            var completed = jobs.SelectMany(x => x.Segments).Sum(x => x.CompletedQuantity);
            // Current corrected checkpoints are authoritative; never sum cumulative entries.
            var events = jobs.SelectMany(x => x.Segments).Where(x => x.CompletedQuantity > 0)
                .Select(x => new { Date = x.ProgressAsOf ?? x.ActualCompletion ?? data.Horizon, Quantity = x.CompletedQuantity })
                .Concat(forecasts.Where(x => jobs.SelectMany(job => job.Segments).Any(s => s.Id == x.SegmentId))
                    .SelectMany(x => x.Capacity).Select(x => new { x.Date, x.Quantity }))
                .GroupBy(x => x.Date).OrderBy(x => x.Key);
            var accumulated = 0m;
            DateOnly? reached = null;
            foreach (var item in events)
            {
                accumulated += item.Sum(x => x.Quantity);
                if (accumulated + 0.000001m >= requirement.CumulativeTarget) { reached = item.Key; break; }
            }
            // For already fulfilled requirements, retain the earliest corrected historical evidence.
            if (completed >= requirement.CumulativeTarget)
            {
                var dates = jobs.SelectMany(job => job.Segments).SelectMany(s => s.Progress).Select(p => p.AsOf).Distinct().Order().ToList();
                foreach (var date in dates)
                {
                    var quantity = jobs.SelectMany(job => job.Segments).Sum(s => s.Progress.Where(p => p.AsOf <= date)
                        .OrderByDescending(p => p.RecordedAt).ThenByDescending(p => p.Id).FirstOrDefault()?.CompletedQuantity ?? 0);
                    if (quantity >= requirement.CumulativeTarget) { reached = date; break; }
                }
            }
            var met = reached.HasValue && reached.Value <= requirement.Date;
            result.Add(new(requirement.Id, reached, met, completed >= requirement.CumulativeTarget
                ? met ? "Satisfied" : "Satisfied late" : met ? "On track" : reached == null ? "Cannot forecast requirement" : "Projected miss"));
        }
        return result;
    }

    public static OptimizationPreview Optimize(ProductionSnapshot data, long machineId)
    {
        var machine = data.Machines.Single(x => x.Id == machineId);
        var queue = data.Segments.Where(x => x.MachineId == machineId && x.State != ProductionState.Completed)
            .OrderBy(x => x.State == ProductionState.Running ? 0 : 1).ThenBy(x => x.Sequence).ThenBy(x => x.Id).ToList();
        // Explicit chains and pins are barriers. Optimize only independent pending runs between them.
        var chained = data.Segments.Where(x => x.PredecessorId != null)
            .SelectMany(x => new[] { x.Id, x.PredecessorId!.Value }).ToHashSet();
        bool Fixed(ProductionSegment s) => s.State != ProductionState.Pending || s.IsPinned || chained.Contains(s.Id);
        for (var i = 0; i < queue.Count;)
        {
            if (Fixed(queue[i])) { i++; continue; }
            var end = i;
            while (end < queue.Count && !Fixed(queue[end])) end++;
            var sorted = queue.GetRange(i, end - i).OrderBy(s => LatestSafeStart(data, machine, s)).ToList();
            for (var j = 0; j < sorted.Count; j++) queue[i + j] = sorted[j];
            i = end;
        }
        var order = queue.Select(x => x.Id).ToList();
        var forecasts = Forecast(data, machineId, order);
        return new(data.Settings.Revision, machineId, data.Horizon, order, forecasts, Requirements(data, forecasts),
            "Earlier latest-safe-start first, using effective PPH and available calendar capacity. Ties retain queue order. Running work, pins, and cut-in chains are barriers. Each unsplit segment consumes its full remaining quantity; partial requirements never stop production. Misses may require a planner cut-in or a constraint change.");
    }

    private static decimal LatestSafeStart(ProductionSnapshot data, SortingMachine machine, ProductionSegment segment)
    {
        var job = data.Jobs.Single(x => x.Id == segment.JobId);
        var partJobs = data.Jobs.Where(x => x.PartId == job.PartId).ToList();
        var completed = partJobs.SelectMany(x => x.Segments).Sum(x => x.CompletedQuantity);
        var next = data.Requirements.Where(x => x.PartId == job.PartId).OrderBy(x => x.Date).FirstOrDefault(x => x.CumulativeTarget > completed);
        if (next == null) return decimal.MaxValue;
        var rate = machine.Parts.SingleOrDefault(x => x.PartId == job.PartId)?.TargetPph ?? 0;
        if (rate <= 0 || !machine.WorkingDays.Any(x => x.Hours > 0)) return decimal.MinValue;
        var hours = Math.Min(segment.Quantity - segment.CompletedQuantity, next.CumulativeTarget - completed)
            / EffectivePph(rate, data.Settings.EfficiencyPercent);
        var date = next.Date;
        for (var i = 0; i < MaximumDays; i++, date = date.AddDays(-1))
        {
            var capacity = Capacity(data, machine, date);
            if (capacity >= hours && capacity > 0) return date.DayNumber + (capacity - hours) / capacity;
            hours -= capacity;
            if (date == DateOnly.MinValue) break;
        }
        return decimal.MinValue;
    }
}
