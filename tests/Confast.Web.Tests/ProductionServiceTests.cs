using Confast.Web.Features.Customers;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Parts;
using Confast.Web.Features.ProductionScheduling;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ProductionServiceTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private ProductionService service = null!;
    private readonly TestClock clock = new();
    private long partId, machineId;
    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        await using var db = database.CreateDbContext();
        await db.Database.ExecuteSqlRawAsync("TRUNCATE production_progress, production_requirements, production_segments, production_jobs, part_machines, machine_working_days, machine_downtime, sorting_machines, production_holidays, downtime_reasons, production_audit RESTART IDENTITY CASCADE");
        var settings = await db.Set<ProductionSettings>().SingleAsync(); settings.EfficiencyPercent = 91;
        foreach (var day in await db.Set<DefaultWorkingDay>().ToListAsync()) day.Hours = day.Day is DayOfWeek.Sunday or DayOfWeek.Saturday ? 0 : 8;
        db.Users.Add(new() { Id = "planner", UserName = "planner", DisplayName = "Planner" });
        db.UserRoles.Add(new() { UserId = "planner", RoleId = await db.Roles.Where(x => x.Name == AppRoles.Administrator).Select(x => x.Id).SingleAsync() });
        var customer = new Customer { Name = "Scheduling customer" };
        var part = new Part { Customer = customer, PartNumber = "SORT-1", BoxQuantity = 1000 }; db.Add(part);
        await db.SaveChangesAsync(); partId = part.Id;
        service = new(database, new TestUser(), clock);
        await service.SaveMachineAsync(await Revision(), 0, "Sorter", true, false, [0, 8, 8, 8, 8, 8, 0]);
        machineId = (await service.GetAsync()).Machines.Single().Id;
        await service.SaveRateAsync(await Revision(), machineId, partId, 10000);
    }
    public Task DisposeAsync() => Task.CompletedTask;
    private async Task<long> Revision() => (await service.GetAsync()).Settings.Revision;
    private async Task<ProductionSegment> AddJob(decimal quantity = 100000)
    {
        await service.SaveJobAsync(await Revision(), 0, partId, machineId, quantity, "PO-1", "MO-1", null);
        return (await service.GetAsync()).Segments.MaxBy(x => x.Id)!;
    }

    [Fact]
    public async Task ProgressCorrectionsAndEfficiencyChangesKeepOriginalHistory()
    {
        var s = await AddJob();
        await service.StartAsync(await Revision(), s.Id);
        await service.ProgressAsync(await Revision(), s.Id, 22000, clock.Today, "first", false);
        await service.ProgressAsync(await Revision(), s.Id, 20000, clock.Today, "correction", false);
        var data = await service.GetAsync(); var before = data.Segments.Single();
        Assert.Equal(20000, before.CompletedQuantity); Assert.Equal(2, before.Progress.Count);
        await service.SaveEfficiencyAsync(data.Settings.Revision, 50);
        data = await service.GetAsync(); var after = data.Segments.Single();
        Assert.Equal(before.OriginalHours, after.OriginalHours); Assert.Equal(before.Progress.Select(x => x.CompletedQuantity), after.Progress.Select(x => x.CompletedQuantity));
        Assert.Equal(16, ProductionScheduler.Forecast(data).Single().RemainingHours);
        await Assert.ThrowsAsync<SchedulingException>(() => service.ProgressAsync(data.Settings.Revision, s.Id, 100001, clock.Today, null, false));
        await Assert.ThrowsAsync<SchedulingException>(() => service.ProgressAsync(data.Settings.Revision, s.Id, 20000, clock.Today, null, true));
    }

    [Fact]
    public async Task MachineRatesDoNotChangeThePartBoxQuantity()
    {
        await service.SaveRateAsync(await Revision(), machineId, partId, 9000);

        await using var db = database.CreateDbContext();
        Assert.Equal(1000, await db.Parts.Where(part => part.Id == partId).Select(part => part.BoxQuantity).SingleAsync());
    }

    [Fact]
    public async Task DowntimeReasonNamesAreUniqueAndOnlyUnusedReasonsCanBeDeleted()
    {
        await service.SaveReasonAsync(await Revision(), 0, "Maintenance", true);
        var reason = (await service.GetAsync()).Reasons.Single();

        var duplicateRevision = await Revision();
        var duplicate = await Assert.ThrowsAsync<SchedulingException>(() => service.SaveReasonAsync(duplicateRevision, 0, " maintenance ", true));
        Assert.Equal("A downtime reason with that name already exists.", duplicate.Message);
        await using (var db = database.CreateDbContext())
        {
            var databaseDuplicate = await Assert.ThrowsAsync<Npgsql.PostgresException>(() =>
                db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO downtime_reasons (name, is_active) VALUES ({"maintenance"}, {true})"));
            Assert.Equal("UX_downtime_reasons_name", databaseDuplicate.ConstraintName);
        }

        await service.SaveDowntimeAsync(await Revision(), 0, machineId, reason.Id, clock.Today, clock.Today, null);
        var inUseRevision = await Revision();
        var inUse = await Assert.ThrowsAsync<SchedulingException>(() => service.DeleteReasonAsync(inUseRevision, reason.Id));
        Assert.Contains("cannot be deleted", inUse.Message);

        await service.SaveReasonAsync(await Revision(), 0, "Unused", true);
        var unused = (await service.GetAsync()).Reasons.Single(x => x.Name == "Unused");
        await service.DeleteReasonAsync(await Revision(), unused.Id);
        Assert.DoesNotContain((await service.GetAsync()).Reasons, x => x.Id == unused.Id);
    }

    [Fact]
    public async Task CutInConservesQuantitiesAndRequiresExplicitStateTransitions()
    {
        var a = await AddJob(); var b = await AddJob(10000);
        await service.StartAsync(await Revision(), a.Id);
        await service.ProgressAsync(await Revision(), a.Id, 22000, clock.Today, null, false);
        await service.CutInAsync(await Revision(), a.Id, b.Id, 30000);
        var data = await service.GetAsync(); var job = data.Jobs.Single(x => x.Id == a.JobId);
        Assert.Equal(100000, job.Segments.Sum(x => x.Quantity)); Assert.Equal(22000, job.Segments.Sum(x => x.CompletedQuantity));
        Assert.Equal(ProductionState.Running, job.Segments.Single(x => x.Id == a.Id).State);
        Assert.Equal(30000, job.Segments.Single(x => x.Id == a.Id).Quantity);
        var resume = job.Segments.Single(x => x.Id != a.Id); Assert.Equal(70000, resume.Quantity);
        Assert.Equal(b.Id, resume.PredecessorId);
        var preview = ProductionScheduler.Optimize(data, machineId);
        Assert.Equal(new[] { a.Id, b.Id, resume.Id }, preview.Order);
        await service.ApplyOptimizationAsync(preview);
        await Assert.ThrowsAsync<SchedulingException>(async () => await service.StartAsync(await Revision(), b.Id));
        await service.ProgressAsync(await Revision(), a.Id, 30000, clock.Today, null, true);
        Assert.Equal(30000, (await service.GetAsync()).Jobs.Single(x => x.Id == a.JobId).Segments.Sum(x => x.CompletedQuantity));
    }

    [Fact]
    public async Task RequirementsUseStableCumulativeTargetsAndAdditionalQuantities()
    {
        var a = await AddJob();
        await service.SaveRequirementAsync(await Revision(), a.JobId, 0, clock.Today, 15000);
        await service.SaveRequirementAsync(await Revision(), a.JobId, 0, clock.Today.AddDays(1), 25000);
        var data = await service.GetAsync(); Assert.Equal(new[] { 15000m, 40000m }, data.Jobs.Single().Requirements.OrderBy(x => x.Date).Select(x => x.CumulativeTarget));
        await service.StartAsync(data.Settings.Revision, a.Id);
        await service.ProgressAsync(await Revision(), a.Id, 22000, clock.Today, null, false);
        await service.SaveRequirementAsync(await Revision(), a.JobId, 0, clock.Today.AddDays(2), null);
        await service.ProgressAsync(await Revision(), a.Id, 30000, clock.Today, null, false);
        data = await service.GetAsync(); Assert.Equal(new[] { 15000m, 40000m, 100000m }, data.Jobs.Single().Requirements.OrderBy(x => x.Date).Select(x => x.CumulativeTarget));
    }

    [Fact]
    public async Task PreviewAndConcurrentEditsRejectStaleRevision()
    {
        var a = await AddJob(); var data = await service.GetAsync(); var preview = ProductionScheduler.Optimize(data, machineId);
        await service.SetConstraintAsync(data.Settings.Revision, a.Id, clock.Today.AddDays(1), true);
        await Assert.ThrowsAsync<SchedulingException>(() => service.ApplyOptimizationAsync(preview));
        var revision = await Revision();
        var outcomes = await Task.WhenAll(Attempt(() => service.SaveEfficiencyAsync(revision, 90)), Attempt(() => service.SaveEfficiencyAsync(revision, 92)));
        Assert.Equal(1, outcomes.Count(x => x));
    }
    private static async Task<bool> Attempt(Func<Task> action) { try { await action(); return true; } catch (SchedulingException) { return false; } }

    [Fact]
    public async Task DefaultWorkingDaysSynchronizeOnlyMachinesThatFollowThem()
    {
        await service.SaveMachineAsync(await Revision(), 0, "Default sorter", true, true, [0, 1, 1, 1, 1, 1, 0]);
        var data = await service.GetAsync();
        var defaultSorter = data.Machines.Single(x => x.Name == "Default sorter");
        Assert.True(defaultSorter.UsesDefaultWorkingDays);
        Assert.Equal(8, defaultSorter.WorkingDays.Single(x => x.Day == DayOfWeek.Monday).Hours);

        await service.SaveDefaultWorkingDaysAsync(data.Settings.Revision, [0, 6.5m, 6.5m, 6.5m, 6.5m, 6.5m, 0]);
        data = await service.GetAsync();
        defaultSorter = data.Machines.Single(x => x.Name == "Default sorter");
        Assert.Equal(6.5m, data.Settings.DefaultWorkingDays.Single(x => x.Day == DayOfWeek.Monday).Hours);
        Assert.Equal(6.5m, defaultSorter.WorkingDays.Single(x => x.Day == DayOfWeek.Monday).Hours);
        Assert.Equal(8, data.Machines.Single(x => x.Id == machineId).WorkingDays.Single(x => x.Day == DayOfWeek.Monday).Hours);

        await service.SaveMachineAsync(data.Settings.Revision, machineId, "Sorter", true, true, [0, 1, 1, 1, 1, 1, 0]);
        data = await service.GetAsync();
        Assert.True(data.Machines.Single(x => x.Id == machineId).UsesDefaultWorkingDays);
        Assert.Equal(6.5m, data.Machines.Single(x => x.Id == machineId).WorkingDays.Single(x => x.Day == DayOfWeek.Monday).Hours);
    }

    [Fact]
    public async Task ReassignmentPreservesPerformedMachineAndRejectsIneligibleDestination()
    {
        var a = await AddJob();
        await service.SaveMachineAsync(await Revision(), 0, "Manual", true, false, [0, 4, 0, 4, 0, 4, 0]);
        var destination = (await service.GetAsync()).Machines.Single(x => x.Name == "Manual").Id;
        await Assert.ThrowsAsync<SchedulingException>(async () => await service.ReassignAsync(await Revision(), a.Id, destination));
        await service.SaveRateAsync(await Revision(), destination, partId, 5000);
        await service.StartAsync(await Revision(), a.Id);
        await service.ProgressAsync(await Revision(), a.Id, 22000, clock.Today, null, false);
        await service.ReassignAsync(await Revision(), a.Id, destination);
        var data = await service.GetAsync(); var history = data.Segments.Single(x => x.Id == a.Id);
        Assert.Equal(machineId, history.MachineId); Assert.Equal(22000, history.Quantity); Assert.Equal(ProductionState.Completed, history.State);
        var pending = data.Segments.Single(x => x.Id != a.Id); Assert.Equal(destination, pending.MachineId); Assert.Equal(78000, pending.Quantity);
        Assert.Equal(100000, data.Segments.Sum(x => x.Quantity)); Assert.Equal(machineId, history.Progress.Single().MachineId);
    }

    [Fact]
    public async Task CutInRejectsLostProgressAndZeroStopDoesNotCompleteAutomatically()
    {
        var a = await AddJob(); var b = await AddJob(10000);
        await service.StartAsync(await Revision(), a.Id);
        await service.CutInAsync(await Revision(), a.Id, b.Id, 0);
        var data = await service.GetAsync();
        Assert.Equal(ProductionState.Running, data.Segments.Single(x => x.Id == a.Id).State);
        Assert.Equal(100000, data.Jobs.Single(x => x.Id == a.JobId).Segments.Sum(x => x.Quantity));
        await Assert.ThrowsAsync<SchedulingException>(() => service.ProgressAsync(data.Settings.Revision, a.Id, 1, clock.Today, null, false));
        await service.ProgressAsync(data.Settings.Revision, a.Id, 0, clock.Today, null, true);
    }

    [Fact]
    public async Task DatabaseGuardsQuantityAndCompletedHistory()
    {
        var a = await AddJob(100);
        await using (var db = database.CreateDbContext())
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE production_segments SET quantity = 99 WHERE id = {a.Id}"));
        }
        await service.StartAsync(await Revision(), a.Id);
        await service.ProgressAsync(await Revision(), a.Id, 100, clock.Today, null, true);
        await using (var db = database.CreateDbContext())
        {
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE production_segments SET completed_quantity = 99 WHERE id = {a.Id}"));
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE production_progress SET completed_quantity = 99 WHERE segment_id = {a.Id}"));
        }
    }

    [Fact]
    public async Task CannotRemoveEligibilityFromUnfinishedWorkOrMoveCutInOutOfOrder()
    {
        var a = await AddJob(); var b = await AddJob(10000);
        await Assert.ThrowsAsync<SchedulingException>(async () => await service.RemoveRateAsync(await Revision(), machineId, partId));
        await service.CutInAsync(await Revision(), a.Id, b.Id, 30000);
        await Assert.ThrowsAsync<SchedulingException>(async () => await service.MoveAsync(await Revision(), b.Id, -1));
        var data = await service.GetAsync();
        Assert.Equal(a.Id, data.Segments.OrderBy(x => x.Sequence).First().Id);
    }

    [Fact]
    public async Task ServerEnforcesAuthorizationAndConfigurationValidation()
    {
        await Assert.ThrowsAsync<SchedulingException>(async () => await service.SaveEfficiencyAsync(await Revision(), 0));
        await Assert.ThrowsAsync<SchedulingException>(async () => await service.SaveEfficiencyAsync(await Revision(), 91.5m));
        await Assert.ThrowsAsync<SchedulingException>(async () => await service.SaveMachineAsync(await Revision(), machineId, "Sorter", true, false, [0, 8.25m, 8, 8, 8, 8, 0]));
        await Assert.ThrowsAsync<SchedulingException>(async () => await service.SaveRateAsync(await Revision(), machineId, partId, 0));
        await Assert.ThrowsAsync<SchedulingException>(async () => await service.SaveRateAsync(await Revision(), machineId, partId, 100.5m));
        await using (var db = database.CreateDbContext()) { db.UserRoles.RemoveRange(await db.UserRoles.ToListAsync()); await db.SaveChangesAsync(); }
        var data = await service.GetAsync(); Assert.False(data.CanEdit);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SaveJobAsync(data.Settings.Revision, 0, partId, machineId, 100, null, null, null));
    }

    private sealed class TestUser : ICurrentUser { public ValueTask<string?> GetUserIdAsync() => ValueTask.FromResult<string?>("planner"); }
    private sealed class TestClock : TimeProvider
    {
        public DateOnly Today => new(2026, 9, 7);
        public override DateTimeOffset GetUtcNow() => new(2026, 9, 7, 12, 0, 0, TimeSpan.Zero);
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}

