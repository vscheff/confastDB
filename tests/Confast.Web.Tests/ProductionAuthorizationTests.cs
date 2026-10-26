using System.Security.Claims;
using System.Data.Common;
using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.ContainerTracking;
using Confast.Web.Features.Customers;
using Confast.Web.Features.Gages;
using Confast.Web.Features.Identity;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.MorningProductionReview;
using Confast.Web.Features.Parts;
using Confast.Web.Features.ProductionScheduling;
using Confast.Web.Features.ProductionTracking;
using Confast.Web.Features.Suppliers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ProductionAuthorizationTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly AuthorizationTestSession session = new(database);
    private readonly FixtureClock clock = new();
    private EffectivePermissionService evaluator = null!;
    private ProductionService production = null!;
    private ProductionTrackingService tracking = null!;
    private MorningProductionReviewService reports = null!;
    private string root = null!;
    private OptimizationPreview optimization = null!;
    private long partId, secondPartId, machineId, secondMachineId, runningId, pendingId, startableId, jobId, inspectionId, logId, reasonId, causeId, downtimeId, containerLineId;
    private static readonly decimal[] Hours = [0, 8, 8, 8, 8, 8, 0];

    public async Task InitializeAsync()
    {
        await database.ResetAsync(); root = await session.ProvisionRootAsync(); await session.SignInAsync(root);
        evaluator = session.Evaluator;
        var guard = new ApplicationAuthorization(evaluator, new HttpContextAccessor());
        production = new(database, guard, clock); tracking = new(database, guard, clock); reports = new(database, guard, clock, production);
        await using var db = database.CreateDbContext();
        // The shared reset preserves machine/calendar configuration; this fixture owns its production scenario.
        await db.Database.ExecuteSqlRawAsync("TRUNCATE production_progress, production_requirements, production_segments, production_jobs, part_machines, machine_working_days, machine_downtime, sorting_machines, production_holidays, downtime_reasons, production_audit RESTART IDENTITY CASCADE");
        await db.Database.ExecuteSqlRawAsync("DELETE FROM sort_log_downtime_causes WHERE id <> -1");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO sort_log_downtime_causes (id, name, normalized_name, is_active) VALUES (-1, 'End of Day', 'END OF DAY', true) ON CONFLICT (id) DO NOTHING");
        (await db.Set<ProductionSettings>().SingleAsync()).EfficiencyPercent = 91;
        var customer = new Customer { Name = "Production Authorization" };
        var supplier = new Supplier { Name = "Production Supplier" };
        var part = new Part { Customer = customer, Supplier = supplier, PartNumber = "AUTH-PRODUCTION", BoxQuantity = 100 };
        var secondPart = new Part { Customer = customer, PartNumber = "AUTH-OTHER" };
        var machine = new SortingMachine { Name = "Auth Sorter A" }; var secondMachine = new SortingMachine { Name = "Auth Sorter B" };
        for (var day = 0; day < 7; day++)
        {
            machine.WorkingDays.Add(new() { Day = (DayOfWeek)day, Hours = Hours[day] });
            secondMachine.WorkingDays.Add(new() { Day = (DayOfWeek)day, Hours = Hours[day] });
        }
        var revision = new InspectionCriteriaRevision { Part = part, RevisionNumber = 1, CreatedAtUtc = clock.GetUtcNow(), PublishedAtUtc = clock.GetUtcNow() };
        var gageType = new GageType { Name = "Auth Pass Fail" };
        var gage = new Gage { GageType = gageType, GageNumber = "AUTH-GAGE" };
        var criterion = new InspectionCriterion { Revision = revision, GageType = gageType, Name = "Check", InspectionNumber = 1, DisplayOrder = 1 };
        db.AddRange(part, secondPart, machine, secondMachine, revision, gage, criterion); await db.SaveChangesAsync();
        partId = part.Id; secondPartId = secondPart.Id; machineId = machine.Id; secondMachineId = secondMachine.Id;
        db.AddRange(new PartMachine { MachineId = machineId, PartId = partId, TargetPph = 1000, IsPreferred = true },
            new PartMachine { MachineId = secondMachineId, PartId = partId, TargetPph = 1000 });
        var inspection = new Inspection { PartId = partId, InspectionCriteriaRevisionId = revision.Id, LotNumber = "AUTH-LOT", ConformancePoNumber = "AUTH-PO", InspectionDate = clock.Today };
        db.Add(inspection); await db.SaveChangesAsync(); inspectionId = inspection.Id;
        db.InspectionResults.Add(new() { InspectionId = inspectionId, InspectionCriteriaRevisionId = revision.Id, InspectionCriterionId = criterion.Id,
            GageId = gage.Id, GageNumber = gage.GageNumber, ActualMin = "Pass", ActualMax = "Pass" });
        ProductionJob Job(long machineKey, int sequence, bool running) => new()
        {
            PartId = partId, PoNumber = "AUTH-PO", Quantity = 1000,
            Segments = [new() { MachineId = machineKey, Sequence = sequence, Quantity = 1000, State = running ? ProductionState.Running : ProductionState.Pending,
                ActualStart = running ? clock.Today : null, CompletedQuantity = running ? 100 : 0, ProgressAsOf = running ? clock.Today : null,
                OriginalTargetPph = 1000, OriginalEfficiencyPercent = 91, OriginalHours = 1000m / 910 }]
        };
        var runningJob = Job(machineId, 1, true); var pendingJob = Job(machineId, 2, false); var startableJob = Job(secondMachineId, 1, false);
        var reason = new DowntimeReason { Name = "Authorization Reason" };
        var cause = new SortLogDowntimeCause { Name = "Authorization Cause", NormalizedName = "AUTHORIZATION CAUSE" };
        db.AddRange(runningJob, pendingJob, startableJob, reason, cause); await db.SaveChangesAsync(); causeId = cause.Id;
        runningId = runningJob.Segments[0].Id; pendingId = pendingJob.Segments[0].Id; startableId = startableJob.Segments[0].Id; jobId = pendingJob.Id; reasonId = reason.Id;
        var downtime = new MachineDowntime { MachineId = machineId, ReasonId = reasonId, Start = clock.Today.AddDays(10), End = clock.Today.AddDays(10) };
        db.Add(downtime); db.Add(new ProductionHoliday { Date = clock.Today.AddDays(20), Name = "Auth Holiday" });
        var container = new Container { Shipment = new Shipment(), ContainerNumber = "AUTH-SOURCE", EstimatedDepartureDate = clock.Today.AddDays(-1), EstimatedArrivalDate = clock.Today };
        var line = new ContainerGroupPart { ContainerGroup = new ContainerGroup { Container = container, BillOfLading = new BillOfLading { Number = "AUTH-SOURCE-BILL", Supplier = supplier } },
            PartId = partId, PurchaseOrderNumber = "AUTH-SOURCE-PO", Quantity = 100 };
        db.Add(line); await db.SaveChangesAsync(); downtimeId = downtime.Id; containerLineId = line.Id;
        logId = await tracking.OpenOrCreateSortLogAsync(machineId, inspectionId, runningId);
        optimization = ProductionScheduler.Optimize(await production.GetReadOnlyAsync(), machineId);
    }
    public Task DisposeAsync() { session.Dispose(); return Task.CompletedTask; }
    private async Task<long> Revision() { await using var db = database.CreateDbContext(); return await db.Set<ProductionSettings>().Select(x => x.Revision).SingleAsync(); }
    private async Task<string> Actor(params string[] keys)
    {
        await using var db = database.CreateDbContext();
        var role = new ApplicationRole { Name = Guid.NewGuid().ToString(), NormalizedName = Guid.NewGuid().ToString(), IsEnabled = true };
        db.Roles.Add(role); db.RolePermissions.AddRange(PermissionCatalog.ExpandRequirements(keys).Select(k => new RolePermission { RoleId = role.Id, PermissionKey = k }));
        var actor = new ApplicationUser { UserName = Guid.NewGuid().ToString(), DisplayName = "Production Actor", IsActive = true, SecurityStamp = Guid.NewGuid().ToString() };
        db.Users.Add(actor); db.UserRoles.Add(new IdentityUserRole<string> { UserId = actor.Id, RoleId = role.Id }); await db.SaveChangesAsync();
        await db.UserRoles.Where(x => x.UserId == actor.Id && x.RoleId == AppRoles.ReadOnlyId).ExecuteDeleteAsync();
        await session.SignInAsync(actor.Id); return actor.Id;
    }

    private Dictionary<string, (string Key, Func<Task> Call)> Boundaries(long revision) => new()
    {
        ["Schedule.Read"] = (Permissions.ProductionSchedules.Read, () => production.GetAsync()),
        ["Schedule.ReadOnly"] = (Permissions.ProductionSchedules.Read, () => production.GetReadOnlyAsync()),
        ["Configuration.Read"] = (Permissions.SortingMachines.Read, () => production.GetConfigurationAsync()),
        ["Part.Read"] = (Permissions.PartMachineEligibility.Read, () => production.GetPartConfigurationAsync(partId)),
        ["Append"] = (Permissions.ProductionSchedules.AppendContainerPart, () => production.AppendContainerPartAsync(containerLineId)),
        ["Jobs.Create"] = (Permissions.ProductionJobs.Create, () => production.SaveJobAsync(revision, 0, partId, machineId, 100, null, null, null)),
        ["Jobs.Update"] = (Permissions.ProductionJobs.Update, () => production.SaveJobAsync(revision, jobId, partId, machineId, 1000, "AUTH-PO", null, "Updated")),
        ["Jobs.Delete"] = (Permissions.ProductionJobs.Delete, () => production.DeleteJobAsync(revision, jobId)),
        ["Jobs.Requirements"] = (Permissions.ProductionJobs.Update, () => production.SaveRequirementAsync(revision, jobId, 0, clock.Today.AddDays(5), 500)),
        ["Start"] = (Permissions.ProductionSchedules.StartWork, () => production.StartAsync(revision, startableId)),
        ["Progress"] = (Permissions.ProductionSchedules.RecordProgress, () => production.ProgressAsync(revision, runningId, 10, clock.Today, null, false)),
        ["Complete"] = (Permissions.ProductionSchedules.CompleteWork, () => production.CompleteSegmentAsync(revision, runningId, 1000, clock.Today, null, CompletionQuantityAdjustment.Exact)),
        ["CorrectProgress"] = (Permissions.ProductionSchedules.CorrectProgress, () => production.CorrectProgressAsync(revision, runningId, 50, clock.Today, "Correction")),
        ["Constraints"] = (Permissions.ProductionSchedules.Arrange, () => production.SetConstraintAsync(revision, pendingId, null, true)),
        ["Move"] = (Permissions.ProductionSchedules.Arrange, () => production.MoveAsync(revision, pendingId, 1)),
        ["Reassign"] = (Permissions.ProductionSchedules.Arrange, () => production.ReassignAsync(revision, pendingId, secondMachineId)),
        ["CutIn"] = (Permissions.ProductionSchedules.Arrange, () => production.CutInAsync(revision, runningId, pendingId, 500)),
        ["Optimize"] = (Permissions.ProductionSchedules.Optimize, () => production.ApplyOptimizationAsync(optimization)),
        ["Machines.Create"] = (Permissions.SortingMachines.Create, () => production.SaveMachineAsync(revision, 0, "New Sorter", true, false, Hours)),
        ["Machines.Update"] = (Permissions.SortingMachines.Update, () => production.SaveMachineAsync(revision, machineId, "Updated Sorter", true, false, Hours)),
        ["Calendar"] = (Permissions.ProductionCalendars.Update, () => production.SaveDefaultWorkingDaysAsync(revision, Hours)),
        ["Eligibility.Create"] = (Permissions.PartMachineEligibility.Create, () => production.SaveRateAsync(revision, machineId, secondPartId, 500)),
        ["Eligibility.Update"] = (Permissions.PartMachineEligibility.Update, () => production.SaveRateAsync(revision, machineId, partId, 500)),
        ["Eligibility.Preference"] = (Permissions.PartMachineEligibility.Update, () => production.SetPreferredMachineAsync(revision, secondMachineId, partId)),
        ["Eligibility.Delete"] = (Permissions.PartMachineEligibility.Delete, () => production.RemoveRateAsync(revision, machineId, partId)),
        ["Efficiency"] = (Permissions.ProductionSettings.Update, () => production.SaveEfficiencyAsync(revision, 85)),
        ["Holiday.Create"] = (Permissions.ProductionHolidays.Create, () => production.SaveHolidayAsync(revision, clock.Today.AddDays(21), "New", false)),
        ["Holiday.SaveExisting"] = (Permissions.ProductionHolidays.Update, () => production.SaveHolidayAsync(revision, clock.Today.AddDays(20), "Updated", false)),
        ["Holiday.Update"] = (Permissions.ProductionHolidays.Update, () => production.UpdateHolidayAsync(revision, clock.Today.AddDays(20), clock.Today.AddDays(22), "Updated")),
        ["Holiday.Delete"] = (Permissions.ProductionHolidays.Delete, () => production.SaveHolidayAsync(revision, clock.Today.AddDays(20), "", true)),
        ["Reasons.Create"] = (Permissions.ProductionDowntimeReasons.Create, () => production.SaveReasonAsync(revision, 0, "New Reason", true)),
        ["Reasons.Update"] = (Permissions.ProductionDowntimeReasons.Update, () => production.SaveReasonAsync(revision, reasonId, "Updated Reason", true)),
        ["Reasons.Delete"] = (Permissions.ProductionDowntimeReasons.Delete, () => production.DeleteReasonAsync(revision, reasonId)),
        ["Downtime.Create"] = (Permissions.MachineDowntime.Create, () => production.SaveDowntimeAsync(revision, 0, machineId, reasonId, clock.Today, clock.Today, null)),
        ["Downtime.Update"] = (Permissions.MachineDowntime.Update, () => production.SaveDowntimeAsync(revision, downtimeId, machineId, reasonId, clock.Today, clock.Today, null)),
        ["Downtime.Delete"] = (Permissions.MachineDowntime.Delete, () => production.SaveDowntimeAsync(revision, downtimeId, 0, 0, default, default, null, true)),
        ["Logs.Read"] = (Permissions.ProductionLogs.Read, () => tracking.GetDashboardAsync(machineId, logId)),
        ["Logs.Create"] = (Permissions.ProductionLogs.Create, () => tracking.OpenOrCreateSortLogAsync(machineId, inspectionId, runningId)),
        ["Logs.Update"] = (Permissions.ProductionLogs.Update, () => tracking.SaveCommentsAsync(logId, "Comment")),
        ["Logs.Start"] = (Permissions.ProductionLogs.StartRun, () => tracking.StartRunAsync(logId, new(0, 0, 0, "AB", null, null))),
        ["Logs.Stop"] = (Permissions.ProductionLogs.StopRun, () => tracking.StopRunAsync(logId)),
        ["Logs.Resume"] = (Permissions.ProductionLogs.ResumeRun, () => tracking.ResumeRunAsync(logId)),
        ["Logs.StopDetails"] = (Permissions.ProductionLogs.StopRun, () => tracking.SaveStopDetailsAsync(1, LineInput())),
        ["Logs.Correct"] = (Permissions.ProductionLogs.CorrectLine, () => tracking.UpdateLineAsync(1, LineInput())),
        ["Causes.Read"] = (Permissions.SortLogDowntimeCauses.Read, () => tracking.GetDowntimeCausesAsync()),
        ["Causes.Create"] = (Permissions.SortLogDowntimeCauses.Create, () => tracking.SaveDowntimeCauseAsync(0, "New Cause", true)),
        ["Causes.Update"] = (Permissions.SortLogDowntimeCauses.Update, () => tracking.SaveDowntimeCauseAsync(causeId, "Updated Cause", true)),
        ["Causes.Delete"] = (Permissions.SortLogDowntimeCauses.Delete, () => tracking.DeleteDowntimeCauseAsync(causeId)),
        ["Reports.Read"] = (Permissions.ProductionReports.Read, () => reports.GetAsync(clock.Today.AddDays(-1)))
    };
    private UpdateSortLogLineInput LineInput() => new(clock.GetUtcNow().AddHours(-1), clock.GetUtcNow(), causeId, 1000, 100, 0, 0, 0, 10, "AB", null, null);

    [Theory]
    [InlineData("anonymous")]
    [InlineData("no grants")]
    [InlineData("inactive")]
    [InlineData("stale session")]
    [InlineData("revoked")]
    [InlineData("forged Administrator")]
    [InlineData("PendingRoot")]
    public async Task EveryPublicBoundaryRejectsUntrustedOrUnauthorizedActors(string kind)
    {
        var revision = await Revision();
        var actor = await Actor(PermissionCatalog.All.Where(x => x.Authority == PermissionAuthority.Ordinary).Select(x => x.Key).ToArray());
        await using (var db = database.CreateDbContext())
        {
            if (kind is "no grants" or "revoked" or "forged Administrator") await db.UserRoles.Where(x => x.UserId == actor).ExecuteDeleteAsync();
            if (kind == "inactive") { (await db.Users.SingleAsync(x => x.Id == actor)).IsActive = false; await db.SaveChangesAsync(); }
            if (kind == "stale session") { (await db.Users.SingleAsync(x => x.Id == actor)).SecurityStamp = Guid.NewGuid().ToString(); await db.SaveChangesAsync(); }
        }
        if (kind == "anonymous") session.Principal = new(new ClaimsIdentity());
        if (kind == "forged Administrator") ((ClaimsIdentity)session.Principal.Identity!).AddClaim(new(ClaimTypes.Role, AppRoles.Administrator));
        if (kind == "PendingRoot") { await database.ResetAsync(); await Actor(Permissions.ProductionSchedules.Read); }
        foreach (var (name, boundary) in Boundaries(revision))
        {
            var error = await Record.ExceptionAsync(boundary.Call);
            Assert.True(error is AuthorizationDeniedException, $"{kind} / {name}: {error}");
        }
    }

    public static IEnumerable<object[]> BoundaryNames() => new[]
    {
        "Schedule.Read", "Schedule.ReadOnly", "Configuration.Read", "Part.Read", "Append", "Jobs.Create", "Jobs.Update", "Jobs.Delete", "Jobs.Requirements",
        "Start", "Progress", "Complete", "CorrectProgress", "Constraints", "Move", "Reassign", "CutIn", "Optimize", "Machines.Create", "Machines.Update", "Calendar",
        "Eligibility.Create", "Eligibility.Update", "Eligibility.Preference", "Eligibility.Delete", "Efficiency", "Holiday.Create", "Holiday.SaveExisting", "Holiday.Update", "Holiday.Delete",
        "Reasons.Create", "Reasons.Update", "Reasons.Delete", "Downtime.Create", "Downtime.Update", "Downtime.Delete", "Logs.Read", "Logs.Create", "Logs.Update", "Logs.Start", "Logs.Stop",
        "Logs.Resume", "Logs.StopDetails", "Logs.Correct", "Causes.Read", "Causes.Create", "Causes.Update", "Causes.Delete", "Reports.Read"
    }.Select(name => new object[] { name });

    [Theory, MemberData(nameof(BoundaryNames))]
    public async Task ExactKeyAndPrerequisitesAuthorizeEachBoundaryWithoutUnrelatedEditorGrants(string name)
    {
        var boundary = Boundaries(await Revision())[name];
        await Actor(boundary.Key);
        var before = evaluator.SnapshotCount;
        var error = await Record.ExceptionAsync(boundary.Call);
        Assert.True(error is null or SchedulingException or ProductionTrackingException, $"{name}: {error}");
        Assert.Equal(before + 1, evaluator.SnapshotCount);
    }

    [Fact]
    public async Task ReadOnlyScheduleCanCatchUpButCannotPlanAndCatchUpIsIdempotent()
    {
        await Actor(Permissions.ProductionSchedules.Read);
        var before = evaluator.SnapshotCount;
        var first = await production.GetAsync();
        Assert.Equal(before + 1, evaluator.SnapshotCount);
        Assert.Single(first.Jobs, x => x.ContainerGroupPartId == containerLineId);
        var second = await production.GetAsync(); Assert.Equal(first.Settings.Revision, second.Settings.Revision);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => production.SaveJobAsync(second.Settings.Revision, 0, partId, machineId, 100, null, null, null));
        await using var db = database.CreateDbContext();
        Assert.All(await db.Set<ProductionAudit>().ToListAsync(), x => Assert.Equal("System", x.RecordedBy));
    }

    [Fact]
    public async Task ProgressCannotCompleteOrCorrectAndUnauthorizedCallsLeaveLedgerAndRevisionUnchanged()
    {
        await Actor(Permissions.ProductionSchedules.RecordProgress);
        await production.ProgressAsync(await Revision(), runningId, 20, clock.Today, null, false);
        var revision = await Revision();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => production.ProgressAsync(revision, runningId, 880, clock.Today, null, true));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => production.CompleteSegmentAsync(revision, runningId, 1000, clock.Today, null, CompletionQuantityAdjustment.Exact));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => production.CorrectProgressAsync(revision, runningId, 10, clock.Today, null));
        await using var db = database.CreateDbContext(); var segment = await db.Set<ProductionSegment>().SingleAsync(x => x.Id == runningId);
        Assert.Equal(ProductionState.Running, segment.State); Assert.Equal(120, segment.CompletedQuantity); Assert.Single(await db.Set<ProductionProgress>().ToListAsync()); Assert.Equal(revision, await Revision());
        await Actor(Permissions.ProductionSchedules.CompleteWork);
        await production.CompleteSegmentAsync(revision, runningId, 1000, clock.Today, null, CompletionQuantityAdjustment.Exact);
    }

    [Fact]
    public async Task PersistedConfigurationStateSeparatesCreateUpdateDeleteAndRejectsMissingIdUpserts()
    {
        await Actor(Permissions.PartMachineEligibility.Create, Permissions.ProductionHolidays.Create, Permissions.SortingMachines.Update, Permissions.ProductionDowntimeReasons.Update, Permissions.MachineDowntime.Update);
        var revision = await Revision();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => production.SaveRateAsync(revision, machineId, partId, 2000));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => production.SaveHolidayAsync(revision, clock.Today.AddDays(20), "Overwrite", false));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => production.SaveHolidayAsync(revision, clock.Today.AddDays(20), "Delete", true));
        await Assert.ThrowsAsync<SchedulingException>(() => production.SaveMachineAsync(revision, long.MaxValue, "Forged", true, false, Hours));
        await Assert.ThrowsAsync<SchedulingException>(() => production.SaveReasonAsync(revision, long.MaxValue, "Forged", true));
        await Assert.ThrowsAsync<SchedulingException>(() => production.SaveDowntimeAsync(revision, long.MaxValue, machineId, reasonId, clock.Today, clock.Today, null));
        Assert.Equal(revision, await Revision());
        await Actor(Permissions.PartMachineEligibility.Update);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => production.SaveRateAsync(revision, machineId, secondPartId, 2000));
    }

    [Fact]
    public async Task ConfigurationReadsAreIndependentAndPartProjectionIsBounded()
    {
        await Actor(Permissions.ProductionHolidays.Read);
        var config = await production.GetConfigurationAsync(); Assert.Single(config.Holidays); Assert.Empty(config.Machines); Assert.Empty(config.Reasons); Assert.Equal(0, config.Settings.EfficiencyPercent);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => production.GetAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () => await production.SaveHolidayAsync(await Revision(), clock.Today.AddDays(20), "Denied", false));
        await Actor(Permissions.Parts.Read);
        var part = await production.GetPartConfigurationAsync(partId);
        Assert.All(part.Machines.SelectMany(x => x.Parts), x => Assert.Equal(partId, x.PartId));
        Assert.All(part.Machines, x => Assert.Empty(x.WorkingDays)); Assert.Equal(91, part.Settings.EfficiencyPercent);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => production.GetConfigurationAsync());
    }

    [Fact]
    public async Task StopRunCanEnterInitialDetailsButCannotCorrectRecordedLinesOrComments()
    {
        var line = await tracking.StartRunAsync(logId, new(0, 0, 0, "AB", null, null)); clock.Advance(TimeSpan.FromHours(1));
        await Actor(Permissions.ProductionLogs.StopRun);
        await tracking.StopRunAsync(logId);
        await Assert.ThrowsAsync<ProductionTrackingException>(() => tracking.SaveStopDetailsAsync(line, LineInput() with { StartBoxCount = 20 }));
        await tracking.SaveStopDetailsAsync(line, LineInput());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.UpdateLineAsync(line, LineInput()));
        await Assert.ThrowsAsync<ProductionTrackingException>(() => tracking.SaveStopDetailsAsync(line, LineInput()));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.SaveCommentsAsync(logId, "Forbidden"));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.ResumeRunAsync(logId));
        var dashboard = await tracking.GetDashboardAsync(machineId, logId);
        Assert.Equal(1000, dashboard.CurrentLog!.Metrics.Pph); Assert.Equal(100, dashboard.CurrentLog.Metrics.TotalFailed); Assert.Equal(1m, dashboard.CurrentLog.Metrics.Efficiency);
    }

    [Fact]
    public async Task StartStopResumeAndCorrectionAreIndependentAndRevocationUsesFreshState()
    {
        await Actor(Permissions.ProductionLogs.StartRun);
        var line = await tracking.StartRunAsync(logId, new(0, 0, 0, "AB", null, null)); clock.Advance(TimeSpan.FromHours(1));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.StopRunAsync(logId));
        await Actor(Permissions.ProductionLogs.StopRun); await tracking.StopRunAsync(logId);
        await Actor(Permissions.ProductionLogs.ResumeRun); await tracking.ResumeRunAsync(logId);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.StopRunAsync(logId));
        var actor = await Actor(Permissions.ProductionLogs.Update); await tracking.SaveCommentsAsync(logId, "Saved");
        await using var db = database.CreateDbContext(); await db.UserRoles.Where(x => x.UserId == actor).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.SaveCommentsAsync(logId, "Denied"));
        Assert.Equal("Saved", await db.Set<SortLog>().Where(x => x.Id == logId).Select(x => x.Comments).SingleAsync());
        Assert.Null((await db.Set<SortLogLine>().SingleAsync(x => x.Id == line)).StopTimeUtc);
        Assert.False(await db.UserRoles.AnyAsync(x => x.UserId == actor && x.RoleId == AppRoles.ReadOnlyId));
    }

    [Fact]
    public async Task ReportOnlyUsesOneSnapshotWithoutScheduleOrLogEditorAuthority()
    {
        await Actor(Permissions.ProductionReports.Read); var before = evaluator.SnapshotCount;
        var report = await reports.GetAsync(clock.Today.AddDays(-2)); Assert.Equal(before + 1, evaluator.SnapshotCount);
        Assert.Equal(clock.Today.AddDays(-2), report.ProductionDate);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => production.GetReadOnlyAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.GetDashboardAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.SaveCommentsAsync(logId, "Forbidden"));
    }

    [Fact]
    public async Task DashboardScheduleAndReportQueriesRemainBoundedAsMachinesAndJobsGrow()
    {
        var counter = new QueryCounter(); var factory = new CountingFactory(database.ConnectionString, counter);
        var schedule = new ProductionService(factory, session.Guard, clock);
        var logs = new ProductionTrackingService(factory, session.Guard, clock);
        var review = new MorningProductionReviewService(factory, session.Guard, clock, schedule);
        async Task<int[]> Counts()
        {
            counter.Count = 0; await schedule.GetReadOnlyAsync(); var scheduleCount = counter.Count;
            counter.Count = 0; await logs.GetDashboardAsync(machineId, logId); var logCount = counter.Count;
            counter.Count = 0; await review.GetAsync(clock.Today.AddDays(-1));
            return [scheduleCount, logCount, counter.Count];
        }
        var before = await Counts();
        await using (var db = database.CreateDbContext())
        {
            for (var i = 0; i < 20; i++)
            {
                var machine = new SortingMachine { Name = $"Bounded query machine {i}" };
                db.Add(machine); await db.SaveChangesAsync();
                db.Add(new ProductionJob { PartId = partId, PoNumber = "AUTH-PO", Quantity = 100,
                    Segments = [new() { MachineId = machine.Id, Quantity = 100, Sequence = 1, OriginalTargetPph = 1000, OriginalEfficiencyPercent = 91 }] });
                db.Add(new SortLog { MachineId = machine.Id, PartId = partId, InspectionId = inspectionId,
                    ProductionDate = clock.Today.AddDays(-1), TargetPphSnapshot = 1000, CreatedAtUtc = clock.GetUtcNow(), CreatedByUserId = root });
            }
            await db.SaveChangesAsync();
        }
        var after = await Counts();
        for (var i = 0; i < before.Length; i++) Assert.InRange(after[i], 1, before[i] + 1);
    }

    [Fact]
    public async Task RootStillObeysBusinessRulesAndOptimizationDoesNotReassignMachines()
    {
        var revision = await Revision();
        await Assert.ThrowsAsync<SchedulingException>(() => production.ProgressAsync(revision, runningId, -1, clock.Today, null, false));
        await Assert.ThrowsAsync<SchedulingException>(() => production.SaveEfficiencyAsync(revision, 0));
        await using var db = database.CreateDbContext();
        var before = await db.Set<ProductionSegment>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.MachineId);
        await Actor(Permissions.ProductionSchedules.Optimize); await production.ApplyOptimizationAsync(optimization);
        var after = await db.Set<ProductionSegment>().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.MachineId);
        Assert.Equal(before.OrderBy(x => x.Key), after.OrderBy(x => x.Key));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(async () => await production.MoveAsync(await Revision(), pendingId, 1));
    }

    [Fact]
    public async Task ConcurrentAuthorizedScheduleEditsCommitOnceAndRejectTheStaleRevision()
    {
        var actor = await Actor(Permissions.ProductionJobs.Update);
        var revision = await Revision();
        var results = await Task.WhenAll(new[] { "Concurrent A", "Concurrent B" }.Select(notes =>
            Record.ExceptionAsync(() => production.SaveJobAsync(revision, jobId, partId, machineId, 1000, "AUTH-PO", null, notes))));
        Assert.Single(results, error => error is null);
        Assert.IsType<SchedulingException>(Assert.Single(results, error => error is not null));
        await using var db = database.CreateDbContext();
        Assert.Equal(revision + 1, await Revision());
        Assert.Contains((await db.Set<ProductionJob>().SingleAsync(x => x.Id == jobId)).Notes, new[] { "Concurrent A", "Concurrent B" });
        Assert.Equal(actor, Assert.Single(await db.Set<ProductionAudit>().ToListAsync()).RecordedBy);
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Count++; return ValueTask.FromResult(result); }
    }
    private sealed class CountingFactory(string connection, QueryCounter counter) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).AddInterceptors(counter).Options);
    }

    private sealed class FixtureClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
        public DateOnly Today => DateOnly.FromDateTime(GetLocalNow().DateTime);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan interval) => now += interval;
    }
}
