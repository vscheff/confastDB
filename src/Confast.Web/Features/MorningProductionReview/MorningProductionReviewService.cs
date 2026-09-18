using Confast.Web.Data;
using Confast.Web.Features.Identity;
using Confast.Web.Features.ProductionScheduling;
using Confast.Web.Features.ProductionTracking;
using Confast.Web.Time;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.MorningProductionReview;

public sealed class MorningProductionReviewService(
    IDbContextFactory<AppDbContext> factory,
    ICurrentUser currentUser,
    TimeProvider clock,
    ProductionService productionService,
    BusinessDateProvider? businessDate = null)
{
    // Keeping the threshold in the query layer makes a future configurable value
    // one change instead of presentation logic scattered across the page.
    private static readonly TimeSpan MinimumDowntimeDuration = TimeSpan.Zero;

    private DateOnly Today => businessDate?.Today
        ?? DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    public async Task<MorningProductionReview> GetAsync(DateOnly? productionDate = null)
    {
        var schedule = await productionService.GetReadOnlyAsync();
        var forecasts = ProductionScheduler.Forecast(schedule);
        var forecastBySegmentId = forecasts.ToDictionary(x => x.SegmentId);

        await using var db = await factory.CreateDbContextAsync();
        await RequireAccessAsync(db);

        var latestPriorProductionDate = await db.Set<SortLog>().AsNoTracking()
            .Where(x => x.ProductionDate < Today)
            .MaxAsync(x => (DateOnly?)x.ProductionDate);
        var selectedDate = productionDate ?? latestPriorProductionDate ?? Today.AddDays(-1);
        if (selectedDate > Today)
            throw new MorningProductionReviewException("Production Review cannot open a future production date.");

        var productionDates = db.Set<SortLog>().AsNoTracking().Select(x => x.ProductionDate).Distinct();
        var previousProductionDate = await productionDates.Where(x => x < selectedDate)
            .MaxAsync(x => (DateOnly?)x);
        var nextProductionDate = await productionDates.Where(x => x > selectedDate && x <= Today)
            .MinAsync(x => (DateOnly?)x);

        var datesToLoad = selectedDate == Today ? new[] { Today } : new[] { selectedDate, Today };
        var logs = await db.Set<SortLog>().AsNoTracking()
            .Where(x => datesToLoad.Contains(x.ProductionDate))
            .Include(x => x.Machine)
            .Include(x => x.Part)
            .Include(x => x.Inspection)
            .Include(x => x.Lines).ThenInclude(x => x.DowntimeCause)
            .AsSplitQuery()
            .ToListAsync();

        var selectedLogs = logs.Where(x => x.ProductionDate == selectedDate).ToList();
        var historicalRuns = selectedLogs.Select(log => BuildRun(log, schedule)).ToList();
        var downtime = BuildDowntime(selectedLogs);
        var scheduledQuantities = ScheduledQuantitiesForDate(schedule, forecasts, selectedDate);
        var machineResults = BuildMachineResults(schedule, historicalRuns, downtime, scheduledQuantities);
        var summary = BuildSummary(historicalRuns, downtime);

        var activeLogs = logs.Where(x => x.ProductionDate == Today && x.Lines.Any(line => line.StopTimeUtc == null))
            .GroupBy(x => x.MachineId)
            .ToDictionary(group => group.Key, group => group
                .OrderByDescending(log => log.Lines.Where(line => line.StopTimeUtc == null)
                    .Max(line => line.StartTimeUtc))
                .First());
        var currentMachines = BuildCurrentMachines(schedule, forecastBySegmentId, activeLogs);
        var changeovers = BuildChangeovers(schedule, forecastBySegmentId, activeLogs);

        return new(selectedDate, latestPriorProductionDate, previousProductionDate, nextProductionDate,
            summary, machineResults, downtime, currentMachines, changeovers);
    }

    private static RunData BuildRun(SortLog log, ProductionSnapshot schedule)
    {
        var lines = log.Lines.OrderBy(x => x.Sequence).Select(ToLineItem).ToList();
        var metrics = SortLogMetricsCalculator.Calculate(lines, log.TargetPphSnapshot);
        var job = FindJob(schedule, log.ProductionSegmentId);
        return new(log, job, metrics, new ProductionRunSummary(log.Id, log.Part.PartNumber,
            job?.MoNumber, job?.PoNumber ?? log.Inspection.ConformancePoNumber, LotLabel(log),
            metrics.TotalPassed, metrics.TotalFailed, metrics.Pph, metrics.TargetPph,
            metrics.Efficiency, metrics.TotalRunTime));
    }

    private static IReadOnlyList<DowntimeSummary> BuildDowntime(IReadOnlyList<SortLog> logs)
    {
        var result = new List<DowntimeSummary>();
        foreach (var machineLogs in logs.GroupBy(x => x.MachineId))
        {
            var intervals = machineLogs.SelectMany(log => log.Lines.Select(line => new LineContext(log, line)))
                .OrderBy(x => x.Line.StartTimeUtc).ThenBy(x => x.Line.Sequence).ThenBy(x => x.Line.Id)
                .ToList();
            for (var index = 0; index + 1 < intervals.Count; index++)
            {
                var current = intervals[index];
                if (current.Line.StopTimeUtc is not { } stop || current.Line.DowntimeCause is null)
                    continue;
                var nextStart = intervals[index + 1].Line.StartTimeUtc;
                var duration = nextStart - stop;
                if (duration <= MinimumDowntimeDuration)
                    continue;
                result.Add(new(current.Log.MachineId, current.Log.Machine.Name, stop, nextStart,
                    duration, current.Line.DowntimeCause.Name, current.Log.Id,
                    current.Log.Part.PartNumber, LotLabel(current.Log)));
            }
        }

        return result.OrderBy(x => x.StartTimeUtc).ThenBy(x => x.MachineName).ToList();
    }

    private static IReadOnlyList<MachineProductionSummary> BuildMachineResults(
        ProductionSnapshot schedule,
        IReadOnlyList<RunData> runs,
        IReadOnlyList<DowntimeSummary> downtime,
        IReadOnlyDictionary<long, decimal> scheduledQuantities)
    {
        return schedule.Machines.Where(x => x.IsActive).OrderBy(x => x.Name).Select(machine =>
        {
            var machineRuns = runs.Where(x => x.Log.MachineId == machine.Id).ToList();
            var metrics = Aggregate(machineRuns);
            var machineDowntime = downtime.Where(x => x.MachineId == machine.Id)
                .Aggregate(TimeSpan.Zero, (total, item) => total + item.Duration);
            var scheduledQuantity = scheduledQuantities.GetValueOrDefault(machine.Id);
            decimal? scheduleEfficiency = scheduledQuantity > 0 ? metrics.Good / scheduledQuantity : null;
            return new MachineProductionSummary(machine.Id, machine.Name, machineRuns.Count,
                metrics.Good, metrics.Failed, metrics.ActualPph, metrics.TargetPph, metrics.Efficiency,
                scheduledQuantity > 0 ? scheduledQuantity : null, scheduleEfficiency, metrics.Runtime,
                machineDowntime, machineRuns.Select(x => x.Summary)
                    .OrderBy(x => x.PartNumber).ThenBy(x => x.LotNumber).ThenBy(x => x.SortLogId).ToList());
        }).ToList();
    }

    private static IReadOnlyDictionary<long, decimal> ScheduledQuantitiesForDate(
        ProductionSnapshot schedule,
        IReadOnlyList<SegmentForecast> forecasts,
        DateOnly productionDate)
    {
        var machineBySegmentId = schedule.Segments.ToDictionary(x => x.Id, x => x.MachineId);
        return forecasts.SelectMany(forecast => forecast.Capacity
                .Where(slice => slice.Date == productionDate)
                .Select(slice => new { MachineId = machineBySegmentId[forecast.SegmentId], slice.Quantity }))
            .GroupBy(x => x.MachineId)
            .ToDictionary(group => group.Key, group => group.Sum(x => x.Quantity));
    }

    private static ProductionSummary BuildSummary(
        IReadOnlyList<RunData> runs,
        IReadOnlyList<DowntimeSummary> downtime)
    {
        var metrics = Aggregate(runs);
        var totalDowntime = downtime.Aggregate(TimeSpan.Zero, (total, item) => total + item.Duration);
        return new(metrics.Good, metrics.Failed, metrics.Good + metrics.Failed,
            metrics.ActualPph, metrics.TargetPph, metrics.Efficiency, metrics.Runtime, totalDowntime);
    }

    private static AggregateMetrics Aggregate(IReadOnlyList<RunData> runs)
    {
        var good = runs.Sum(x => x.Metrics.TotalPassed);
        var failed = runs.Sum(x => x.Metrics.TotalFailed);
        var runtime = runs.Aggregate(TimeSpan.Zero, (total, run) => total + run.Metrics.TotalRunTime);
        var runHours = (decimal)runtime.TotalHours;
        var actualPph = runHours <= 0 ? 0 : good / runHours;
        var targetOutput = runs.Sum(x => x.Metrics.TargetPph * (decimal)x.Metrics.TotalRunTime.TotalHours);
        var targetPph = runHours > 0
            ? targetOutput / runHours
            : runs.Select(x => x.Metrics.TargetPph).DefaultIfEmpty(0).Average();
        var efficiency = targetPph <= 0 ? 0 : actualPph / targetPph;
        return new(good, failed, actualPph, targetPph, efficiency, runtime);
    }

    private static IReadOnlyList<MachineCurrentStatus> BuildCurrentMachines(
        ProductionSnapshot schedule,
        IReadOnlyDictionary<long, SegmentForecast> forecastBySegmentId,
        IReadOnlyDictionary<long, SortLog> activeLogs)
    {
        return schedule.Machines.Where(x => x.IsActive).OrderBy(x => x.Name).Select(machine =>
        {
            activeLogs.TryGetValue(machine.Id, out var activeLog);
            var activeSegment = activeLog?.ProductionSegmentId is long segmentId
                ? schedule.Segments.SingleOrDefault(x => x.Id == segmentId)
                : null;
            var activeJob = activeSegment is null ? null : schedule.Jobs.Single(x => x.Id == activeSegment.JobId);
            var queue = OrderedUnfinishedSegments(schedule, machine.Id);
            ProductionSegment? nextSegment;
            if (activeSegment is null)
            {
                nextSegment = queue.FirstOrDefault();
            }
            else
            {
                var activeIndex = queue.FindIndex(x => x.Id == activeSegment.Id);
                nextSegment = activeIndex < 0 ? queue.FirstOrDefault(x => x.JobId != activeSegment.JobId)
                    : queue.Skip(activeIndex + 1).FirstOrDefault(x => x.JobId != activeSegment.JobId);
            }

            var nextJob = nextSegment is null ? null : schedule.Jobs.Single(x => x.Id == nextSegment.JobId);
            var next = nextSegment is null || nextJob is null ? null : new ScheduledJobSummary(
                nextJob.Id, nextSegment.Id, nextJob.Part.PartNumber, nextJob.MoNumber, nextJob.PoNumber,
                forecastBySegmentId.GetValueOrDefault(nextSegment.Id)?.Start);

            if (activeLog is null)
                return new MachineCurrentStatus(machine.Id, machine.Name, true, null, null, null, null,
                    null, null, 0, null, null, next);

            var good = activeLog.Lines.Where(x => x.StopTimeUtc != null).Sum(x => x.PassQuantity);
            var scheduledQuantity = activeSegment?.Quantity;
            decimal? progress = scheduledQuantity > 0 ? good / scheduledQuantity.Value * 100m : null;
            return new MachineCurrentStatus(machine.Id, machine.Name, false, activeLog.Id,
                activeLog.Part.PartNumber, activeJob?.MoNumber,
                activeJob?.PoNumber ?? activeLog.Inspection.ConformancePoNumber, LotLabel(activeLog),
                activeLog.Lines.Min(x => x.StartTimeUtc), good, scheduledQuantity, progress, next);
        }).ToList();
    }

    private static IReadOnlyList<PlannedChangeover> BuildChangeovers(
        ProductionSnapshot schedule,
        IReadOnlyDictionary<long, SegmentForecast> forecastBySegmentId,
        IReadOnlyDictionary<long, SortLog> activeLogs)
    {
        var result = new List<PlannedChangeover>();
        foreach (var machine in schedule.Machines.Where(x => x.IsActive))
        {
            var ordered = OrderedSegments(schedule, machine.Id);
            for (var index = 0; index < ordered.Count; index++)
            {
                var incomingSegment = ordered[index];
                if (incomingSegment.State == ProductionState.Completed
                    || forecastBySegmentId.GetValueOrDefault(incomingSegment.Id)?.Start != schedule.Horizon)
                    continue;
                var incomingJob = schedule.Jobs.Single(x => x.Id == incomingSegment.JobId);
                var previousSegment = index == 0 ? null : ordered[index - 1];
                var previousJob = previousSegment is null
                    ? null
                    : schedule.Jobs.Single(x => x.Id == previousSegment.JobId);

                if (previousJob?.Id == incomingJob.Id)
                    continue;

                long? previousJobId;
                string previousPart;
                string? previousMo;
                string? previousPo;
                if (previousJob is not null)
                {
                    previousJobId = previousJob.Id;
                    previousPart = previousJob.Part.PartNumber;
                    previousMo = previousJob.MoNumber;
                    previousPo = previousJob.PoNumber;
                }
                else if (activeLogs.GetValueOrDefault(machine.Id) is { } activeLog)
                {
                    var activeJob = FindJob(schedule, activeLog.ProductionSegmentId);
                    if (activeJob?.Id == incomingJob.Id)
                        continue;
                    previousJobId = activeJob?.Id;
                    previousPart = activeLog.Part.PartNumber;
                    previousMo = activeJob?.MoNumber;
                    previousPo = activeJob?.PoNumber ?? activeLog.Inspection.ConformancePoNumber;
                }
                else
                {
                    continue;
                }

                result.Add(new(schedule.Horizon, machine.Id, machine.Name, previousJobId,
                    previousPart, previousMo, previousPo, incomingJob.Id,
                    incomingJob.Part.PartNumber, incomingJob.MoNumber, incomingJob.PoNumber, index));
            }
        }

        return result.OrderBy(x => x.PlannedDate).ThenBy(x => x.MachineName)
            .ThenBy(x => x.ScheduleOrder).ToList();
    }

    private static List<ProductionSegment> OrderedSegments(ProductionSnapshot schedule, long machineId) =>
        schedule.Segments.Where(x => x.MachineId == machineId)
            .OrderBy(x => x.State == ProductionState.Completed ? 0 : x.State == ProductionState.Running ? 1 : 2)
            .ThenBy(x => x.Sequence).ThenBy(x => x.Id).ToList();

    private static List<ProductionSegment> OrderedUnfinishedSegments(ProductionSnapshot schedule, long machineId) =>
        schedule.Segments.Where(x => x.MachineId == machineId && x.State != ProductionState.Completed)
            .OrderBy(x => x.State == ProductionState.Running ? 0 : 1)
            .ThenBy(x => x.Sequence).ThenBy(x => x.Id).ToList();

    private static ProductionJob? FindJob(ProductionSnapshot schedule, long? productionSegmentId)
    {
        if (productionSegmentId is not long segmentId)
            return null;
        var segment = schedule.Segments.SingleOrDefault(x => x.Id == segmentId);
        return segment is null ? null : schedule.Jobs.SingleOrDefault(x => x.Id == segment.JobId);
    }

    private static SortLogLineItem ToLineItem(SortLogLine line) => new(line.Id, line.Sequence,
        line.StartTimeUtc, line.StopTimeUtc, line.DowntimeCauseId, line.DowntimeCause?.Name,
        line.PassQuantity, line.FailQuantity, line.BoundarySamplesRanQuantity,
        line.BoundarySamplesPassedQuantity, line.StartBoxCount, line.EndBoxCount,
        line.ProductionInitials, line.QualityInitials, line.Notes);

    private static string LotLabel(SortLog log) => string.IsNullOrWhiteSpace(log.Inspection.LotNumber)
        ? $"Inspection {log.Inspection.Id}"
        : log.Inspection.LotNumber;

    private async Task RequireAccessAsync(AppDbContext db)
    {
        var userId = await currentUser.GetUserIdAsync();
        if (userId is null || !await db.Users.AnyAsync(x => x.Id == userId && x.IsActive))
            throw new UnauthorizedAccessException("Sign in with an active account to use Production Review.");
        var allowed = await (from userRole in db.UserRoles
            join role in db.Roles on userRole.RoleId equals role.Id
            where userRole.UserId == userId
            select role.Name).AnyAsync(x => x == AppRoles.Production || x == AppRoles.Administrator);
        if (!allowed)
            throw new UnauthorizedAccessException("Production or Administrator access is required to use Production Review.");
    }

    private sealed record RunData(SortLog Log, ProductionJob? Job, SortLogMetrics Metrics,
        ProductionRunSummary Summary);
    private sealed record LineContext(SortLog Log, SortLogLine Line);
    private sealed record AggregateMetrics(long Good, long Failed, decimal ActualPph,
        decimal TargetPph, decimal Efficiency, TimeSpan Runtime);
}
