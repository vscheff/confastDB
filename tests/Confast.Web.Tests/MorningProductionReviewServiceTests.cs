using Confast.Web.Features.Customers;
using Confast.Web.Features.Identity;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.MorningProductionReview;
using Confast.Web.Features.Parts;
using Confast.Web.Features.ProductionScheduling;
using Confast.Web.Features.ProductionTracking;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class MorningProductionReviewServiceTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly MutableClock clock = new();
    private readonly TestUser user = new();
    private MorningProductionReviewService service = null!;
    private long machineId;
    private long idleMachineId;
    private long partId;
    private long inspectionId;

    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        await using var db = database.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE sort_log_lines, sort_logs, production_progress, production_requirements, production_segments, production_jobs, part_machines, machine_working_days, machine_downtime, sorting_machines, production_holidays, downtime_reasons, production_audit RESTART IDENTITY CASCADE");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM sort_log_downtime_causes WHERE id <> -1");

        db.Users.Add(new() { Id = user.Id, UserName = user.Id, DisplayName = "Morning Reviewer" });
        var productionRoleId = await db.Roles.Where(x => x.Name == AppRoles.Production).Select(x => x.Id).SingleAsync();
        db.UserRoles.Add(new() { UserId = user.Id, RoleId = productionRoleId });

        var machine = new SortingMachine { Name = "Sorter A", IsActive = true };
        var idleMachine = new SortingMachine { Name = "Sorter B", IsActive = true };
        db.AddRange(machine, idleMachine);
        await db.SaveChangesAsync();
        machineId = machine.Id;
        idleMachineId = idleMachine.Id;
        db.Add(new MachineWorkingDay { MachineId = machineId, Day = clock.Today.DayOfWeek, Hours = 8 });

        (partId, inspectionId) = await AddPartAsync(db, "PART-1", "LOT-1", "PO-1");
        db.Add(new PartMachine { MachineId = machineId, PartId = partId, TargetPph = 1000, IsPreferred = true });
        await db.SaveChangesAsync();

        var productionService = new ProductionService(database, user, clock);
        service = new(database, user, clock, productionService);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task DefaultsToLatestPriorProductionDateAndNavigatesOnlyDatesWithProduction()
    {
        await using (var db = database.CreateDbContext())
        {
            db.AddRange(
                Log(clock.Today.AddDays(-5)),
                Log(clock.Today.AddDays(-2)),
                Log(clock.Today));
            await db.SaveChangesAsync();
        }

        var review = await service.GetAsync();

        Assert.Equal(clock.Today.AddDays(-2), review.ProductionDate);
        Assert.Equal(clock.Today.AddDays(-2), review.LatestPriorProductionDate);
        Assert.Equal(clock.Today.AddDays(-5), review.PreviousProductionDate);
        Assert.Equal(clock.Today, review.NextProductionDate);
        var idleMachine = Assert.Single(review.MachineResults, x => x.MachineId == idleMachineId);
        Assert.Equal(0, idleMachine.RunCount);
        Assert.Empty(idleMachine.Runs);

        var arbitraryDate = await service.GetAsync(clock.Today.AddDays(-4));
        Assert.Equal(clock.Today.AddDays(-5), arbitraryDate.PreviousProductionDate);
        Assert.Equal(clock.Today.AddDays(-2), arbitraryDate.NextProductionDate);
        await Assert.ThrowsAsync<MorningProductionReviewException>(() => service.GetAsync(clock.Today.AddDays(1)));
    }

    [Fact]
    public async Task AggregatesMultipleRunsWithGoodOnlyPphAndMachineWideDowntime()
    {
        await using (var db = database.CreateDbContext())
        {
            var cause = new SortLogDowntimeCause { Name = "Material", NormalizedName = "MATERIAL" };
            db.Add(cause);
            await db.SaveChangesAsync();
            var date = clock.Today.AddDays(-1);
            var first = Log(date, 100);
            first.Lines.Add(Line(1, clock.TodayAt(8), clock.TodayAt(9), cause.Id, 100, 10));
            first.Lines.Add(Line(2, clock.TodayAt(9, 30), clock.TodayAt(10), cause.Id, 50, 0));
            var (secondPartId, secondInspectionId) = await AddPartAsync(db, "PART-2", "LOT-2", "PO-2");
            var second = Log(date, 200);
            second.PartId = secondPartId;
            second.InspectionId = secondInspectionId;
            second.Lines.Add(Line(1, clock.TodayAt(10, 15), clock.TodayAt(11, 15), cause.Id, 100, 5));
            db.AddRange(first, second);
            await db.SaveChangesAsync();
        }

        var review = await service.GetAsync(clock.Today.AddDays(-1));

        Assert.Equal(250, review.Summary.GoodQuantity);
        Assert.Equal(15, review.Summary.FailedQuantity);
        Assert.Equal(265, review.Summary.FeedQuantity);
        Assert.Equal(100m, review.Summary.ActualPph);
        Assert.Equal(140m, review.Summary.TargetPph);
        Assert.Equal(100m / 140m, review.Summary.Efficiency);
        Assert.Equal(TimeSpan.FromHours(2.5), review.Summary.ProductionTime);
        Assert.Equal(TimeSpan.FromMinutes(45), review.Summary.Downtime);

        var machine = Assert.Single(review.MachineResults, x => x.MachineId == machineId);
        Assert.Equal(2, machine.RunCount);
        Assert.Equal(2, machine.Runs.Count);
        Assert.Equal(TimeSpan.FromMinutes(45), machine.Downtime);
        Assert.Equal([TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(15)],
            review.Downtime.Select(x => x.Duration));
    }

    [Fact]
    public async Task ActualActiveRunOverridesScheduleAndIdleMachinesRemainVisible()
    {
        await using (var db = database.CreateDbContext())
        {
            var job = ScheduledJob("MO-100", 1, 100);
            db.Add(job);
            var (otherPartId, otherInspectionId) = await AddPartAsync(db, "PART-ACTUAL", "LOT-ACTUAL", "PO-ACTUAL");
            await db.SaveChangesAsync();
            var active = new SortLog
            {
                ProductionDate = clock.Today,
                MachineId = machineId,
                PartId = otherPartId,
                InspectionId = otherInspectionId,
                TargetPphSnapshot = 500,
                CreatedAtUtc = clock.GetUtcNow(),
                CreatedByUserId = user.Id
            };
            active.Lines.Add(Line(1, clock.TodayAt(7, 45), null, null, 0, 0));
            db.Add(active);
            await db.SaveChangesAsync();
        }

        var review = await service.GetAsync();

        var running = Assert.Single(review.CurrentMachines, x => x.MachineId == machineId);
        Assert.False(running.IsIdle);
        Assert.Equal("PART-ACTUAL", running.PartNumber);
        Assert.Equal("LOT-ACTUAL", running.LotNumber);
        Assert.Equal("PART-1", running.NextJob!.PartNumber);
        Assert.Equal("MO-100", running.NextJob.MoNumber);
        Assert.Equal(clock.Today, running.NextJob.ExpectedStart);

        var idle = Assert.Single(review.CurrentMachines, x => x.MachineId == idleMachineId);
        Assert.True(idle.IsIdle);
        Assert.Null(idle.SortLogId);
        var firstChangeover = Assert.Single(review.Changeovers);
        Assert.Equal("PART-ACTUAL", firstChangeover.PreviousPartNumber);
        Assert.Equal("PART-1", firstChangeover.NextPartNumber);
    }

    [Fact]
    public async Task SamePartDifferentScheduledJobsAreSeparateChangeovers()
    {
        await using (var db = database.CreateDbContext())
        {
            db.AddRange(
                ScheduledJob("MO-100", 1, 100),
                ScheduledJob("MO-101", 2, 100),
                ScheduledJob("MO-102", 3, 100));
            await db.SaveChangesAsync();
        }

        var review = await service.GetAsync();

        Assert.Equal(2, review.Changeovers.Count);
        Assert.All(review.Changeovers, x =>
        {
            Assert.Equal("PART-1", x.PreviousPartNumber);
            Assert.Equal("PART-1", x.NextPartNumber);
        });
        Assert.Collection(review.Changeovers,
            first => { Assert.Equal("MO-100", first.PreviousMoNumber); Assert.Equal("MO-101", first.NextMoNumber); },
            second => { Assert.Equal("MO-101", second.PreviousMoNumber); Assert.Equal("MO-102", second.NextMoNumber); });
    }

    [Fact]
    public async Task ScheduleEfficiencyComparesGoodPartsWithTheScheduledEffectiveCapacity()
    {
        await using (var db = database.CreateDbContext())
        {
            var rate = await db.Set<PartMachine>().SingleAsync(x => x.MachineId == machineId && x.PartId == partId);
            rate.TargetPph = 10_000;
            db.Add(ScheduledJob("MO-100", 1, 100_000));
            var log = Log(clock.Today, 10_000);
            log.Lines.Add(Line(1, clock.TodayAt(8), clock.TodayAt(16), null, 75_000, 0));
            db.Add(log);
            await db.SaveChangesAsync();
        }

        var review = await service.GetAsync(clock.Today);
        var machine = Assert.Single(review.MachineResults, x => x.MachineId == machineId);

        Assert.Equal(72_800m, machine.ScheduledQuantity);
        Assert.Equal(75_000m / 72_800m, machine.ScheduleEfficiency);
    }

    private SortLog Log(DateOnly date, decimal targetPph = 100) => new()
    {
        ProductionDate = date,
        MachineId = machineId,
        PartId = partId,
        InspectionId = inspectionId,
        TargetPphSnapshot = targetPph,
        CreatedAtUtc = clock.GetUtcNow(),
        CreatedByUserId = user.Id
    };

    private ProductionJob ScheduledJob(string moNumber, int sequence, decimal quantity)
    {
        var job = new ProductionJob { PartId = partId, PoNumber = "PO-1", MoNumber = moNumber, Quantity = quantity };
        job.Segments.Add(new ProductionSegment
        {
            MachineId = machineId,
            Sequence = sequence,
            Quantity = quantity,
            State = ProductionState.Pending,
            OriginalHours = quantity / 910m,
            OriginalTargetPph = 1000,
            OriginalEfficiencyPercent = 91
        });
        return job;
    }

    private static SortLogLine Line(int sequence, DateTimeOffset start, DateTimeOffset? stop,
        long? causeId, long good, long failed) => new()
    {
        Sequence = sequence,
        StartTimeUtc = start,
        StopTimeUtc = stop,
        DowntimeCauseId = causeId,
        PassQuantity = good,
        FailQuantity = failed,
        ProductionInitials = "PO"
    };

    private async Task<(long PartId, long InspectionId)> AddPartAsync(
        Confast.Web.Data.AppDbContext db, string partNumber, string lotNumber, string poNumber)
    {
        var part = new Part { Customer = new Customer { Name = $"Customer {partNumber}" }, PartNumber = partNumber };
        db.Add(part);
        await db.SaveChangesAsync();
        var revision = new InspectionCriteriaRevision
        {
            PartId = part.Id,
            RevisionNumber = 1,
            CreatedAtUtc = clock.GetUtcNow(),
            PublishedAtUtc = clock.GetUtcNow()
        };
        db.Add(revision);
        await db.SaveChangesAsync();
        var inspection = new Inspection
        {
            PartId = part.Id,
            InspectionCriteriaRevisionId = revision.Id,
            LotNumber = lotNumber,
            ConformancePoNumber = poNumber,
            InspectionDate = clock.Today,
            CreatedAtUtc = clock.GetUtcNow()
        };
        db.Add(inspection);
        await db.SaveChangesAsync();
        return (part.Id, inspection.Id);
    }

    private sealed class TestUser : ICurrentUser
    {
        public string Id => "morning-reviewer";
        public ValueTask<string?> GetUserIdAsync() => ValueTask.FromResult<string?>(Id);
    }

    private sealed class MutableClock : TimeProvider
    {
        private readonly DateTimeOffset now = new(2026, 9, 18, 8, 0, 0, TimeSpan.Zero);
        public DateOnly Today => DateOnly.FromDateTime(now.DateTime);
        public DateTimeOffset TodayAt(int hour, int minute = 0) => new(now.Year, now.Month, now.Day, hour, minute, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
