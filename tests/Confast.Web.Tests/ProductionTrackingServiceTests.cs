using Confast.Web.Features.Customers;
using Confast.Web.Features.Identity;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.Parts;
using Confast.Web.Features.ProductionScheduling;
using Confast.Web.Features.ProductionTracking;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ProductionTrackingServiceTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly MutableClock clock = new();
    private ProductionTrackingService service = null!;
    private long machineId, partId, inspectionId, segmentId;

    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        await using var db = database.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE sort_log_lines, sort_logs, production_progress, production_requirements, production_segments, production_jobs, part_machines, machine_working_days, machine_downtime, sorting_machines, production_holidays, downtime_reasons, production_audit RESTART IDENTITY CASCADE");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM sort_log_downtime_causes WHERE id <> -1");
        db.Users.AddRange(
            new() { Id = "operator", UserName = "operator", DisplayName = "Production Operator" },
            new() { Id = "quality", UserName = "quality", DisplayName = "Quality Inspector" });
        var productionRoleId = await db.Roles.Where(x => x.Name == AppRoles.Production).Select(x => x.Id).SingleAsync();
        var qualityRoleId = await db.Roles.Where(x => x.Name == AppRoles.Quality).Select(x => x.Id).SingleAsync();
        var administratorRoleId = await db.Roles.Where(x => x.Name == AppRoles.Administrator).Select(x => x.Id).SingleAsync();
        db.UserRoles.AddRange(
            new() { UserId = "operator", RoleId = productionRoleId },
            new() { UserId = "operator", RoleId = administratorRoleId },
            new() { UserId = "quality", RoleId = qualityRoleId });
        var part = new Part { Customer = new Customer { Name = "Tracking customer" }, PartNumber = "TRACK-1" };
        db.Add(part);
        await db.SaveChangesAsync();
        partId = part.Id;
        var revision = new InspectionCriteriaRevision
        {
            PartId = partId,
            RevisionNumber = 1,
            CreatedAtUtc = clock.GetUtcNow(),
            PublishedAtUtc = clock.GetUtcNow()
        };
        db.Add(revision);
        await db.SaveChangesAsync();
        var inspection = new Inspection
        {
            PartId = partId,
            InspectionCriteriaRevisionId = revision.Id,
            LotNumber = "LOT-TRACK-1",
            ConformancePoNumber = "PO-TRACK-1",
            InspectionDate = clock.Today,
            CreatedAtUtc = clock.GetUtcNow()
        };
        var machine = new SortingMachine { Name = "Tracking Sorter", IsActive = true };
        db.AddRange(inspection, machine);
        await db.SaveChangesAsync();
        inspectionId = inspection.Id;
        machineId = machine.Id;
        db.Add(new PartMachine { MachineId = machineId, PartId = partId, TargetPph = 10000, IsPreferred = true });
        var job = new ProductionJob { PartId = partId, PoNumber = "PO-TRACK-1", Quantity = 50000 };
        var segment = new ProductionSegment
        {
            JobId = 0,
            MachineId = machineId,
            Sequence = 1,
            Quantity = 50000,
            State = ProductionState.Pending,
            OriginalHours = 5,
            OriginalTargetPph = 10000,
            OriginalEfficiencyPercent = 91
        };
        job.Segments.Add(segment);
        db.Add(job);
        await db.SaveChangesAsync();
        segmentId = segment.Id;
        service = new(database, new TestUser(), clock);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ScheduledSelectionCreatesOneDailyLogAndSnapshotsConfiguredTargetPph()
    {
        var dashboard = await service.GetDashboardAsync(machineId);
        var scheduled = Assert.Single(dashboard.ScheduledJobs);
        Assert.Equal(segmentId, scheduled.SegmentId);
        Assert.Equal(inspectionId, scheduled.InspectionId);

        var firstId = await service.OpenOrCreateSortLogAsync(machineId, inspectionId, segmentId);
        await using (var db = database.CreateDbContext())
        {
            var rate = await db.Set<PartMachine>().SingleAsync();
            rate.TargetPph = 12000;
            await db.SaveChangesAsync();
        }
        var reopenedId = await service.OpenOrCreateSortLogAsync(machineId, inspectionId, segmentId);
        var log = (await service.GetDashboardAsync(machineId, firstId)).CurrentLog!;

        Assert.Equal(firstId, reopenedId);
        Assert.Equal(10000, log.TargetPphSnapshot);
        Assert.Equal(segmentId, log.ProductionSegmentId);
        Assert.Equal("LOT-TRACK-1", log.LotNumber);
    }

    [Fact]
    public async Task DashboardListsTheMostRecentlyCreatedLogFirst()
    {
        var firstLogId = await service.OpenOrCreateSortLogAsync(machineId, inspectionId, null);
        clock.Advance(TimeSpan.FromMinutes(1));
        await using var db = database.CreateDbContext();
        var revisionId = await db.Inspections.Where(x => x.Id == inspectionId)
            .Select(x => x.InspectionCriteriaRevisionId).SingleAsync();
        var laterInspection = new Inspection
        {
            PartId = partId,
            InspectionCriteriaRevisionId = revisionId,
            LotNumber = "LOT-TRACK-2",
            InspectionDate = clock.Today,
            CreatedAtUtc = clock.GetUtcNow()
        };
        db.Add(laterInspection);
        await db.SaveChangesAsync();

        var latestLogId = await service.OpenOrCreateSortLogAsync(machineId, laterInspection.Id, null);
        var dashboard = await service.GetDashboardAsync(machineId);

        Assert.Equal(latestLogId, dashboard.TodayLogs.First().Id);
        Assert.Equal(firstLogId, dashboard.TodayLogs.Last().Id);
    }

    [Fact]
    public async Task StartStopAndEditEnforceSingleActiveLineSamplesAndNoOverlap()
    {
        var logId = await service.OpenOrCreateSortLogAsync(machineId, inspectionId, null);
        await Assert.ThrowsAsync<ProductionTrackingException>(() => service.StartRunAsync(logId,
            new(2, 3, null, "PO", null, null)));
        await Assert.ThrowsAsync<ProductionTrackingException>(() => service.StartRunAsync(logId,
            new(0, 0, null, "", null, null)));
        await Assert.ThrowsAsync<ProductionTrackingException>(() => service.StartRunAsync(logId,
            new(0, 0, null, "P", null, null)));
        var firstLineId = await service.StartRunAsync(logId, new(3, 3, 10, "po", "qi", "startup"));
        await Assert.ThrowsAsync<ProductionTrackingException>(() => service.StartRunAsync(logId,
            new(0, 0, null, "PO", null, null)));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(firstLineId, await service.StopRunAsync(logId));
        var first = (await service.GetDashboardAsync(machineId, logId)).CurrentLog!.Lines.Single();
        await Assert.ThrowsAsync<ProductionTrackingException>(() => service.UpdateLineAsync(first.Id,
            new(first.StartTimeUtc, first.StopTimeUtc!.Value, -1, 9100, 100, 3, 3, 10, null, "PO", "QI", "complete")));
        await Assert.ThrowsAsync<ProductionTrackingException>(() => service.UpdateLineAsync(first.Id,
            new(first.StartTimeUtc, first.StopTimeUtc!.Value, -1, 9100, 100, 3, 3, 10, 11, null, null, "complete")));
        await service.UpdateLineAsync(first.Id, new(first.StartTimeUtc, first.StopTimeUtc!.Value,
            -1, 9100, 100, 3, 3, 10, 11, "po", "qi", "complete"));

        clock.Advance(TimeSpan.FromMinutes(15));
        var secondLineId = await service.StartRunAsync(logId, new(0, 0, null, null, "QI", null));
        clock.Advance(TimeSpan.FromHours(1));
        await service.StopRunAsync(logId);
        var second = (await service.GetDashboardAsync(machineId, logId)).CurrentLog!.Lines.Single(x => x.Id == secondLineId);
        Assert.Equal(11, second.StartBoxCount);
        await Assert.ThrowsAsync<ProductionTrackingException>(() => service.UpdateLineAsync(second.Id,
            new(first.StartTimeUtc.AddMinutes(30), second.StopTimeUtc!.Value, -1, 100, 0,
                0, 0, 11, 12, "PO", null, null)));
        await service.UpdateLineAsync(second.Id, new(second.StartTimeUtc, second.StopTimeUtc!.Value,
            -1, 9100, 0, 0, 0, 11, 12, "PO", null, null));

        var final = (await service.GetDashboardAsync(machineId, logId)).CurrentLog!;
        Assert.Equal(18200, final.Metrics.TotalPassed);
        Assert.Equal(TimeSpan.FromMinutes(15), final.Metrics.TotalDowntime);
        Assert.Equal(9100, final.Metrics.Pph);
        Assert.Equal(.91m, final.Metrics.Efficiency);
        Assert.Equal("PO", final.Lines.First().ProductionInitials);
        Assert.Equal("QI", final.Lines.First().QualityInitials);
    }

    [Fact]
    public async Task ResumeRunRestoresOnlyTheStoppedRunAwaitingDetails()
    {
        var logId = await service.OpenOrCreateSortLogAsync(machineId, inspectionId, null);
        var lineId = await service.StartRunAsync(logId, new(0, 0, null, "PO", null, null));
        clock.Advance(TimeSpan.FromMinutes(30));
        await service.StopRunAsync(logId);

        await service.ResumeRunAsync(logId);

        var resumed = (await service.GetDashboardAsync(machineId, logId)).CurrentLog!;
        Assert.Equal(lineId, resumed.ActiveLine!.Id);
        Assert.Null(resumed.ActiveLine.StopTimeUtc);
        await Assert.ThrowsAsync<ProductionTrackingException>(() => service.ResumeRunAsync(logId));
    }

    [Fact]
    public async Task UnscheduledSelectionStillRequiresMachineEligibilityAndMatchingLotPart()
    {
        await using var db = database.CreateDbContext();
        var otherPart = new Part { CustomerId = await db.Parts.Where(x => x.Id == partId).Select(x => x.CustomerId).SingleAsync(), PartNumber = "TRACK-2" };
        db.Add(otherPart);
        await db.SaveChangesAsync();
        var revision = new InspectionCriteriaRevision { PartId = otherPart.Id, RevisionNumber = 1, CreatedAtUtc = clock.GetUtcNow(), PublishedAtUtc = clock.GetUtcNow() };
        db.Add(revision);
        await db.SaveChangesAsync();
        var otherLot = new Inspection { PartId = otherPart.Id, InspectionCriteriaRevisionId = revision.Id, LotNumber = "LOT-TRACK-2", InspectionDate = clock.Today, CreatedAtUtc = clock.GetUtcNow() };
        db.Add(otherLot);
        await db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<ProductionTrackingException>(() =>
            service.OpenOrCreateSortLogAsync(machineId, otherLot.Id, null));
        Assert.Contains("not eligible", exception.Message);
    }

    [Fact]
    public async Task SortLogDowntimeCausesAreConfiguredSeparatelyFromSchedulingReasonsAndOrderedByUse()
    {
        Assert.Contains(await service.GetDowntimeCausesAsync(), x => x.Name == "End of Day");

        await service.SaveDowntimeCauseAsync(0, "Box Change", true);
        await service.SaveDowntimeCauseAsync(0, "Alpha Check", true);
        await service.SaveDowntimeCauseAsync(0, "Beta Check", true);

        await using var db = database.CreateDbContext();
        var causes = await db.Set<SortLogDowntimeCause>().ToDictionaryAsync(x => x.Name);
        var logId = await service.OpenOrCreateSortLogAsync(machineId, inspectionId, null);
        var start = clock.GetUtcNow();
        db.AddRange(
            Line(logId, 1, causes["Box Change"].Id, start),
            Line(logId, 2, causes["Box Change"].Id, start.AddMinutes(1)),
            Line(logId, 3, causes["Beta Check"].Id, start.AddMinutes(2)),
            Line(logId, 4, causes["Alpha Check"].Id, start.AddMinutes(3)));
        await db.SaveChangesAsync();

        var ordered = await service.GetDowntimeCausesAsync();

        Assert.Equal(["Box Change", "Alpha Check", "Beta Check", "End of Day"], ordered.Select(x => x.Name));
        Assert.Empty(await db.Set<DowntimeReason>().ToListAsync());
    }

    private static SortLogLine Line(long sortLogId, int sequence, long causeId, DateTimeOffset start) => new()
    {
        SortLogId = sortLogId,
        Sequence = sequence,
        DowntimeCauseId = causeId,
        StartTimeUtc = start,
        StopTimeUtc = start.AddMinutes(1),
        ProductionInitials = "PO"
    };

    private sealed class TestUser : ICurrentUser
    {
        public ValueTask<string?> GetUserIdAsync() => ValueTask.FromResult<string?>("operator");
    }

    private sealed class MutableClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 16, 8, 0, 0, TimeSpan.Zero);
        public DateOnly Today => DateOnly.FromDateTime(now.DateTime);
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void Advance(TimeSpan duration) => now += duration;
    }
}
