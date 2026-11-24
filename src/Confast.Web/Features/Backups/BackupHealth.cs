using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Features.Backups;

internal sealed record BackupStatus(
    BackupExecution? Active,
    int Pending,
    BackupExecution? LastSuccess,
    BackupExecution? LastAttempt,
    Guid? LatestAvailable,
    long? DatabaseBytes,
    long? FreeBytes,
    long StorageBytes,
    int Rolling,
    int Pinned,
    double? AverageBytes,
    double? AverageMilliseconds,
    long? EstimatedRetentionBytes,
    IReadOnlyDictionary<BackupNotice, string> Warnings);

internal sealed class BackupHealth(BackupStorage storage, BackupPostgres postgres, TimeProvider clock)
{
    internal async Task<BackupStatus> ReadAsync(AppDbContext db, BackupSettings settings, CancellationToken ct)
    {
        var history = db.BackupExecutions.AsNoTracking().Where(x => x.DeploymentId == storage.DeploymentId);
        var completed = history.Where(x => x.Outcome == BackupOutcome.Completed);
        var available = completed.Where(x => x.Availability == BackupAvailability.Available);
        var active = await history.Where(x => x.Outcome == BackupOutcome.Running || x.Stage == BackupStage.ApplyingRetention)
            .OrderByDescending(x => x.StartedAtUtc).FirstOrDefaultAsync(ct);
        var pending = await history.CountAsync(x => x.Outcome == BackupOutcome.Pending, ct);
        var lastSuccess = await completed.OrderByDescending(x => x.FinishedAtUtc).FirstOrDefaultAsync(ct);
        var lastAttempt = await history.Where(x => x.Outcome != BackupOutcome.Pending)
            .OrderByDescending(x => x.StartedAtUtc ?? x.RequestedAtUtc).FirstOrDefaultAsync(ct);
        var latestAvailable = await available.OrderByDescending(x => x.FinishedAtUtc)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);
        var stats = await completed.GroupBy(x => 1).Select(g => new
        {
            Average = g.Average(x => x.TotalBytes),
            Duration = g.Average(x => x.DurationMilliseconds),
            Max = g.Max(x => x.TotalBytes),
            Stored = g.Sum(x => x.Availability == BackupAvailability.Available || x.Availability == BackupAvailability.Corrupted
                ? x.TotalBytes : 0),
            PinnedBytes = g.Sum(x => x.Availability == BackupAvailability.Available && x.Pinned ? x.TotalBytes : 0),
            Rolling = g.Count(x => x.Availability == BackupAvailability.Available && !x.Pinned),
            Pinned = g.Count(x => x.Availability == BackupAvailability.Available && x.Pinned),
            Integrity = g.Any(x => x.Availability == BackupAvailability.Corrupted ||
                x.Verification == BackupVerification.Failed && x.Availability != BackupAvailability.Expired &&
                x.Availability != BackupAvailability.Deleted)
        }).SingleOrDefaultAsync(ct);

        var warnings = new Dictionary<BackupNotice, string>();
        long? size = null;
        try { size = (await postgres.DescribeAsync(ct)).Bytes; }
        catch (NpgsqlException) { /* Report unknown size without publishing connection diagnostics. */ }
        long? free = null;
        if (!storage.Configured)
            warnings[BackupNotice.Storage] = storage.ConfigurationIssue ?? "Backup storage has not been provisioned for this deployment.";
        else if (settings.DeploymentId != storage.DeploymentId || settings.Destination is null)
            warnings[BackupNotice.Storage] = "Select and save an approved server destination.";
        else
        {
            try
            {
                storage.CheckControl();
                var location = storage.RequireLocation(settings.Destination, settings.DestinationId);
                free = BackupStorage.FreeBytes(location);
                if (free is null)
                    warnings[BackupNotice.Capacity] = "Available destination space could not be measured.";
                else if (size.HasValue && free < RequiredSpace(size.Value, stats?.Max, settings.ReserveBytes))
                    warnings[BackupNotice.Capacity] = "The destination has insufficient space for a new backup plus the configured reserve.";
                var generation = await db.AuthorizationState.Select(x => x.InstallationGeneration).SingleAsync(ct);
                try
                {
                    if (storage.ReadPolicy()?.Allows(settings, storage.DeploymentId, generation) != true)
                        warnings[BackupNotice.Retention] = BackupPolicy.ReviewMessage;
                }
                catch (Exception ex) when (BackupStorage.IsFilesystemError(ex))
                {
                    warnings[BackupNotice.Retention] = BackupPolicy.ReviewMessage;
                }
            }
            catch (Exception ex) when (BackupStorage.IsFilesystemError(ex))
            {
                warnings[BackupNotice.Storage] = "The active destination or the shared coordination directory is unavailable.";
            }
        }
        if (lastAttempt?.Outcome is BackupOutcome.Failed or BackupOutcome.Interrupted)
            warnings[BackupNotice.Failure] = "The latest backup attempt failed or was interrupted.";
        if (stats?.Integrity == true)
            warnings[BackupNotice.Integrity] = "One or more backup sets have integrity problems.";
        if (settings.CatalogIssue is { } catalog) warnings[BackupNotice.Catalog] = catalog;
        if (settings.RetentionIssue is { } retention) warnings[BackupNotice.Retention] = retention;
        if (settings.Enabled && settings.DeploymentId == storage.DeploymentId)
        {
            if (settings.ServerZoneId != TimeZoneInfo.Local.Id)
                warnings[BackupNotice.Overdue] = "The server time zone changed. Confirm and save the schedule before it runs again.";
            else
            {
                var now = clock.GetUtcNow();
                var due = settings.Schedule.Previous(now.AddMinutes(-15), TimeZoneInfo.Local);
                if (due >= settings.ScheduleStartedAtUtc && (lastSuccess?.FinishedAtUtc is not { } finished || finished < due))
                    warnings[BackupNotice.Overdue] = "A scheduled recovery point is overdue.";
            }
        }
        var estimate = stats?.Average is { } mean
            ? Saturate(mean * settings.Retain + stats.PinnedBytes.GetValueOrDefault()) : (long?)null;
        return new(active, pending, lastSuccess, lastAttempt, latestAvailable, size, free, stats?.Stored ?? 0,
            stats?.Rolling ?? 0, stats?.Pinned ?? 0, stats?.Average, stats?.Duration, estimate, warnings);
    }

    internal static long RequiredSpace(long databaseBytes, long? previousLargest, long reserve) =>
        Saturate(Math.Max(databaseBytes, (previousLargest ?? 0) * 1.15) + reserve + BackupStorage.MaxArtworkBytes + 65536);

    private static long Saturate(double value) => value >= long.MaxValue ? long.MaxValue : (long)Math.Ceiling(value);
}
