using System.Data;
using Confast.Web.Data;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.ContainerTracking;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Features.ProductionScheduling;

public sealed class ProductionService(IDbContextFactory<AppDbContext> factory, ICurrentUser currentUser, TimeProvider clock)
{
    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private async Task<(string User, bool Edit, bool Admin)> Access(AppDbContext db)
    {
        var id = await currentUser.GetUserIdAsync();
        if (id == null || !await db.Users.AnyAsync(x => x.Id == id && x.IsActive))
            throw new UnauthorizedAccessException("Sign in with an active account to use Production Scheduling.");
        var roles = await (from ur in db.UserRoles join r in db.Roles on ur.RoleId equals r.Id where ur.UserId == id select r.Name).ToListAsync();
        var admin = roles.Contains(AppRoles.Administrator);
        return (id, admin || roles.Contains(AppRoles.Production), admin);
    }

    public async Task<ProductionSnapshot> GetAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var access = await Access(db);
        // Passing an ETD is a time-based domain event, not a checkbox a planner has
        // to remember. Catch it up when the schedule is opened; the source-line key
        // makes repeated reads harmless.
        var settings = await db.Set<ProductionSettings>().FromSqlRaw("SELECT *, xmin FROM production_settings WHERE id = 1 FOR UPDATE").SingleAsync();
        var result = await Load(db, access.Edit, access.Admin);
        if (await AppendDepartedContainerPartsAsync(db, result))
        {
            settings.Revision++;
            db.Set<ProductionAudit>().Add(new()
            {
                Revision = settings.Revision,
                RecordedAt = clock.GetUtcNow().UtcDateTime,
                RecordedBy = "System",
                Description = "Appended departed container parts to the production schedule"
            });
            await db.SaveChangesAsync();
            result = await Load(db, access.Edit, access.Admin);
        }
        await transaction.CommitAsync();
        return result;
    }

    private async Task<bool> AppendDepartedContainerPartsAsync(AppDbContext db, ProductionSnapshot data, long? containerGroupPartId = null)
    {
        var containers = await db.Containers
            .Where(x => x.EstimatedDepartureDate < Today && x.EstimatedArrivalDate != null &&
                (containerGroupPartId == null || x.Groups.Any(group => group.Parts.Any(part => part.Id == containerGroupPartId))) )
            .Include(x => x.Groups).ThenInclude(x => x.Parts)
            .OrderBy(x => x.EstimatedDepartureDate).ThenBy(x => x.Id)
            .ToListAsync();
        var changed = false;

        foreach (var container in containers)
        {
            var lines = container.Groups.SelectMany(x => x.Parts).Where(x => x.Quantity > 0).OrderBy(x => x.Id).ToList();
            foreach (var line in lines)
            {
                if (data.Jobs.Any(job => job.ContainerGroupPartId == line.Id)) continue;
                if (!data.Parts.Any(part => part.Id == line.PartId && part.IsActive)
                    || !data.Machines.Any(machine => machine.IsActive && machine.Parts.Any(rate => rate.PartId == line.PartId && rate.IsPreferred)))
                    continue;
                var machine = data.Machines.Single(x => x.IsActive
                    && x.Parts.Any(rate => rate.PartId == line.PartId && rate.IsPreferred));
                var job = new ProductionJob
                {
                    PartId = line.PartId,
                    Part = data.Parts.Single(x => x.Id == line.PartId),
                    PoNumber = line.PurchaseOrderNumber,
                    Quantity = line.Quantity,
                    Notes = $"Container {container.ContainerNumber}",
                    ContainerGroupPartId = line.Id
                };
                // ETA is a hard earliest-start constraint, even if the container is
                // appended before the ship physically arrives.
                var segment = NewSegment(data, job, machine.Id, job.Quantity);
                segment.NotBefore = container.EstimatedArrivalDate;
                job.Segments.Add(segment);
                data.Jobs.Add(job);
                db.Add(job);
                changed = true;
            }

            var allLinesScheduled = lines.All(line => data.Jobs.Any(job => job.ContainerGroupPartId == line.Id));
            if (container.AddedToProductionSchedule != allLinesScheduled)
            {
                container.AddedToProductionSchedule = allLinesScheduled;
                changed = true;
            }
        }

        return changed;
    }

    public async Task AppendContainerPartAsync(long containerGroupPartId)
    {
        var snapshot = await GetAsync();
        await Write(snapshot.Settings.Revision, false, $"Append container part {containerGroupPartId} to the production schedule", async (db, data, user) =>
        {
            await AppendDepartedContainerPartsAsync(db, data, containerGroupPartId);
            if (!data.Jobs.Any(job => job.ContainerGroupPartId == containerGroupPartId))
                throw new SchedulingException("This part still has no active preferred machine eligibility. Update machine eligibility, then try again.");
        });
    }

    private async Task<ProductionSnapshot> Load(AppDbContext db, bool edit, bool admin)
    {
        var jobs = await db.Set<ProductionJob>().Include(x => x.Part)
            .Include(x => x.Segments).ThenInclude(x => x.Progress).AsSplitQuery().ToListAsync();

        var containerJobs = await (
            from job in db.Set<ProductionJob>()
            join line in db.ContainerGroupParts on job.ContainerGroupPartId equals line.Id
            join containerGroup in db.ContainerGroups on line.ContainerGroupId equals containerGroup.Id
            join container in db.Containers on containerGroup.ContainerId equals container.Id
            select new { job.Id, container.ReceivedDate, container.EstimatedArrivalDate }).ToListAsync();

        return new(
            await db.Set<ProductionSettings>().Include(x => x.DefaultWorkingDays).SingleAsync(),
            await db.Set<SortingMachine>().Include(x => x.WorkingDays).Include(x => x.Parts).OrderBy(x => x.Name).AsSplitQuery().ToListAsync(),
            await db.Set<ProductionHoliday>().OrderBy(x => x.Date).ToListAsync(),
            await db.Set<DowntimeReason>().OrderBy(x => x.Name).ToListAsync(),
            await db.Set<MachineDowntime>().Include(x => x.Reason).ToListAsync(),
            jobs,
            await db.Set<ProductionRequirement>().OrderBy(x => x.Date).ToListAsync(),
            await db.Parts.OrderBy(x => x.PartNumber).ToListAsync(),
            await LoadStartReadinessAsync(db, jobs), Today, edit, admin)
        {
            MaterialEnRouteJobIds = containerJobs.Where(x => x.ReceivedDate == null).Select(x => x.Id).ToHashSet(),
            ContainerArrivalDatesByJobId = containerJobs.Where(x => x.EstimatedArrivalDate != null)
                .ToDictionary(x => x.Id, x => x.EstimatedArrivalDate!.Value)
        };
    }

    private static async Task<List<ProductionStartReadiness>> LoadStartReadinessAsync(
        AppDbContext db,
        List<ProductionJob> jobs)
    {
        var partNumbers = jobs.Select(x => x.Part.PartNumber).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (partNumbers.Length == 0) return [];

        var inspections = await db.Inspections.AsNoTracking()
            .Where(x => partNumbers.Contains(x.Part.PartNumber))
            .Select(x => new ProductionInspection(x.Id, x.Part.PartNumber, x.ConformancePoNumber))
            .ToListAsync();
        var inspectionIds = inspections.Select(x => x.Id).ToArray();
        var nominalToleranceSettings = await db.NominalToleranceSettings.AsNoTracking().SingleOrDefaultAsync()
            ?? new NominalToleranceSettings();
        var results = inspectionIds.Length == 0 ? [] : await db.InspectionResults.AsNoTracking()
            .Where(x => inspectionIds.Contains(x.InspectionId))
            .Select(x => new ProductionInspectionResult(
                x.InspectionId,
                x.GageId,
                x.InspectionCriterion.SecondaryProcessRequirementId,
                x.InspectionCriterion.Minimum,
                x.InspectionCriterion.MaximumOrTolerance,
                x.ActualMin,
                x.ActualMax,
                x.DeviationApproved))
            .ToListAsync();
        var processes = inspectionIds.Length == 0 ? [] : await db.InspectionSecondaryProcesses.AsNoTracking()
            .Where(x => inspectionIds.Contains(x.InspectionId))
            .Select(x => new ProductionInspectionProcess(
                x.InspectionId,
                x.SecondaryProcessRequirementId,
                x.ProcessName,
                x.IsComplete))
            .ToListAsync();

        var processesByInspection = processes.GroupBy(x => x.InspectionId)
            .ToDictionary(x => x.Key, x => x.ToList());
        var acceptedInspectionIds = results.GroupBy(x => x.InspectionId)
            .Where(group => IsAccepted(group, processesByInspection.GetValueOrDefault(group.Key, []), nominalToleranceSettings))
            .Select(x => x.Key)
            .ToHashSet();

        return jobs.Select(job => StartReadiness(job, inspections, processesByInspection, acceptedInspectionIds)).ToList();
    }

    private static ProductionStartReadiness StartReadiness(
        ProductionJob job,
        IEnumerable<ProductionInspection> inspections,
        IReadOnlyDictionary<long, List<ProductionInspectionProcess>> processesByInspection,
        IReadOnlySet<long> acceptedInspectionIds)
    {
        if (string.IsNullOrWhiteSpace(job.PoNumber))
            return new(job.Id, ProductionStartBlocker.MissingPoNumber);

        var matches = inspections.Where(x => string.Equals(x.PartNumber, job.Part.PartNumber, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.ConformancePoNumber, job.PoNumber, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return new(job.Id, ProductionStartBlocker.NoMatchingInspection);

        var acceptedMatches = matches.Where(x => acceptedInspectionIds.Contains(x.Id)).ToList();
        if (acceptedMatches.Count == 0) return new(job.Id, ProductionStartBlocker.InspectionNotAccepted);

        var nonSortProcesses = acceptedMatches
            .Select(x => processesByInspection.GetValueOrDefault(x.Id, []))
            .ToList();
        if (nonSortProcesses.Any(processes => processes
            .Where(process => !string.Equals(process.ProcessName.Trim(), "Sort", StringComparison.OrdinalIgnoreCase))
            .All(process => process.IsComplete)))
        {
            return new(job.Id, ProductionStartBlocker.None);
        }

        var awaitingProcess = nonSortProcesses.SelectMany(x => x)
            .Where(process => !process.IsComplete
                && !string.Equals(process.ProcessName.Trim(), "Sort", StringComparison.OrdinalIgnoreCase))
            .OrderBy(process => process.RequirementId)
            .First();
        return new(job.Id, ProductionStartBlocker.SecondaryProcessesIncomplete, awaitingProcess.ProcessName);
    }

    private static bool IsAccepted(
        IEnumerable<ProductionInspectionResult> results,
        IEnumerable<ProductionInspectionProcess> processes,
        NominalToleranceSettings nominalToleranceSettings)
    {
        var processCompletion = processes.ToDictionary(x => x.RequirementId, x => x.IsComplete);
        return results.Any() && results.All(result =>
            result.SecondaryProcessRequirementId is long requirementId
                && processCompletion.TryGetValue(requirementId, out var isComplete)
                && !isComplete
            || result.GageId is not null
                && InspectionResultEvaluator.Evaluate(
                    result.Minimum,
                    result.MaximumOrTolerance,
                    result.ActualMin,
                    result.ActualMax,
                    result.DeviationApproved,
                    nominalToleranceSettings.ToleranceFloor,
                    nominalToleranceSettings.LargeDimensionDivisor) == InspectionResultEvaluation.Pass);
    }

    private sealed record ProductionInspectionResult(long InspectionId, long? GageId,
        long? SecondaryProcessRequirementId, string? Minimum, string? MaximumOrTolerance,
        string? ActualMin, string? ActualMax, bool DeviationApproved);

    private sealed record ProductionInspection(long Id, string PartNumber, string? ConformancePoNumber);

    private sealed record ProductionInspectionProcess(long InspectionId, long RequirementId,
        string ProcessName, bool IsComplete);

    private async Task Write(long revision, bool administrator, string description,
        Func<AppDbContext, ProductionSnapshot, string, Task> action)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var access = await Access(db);
        if (!access.Edit || administrator && !access.Admin)
            throw new UnauthorizedAccessException(administrator ? "Administrator access is required for scheduling settings." : "Production or Administrator access is required to plan work.");
        // A single small planning aggregate serializes changes across machines, including global calendars.
        var settings = await db.Set<ProductionSettings>().FromSqlRaw("SELECT *, xmin FROM production_settings WHERE id = 1 FOR UPDATE").SingleAsync();
        if (settings.Revision != revision) throw new SchedulingException("The schedule changed elsewhere. Reload and review before saving.");
        var data = await Load(db, access.Edit, access.Admin);
        await action(db, data, access.User);
        Validate(data);
        settings.Revision++;
        db.Set<ProductionAudit>().Add(new() { Revision = settings.Revision, RecordedAt = clock.GetUtcNow().UtcDateTime, RecordedBy = access.User, Description = description });
        try
        {
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new SchedulingException("The record changed elsewhere. Reload before saving.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { ConstraintName: "UX_downtime_reasons_name" })
        {
            throw new SchedulingException("A downtime reason with that name already exists.");
        }
    }

    private static void Validate(ProductionSnapshot data)
    {
        _ = ProductionScheduler.EffectivePph(1, data.Settings.EfficiencyPercent);
        foreach (var job in data.Jobs)
        {
            Quantity(job.Quantity, "Job quantity");
            if (job.Segments.Sum(x => x.Quantity) != job.Quantity) throw new SchedulingException("Segment quantities must reconcile with the job total.");
            foreach (var s in job.Segments)
            {
                if (s.Quantity < 0 || s.CompletedQuantity < 0 || s.CompletedQuantity > s.Quantity)
                    throw new SchedulingException("Completed quantity must be between zero and segment quantity.");
                if (s.State == ProductionState.Completed && s.CompletedQuantity != s.Quantity)
                    throw new SchedulingException("A completed segment must have its full quantity recorded.");
            }
        }
        foreach (var partRequirements in data.Requirements.GroupBy(x => x.PartId))
        {
            var target = 0m;
            var scheduled = data.Jobs.Where(x => x.PartId == partRequirements.Key).Sum(x => x.Quantity);
            foreach (var requirement in partRequirements.OrderBy(x => x.Date))
            {
                if (requirement.CumulativeTarget <= target || requirement.CumulativeTarget > scheduled)
                    throw new SchedulingException("Requirements must increase cumulatively in date order and cannot exceed the scheduled quantity for the part.");
                target = requirement.CumulativeTarget;
            }
        }
        foreach (var s in data.Segments.Where(x => x.PredecessorId != null && x.State != ProductionState.Completed))
        {
            var before = data.Segments.Single(x => x.Id == s.PredecessorId);
            if (before.MachineId != s.MachineId || before.State != ProductionState.Completed && before.Sequence >= s.Sequence)
                throw new SchedulingException("Keep the cut-in predecessor before its dependent segment on the same machine.");
        }
    }

    private static void Quantity(decimal value, string name)
    {
        if (value <= 0 || value > 1_000_000_000_000m || decimal.Round(value, 3) != value)
            throw new SchedulingException($"{name} must be positive, at most one trillion, and have no more than three decimal places.");
    }

    private static void WholeNumber(decimal value, string name)
    {
        Quantity(value, name);
        if (value != decimal.Truncate(value))
            throw new SchedulingException($"{name} must be a whole number.");
    }

    private static decimal Rate(ProductionSnapshot data, long machineId, long partId)
    {
        var machine = data.Machines.SingleOrDefault(x => x.Id == machineId && x.IsActive)
            ?? throw new SchedulingException("Select an active machine.");
        if (!data.Parts.Any(x => x.Id == partId && x.IsActive)) throw new SchedulingException("Select an active part.");
        var rate = machine.Parts.SingleOrDefault(x => x.PartId == partId)?.TargetPph
            ?? throw new SchedulingException("This part is not eligible for the selected machine. Configure its assignment first.");
        if (!machine.WorkingDays.Any(x => x.Hours > 0)) throw new SchedulingException("Configure working hours before scheduling this machine.");
        _ = ProductionScheduler.EffectivePph(rate, data.Settings.EfficiencyPercent);
        return rate;
    }

    private static ProductionSegment NewSegment(ProductionSnapshot data, ProductionJob job, long machineId, decimal quantity)
    {
        var rate = Rate(data, machineId, job.PartId);
        return new() { JobId = job.Id, MachineId = machineId, Quantity = quantity,
            Sequence = data.Segments.Where(x => x.MachineId == machineId).Select(x => x.Sequence).DefaultIfEmpty(0).Max() + 1,
            OriginalTargetPph = rate, OriginalEfficiencyPercent = data.Settings.EfficiencyPercent,
            OriginalHours = quantity / ProductionScheduler.EffectivePph(rate, data.Settings.EfficiencyPercent) };
    }

    public Task SaveJobAsync(long revision, long id, long partId, long machineId, decimal quantity, string? po, string? mo, string? notes) =>
        Write(revision, false, $"Save job {id}: part {partId}, quantity {quantity}, PO {po}, MO {mo}", (db, data, user) =>
        {
            Quantity(quantity, "Job quantity");
            var job = data.Jobs.SingleOrDefault(x => x.Id == id);
            if (id != 0 && job == null) throw new SchedulingException("Job no longer exists.");
            if (job == null)
            {
                job = new() { PartId = partId, Quantity = quantity };
                job.Segments.Add(NewSegment(data, job, machineId, quantity));
                data.Jobs.Add(job); db.Add(job);
            }
            else if (job.PartId != partId || job.Quantity != quantity)
            {
                if (job.Segments.Count != 1 || job.Segments[0].State != ProductionState.Pending)
                    throw new SchedulingException("Part and total quantity can only change before an unsplit job starts. Notes and references remain editable.");
                job.PartId = partId;
                _ = Rate(data, job.Segments[0].MachineId, partId);
                job.Quantity = quantity;
                job.Segments[0].Quantity = quantity;
            }
            job.PoNumber = Clean(po, 150); job.MoNumber = Clean(mo, 150); job.Notes = Clean(notes, 4000);
            return Task.CompletedTask;
        });

    public Task DeleteJobAsync(long revision, long jobId) =>
        Write(revision, false, $"Delete job {jobId}", async (db, data, user) =>
        {
            var job = data.Jobs.SingleOrDefault(x => x.Id == jobId)
                ?? throw new SchedulingException("Job no longer exists.");
            if (job.ContainerGroupPartId != null)
                throw new SchedulingException("Container-created work remains linked to its container line and cannot be deleted from the production schedule.");
            var segmentIds = job.Segments.Select(x => x.Id).ToHashSet();
            foreach (var dependent in data.Segments.Where(x => x.PredecessorId is { } predecessor && segmentIds.Contains(predecessor)))
                dependent.PredecessorId = null;

            await db.Database.ExecuteSqlRawAsync("SELECT set_config('confast.allow_production_job_deletion', 'on', true)");
            if (!data.Jobs.Any(x => x.Id != job.Id && x.PartId == job.PartId))
                db.RemoveRange(data.Requirements.Where(x => x.PartId == job.PartId));
            db.RemoveRange(job.Segments.SelectMany(x => x.Progress));
            db.RemoveRange(job.Segments);
            db.Remove(job);
        });

    private static string? Clean(string? value, int maximum)
    {
        value = value?.Trim();
        if (value?.Length > maximum) throw new SchedulingException($"Text exceeds {maximum} characters.");
        return string.IsNullOrEmpty(value) ? null : value;
    }

    public Task SaveRequirementAsync(long revision, long jobId, long requirementId, DateOnly date, decimal? additionalQuantity, bool remove = false) =>
        Write(revision, false, $"Requirement for job {jobId}: {date}, additional {additionalQuantity}, remove {remove}", (db, data, user) =>
        {
            var job = data.Jobs.Single(x => x.Id == jobId);
            var partRequirements = data.Requirements.Where(x => x.PartId == job.PartId).ToList();
            var requirement = partRequirements.SingleOrDefault(x => x.Id == requirementId);
            var completed = data.Jobs.Where(x => x.PartId == job.PartId).SelectMany(x => x.Segments).Sum(x => x.CompletedQuantity);
            if (requirement != null && requirement.CumulativeTarget <= completed)
                throw new SchedulingException("Satisfied requirements retain their history. Add the next requirement instead.");
            if (remove)
            {
                if (requirement != null) db.Remove(requirement);
                return Task.CompletedTask;
            }
            if (partRequirements.Any(x => x.Id != requirementId && x.Date == date)) throw new SchedulingException("A requirement already exists for this part on that date.");
            if (additionalQuantity.HasValue) Quantity(additionalQuantity.Value, "Additional pieces needed");
            var earlierTarget = partRequirements.Where(x => x.Id != requirementId && x.Date < date).Select(x => x.CumulativeTarget).DefaultIfEmpty(0).Max();
            var target = additionalQuantity.HasValue ? Math.Max(completed, earlierTarget) + additionalQuantity.Value : data.Jobs.Where(x => x.PartId == job.PartId).Sum(x => x.Quantity);
            requirement ??= new() { PartId = job.PartId };
            if (requirement.Id == 0) { data.Requirements.Add(requirement); db.Add(requirement); }
            requirement.Date = date; requirement.CumulativeTarget = target;
            return Task.CompletedTask;
        });

    public Task StartAsync(long revision, long segmentId) => Write(revision, false, $"Start segment {segmentId}", (db, data, user) =>
    {
        var s = data.Segments.Single(x => x.Id == segmentId);
        if (s.State != ProductionState.Pending) throw new SchedulingException("Only pending work can start.");
        var inspectionReadiness = data.StartReadiness.Single(x => x.JobId == s.JobId);
        if (!inspectionReadiness.IsReady) throw new SchedulingException(inspectionReadiness.Message);
        if (ContainerArrivalDate(data, s) is { } arrival && arrival > Today)
            throw new SchedulingException($"Material is not expected until {arrival:MMM d, yyyy}; production cannot start before the container's Estimated Arrival date.");
        if (s.NotBefore > Today) throw new SchedulingException("Remove or adjust the start constraint before starting early.");
        if (s.PredecessorId is { } predecessor && data.Segments.Single(x => x.Id == predecessor).State != ProductionState.Completed)
            throw new SchedulingException("Complete the cut-in predecessor first.");
        if (data.Segments.Any(x => x.MachineId == s.MachineId && x.State == ProductionState.Running)) throw new SchedulingException("Complete the running segment first.");
        if (data.Segments.Any(x => x.MachineId == s.MachineId && x.State == ProductionState.Pending && x.Sequence < s.Sequence))
            throw new SchedulingException("Move this segment to the front of the pending queue before starting it.");
        _ = Rate(data, s.MachineId, data.Jobs.Single(x => x.Id == s.JobId).PartId);
        if (ProductionScheduler.Capacity(data, data.Machines.Single(x => x.Id == s.MachineId), Today) <= 0)
            throw new SchedulingException("Today has no production capacity. Review the machine calendar, holidays, and downtime.");
        s.State = ProductionState.Running; s.ActualStart = Today;
        return Task.CompletedTask;
    });

    public Task ProgressAsync(long revision, long segmentId, decimal additionalCompleted, DateOnly asOf, string? notes, bool complete) =>
        Write(revision, false, $"Progress segment {segmentId}: add {additionalCompleted} through {asOf}; complete {complete}", (db, data, user) =>
        {
            var s = data.Segments.Single(x => x.Id == segmentId);
            if (s.State != ProductionState.Running) throw new SchedulingException("Only running segments accept progress. Completed history is locked.");
            if (asOf > Today || asOf < s.ActualStart || asOf < s.ProgressAsOf)
                throw new SchedulingException("Checkpoint must be on or after actual start and the previous checkpoint, and no later than today.");
            if (additionalCompleted < 0 || decimal.Round(additionalCompleted, 3) != additionalCompleted || additionalCompleted > s.Quantity - s.CompletedQuantity)
                throw new SchedulingException("Additional completed pieces must fit the remaining segment quantity, with at most three decimals.");
            RecordProgress(data, s, s.CompletedQuantity + additionalCompleted, asOf, notes, complete, user);
            return Task.CompletedTask;
        });

    public Task CompleteSegmentAsync(long revision, long segmentId, decimal finalCompleted, DateOnly asOf, string? notes,
        CompletionQuantityAdjustment adjustment) =>
        Write(revision, false, $"Complete segment {segmentId}: final {finalCompleted}; adjustment {adjustment}", (db, data, user) =>
        {
            var segment = data.Segments.Single(x => x.Id == segmentId);
            if (segment.State != ProductionState.Running) throw new SchedulingException("Only running segments can be completed.");
            if (asOf > Today || asOf < segment.ActualStart || asOf < segment.ProgressAsOf)
                throw new SchedulingException("Completion must be on or after actual start and the previous checkpoint, and no later than today.");
            if (finalCompleted < segment.CompletedQuantity || finalCompleted < 0 || decimal.Round(finalCompleted, 3) != finalCompleted)
                throw new SchedulingException("Final completed pieces cannot be less than the recorded total and must have at most three decimals.");

            var difference = finalCompleted - segment.Quantity;
            var job = data.Jobs.Single(x => x.Id == segment.JobId);
            var nextSegment = job.Segments.SingleOrDefault(x => x.Id != segment.Id && x.State != ProductionState.Completed);
            if (difference == 0)
            {
                if (adjustment != CompletionQuantityAdjustment.Exact)
                    throw new SchedulingException("This segment already matches its scheduled quantity. Reload and try again.");
            }
            else if (nextSegment != null)
            {
                if (adjustment != CompletionQuantityAdjustment.RebalanceNextSegment)
                    throw new SchedulingException("Review the quantity difference before completing this segment.");
                var revisedNextQuantity = nextSegment.Quantity - difference;
                if (revisedNextQuantity < 0)
                    throw new SchedulingException("The quantity difference is larger than the next segment. Adjust the final quantity or revise the schedule first.");
                if (revisedNextQuantity == 0)
                {
                    job.Segments.Remove(nextSegment);
                    db.Remove(nextSegment);
                }
                else nextSegment.Quantity = revisedNextQuantity;
            }
            else
            {
                if (adjustment != CompletionQuantityAdjustment.UpdateJobQuantity)
                    throw new SchedulingException("Review the quantity difference before updating the job total.");
                var revisedJobQuantity = job.Quantity + difference;
                Quantity(revisedJobQuantity, "Job quantity");
                job.Quantity = revisedJobQuantity;
            }

            segment.Quantity = finalCompleted;
            RecordProgress(data, segment, finalCompleted, asOf, notes, true, user);
            return Task.CompletedTask;
        });

    public Task CorrectProgressAsync(long revision, long segmentId, decimal cumulativeCompleted, DateOnly asOf, string? notes) =>
        Write(revision, false, $"Correct progress segment {segmentId}: {cumulativeCompleted} through {asOf}", (db, data, user) =>
        {
            var s = data.Segments.Single(x => x.Id == segmentId);
            if (s.State != ProductionState.Running) throw new SchedulingException("Only running segments accept progress corrections. Completed history is locked.");
            if (asOf > Today || asOf < s.ActualStart || asOf < s.ProgressAsOf)
                throw new SchedulingException("Correction must be on or after actual start and the previous checkpoint, and no later than today.");
            if (cumulativeCompleted < 0 || cumulativeCompleted > s.Quantity || decimal.Round(cumulativeCompleted, 3) != cumulativeCompleted)
                throw new SchedulingException("Corrected segment completion must be between zero and segment quantity, with at most three decimals.");
            RecordProgress(data, s, cumulativeCompleted, asOf, notes, false, user);
            return Task.CompletedTask;
        });

    private void RecordProgress(ProductionSnapshot data, ProductionSegment segment, decimal cumulativeCompleted, DateOnly asOf, string? notes, bool complete, string user)
    {
        if (complete && cumulativeCompleted != segment.Quantity) throw new SchedulingException("Record all remaining pieces before completing the segment.");
        segment.Progress.Add(new() { MachineId = segment.MachineId, AsOf = asOf, PreviousQuantity = segment.CompletedQuantity,
            CompletedQuantity = cumulativeCompleted, RecordedAt = clock.GetUtcNow().UtcDateTime, RecordedBy = user, Notes = Clean(notes, 4000) });
        segment.CompletedQuantity = cumulativeCompleted; segment.ProgressAsOf = asOf;
        if (!complete) return;

        segment.State = ProductionState.Completed; segment.ActualCompletion = asOf;
        segment.ActualElapsedWorkingDays = ProductionScheduler.Elapsed(data, data.Machines.Single(x => x.Id == segment.MachineId), segment);
    }

    public Task SetConstraintAsync(long revision, long segmentId, DateOnly? notBefore, bool pinned) =>
        Write(revision, false, $"Constraint segment {segmentId}: {notBefore}, pinned {pinned}", (db, data, user) =>
        {
            var s = data.Segments.Single(x => x.Id == segmentId);
            if (s.State != ProductionState.Pending) throw new SchedulingException("Only pending work accepts start constraints and optimizer pins.");
            if (ContainerArrivalDate(data, s) is { } arrival && (notBefore == null || notBefore < arrival))
                throw new SchedulingException($"This job's container is not expected until {arrival:MMM d, yyyy}. Its start constraint cannot be earlier than the Estimated Arrival date.");
            s.NotBefore = notBefore; s.IsPinned = pinned;
            return Task.CompletedTask;
        });

    private static DateOnly? ContainerArrivalDate(ProductionSnapshot data, ProductionSegment segment) =>
        data.ContainerArrivalDatesByJobId.GetValueOrDefault(segment.JobId);

    public Task MoveAsync(long revision, long segmentId, int direction) => Write(revision, false, $"Move segment {segmentId}: {direction}", (db, data, user) =>
    {
        var s = data.Segments.Single(x => x.Id == segmentId);
        if (s.State != ProductionState.Pending || direction is not (-1 or 1)) throw new SchedulingException("Only pending work can move earlier or later.");
        var queue = data.Segments.Where(x => x.MachineId == s.MachineId && x.State == ProductionState.Pending).OrderBy(x => x.Sequence).ThenBy(x => x.Id).ToList();
        var index = queue.IndexOf(s); var destination = index + direction;
        if (destination >= 0 && destination < queue.Count) (s.Sequence, queue[destination].Sequence) = (queue[destination].Sequence, s.Sequence);
        return Task.CompletedTask;
    });

    public Task ReassignAsync(long revision, long segmentId, long machineId) => Write(revision, false, $"Reassign remaining segment {segmentId} to {machineId}", (db, data, user) =>
    {
        var s = data.Segments.Single(x => x.Id == segmentId);
        if (s.State == ProductionState.Completed) throw new SchedulingException("Completed machine history cannot be reassigned.");
        if (machineId == s.MachineId) throw new SchedulingException("Select a different destination machine.");
        if (s.PredecessorId is { } predecessor && data.Segments.Single(x => x.Id == predecessor).State != ProductionState.Completed
            || data.Segments.Any(x => x.PredecessorId == s.Id && x.State != ProductionState.Completed))
            throw new SchedulingException("Finish the linked cut-in work before reassigning this segment.");
        _ = Rate(data, machineId, data.Jobs.Single(x => x.Id == s.JobId).PartId);
        if (s.State == ProductionState.Running)
        {
            if (s.ProgressAsOf == null || s.ProgressAsOf < Today.AddDays(-1)) throw new SchedulingException("Record an up-to-date end-of-day progress checkpoint before reassigning running work.");
            if (s.CompletedQuantity == s.Quantity) throw new SchedulingException("This segment has no remaining work. Complete it instead.");
            var job = data.Jobs.Single(x => x.Id == s.JobId);
            var remainder = NewSegment(data, job, machineId, s.Quantity - s.CompletedQuantity);
            remainder.NotBefore = s.ProgressAsOf.Value.AddDays(1);
            job.Segments.Add(remainder);
            s.Quantity = s.CompletedQuantity;
            s.State = ProductionState.Completed; s.ActualCompletion = s.ProgressAsOf;
            s.ActualElapsedWorkingDays = ProductionScheduler.Elapsed(data, data.Machines.Single(x => x.Id == s.MachineId), s);
            return Task.CompletedTask;
        }
        s.PredecessorId = null;
        s.MachineId = machineId;
        s.Sequence = data.Segments.Where(x => x.MachineId == machineId).Max(x => x.Sequence) + 1;
        return Task.CompletedTask;
    });

    public Task CutInAsync(long revision, long segmentId, long interruptingSegmentId, decimal jobStopTarget) =>
        Write(revision, false, $"Cut-in segment {segmentId}, stop job at {jobStopTarget}, insert {interruptingSegmentId}", async (db, data, user) =>
        {
            var s = data.Segments.Single(x => x.Id == segmentId);
            var interrupt = data.Segments.Single(x => x.Id == interruptingSegmentId);
            if (s.State == ProductionState.Completed || interrupt.State != ProductionState.Pending || interrupt.MachineId != s.MachineId || interrupt.JobId == s.JobId)
                throw new SchedulingException("Select another pending job on this machine as the cut-in.");
            if (s.PredecessorId is { } prior && data.Segments.Single(x => x.Id == prior).State != ProductionState.Completed
                || interrupt.PredecessorId is { } interruptPrior && data.Segments.Single(x => x.Id == interruptPrior).State != ProductionState.Completed
                || data.Segments.Any(x => (x.PredecessorId == s.Id || x.PredecessorId == interrupt.Id) && x.State != ProductionState.Completed))
                throw new SchedulingException("These segments already belong to a cut-in chain. Finish the existing chain before creating another.");
            var job = data.Jobs.Single(x => x.Id == s.JobId);
            var otherCompleted = job.Segments.Where(x => x.Id != s.Id).Sum(x => x.CompletedQuantity);
            var stop = jobStopTarget - otherCompleted;
            if (decimal.Truncate(jobStopTarget) != jobStopTarget || decimal.Round(stop, 3) != stop || stop < s.CompletedQuantity || stop < 0 || stop >= s.Quantity)
                throw new SchedulingException("Stop target must be a whole number, include completed pieces, and leave a positive remainder for resumption.");
            var resume = NewSegment(data, job, s.MachineId, s.Quantity - stop);
            s.Quantity = stop;
            job.Segments.Add(resume);
            // Assign IDs before creating the explicit dependency chain, within the same transaction.
            await db.SaveChangesAsync();
            interrupt.PredecessorId = s.Id; resume.PredecessorId = interrupt.Id;
            var queue = data.Segments.Where(x => x.MachineId == s.MachineId && x.Id != interrupt.Id && x.Id != resume.Id)
                .OrderBy(x => x.State == ProductionState.Completed ? 0 : x.State == ProductionState.Running ? 1 : 2).ThenBy(x => x.Sequence).ToList();
            var index = queue.IndexOf(s);
            queue.Insert(index + 1, interrupt); queue.Insert(index + 2, resume);
            for (var i = 0; i < queue.Count; i++) queue[i].Sequence = i + 1;
        });

    public Task ApplyOptimizationAsync(OptimizationPreview preview) => Write(preview.Revision, false, $"Optimize machine {preview.MachineId}", (db, data, user) =>
    {
        if (preview.Horizon != Today) throw new SchedulingException("The planning date changed. Generate a fresh preview.");
        var verified = ProductionScheduler.Optimize(data, preview.MachineId);
        if (!verified.Order.SequenceEqual(preview.Order)) throw new SchedulingException("Optimization preview does not match the current schedule. Generate a fresh preview.");
        var sequence = data.Segments.Where(x => x.MachineId == preview.MachineId && x.State == ProductionState.Running)
            .Select(x => x.Sequence).DefaultIfEmpty(0).Max();
        for (var i = 0; i < verified.Order.Count; i++)
        {
            var s = data.Segments.Single(x => x.Id == verified.Order[i]);
            if (s.State == ProductionState.Pending) s.Sequence = ++sequence;
        }
        return Task.CompletedTask;
    });

    public Task SaveMachineAsync(long revision, long id, string name, bool active, bool usesDefaultWorkingDays, decimal[] hours) =>
        Write(revision, true, $"Machine {id}: {name}, active {active}, uses default calendar {usesDefaultWorkingDays}, hours {string.Join(',', hours)}", (db, data, user) =>
        {
            name = Clean(name, 150) ?? throw new SchedulingException("Enter a machine name.");
            if (data.Machines.Any(x => x.Id != id && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new SchedulingException("Machine names must be unique.");
            ValidateWorkingDayHours(hours);
            var machine = data.Machines.SingleOrDefault(x => x.Id == id);
            if (machine == null) { machine = new(); data.Machines.Add(machine); db.Add(machine); }
            machine.Name = name; machine.IsActive = active; machine.UsesDefaultWorkingDays = usesDefaultWorkingDays;
            SetWorkingDayHours(machine, usesDefaultWorkingDays ? DefaultWorkingDayHours(data.Settings) : hours);
            return Task.CompletedTask;
        });

    public Task SaveDefaultWorkingDaysAsync(long revision, decimal[] hours) =>
        Write(revision, true, $"Default working days: {string.Join(',', hours)}", (db, data, user) =>
        {
            ValidateWorkingDayHours(hours);
            for (var i = 0; i < 7; i++)
            {
                var day = data.Settings.DefaultWorkingDays.SingleOrDefault(x => (int)x.Day == i);
                if (day == null) { day = new() { Day = (DayOfWeek)i }; data.Settings.DefaultWorkingDays.Add(day); }
                day.Hours = hours[i];
            }
            foreach (var machine in data.Machines.Where(x => x.UsesDefaultWorkingDays)) SetWorkingDayHours(machine, hours);
            return Task.CompletedTask;
        });

    private static decimal[] DefaultWorkingDayHours(ProductionSettings settings) => Enumerable.Range(0, 7)
        .Select(i => settings.DefaultWorkingDays.SingleOrDefault(x => (int)x.Day == i)?.Hours ?? 0).ToArray();

    private static void ValidateWorkingDayHours(decimal[] hours)
    {
        if (hours.Length != 7 || hours.Any(x => x < 0 || x > 24 || decimal.Round(x, 1) != x))
            throw new SchedulingException("Enter seven weekday capacities between 0 and 24 hours, with at most one decimal place.");
    }

    private static void SetWorkingDayHours(SortingMachine machine, decimal[] hours)
    {
        for (var i = 0; i < 7; i++)
        {
            var day = machine.WorkingDays.SingleOrDefault(x => (int)x.Day == i);
            if (day == null) { day = new() { Day = (DayOfWeek)i }; machine.WorkingDays.Add(day); }
            day.Hours = hours[i];
        }
    }

    public Task SaveRateAsync(long revision, long machineId, long partId, decimal pph) =>
        Write(revision, true, $"Part {partId}, machine {machineId}: target PPH {pph}", (db, data, user) =>
        {
            WholeNumber(pph, "Target PPH");
            var machine = data.Machines.SingleOrDefault(x => x.Id == machineId) ?? throw new SchedulingException("Select a machine.");
            _ = data.Parts.SingleOrDefault(x => x.Id == partId) ?? throw new SchedulingException("Select a part.");
            var rate = machine.Parts.SingleOrDefault(x => x.PartId == partId);
            if (rate == null)
            {
                rate = new()
                {
                    PartId = partId,
                    // A part with no eligible machines cannot have a preference;
                    // its first eligibility therefore becomes the preference.
                    IsPreferred = !data.Machines.SelectMany(x => x.Parts).Any(x => x.PartId == partId)
                };
                machine.Parts.Add(rate);
            }
            rate.TargetPph = pph;
            return Task.CompletedTask;
        });

    public Task SetPreferredMachineAsync(long revision, long machineId, long partId) =>
        Write(revision, true, $"Part {partId}: preferred machine {machineId}", async (db, data, user) =>
        {
            var machine = data.Machines.SingleOrDefault(x => x.Id == machineId) ?? throw new SchedulingException("Select a machine.");
            if (!machine.IsActive) throw new SchedulingException("A preferred machine must be active.");
            _ = data.Parts.SingleOrDefault(x => x.Id == partId) ?? throw new SchedulingException("Select a part.");
            var preferred = machine.Parts.SingleOrDefault(x => x.PartId == partId)
                ?? throw new SchedulingException("The preferred machine must be eligible for this part.");
            var currentPreferred = data.Machines.SelectMany(x => x.Parts)
                .Where(x => x.PartId == partId && x.IsPreferred && x != preferred)
                .ToList();
            foreach (var rate in currentPreferred) rate.IsPreferred = false;
            // PostgreSQL enforces the filtered unique index after each statement.
            // Flush the old preference before setting its replacement so EF command
            // ordering cannot briefly create two preferred rows.
            if (currentPreferred.Count != 0) await db.SaveChangesAsync();
            preferred.IsPreferred = true;
        });

    public Task RemoveRateAsync(long revision, long machineId, long partId) => Write(revision, true, $"Remove eligibility: part {partId}, machine {machineId}", (db, data, user) =>
    {
        if (data.Jobs.Any(j => j.PartId == partId && j.Segments.Any(s => s.MachineId == machineId && s.State != ProductionState.Completed)))
            throw new SchedulingException("Finish or explicitly reassign this part's unfinished work before removing eligibility.");
        var machine = data.Machines.SingleOrDefault(x => x.Id == machineId) ?? throw new SchedulingException("Select a machine.");
        var rate = machine.Parts.SingleOrDefault(x => x.PartId == partId);
        if (rate?.IsPreferred == true)
        {
            var eligible = data.Machines
                .Where(x => x.Parts.Any(partRate => partRate.PartId == partId))
                .OrderBy(x => x.Name).ThenBy(x => x.Id)
                .ToList();
            var currentIndex = eligible.FindIndex(x => x.Id == machineId);
            var replacement = eligible.Count > 1
                ? eligible[currentIndex > 0 ? currentIndex - 1 : eligible.Count - 1]
                : null;
            if (replacement != null)
                replacement.Parts.Single(x => x.PartId == partId).IsPreferred = true;
        }
        if (rate != null) { machine.Parts.Remove(rate); db.Remove(rate); }
        return Task.CompletedTask;
    });

    public Task SaveEfficiencyAsync(long revision, decimal efficiency) => Write(revision, true, $"Efficiency {efficiency}%", (db, data, user) =>
    {
        _ = ProductionScheduler.EffectivePph(1, efficiency);
        if (efficiency != decimal.Truncate(efficiency)) throw new SchedulingException("Production efficiency must be a whole number.");
        data.Settings.EfficiencyPercent = efficiency;
        return Task.CompletedTask;
    });

    public Task SaveHolidayAsync(long revision, DateOnly date, string name, bool remove) => Write(revision, true, $"Holiday {date}: {name}; remove {remove}", (db, data, user) =>
    {
        var holiday = data.Holidays.SingleOrDefault(x => x.Date == date);
        if (remove) { if (holiday != null) db.Remove(holiday); }
        else
        {
            if (holiday == null) { holiday = new() { Date = date }; db.Add(holiday); }
            holiday.Name = Clean(name, 150) ?? throw new SchedulingException("Enter a holiday name.");
        }
        return Task.CompletedTask;
    });

    public Task UpdateHolidayAsync(long revision, DateOnly originalDate, DateOnly date, string name) => Write(revision, true, $"Holiday {originalDate} changed to {date}: {name}", (db, data, user) =>
    {
        var holiday = data.Holidays.SingleOrDefault(x => x.Date == originalDate)
            ?? throw new SchedulingException("This holiday no longer exists. Reload and try again.");
        var cleanName = Clean(name, 150) ?? throw new SchedulingException("Enter a holiday name.");

        if (date == originalDate)
        {
            holiday.Name = cleanName;
            return Task.CompletedTask;
        }

        if (data.Holidays.Any(x => x.Date == date)) throw new SchedulingException("A holiday already exists on that date.");

        db.Remove(holiday);
        db.Add(new ProductionHoliday { Date = date, Name = cleanName });
        return Task.CompletedTask;
    });

    public Task SaveReasonAsync(long revision, long id, string name, bool active) => Write(revision, true, $"Downtime reason {id}: {name}, active {active}", (db, data, user) =>
    {
        var reason = data.Reasons.SingleOrDefault(x => x.Id == id);
        if (reason == null) { reason = new(); db.Add(reason); }
        var cleanName = Clean(name, 150) ?? throw new SchedulingException("Enter a reason name.");
        if (data.Reasons.Any(x => x.Id != reason.Id && string.Equals(x.Name, cleanName, StringComparison.OrdinalIgnoreCase)))
            throw new SchedulingException("A downtime reason with that name already exists.");
        reason.Name = cleanName; reason.IsActive = active;
        return Task.CompletedTask;
    });

    public Task DeleteReasonAsync(long revision, long id) => Write(revision, true, $"Delete downtime reason {id}", (db, data, user) =>
    {
        var reason = data.Reasons.SingleOrDefault(x => x.Id == id) ?? throw new SchedulingException("The downtime reason no longer exists.");
        if (data.Downtime.Any(x => x.ReasonId == id))
            throw new SchedulingException("This downtime reason is used by downtime history and cannot be deleted. Set it inactive instead.");
        db.Remove(reason);
        return Task.CompletedTask;
    });

    public Task SaveDowntimeAsync(long revision, long id, long machineId, long reasonId, DateOnly start, DateOnly end, string? notes, bool remove = false) =>
        Write(revision, false, $"Downtime {id}: machine {machineId}, reason {reasonId}, {start} through {end}, remove {remove}", (db, data, user) =>
        {
            var down = data.Downtime.SingleOrDefault(x => x.Id == id);
            if (remove) { if (down != null) db.Remove(down); return Task.CompletedTask; }
            if (start > end) throw new SchedulingException("Downtime end date must be on or after its start date.");
            if (!data.Machines.Any(x => x.Id == machineId)) throw new SchedulingException("Select a machine.");
            if (!data.Reasons.Any(x => x.Id == reasonId && (x.IsActive || down?.ReasonId == reasonId))) throw new SchedulingException("Select an active downtime reason.");
            if (down == null) { down = new(); db.Add(down); }
            down.MachineId = machineId; down.ReasonId = reasonId; down.Start = start; down.End = end; down.Notes = Clean(notes, 4000);
            return Task.CompletedTask;
        });
}
