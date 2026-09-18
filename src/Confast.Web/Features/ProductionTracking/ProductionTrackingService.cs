using System.Data;
using Confast.Web.Data;
using Confast.Web.Features.Identity;
using Confast.Web.Features.ProductionScheduling;
using Confast.Web.Time;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Features.ProductionTracking;

public sealed class ProductionTrackingService(
    IDbContextFactory<AppDbContext> factory,
    ICurrentUser currentUser,
    TimeProvider clock,
    BusinessDateProvider? businessDate = null)
{
    private DateOnly Today => businessDate?.Today
        ?? DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    public async Task<ProductionTrackingDashboard> GetDashboardAsync(long? machineId = null, long? sortLogId = null,
        DateOnly? productionDate = null)
    {
        await using var db = await factory.CreateDbContextAsync();
        await RequireAccessAsync(db);
        var selectedDate = productionDate ?? Today;
        if (selectedDate > Today)
            throw new ProductionTrackingException("Production Tracking cannot open future Sort Logs.");
        var machines = await db.Set<SortingMachine>().AsNoTracking()
            .Where(x => x.IsActive).OrderBy(x => x.Name)
            .Select(x => new TrackingMachineOption(x.Id, x.Name)).ToListAsync();

        if (machineId is not null && machines.All(x => x.Id != machineId))
            throw new ProductionTrackingException("Select an active machine.");

        var eligibleParts = machineId is null
            ? []
            : await (from rate in db.Set<PartMachine>().AsNoTracking()
                join part in db.Parts.AsNoTracking() on rate.PartId equals part.Id
                where rate.MachineId == machineId && part.IsActive
                orderby part.PartNumber
                select new TrackingPartOption(part.Id, part.PartNumber, part.Customer.Name)).ToListAsync();

        var eligiblePartIds = eligibleParts.Select(x => x.Id).ToList();
        var lots = machineId is null
            ? []
            : await db.Inspections.AsNoTracking()
                .Where(x => eligiblePartIds.Contains(x.PartId) && x.LotNumber != null && x.LotNumber != "")
                .OrderByDescending(x => x.InspectionDate).ThenBy(x => x.LotNumber)
                .Select(x => new TrackingLotOption(x.Id, x.PartId, x.LotNumber!)).ToListAsync();

        var scheduledJobs = machineId is null
            ? []
            : await (from segment in db.Set<ProductionSegment>().AsNoTracking()
                join job in db.Set<ProductionJob>().AsNoTracking() on segment.JobId equals job.Id
                join inspection in db.Inspections.AsNoTracking()
                    on new { job.PartId, Po = job.PoNumber } equals new { inspection.PartId, Po = inspection.ConformancePoNumber }
                where segment.MachineId == machineId && segment.State != ProductionState.Completed
                    && job.PoNumber != null
                    && inspection.LotNumber != null && inspection.LotNumber != ""
                orderby segment.State == ProductionState.Running ? 0 : 1, segment.Sequence, job.Part.PartNumber, inspection.LotNumber
                select new TrackingScheduledJobOption(segment.Id, job.PartId, job.Part.PartNumber,
                    inspection.Id, inspection.LotNumber!, job.Part.Customer.Name, segment.State)).ToListAsync();
        scheduledJobs = scheduledJobs.GroupBy(x => new { x.PartId, x.InspectionId })
            .Select(x => x.First()).ToList();

        var todayEntities = await db.Set<SortLog>().AsNoTracking()
            .Where(x => x.ProductionDate == selectedDate && (machineId == null || x.MachineId == machineId))
            .Include(x => x.Part).ThenInclude(x => x.Customer)
            .Include(x => x.Inspection).Include(x => x.Lines)
            .OrderByDescending(x => x.CreatedAtUtc).ToListAsync();
        var todayLogs = todayEntities.Select(x => new TodaySortLogItem(x.Id, x.MachineId,
            x.Part.PartNumber, LotLabel(x.Inspection), x.Part.Customer.Name,
            x.Lines.Where(line => line.StopTimeUtc != null).Sum(line => line.PassQuantity),
            x.Lines.Any(line => line.StopTimeUtc == null))).ToList();

        var logDates = db.Set<SortLog>().AsNoTracking()
            .Where(x => machineId == null || x.MachineId == machineId);
        var previousLogDate = await logDates.Where(x => x.ProductionDate < selectedDate)
            .MaxAsync(x => (DateOnly?)x.ProductionDate);
        var nextLogDate = await logDates.Where(x => x.ProductionDate > selectedDate && x.ProductionDate <= Today)
            .MinAsync(x => (DateOnly?)x.ProductionDate);

        var current = sortLogId is null ? null : await LoadDetailAsync(db, sortLogId.Value);
        if (current is not null && current.ProductionDate != selectedDate)
            throw new ProductionTrackingException("The selected Sort Log belongs to a different production date.");
        if (current is not null && machineId is not null && current.MachineId != machineId)
            throw new ProductionTrackingException("The selected Sort Log belongs to a different machine.");

        var causes = await OrderedDowntimeCauses(db).ToListAsync();

        return new(selectedDate, previousLogDate, nextLogDate, machines, scheduledJobs, eligibleParts, lots,
            todayLogs, causes, current);
    }

    public async Task<IReadOnlyList<SortLogDowntimeCause>> GetDowntimeCausesAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        await RequireAdministratorAsync(db);
        return await OrderedDowntimeCauses(db).ToListAsync();
    }

    public async Task SaveDowntimeCauseAsync(long id, string name, bool isActive)
    {
        var cleanName = Clean(name, 150)
            ?? throw new ProductionTrackingException("Enter a downtime cause name.");
        await using var db = await factory.CreateDbContextAsync();
        await RequireAdministratorAsync(db);
        var normalizedName = cleanName.ToUpperInvariant();
        if (await db.Set<SortLogDowntimeCause>().AnyAsync(x => x.Id != id && x.NormalizedName == normalizedName))
            throw new ProductionTrackingException("A Sort Log downtime cause with that name already exists.");
        var cause = await db.Set<SortLogDowntimeCause>().SingleOrDefaultAsync(x => x.Id == id);
        if (cause is null)
        {
            if (id != 0) throw new ProductionTrackingException("This downtime cause no longer exists.");
            cause = new();
            db.Add(cause);
        }
        cause.Name = cleanName;
        cause.NormalizedName = normalizedName;
        cause.IsActive = isActive;
        await SaveAsync(db);
    }

    public async Task DeleteDowntimeCauseAsync(long id)
    {
        await using var db = await factory.CreateDbContextAsync();
        await RequireAdministratorAsync(db);
        var cause = await db.Set<SortLogDowntimeCause>().SingleOrDefaultAsync(x => x.Id == id)
            ?? throw new ProductionTrackingException("This downtime cause no longer exists.");
        if (await db.Set<SortLogLine>().AnyAsync(x => x.DowntimeCauseId == id))
            throw new ProductionTrackingException("This downtime cause is used by Sort Log history and cannot be deleted. Set it inactive instead.");
        db.Remove(cause);
        await SaveAsync(db);
    }

    public async Task<long> OpenOrCreateSortLogAsync(long machineId, long inspectionId, long? productionSegmentId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable);
        var userId = await RequireAccessAsync(db);
        var inspection = await db.Inspections.AsNoTracking().SingleOrDefaultAsync(x => x.Id == inspectionId)
            ?? throw new ProductionTrackingException("Select an existing lot.");
        var rate = await db.Set<PartMachine>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.MachineId == machineId && x.PartId == inspection.PartId)
            ?? throw new ProductionTrackingException("The selected part is not eligible for this machine.");
        if (!await db.Set<SortingMachine>().AnyAsync(x => x.Id == machineId && x.IsActive))
            throw new ProductionTrackingException("Select an active machine.");

        if (productionSegmentId is not null)
        {
            var matchesSchedule = await (from segment in db.Set<ProductionSegment>()
                join job in db.Set<ProductionJob>() on segment.JobId equals job.Id
                where segment.Id == productionSegmentId && segment.MachineId == machineId
                    && job.PartId == inspection.PartId && job.PoNumber != null
                    && job.PoNumber == inspection.ConformancePoNumber
                select segment.Id).AnyAsync();
            if (!matchesSchedule)
                throw new ProductionTrackingException("The selected schedule item no longer matches this machine, part, and lot.");
        }

        var existingId = await db.Set<SortLog>()
            .Where(x => x.ProductionDate == Today && x.MachineId == machineId
                && x.PartId == inspection.PartId && x.InspectionId == inspectionId)
            .Select(x => (long?)x.Id).SingleOrDefaultAsync();
        if (existingId is not null)
        {
            await transaction.CommitAsync();
            return existingId.Value;
        }

        var log = new SortLog
        {
            ProductionDate = Today,
            MachineId = machineId,
            PartId = inspection.PartId,
            InspectionId = inspection.Id,
            ProductionSegmentId = productionSegmentId,
            TargetPphSnapshot = rate.TargetPph,
            CreatedAtUtc = clock.GetUtcNow(),
            CreatedByUserId = userId
        };
        db.Add(log);
        try
        {
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return log.Id;
        }
        catch (DbUpdateException exception) when (HasConstraint(exception, "UX_sort_logs_daily_job"))
        {
            await transaction.RollbackAsync();
            await using var retryDb = await factory.CreateDbContextAsync();
            return await retryDb.Set<SortLog>().Where(x => x.ProductionDate == Today
                    && x.MachineId == machineId && x.PartId == inspection.PartId && x.InspectionId == inspectionId)
                .Select(x => x.Id).SingleAsync();
        }
    }

    public async Task SaveCommentsAsync(long sortLogId, string? comments)
    {
        await using var db = await factory.CreateDbContextAsync();
        await RequireAccessAsync(db);
        var log = await db.Set<SortLog>().SingleOrDefaultAsync(x => x.Id == sortLogId)
            ?? throw new ProductionTrackingException("This Sort Log no longer exists.");
        log.Comments = Clean(comments, 4000);
        await SaveAsync(db);
    }

    public async Task<long> StartRunAsync(long sortLogId, StartSortLogLineInput input)
    {
        ValidateSamples(input.BoundarySamplesRanQuantity, input.BoundarySamplesPassedQuantity);
        ValidateOptionalNonnegative(input.StartBoxCount, "Start Box Count");
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await RequireAccessAsync(db);
        var log = await LoadLockedLogAsync(db, sortLogId);
        if (log.ProductionDate != Today)
            throw new ProductionTrackingException("Runs can only be started on today's Sort Logs.");
        if (log.Lines.Any(x => x.StopTimeUtc == null))
            throw new ProductionTrackingException("This Sort Log already has a running interval.");
        if (log.Lines.Any(x => x.StopTimeUtc != null && x.DowntimeCauseId == null))
            throw new ProductionTrackingException("Finish the stop details for the previous run before starting another.");

        var now = clock.GetUtcNow();
        var priorStop = log.Lines.Max(x => x.StopTimeUtc);
        if (priorStop is not null && now < priorStop)
            throw new ProductionTrackingException("A new run cannot start before the previous run stopped.");
        var productionInitials = OptionalInitials(input.ProductionInitials, "Production Initials");
        var qualityInitials = OptionalInitials(input.QualityInitials, "Quality Initials");
        RequireAnyInitials(productionInitials, qualityInitials);
        var startBoxCount = input.StartBoxCount ?? log.Lines
            .Where(x => x.StopTimeUtc is not null)
            .OrderByDescending(x => x.Sequence)
            .Select(x => x.EndBoxCount)
            .FirstOrDefault();
        var line = new SortLogLine
        {
            SortLogId = log.Id,
            Sequence = log.Lines.Select(x => x.Sequence).DefaultIfEmpty().Max() + 1,
            StartTimeUtc = now,
            BoundarySamplesRanQuantity = input.BoundarySamplesRanQuantity,
            BoundarySamplesPassedQuantity = input.BoundarySamplesPassedQuantity,
            StartBoxCount = startBoxCount,
            ProductionInitials = productionInitials,
            QualityInitials = qualityInitials,
            Notes = Clean(input.Notes, 4000)
        };
        db.Add(line);
        await SaveAsync(db);
        await transaction.CommitAsync();
        return line.Id;
    }

    public async Task<long> StopRunAsync(long sortLogId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await RequireAccessAsync(db);
        var log = await LoadLockedLogAsync(db, sortLogId);
        var line = log.Lines.SingleOrDefault(x => x.StopTimeUtc == null)
            ?? throw new ProductionTrackingException("This Sort Log has no running interval.");
        var now = clock.GetUtcNow();
        if (now < line.StartTimeUtc)
            throw new ProductionTrackingException("Stop Time cannot be before Start Time.");
        line.StopTimeUtc = now;
        await SaveAsync(db);
        await transaction.CommitAsync();
        return line.Id;
    }

    public async Task ResumeRunAsync(long sortLogId)
    {
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await RequireAccessAsync(db);
        var log = await LoadLockedLogAsync(db, sortLogId);
        if (log.Lines.Any(x => x.StopTimeUtc is null))
            throw new ProductionTrackingException("This Sort Log already has a running interval.");

        var line = log.Lines.SingleOrDefault(x => x.StopTimeUtc is not null && x.DowntimeCauseId is null)
            ?? throw new ProductionTrackingException("This Sort Log has no stopped run awaiting details.");
        line.StopTimeUtc = null;
        await SaveAsync(db);
        await transaction.CommitAsync();
    }

    public async Task UpdateLineAsync(long lineId, UpdateSortLogLineInput input)
    {
        ValidateLine(input);
        await using var db = await factory.CreateDbContextAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await RequireAccessAsync(db);
        var line = await db.Set<SortLogLine>().FromSqlInterpolated(
            $"SELECT *, xmin FROM sort_log_lines WHERE id = {lineId} FOR UPDATE").SingleOrDefaultAsync()
            ?? throw new ProductionTrackingException("This run interval no longer exists.");
        var reason = await db.Set<SortLogDowntimeCause>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.DowntimeCauseId);
        if (reason is null || !reason.IsActive && line.DowntimeCauseId != reason.Id)
            throw new ProductionTrackingException("Select an active downtime cause.");
        var otherLines = await db.Set<SortLogLine>().AsNoTracking()
            .Where(x => x.SortLogId == line.SortLogId && x.Id != line.Id).ToListAsync();
        if (otherLines.Any(other => Overlaps(input.StartTimeUtc, input.StopTimeUtc,
                other.StartTimeUtc, other.StopTimeUtc)))
            throw new ProductionTrackingException("Sort Log run intervals may not overlap.");

        var productionInitials = OptionalInitials(input.ProductionInitials, "Production Initials");
        var qualityInitials = OptionalInitials(input.QualityInitials, "Quality Initials");
        RequireAnyInitials(productionInitials, qualityInitials);
        line.StartTimeUtc = input.StartTimeUtc;
        line.StopTimeUtc = input.StopTimeUtc;
        line.DowntimeCauseId = input.DowntimeCauseId;
        line.PassQuantity = input.PassQuantity;
        line.FailQuantity = input.FailQuantity;
        line.BoundarySamplesRanQuantity = input.BoundarySamplesRanQuantity;
        line.BoundarySamplesPassedQuantity = input.BoundarySamplesPassedQuantity;
        line.StartBoxCount = input.StartBoxCount;
        line.EndBoxCount = input.EndBoxCount;
        line.ProductionUserId = null;
        line.ProductionInitials = productionInitials;
        line.QualityUserId = null;
        line.QualityInitials = qualityInitials;
        line.Notes = Clean(input.Notes, 4000);
        await SaveAsync(db);
        await transaction.CommitAsync();
    }

    private async Task<string> RequireAccessAsync(AppDbContext db)
    {
        var userId = await currentUser.GetUserIdAsync();
        if (userId is null || !await db.Users.AnyAsync(x => x.Id == userId && x.IsActive))
            throw new UnauthorizedAccessException("Sign in with an active account to use Production Tracking.");
        var allowed = await (from userRole in db.UserRoles
            join role in db.Roles on userRole.RoleId equals role.Id
            where userRole.UserId == userId
            select role.Name).AnyAsync(x => x == AppRoles.Production || x == AppRoles.Administrator);
        if (!allowed)
            throw new UnauthorizedAccessException("Production or Administrator access is required to use Production Tracking.");
        return userId;
    }

    private async Task<string> RequireAdministratorAsync(AppDbContext db)
    {
        var userId = await currentUser.GetUserIdAsync();
        if (userId is null || !await db.Users.AnyAsync(x => x.Id == userId && x.IsActive))
            throw new UnauthorizedAccessException("Sign in with an active account to configure Production Tracking.");
        var administrator = await (from userRole in db.UserRoles
            join role in db.Roles on userRole.RoleId equals role.Id
            where userRole.UserId == userId
            select role.Name).AnyAsync(x => x == AppRoles.Administrator);
        if (!administrator)
            throw new UnauthorizedAccessException("Administrator access is required for Production Tracking settings.");
        return userId;
    }

    private static IQueryable<SortLogDowntimeCause> OrderedDowntimeCauses(AppDbContext db) =>
        from cause in db.Set<SortLogDowntimeCause>().AsNoTracking()
        join line in db.Set<SortLogLine>().AsNoTracking() on (long?)cause.Id equals line.DowntimeCauseId into lines
        orderby lines.Count() descending, cause.Name
        select cause;

    private static async Task<SortLog> LoadLockedLogAsync(AppDbContext db, long sortLogId)
    {
        var log = await db.Set<SortLog>().FromSqlInterpolated(
            $"SELECT *, xmin FROM sort_logs WHERE id = {sortLogId} FOR UPDATE").SingleOrDefaultAsync()
            ?? throw new ProductionTrackingException("This Sort Log no longer exists.");
        await db.Entry(log).Collection(x => x.Lines).LoadAsync();
        return log;
    }

    private static async Task<SortLogDetail?> LoadDetailAsync(AppDbContext db, long id)
    {
        var log = await db.Set<SortLog>().AsNoTracking()
            .Include(x => x.Machine).Include(x => x.Part).ThenInclude(x => x.Customer)
            .Include(x => x.Inspection).Include(x => x.Lines).ThenInclude(x => x.DowntimeCause)
            .SingleOrDefaultAsync(x => x.Id == id);
        if (log is null) return null;
        var lines = log.Lines.OrderBy(x => x.Sequence).Select(x => new SortLogLineItem(x.Id,
            x.Sequence, x.StartTimeUtc, x.StopTimeUtc, x.DowntimeCauseId, x.DowntimeCause?.Name,
            x.PassQuantity, x.FailQuantity, x.BoundarySamplesRanQuantity,
            x.BoundarySamplesPassedQuantity, x.StartBoxCount, x.EndBoxCount,
            x.ProductionInitials, x.QualityInitials, x.Notes)).ToList();
        return new(log.Id, log.ProductionDate, log.MachineId, log.Machine.Name, log.PartId,
            log.Part.PartNumber, log.InspectionId, LotLabel(log.Inspection), log.Part.Customer.Name,
            log.ProductionSegmentId, log.TargetPphSnapshot, log.Comments, lines,
            SortLogMetricsCalculator.Calculate(lines, log.TargetPphSnapshot));
    }

    private static string? OptionalInitials(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var initials = value.Trim().ToUpperInvariant();
        if (initials.Length != 2 || !initials.All(char.IsLetter))
            throw new ProductionTrackingException($"{name} must be exactly two letters.");
        return initials;
    }

    private static void RequireAnyInitials(string? productionInitials, string? qualityInitials)
    {
        if (productionInitials is null && qualityInitials is null)
            throw new ProductionTrackingException("Enter Production Initials or Quality Initials.");
    }

    private static void ValidateLine(UpdateSortLogLineInput input)
    {
        if (input.StopTimeUtc < input.StartTimeUtc)
            throw new ProductionTrackingException("Stop Time must be on or after Start Time.");
        ValidateNonnegative(input.PassQuantity, "Pass Quantity");
        ValidateNonnegative(input.FailQuantity, "Fail Quantity");
        ValidateSamples(input.BoundarySamplesRanQuantity, input.BoundarySamplesPassedQuantity);
        ValidateOptionalNonnegative(input.StartBoxCount, "Start Box Count");
        if (input.EndBoxCount is null)
            throw new ProductionTrackingException("Enter End Box Count.");
        ValidateNonnegative(input.EndBoxCount.Value, "End Box Count");
        if (input.DowntimeCauseId == 0)
            throw new ProductionTrackingException("Select a downtime cause.");
    }

    private static void ValidateSamples(long ran, long passed)
    {
        ValidateNonnegative(ran, "Boundary Samples Ran");
        ValidateNonnegative(passed, "Boundary Samples Passed");
        if (passed > ran)
            throw new ProductionTrackingException("Boundary Samples Passed cannot exceed Boundary Samples Ran.");
    }

    private static void ValidateNonnegative(long value, string name)
    {
        if (value < 0) throw new ProductionTrackingException($"{name} cannot be negative.");
    }

    private static void ValidateOptionalNonnegative(long? value, string name)
    {
        if (value < 0) throw new ProductionTrackingException($"{name} cannot be negative.");
    }

    private static bool Overlaps(DateTimeOffset start, DateTimeOffset stop,
        DateTimeOffset otherStart, DateTimeOffset? otherStop) =>
        start < (otherStop ?? DateTimeOffset.MaxValue) && otherStart < stop;

    private static string LotLabel(Features.Inspections.Inspection inspection) =>
        string.IsNullOrWhiteSpace(inspection.LotNumber) ? $"Inspection {inspection.Id}" : inspection.LotNumber;

    private static string? Clean(string? value, int maxLength)
    {
        var clean = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        if (clean?.Length > maxLength)
            throw new ProductionTrackingException($"Text cannot exceed {maxLength:N0} characters.");
        return clean;
    }

    private static async Task SaveAsync(AppDbContext db)
    {
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ProductionTrackingException("This Sort Log changed elsewhere. Reload and try again.");
        }
        catch (DbUpdateException exception) when (HasConstraint(exception, "UX_sort_log_lines_active"))
        {
            throw new ProductionTrackingException("This Sort Log already has a running interval.");
        }
        catch (DbUpdateException exception) when (HasConstraint(exception, "EX_sort_log_lines_no_overlap"))
        {
            throw new ProductionTrackingException("Sort Log run intervals may not overlap.");
        }
        catch (DbUpdateException exception) when (HasConstraint(exception, "UX_sort_log_downtime_causes_normalized_name"))
        {
            throw new ProductionTrackingException("A Sort Log downtime cause with that name already exists.");
        }
    }

    private static bool HasConstraint(DbUpdateException exception, string name) =>
        exception.InnerException is PostgresException postgres && postgres.ConstraintName == name;
}
