using Confast.Web.Features.Customers;
using Confast.Web.Features.ContainerTracking;
using Confast.Web.Features.Gages;
using Confast.Web.Features.Identity;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.Parts;
using Confast.Web.Features.ProductionScheduling;
using Confast.Web.Features.Suppliers;
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
        var revision = new InspectionCriteriaRevision { PartId = partId, RevisionNumber = 1, CreatedAtUtc = clock.GetUtcNow(), PublishedAtUtc = clock.GetUtcNow() };
        var gageType = new GageType { Name = "Scheduling gage" };
        db.AddRange(revision, gageType);
        await db.SaveChangesAsync();
        var gage = new Gage { GageTypeId = gageType.Id, GageNumber = "SCHED-1" };
        var criterion = new InspectionCriterion { InspectionCriteriaRevisionId = revision.Id, Name = "Scheduling criterion", InspectionNumber = 1, GageTypeId = gageType.Id, DisplayOrder = 1 };
        db.AddRange(gage, criterion);
        await db.SaveChangesAsync();
        var plateRequirement = new SecondaryProcessRequirement
        {
            InspectionCriteriaRevisionId = revision.Id,
            SecondaryProcessTypeId = await db.Set<SecondaryProcessType>().Where(x => x.Name == "Plate").Select(x => x.Id).SingleAsync()
        };
        var sortRequirement = new SecondaryProcessRequirement
        {
            InspectionCriteriaRevisionId = revision.Id,
            SecondaryProcessTypeId = await db.Set<SecondaryProcessType>().Where(x => x.Name == "Sort").Select(x => x.Id).SingleAsync()
        };
        db.AddRange(plateRequirement, sortRequirement);
        await db.SaveChangesAsync();
        var inspection = new Inspection
        {
            PartId = partId,
            InspectionCriteriaRevisionId = revision.Id,
            LotNumber = "SCHEDULE-PO-1",
            ConformancePoNumber = "PO-1",
            InspectionDate = clock.Today,
            CreatedAtUtc = clock.GetUtcNow()
        };
        db.Add(inspection);
        await db.SaveChangesAsync();
        db.InspectionResults.Add(new InspectionResult
        {
            InspectionId = inspection.Id,
            InspectionCriteriaRevisionId = revision.Id,
            InspectionCriterionId = criterion.Id,
            GageId = gage.Id,
            GageNumber = gage.GageNumber,
            ActualMin = "Pass",
            ActualMax = "Pass"
        });
        db.InspectionSecondaryProcesses.AddRange(
            new InspectionSecondaryProcess
            {
                InspectionId = inspection.Id,
                InspectionCriteriaRevisionId = revision.Id,
                SecondaryProcessRequirementId = plateRequirement.Id,
                ProcessName = "Plate",
                IsComplete = true
            },
            new InspectionSecondaryProcess
            {
                InspectionId = inspection.Id,
                InspectionCriteriaRevisionId = revision.Id,
                SecondaryProcessRequirementId = sortRequirement.Id,
                ProcessName = "Sort",
                IsComplete = false
            });
        await db.SaveChangesAsync();
        service = new(database, new TestUser(), clock);
        await service.SaveMachineAsync(await Revision(), 0, "Sorter", true, false, [0, 8, 8, 8, 8, 8, 0]);
        machineId = (await service.GetAsync()).Machines.Single().Id;
        await service.SaveRateAsync(await Revision(), machineId, partId, 10000);
        await service.SetPreferredMachineAsync(await Revision(), machineId, partId);
    }
    public Task DisposeAsync() => Task.CompletedTask;
    private async Task<long> Revision() => (await service.GetAsync()).Settings.Revision;
    private async Task<ProductionSegment> AddJob(decimal quantity = 100000)
    {
        await service.SaveJobAsync(await Revision(), 0, partId, machineId, quantity, "PO-1", "MO-1", null);
        return (await service.GetAsync()).Segments.MaxBy(x => x.Id)!;
    }

    [Fact]
    public async Task DepartedContainerPartsAreAppendedOnceAndRespectEstimatedArrival()
    {
        long lineId, missingEligibilityLineId, missingEligibilityPartId;
        var arrival = clock.Today.AddDays(4);
        await using (var db = database.CreateDbContext())
        {
            var supplier = new Supplier { Name = "Container scheduling supplier" };
            var shipment = new Shipment();
            var container = new Container
            {
                Shipment = shipment,
                ContainerNumber = "SCHEDULE-CONTAINER",
                EstimatedDepartureDate = clock.Today.AddDays(-1),
                EstimatedArrivalDate = arrival
            };
            var bill = new BillOfLading { Supplier = supplier, Number = "SCHEDULE-BOL" };
            var group = new ContainerGroup { Container = container, BillOfLading = bill };
            var line = new ContainerGroupPart
            {
                ContainerGroup = group,
                PartId = partId,
                PurchaseOrderNumber = "PO-1",
                Quantity = 250
            };
            var missingEligibilityPart = new Part
            {
                CustomerId = await db.Parts.Where(x => x.Id == partId).Select(x => x.CustomerId).SingleAsync(),
                PartNumber = "SCHEDULE-NO-ELIGIBILITY",
                BoxQuantity = 1000
            };
            var missingEligibilityLine = new ContainerGroupPart
            {
                ContainerGroup = group,
                Part = missingEligibilityPart,
                PurchaseOrderNumber = "PO-MISSING",
                Quantity = 100
            };
            db.AddRange(line, missingEligibilityLine);
            await db.SaveChangesAsync();
            lineId = line.Id;
            missingEligibilityLineId = missingEligibilityLine.Id;
            missingEligibilityPartId = missingEligibilityPart.Id;
        }

        var first = await service.GetAsync();
        var job = Assert.Single(first.Jobs);
        var segment = Assert.Single(job.Segments);
        var forecast = Assert.Single(ProductionScheduler.Forecast(first));
        Assert.Equal(lineId, job.ContainerGroupPartId);
        Assert.Equal("PO-1", job.PoNumber);
        Assert.Equal(250, job.Quantity);
        Assert.Null(segment.NotBefore);
        Assert.True(forecast.Start >= arrival);
        Assert.Contains(job.Id, first.MaterialEnRouteJobIds);
        var tooEarly = await Assert.ThrowsAsync<SchedulingException>(() =>
            service.SetConstraintAsync(first.Settings.Revision, segment.Id, arrival.AddDays(-1), false));
        Assert.Contains("not available until", tooEarly.Message);
        await Assert.ThrowsAsync<SchedulingException>(() =>
            service.SetConstraintAsync(first.Settings.Revision, segment.Id, null, false));

        var second = await service.GetAsync();
        Assert.Single(second.Jobs);
        await using var verify = database.CreateDbContext();
        var persistedContainer = await verify.Containers.SingleAsync(x => x.ContainerNumber == "SCHEDULE-CONTAINER");
        Assert.False(persistedContainer.AddedToProductionSchedule);
        await service.SaveRateAsync(await Revision(), machineId, missingEligibilityPartId, 10000);
        await service.AppendContainerPartAsync(missingEligibilityLineId);
        Assert.Equal(new[] { lineId, missingEligibilityLineId }, (await service.GetAsync()).Jobs
            .Select(x => x.ContainerGroupPartId!.Value).Order());
        await verify.Entry(persistedContainer).ReloadAsync();
        var correctedArrival = arrival.AddDays(3);
        persistedContainer.EstimatedArrivalDate = correctedArrival;
        await verify.SaveChangesAsync();
        Assert.True(ProductionScheduler.Forecast(await service.GetAsync())
            .Single(x => x.SegmentId == segment.Id).Start >= correctedArrival);

        persistedContainer.ReceivedDate = clock.Today;
        await verify.SaveChangesAsync();
        var received = await service.GetAsync();
        Assert.DoesNotContain(job.Id, received.MaterialEnRouteJobIds);
        Assert.Equal(clock.Today, received.ContainerArrivalDatesByJobId[job.Id]);
        Assert.True(ProductionScheduler.Forecast(received).Single(x => x.SegmentId == segment.Id).Start < correctedArrival);
        await service.SetConstraintAsync(received.Settings.Revision, segment.Id, clock.Today, false);
        await service.StartAsync(await Revision(), segment.Id);
        Assert.Equal(ProductionState.Running, (await service.GetAsync()).Segments.Single(x => x.Id == segment.Id).State);
    }

    [Fact]
    public async Task DepartedContainerPartsUseThePartPreferredMachine()
    {
        await service.SaveMachineAsync(await Revision(), 0, "Preferred sorter", true, false, [0, 8, 8, 8, 8, 8, 0]);
        var preferredMachineId = (await service.GetAsync()).Machines.Single(x => x.Name == "Preferred sorter").Id;
        await service.SaveRateAsync(await Revision(), preferredMachineId, partId, 10000);
        await service.SetPreferredMachineAsync(await Revision(), preferredMachineId, partId);

        await using (var db = database.CreateDbContext())
        {
            var supplier = new Supplier { Name = "Preferred machine supplier" };
            var shipment = new Shipment();
            var container = new Container
            {
                Shipment = shipment,
                ContainerNumber = "PREFERRED-MACHINE-CONTAINER",
                EstimatedDepartureDate = clock.Today.AddDays(-1),
                EstimatedArrivalDate = clock.Today.AddDays(1)
            };
            var bill = new BillOfLading { Supplier = supplier, Number = "PREFERRED-MACHINE-BOL" };
            db.Add(new ContainerGroupPart
            {
                ContainerGroup = new ContainerGroup { Container = container, BillOfLading = bill },
                PartId = partId,
                PurchaseOrderNumber = "PO-1",
                Quantity = 250
            });
            await db.SaveChangesAsync();
        }

        var scheduled = await service.GetAsync();
        Assert.Equal(preferredMachineId, Assert.Single(Assert.Single(scheduled.Jobs).Segments).MachineId);
    }

    [Fact]
    public async Task ChangingPreferredMachineDoesNotViolateTheSinglePreferenceConstraint()
    {
        await service.SaveMachineAsync(await Revision(), 0, "Before sorter", true, false, [0, 8, 8, 8, 8, 8, 0]);
        var beforeMachineId = (await service.GetAsync()).Machines.Single(x => x.Name == "Before sorter").Id;
        await service.SaveRateAsync(await Revision(), beforeMachineId, partId, 10000);

        await service.SetPreferredMachineAsync(await Revision(), beforeMachineId, partId);

        var rates = (await service.GetAsync()).Machines.SelectMany(x => x.Parts).Where(x => x.PartId == partId).ToList();
        Assert.Equal(beforeMachineId, rates.Single(x => x.IsPreferred).MachineId);
    }

    [Fact]
    public async Task FirstEligibleMachineIsAutomaticallyPreferred()
    {
        await service.SaveMachineAsync(await Revision(), 0, "First preference sorter", true, false, [0, 8, 8, 8, 8, 8, 0]);
        var firstMachineId = (await service.GetAsync()).Machines.Single(x => x.Name == "First preference sorter").Id;
        var part = new Part { CustomerId = (await database.CreateDbContext().Parts.Select(x => x.CustomerId).SingleAsync()), PartNumber = "SORT-2", BoxQuantity = 1000 };
        await using (var db = database.CreateDbContext())
        {
            db.Add(part);
            await db.SaveChangesAsync();
        }

        await service.SaveRateAsync(await Revision(), firstMachineId, part.Id, 10000);

        var rate = Assert.Single((await service.GetAsync()).Machines.Single(x => x.Id == firstMachineId).Parts, x => x.PartId == part.Id);
        Assert.True(rate.IsPreferred);
    }

    [Fact]
    public async Task RemovingPreferredMachinePromotesThePreviousEligibleMachine()
    {
        await service.SaveMachineAsync(await Revision(), 0, "Before sorter", true, false, [0, 8, 8, 8, 8, 8, 0]);
        var beforeMachineId = (await service.GetAsync()).Machines.Single(x => x.Name == "Before sorter").Id;
        await service.SaveRateAsync(await Revision(), beforeMachineId, partId, 10000);
        await service.SetPreferredMachineAsync(await Revision(), machineId, partId);

        await service.RemoveRateAsync(await Revision(), machineId, partId);

        var remaining = (await service.GetAsync()).Machines.Single(x => x.Id == beforeMachineId).Parts.Single(x => x.PartId == partId);
        Assert.True(remaining.IsPreferred);
    }

    [Fact]
    public async Task RemovingTheOnlyEligiblePreferredMachineIsAllowed()
    {
        await service.RemoveRateAsync(await Revision(), machineId, partId);

        Assert.DoesNotContain((await service.GetAsync()).Machines.SelectMany(x => x.Parts), x => x.PartId == partId);
    }

    [Fact]
    public async Task StartingRequiresAnAcceptedInspectionWithCompletedNonSortProcesses()
    {
        var unmatched = await AddJob();
        await using (var db = database.CreateDbContext())
        {
            var job = await db.Set<ProductionJob>().SingleAsync(x => x.Id == unmatched.JobId);
            job.PoNumber = "PO-OTHER";
            await db.SaveChangesAsync();
        }
        var unmatchedReadiness = Assert.Single((await service.GetAsync()).StartReadiness);
        Assert.Equal(ProductionStartBlocker.NoMatchingInspection, unmatchedReadiness.Blocker);
        Assert.Equal("Awaiting Inspection Creation", unmatchedReadiness.Message);
        var unmatchedException = await Assert.ThrowsAsync<SchedulingException>(async () => await service.StartAsync(await Revision(), unmatched.Id));
        Assert.Equal(unmatchedReadiness.Message, unmatchedException.Message);

        await using (var db = database.CreateDbContext())
        {
            var job = await db.Set<ProductionJob>().SingleAsync(x => x.Id == unmatched.JobId);
            job.PoNumber = "PO-1";
            var result = await db.InspectionResults.SingleAsync();
            result.ActualMin = result.ActualMax = "Fail";
            await db.SaveChangesAsync();
        }
        var unacceptedInspectionReadiness = Assert.Single((await service.GetAsync()).StartReadiness);
        Assert.Equal(ProductionStartBlocker.InspectionNotAccepted, unacceptedInspectionReadiness.Blocker);
        Assert.Equal("Awaiting Inspection Acceptance", unacceptedInspectionReadiness.Message);

        await using (var db = database.CreateDbContext())
        {
            var result = await db.InspectionResults.SingleAsync();
            result.ActualMin = result.ActualMax = "Pass";
            await db.SaveChangesAsync();
        }
        await using (var db = database.CreateDbContext())
        {
            (await db.InspectionSecondaryProcesses.SingleAsync(x => x.ProcessName == "Plate")).IsComplete = false;
            await db.SaveChangesAsync();
        }
        var incompleteProcessReadiness = Assert.Single((await service.GetAsync()).StartReadiness);
        Assert.Equal(ProductionStartBlocker.SecondaryProcessesIncomplete, incompleteProcessReadiness.Blocker);
        Assert.Equal("Awaiting Plate", incompleteProcessReadiness.Message);

        await using (var db = database.CreateDbContext())
        {
            (await db.InspectionSecondaryProcesses.SingleAsync(x => x.ProcessName == "Plate")).IsComplete = true;
            await db.SaveChangesAsync();
        }
        var readiness = Assert.Single((await service.GetAsync()).StartReadiness);
        Assert.True(readiness.IsReady);
        await service.StartAsync(await Revision(), unmatched.Id);
    }

    [Fact]
    public async Task ProgressAddsOutputAndCorrectionsKeepOriginalHistory()
    {
        var s = await AddJob();
        await service.StartAsync(await Revision(), s.Id);
        await service.ProgressAsync(await Revision(), s.Id, 22000, clock.Today, "first", false);
        await service.ProgressAsync(await Revision(), s.Id, 3000, clock.Today, "additional", false);
        await service.CorrectProgressAsync(await Revision(), s.Id, 20000, clock.Today, "correction");
        var data = await service.GetAsync(); var before = data.Segments.Single();
        Assert.Equal(20000, before.CompletedQuantity); Assert.Equal(3, before.Progress.Count);
        Assert.Equal(new[] { 0m, 22000m, 25000m }, before.Progress.Select(x => x.PreviousQuantity));
        Assert.Equal(new[] { 22000m, 25000m, 20000m }, before.Progress.Select(x => x.CompletedQuantity));
        await service.SaveEfficiencyAsync(data.Settings.Revision, 50);
        data = await service.GetAsync(); var after = data.Segments.Single();
        Assert.Equal(before.OriginalHours, after.OriginalHours); Assert.Equal(before.Progress.Select(x => x.CompletedQuantity), after.Progress.Select(x => x.CompletedQuantity));
        Assert.Equal(16, ProductionScheduler.Forecast(data).Single().RemainingHours);
        await Assert.ThrowsAsync<SchedulingException>(() => service.ProgressAsync(data.Settings.Revision, s.Id, 100001, clock.Today, null, false));
        await Assert.ThrowsAsync<SchedulingException>(() => service.ProgressAsync(data.Settings.Revision, s.Id, 20000, clock.Today, null, true));
        await Assert.ThrowsAsync<SchedulingException>(() => service.CorrectProgressAsync(data.Settings.Revision, s.Id, 100001, clock.Today, null));
    }

    [Fact]
    public async Task MachineRatesDoNotChangeThePartBoxQuantity()
    {
        await service.SaveRateAsync(await Revision(), machineId, partId, 9000);

        await using var db = database.CreateDbContext();
        Assert.Equal(1000, await db.Parts.Where(part => part.Id == partId).Select(part => part.BoxQuantity).SingleAsync());
    }

    [Fact]
    public async Task JobsCanBeDeletedRegardlessOfState()
    {
        var untouched = await AddJob();
        await service.SaveRequirementAsync(await Revision(), untouched.JobId, 0, clock.Today.AddDays(1), 5000);
        await service.DeleteJobAsync(await Revision(), untouched.JobId);
        Assert.Empty((await service.GetAsync()).Jobs);

        var started = await AddJob();
        await service.StartAsync(await Revision(), started.Id);
        await service.ProgressAsync(await Revision(), started.Id, 22000, clock.Today, null, false);
        await service.DeleteJobAsync(await Revision(), started.JobId);
        Assert.Empty((await service.GetAsync()).Jobs);

        var completed = await AddJob();
        await service.StartAsync(await Revision(), completed.Id);
        await service.ProgressAsync(await Revision(), completed.Id, completed.Quantity, clock.Today, null, true);
        await service.DeleteJobAsync(await Revision(), completed.JobId);
        Assert.Empty((await service.GetAsync()).Jobs);
    }

    [Fact]
    public async Task CompletingWithADifferentQuantityExplicitlyRebalancesTheNextSegmentOrJobTotal()
    {
        var current = await AddJob(); var cutIn = await AddJob(10000);
        await service.StartAsync(await Revision(), current.Id);
        await service.ProgressAsync(await Revision(), current.Id, 22000, clock.Today, null, false);
        await service.CutInAsync(await Revision(), current.Id, cutIn.Id, 30000);
        var next = (await service.GetAsync()).Jobs.Single(x => x.Id == current.JobId).Segments.Single(x => x.Id != current.Id);

        await service.CompleteSegmentAsync(await Revision(), current.Id, 25000, clock.Today, "short", CompletionQuantityAdjustment.RebalanceNextSegment);
        var data = await service.GetAsync();
        Assert.Equal(25000, data.Segments.Single(x => x.Id == current.Id).Quantity);
        Assert.Equal(25000, data.Segments.Single(x => x.Id == current.Id).CompletedQuantity);
        Assert.Equal(75000, data.Segments.Single(x => x.Id == next.Id).Quantity);
        Assert.Equal(100000, data.Jobs.Single(x => x.Id == current.JobId).Quantity);

        await service.DeleteJobAsync(await Revision(), current.JobId);
        await service.DeleteJobAsync(await Revision(), cutIn.JobId);

        var overCurrent = await AddJob(); var overCutIn = await AddJob(10000);
        await service.StartAsync(await Revision(), overCurrent.Id);
        await service.CutInAsync(await Revision(), overCurrent.Id, overCutIn.Id, 30000);
        var overNext = (await service.GetAsync()).Jobs.Single(x => x.Id == overCurrent.JobId).Segments.Single(x => x.Id != overCurrent.Id);
        await service.CompleteSegmentAsync(await Revision(), overCurrent.Id, 35000, clock.Today, "over", CompletionQuantityAdjustment.RebalanceNextSegment);
        Assert.Equal(65000, (await service.GetAsync()).Segments.Single(x => x.Id == overNext.Id).Quantity);

        await service.DeleteJobAsync(await Revision(), overCurrent.JobId);
        await service.DeleteJobAsync(await Revision(), overCutIn.JobId);

        var consumedCurrent = await AddJob(); var consumedCutIn = await AddJob(10000);
        await service.StartAsync(await Revision(), consumedCurrent.Id);
        await service.CutInAsync(await Revision(), consumedCurrent.Id, consumedCutIn.Id, 30000);
        await service.CompleteSegmentAsync(await Revision(), consumedCurrent.Id, 100000, clock.Today, "consumed", CompletionQuantityAdjustment.RebalanceNextSegment);
        Assert.Single((await service.GetAsync()).Jobs.Single(x => x.Id == consumedCurrent.JobId).Segments);

        await service.DeleteJobAsync(await Revision(), consumedCurrent.JobId);
        await service.DeleteJobAsync(await Revision(), consumedCutIn.JobId);
        var onlySegment = await AddJob();
        await service.StartAsync(await Revision(), onlySegment.Id);
        await service.CompleteSegmentAsync(await Revision(), onlySegment.Id, 95000, clock.Today, "over", CompletionQuantityAdjustment.UpdateJobQuantity);
        data = await service.GetAsync();
        var revisedJob = data.Jobs.Single(x => x.Id == onlySegment.JobId);
        Assert.Equal(95000, revisedJob.Quantity);
        Assert.Equal(95000, Assert.Single(revisedJob.Segments).Quantity);

        var invalid = await AddJob(100);
        await service.StartAsync(await Revision(), invalid.Id);
        await Assert.ThrowsAsync<SchedulingException>(async () =>
            await service.CompleteSegmentAsync(await Revision(), invalid.Id, 99, clock.Today, null, CompletionQuantityAdjustment.Exact));
    }

    [Fact]
    public async Task DeletingACutInJobReleasesItsDependentSegment()
    {
        var source = await AddJob();
        var cutIn = await AddJob(10000);
        await service.StartAsync(await Revision(), source.Id);
        await service.ProgressAsync(await Revision(), source.Id, 22000, clock.Today, null, false);
        await service.CutInAsync(await Revision(), source.Id, cutIn.Id, 30000);

        await service.DeleteJobAsync(await Revision(), cutIn.JobId);

        var remainingJob = Assert.Single((await service.GetAsync()).Jobs);
        Assert.DoesNotContain(remainingJob.Segments, segment => segment.PredecessorId == cutIn.Id);
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
        await Assert.ThrowsAsync<SchedulingException>(async () => await service.CutInAsync(await Revision(), a.Id, b.Id, 30000.5m));
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
        await service.ProgressAsync(await Revision(), a.Id, 8000, clock.Today, null, true);
        Assert.Equal(30000, (await service.GetAsync()).Jobs.Single(x => x.Id == a.JobId).Segments.Sum(x => x.CompletedQuantity));
    }

    [Fact]
    public async Task RequirementsUseStableCumulativeTargetsAndAdditionalQuantities()
    {
        var a = await AddJob();
        await service.SaveRequirementAsync(await Revision(), a.JobId, 0, clock.Today, 15000);
        await service.SaveRequirementAsync(await Revision(), a.JobId, 0, clock.Today.AddDays(1), 25000);
        var data = await service.GetAsync(); Assert.Equal(new[] { 15000m, 40000m }, data.Requirements.OrderBy(x => x.Date).Select(x => x.CumulativeTarget));
        await service.StartAsync(data.Settings.Revision, a.Id);
        await service.ProgressAsync(await Revision(), a.Id, 22000, clock.Today, null, false);
        await service.SaveRequirementAsync(await Revision(), a.JobId, 0, clock.Today.AddDays(2), null);
        await service.ProgressAsync(await Revision(), a.Id, 8000, clock.Today, null, false);
        data = await service.GetAsync(); Assert.Equal(new[] { 15000m, 40000m, 100000m }, data.Requirements.OrderBy(x => x.Date).Select(x => x.CumulativeTarget));
    }

    [Fact]
    public async Task RequirementsAreSharedByAllScheduledJobsForTheSamePart()
    {
        var first = await AddJob();
        _ = await AddJob();

        await service.SaveRequirementAsync(await Revision(), first.JobId, 0, clock.Today, 150000);

        var data = await service.GetAsync();
        var requirement = Assert.Single(data.Requirements);
        Assert.Equal(partId, requirement.PartId);
        var forecast = Assert.Single(ProductionScheduler.Requirements(data, ProductionScheduler.Forecast(data)));
        Assert.False(forecast.Met);
        Assert.Equal("Projected miss", forecast.Message);
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

