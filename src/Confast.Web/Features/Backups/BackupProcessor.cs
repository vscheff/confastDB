using System.Diagnostics;
using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Keys = Confast.Web.Features.Authorization.Permissions.DatabaseBackups;

namespace Confast.Web.Features.Backups;

internal sealed record BackupCompletion(Guid Id, string[] Recipients)
{
    internal static BackupCompletion? For(Guid id, IEnumerable<BackupRecipient> recipients)
    {
        var addresses = recipients.Where(x => x.Notices.HasFlag(BackupNotice.Success)).Select(x => x.Email).ToArray();
        return addresses.Length == 0 ? null : new(id, addresses);
    }
}

internal sealed class BackupProcessor(
    IDbContextFactory<AppDbContext> factory,
    BackupStorage storage,
    BackupCoordinator coordinator,
    BackupCatalog catalog,
    BackupPostgres postgres,
    EffectivePermissionService permissions,
    TimeProvider clock,
    ILogger<BackupProcessor> logger)
{
    internal async Task<BackupCompletion?> RunOnceAsync(CancellationToken cancellationToken, bool forceReconcile = false)
    {
        if (!storage.Configured) return null;
        await using var lease = await coordinator.TryAcquireAsync(cancellationToken);
        if (lease is null) return null;
        var ct = lease.Token;
        await using var db = await factory.CreateDbContextAsync(ct);
        var settings = await db.BackupSettings.Include(x => x.Recipients).SingleAsync(ct);
        var rows = db.BackupExecutions.Where(x => x.DeploymentId == storage.DeploymentId);
        var now = clock.GetUtcNow();
        var interrupted = await rows.AnyAsync(x => x.Outcome == BackupOutcome.Running ||
            x.Stage == BackupStage.ApplyingRetention || x.VerifyStartedAtUtc != null, ct);
        var actor = settings.ReconcileRequestedBy;
        if (actor is not null)
        {
            try { await permissions.DemandBackupRequesterAsync(db, actor, settings.ReconcileGeneration, [Keys.Reconcile], ct); }
            catch (AuthorizationDeniedException)
            {
                BackupMapping.Audit(db, storage.DeploymentId, now, "ReconciliationAuthorizationExpired", actor);
                actor = null;
            }
            settings.ReconcileRequestedBy = null;
            settings.ReconcileGeneration = null;
        }
        if (forceReconcile || actor is not null || interrupted || settings.LastReconciledAtUtc is null ||
            settings.LastReconciledAtUtc > now || now - settings.LastReconciledAtUtc >= TimeSpan.FromMinutes(1))
            await catalog.ReconcileAsync(db, settings, lease, actor);
        if (interrupted)
        {
            foreach (var row in await rows.Where(x => x.Outcome == BackupOutcome.Running || x.Stage == BackupStage.ApplyingRetention ||
                         x.VerifyStartedAtUtc != null).ToListAsync(ct))
            {
                if (row.Outcome == BackupOutcome.Running)
                {
                    FinishFailure(db, row, BackupOutcome.Interrupted, "Backup execution was interrupted before publication.", now);
                }
                else if (row.Stage == BackupStage.ApplyingRetention)
                {
                    row.Stage = BackupStage.Finished;
                    row.RetentionDiagnostic = settings.RetentionIssue = "Retention was interrupted. The completed backup was preserved.";
                    BackupMapping.Audit(db, storage.DeploymentId, now, "RetentionInterrupted", id: row.Id);
                }
                if (row.VerifyStartedAtUtc.HasValue)
                {
                    BackupMapping.Audit(db, storage.DeploymentId, now, "VerificationInterrupted", row.VerifyRequestedBy, row.Id);
                    row.VerificationDiagnostic = "The last verification request was interrupted.";
                    ClearVerificationRequest(row);
                }
            }
        }

        if (settings.DeploymentId == storage.DeploymentId && settings.Enabled && settings.DestinationId.HasValue &&
            settings.Destination is not null && settings.ServerZoneId == TimeZoneInfo.Local.Id)
        {
            if (BackupOccurrences.TakeDue(settings, now, TimeZoneInfo.Local) is { } scheduled)
            {
                if (!await rows.AnyAsync(x => x.ScheduledForUtc == scheduled, ct))
                    AddScheduled(db, settings, now, scheduled, 1);
            }
            else if (settings.NextDueAtUtc > now)
            {
                var latest = await rows.Where(x => x.Trigger == BackupTrigger.Scheduled && x.SettingsRevision == settings.Revision)
                    .OrderByDescending(x => x.ScheduledForUtc).ThenByDescending(x => x.Attempt).FirstOrDefaultAsync(ct);
                if (latest is { Outcome: BackupOutcome.Failed or BackupOutcome.Interrupted, Attempt: 1, ScheduledForUtc: { } occurrence } &&
                    latest.FinishedAtUtc <= now - BackupOccurrences.RetryDelay &&
                    !await rows.AnyAsync(x => x.ScheduledForUtc == occurrence && x.Attempt == 2, ct))
                    AddScheduled(db, settings, now, occurrence, 2);
            }
        }
        await db.SaveChangesAsync(ct);

        var request = await rows.Where(x => x.Outcome == BackupOutcome.Pending)
            .OrderByDescending(x => x.Trigger).ThenBy(x => x.RequestedAtUtc).ThenBy(x => x.Id).FirstOrDefaultAsync(ct);
        if (request is not null) return await RunBackupAsync(db, settings, request, lease);
        var verification = await rows.Where(x => x.VerifyRequestedBy != null)
            .OrderBy(x => x.RequestedAtUtc).FirstOrDefaultAsync(ct);
        if (verification is not null) await VerifyAsync(db, verification, lease);
        return null;
    }

    private static void AddScheduled(AppDbContext db, BackupSettings settings, DateTimeOffset now,
        DateTimeOffset occurrence, int attempt)
    {
        var row = BackupManager.NewRequest(settings, now, null, null);
        (row.Trigger, row.ScheduledForUtc, row.Attempt) = (BackupTrigger.Scheduled, occurrence, attempt);
        db.BackupExecutions.Add(row);
        BackupMapping.Audit(db, row.DeploymentId, now, "ScheduledBackupQueued", id: row.Id, after: new { occurrence, attempt });
    }

    private async Task<BackupCompletion?> RunBackupAsync(AppDbContext db, BackupSettings settings, BackupExecution row, BackupLease lease)
    {
        var ct = lease.Token;
        var elapsed = Stopwatch.StartNew();
        row.StartedAtUtc = clock.GetUtcNow();
        row.Outcome = BackupOutcome.Running;
        await StageAsync(db, row, BackupStage.CheckingDestination, ct);
        string? staging = null;
        var published = false;
        BackupLocation? location = null;
        try
        {
            if (row.RequestedBy is { } actor)
                await permissions.DemandBackupRequesterAsync(db, actor, row.RequestedGeneration, BackupManager.CreatePermissions(row.Pinned), ct);
            else if (row.Trigger != BackupTrigger.Scheduled || !settings.Enabled || row.SettingsRevision != settings.Revision ||
                     settings.ServerZoneId != TimeZoneInfo.Local.Id)
                throw new BackupException("ScheduleChanged", "The schedule changed before this backup could run.");

            if (settings.DeploymentId != row.DeploymentId || settings.DestinationId != row.DestinationId || settings.Destination != row.Destination)
                throw new BackupException("DestinationChanged", "The destination changed after this backup was queued. Request a new backup.");
            row.SettingsRevision = settings.Revision;
            location = storage.RequireLocation(row.Destination, row.DestinationId);
            var source = await postgres.DescribeAsync(ct);
            var historicalSize = await db.BackupExecutions.Where(x => x.DeploymentId == storage.DeploymentId &&
                x.Outcome == BackupOutcome.Completed).MaxAsync(x => x.TotalBytes, ct);
            CheckSpace(location, BackupHealth.RequiredSpace(source.Bytes, historicalSize, settings.ReserveBytes));
            var utilities = await postgres.CheckUtilitiesAsync(source.Major, ct);
            var name = BackupStorage.SetName(row.StartedAtUtc.Value, row.Id);

            await StageAsync(db, row, BackupStage.DumpingDatabase, ct);
            await lease.CheckAsync();
            storage.RequireLocation(location.Name, location.Id);
            staging = BackupStorage.BeginSet(location, name);
            var archive = Path.Combine(staging, BackupStorage.ArchiveName);
            await postgres.DumpAsync(utilities, archive, ct);
            await StageAsync(db, row, BackupStage.CopyingApplicationData, ct);
            var copiedArtwork = await storage.CopyArtworkAsync(staging, ct);

            await StageAsync(db, row, BackupStage.ValidatingArchive, ct);
            await postgres.ValidateArchiveAsync(archive, settings.FullVerification, ct, utilities.RestorePath);
            var files = new List<BackupFile> { await BackupStorage.FingerprintAsync(staging, BackupStorage.ArchiveName, ct) };
            if (copiedArtwork) files.Add(await BackupStorage.FingerprintAsync(staging, BackupStorage.ArtworkName, ct));

            await StageAsync(db, row, BackupStage.Publishing, ct);
            await lease.CheckAsync();
            storage.RequireLocation(location.Name, location.Id);
            CheckSpace(location, settings.ReserveBytes);
            var completed = clock.GetUtcNow();
            var manifest = new BackupManifest(1, row.Id, row.DeploymentId, row.DestinationId, settings.Revision,
                source.Name, typeof(Program).Assembly.GetName().Version?.ToString() ?? "Unknown",
                source.Version, utilities.DumpVersion, row.RequestedAtUtc, row.StartedAtUtc.Value, completed,
                elapsed.ElapsedMilliseconds, row.Trigger, row.ScheduledForUtc, row.Attempt, row.RequestedBy,
                row.Pinned, row.Label, row.Notes, Guid.NewGuid(), settings.FullVerification, files.ToArray());
            BackupStorage.WriteManifest(staging, manifest);
            if (settings.FullVerification)
                await BackupCatalog.VerifyChecksumsAsync(storage.ReadSet(location, name, BackupStorage.StagePrefix), ct);
            await lease.CheckAsync();
            var set = storage.Publish(location, name);
            published = true;
            BackupCatalog.Register(row, set);
            row.Stage = BackupStage.ApplyingRetention;
            BackupMapping.Audit(db, storage.DeploymentId, completed, "BackupCompleted", row.RequestedBy, row.Id,
                after: new { row.TotalBytes, row.DurationMilliseconds, Verification = row.Verification.ToString() });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (!published && !ct.IsCancellationRequested)
        {
            var message = DescribeFailure(ex);
            logger.LogWarning("Backup {BackupId} failed in {Stage} ({FailureType}).", row.Id, row.Stage, FailureType(ex));
            FinishFailure(db, row, BackupOutcome.Failed, message, clock.GetUtcNow(), elapsed.ElapsedMilliseconds);
            await db.SaveChangesAsync(ct);
            return null;
        }
        finally
        {
            if (!published && staging is not null && location is not null && !ct.IsCancellationRequested)
            {
                try
                {
                    await lease.CheckAsync();
                    storage.RequireLocation(location.Name, location.Id);
                    BackupStorage.DeleteFiles(staging, [BackupStorage.ArchiveName, BackupStorage.ArtworkName, BackupStorage.ManifestName]);
                }
                catch (Exception ex) when (BackupStorage.IsFilesystemError(ex))
                {
                    logger.LogWarning("Staging for backup {BackupId} could not be cleaned up ({FailureType}).", row.Id, FailureType(ex));
                }
            }
        }

        try
        {
            await catalog.ApplyRetentionAsync(db, settings, row, lease);
            settings.RetentionIssue = null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            settings.RetentionIssue = row.RetentionDiagnostic = ex is BackupException known ? known.Message :
                "The new backup was completed. Automatic retention could not finish.";
            BackupMapping.Audit(db, storage.DeploymentId, clock.GetUtcNow(), "RetentionFailed", id: row.Id,
                after: new { row.RetentionDiagnostic });
            logger.LogWarning("Retention after backup {BackupId} did not finish ({FailureType}).", row.Id, FailureType(ex));
        }
        await StageAsync(db, row, BackupStage.Finished, ct);
        return BackupCompletion.For(row.Id, settings.Recipients);
    }

    private async Task VerifyAsync(AppDbContext db, BackupExecution row, BackupLease lease)
    {
        var ct = lease.Token;
        var actor = row.VerifyRequestedBy!;
        try { await permissions.DemandBackupRequesterAsync(db, actor, row.VerifyGeneration, [Keys.Verify], ct); }
        catch (AuthorizationDeniedException)
        {
            BackupMapping.Audit(db, storage.DeploymentId, clock.GetUtcNow(), "VerificationAuthorizationExpired", actor, row.Id);
            ClearVerificationRequest(row);
            await db.SaveChangesAsync(ct);
            return;
        }
        row.VerifyStartedAtUtc = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        try
        {
            var set = catalog.RequireSet(row);
            await BackupCatalog.VerifyChecksumsAsync(set, ct);
            await postgres.ValidateArchiveAsync(Path.Combine(set.Path, BackupStorage.ArchiveName), fullRead: true, ct);
            await lease.CheckAsync();
            row.Verification = BackupVerification.Verified;
            row.Availability = BackupAvailability.Available;
            row.VerificationDiagnostic = null;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            row.Verification = BackupVerification.Failed;
            row.VerificationDiagnostic = DescribeFailure(ex);
            row.Availability = ex switch
            {
                BackupException { Code: "StorageIdentity" or "DestinationChanged" or "UnapprovedDestination" } => BackupAvailability.Unavailable,
                BackupException { Code: "MissingSet" } => BackupAvailability.Missing,
                BackupException { Code: "Manifest" or "FileSize" or "Checksum" or "ArchiveRead" or "EmptyFile" or "UnsafePath" } =>
                    BackupAvailability.Corrupted,
                _ => row.Availability
            };
        }
        ClearVerificationRequest(row);
        row.VerifiedAtUtc = clock.GetUtcNow();
        BackupMapping.Audit(db, storage.DeploymentId, row.VerifiedAtUtc.Value,
            row.Verification == BackupVerification.Verified ? "VerificationPassed" : "VerificationFailed", actor, row.Id,
            after: new { row.VerificationDiagnostic });
        await db.SaveChangesAsync(ct);
    }

    private static void CheckSpace(BackupLocation location, long required)
    {
        if (BackupStorage.FreeBytes(location) is not { } free)
            throw new BackupException("CapacityUnknown", "The destination's free space could not be measured. The backup was not published.");
        if (free < required)
            throw new BackupException("Capacity", "The destination has insufficient free space for the backup and storage reserve.");
    }

    private static async Task StageAsync(AppDbContext db, BackupExecution row, BackupStage stage, CancellationToken ct)
    {
        row.Stage = stage;
        await db.SaveChangesAsync(ct);
    }

    private static void FinishFailure(AppDbContext db, BackupExecution row, BackupOutcome outcome, string diagnostic,
        DateTimeOffset now, long? duration = null)
    {
        row.Outcome = outcome;
        row.Diagnostic = diagnostic;
        row.FinishedAtUtc = now;
        row.DurationMilliseconds = duration ?? Math.Max(0, (long)(now - (row.StartedAtUtc ?? row.RequestedAtUtc)).TotalMilliseconds);
        row.Stage = BackupStage.Finished;
        BackupMapping.Audit(db, row.DeploymentId, now, outcome == BackupOutcome.Interrupted ? "BackupInterrupted" : "BackupFailed",
            row.RequestedBy, row.Id, after: new { row.Diagnostic, row.DurationMilliseconds });
    }

    private static void ClearVerificationRequest(BackupExecution row)
    {
        row.VerifyRequestedBy = null;
        row.VerifyGeneration = null;
        row.VerifyStartedAtUtc = null;
    }

    private static string DescribeFailure(Exception ex) => ex switch
    {
        BackupException known => known.Message,
        AuthorizationDeniedException => "The requesting account no longer has the required permission.",
        NpgsqlException => "The PostgreSQL connection or command failed.",
        UnauthorizedAccessException => "The service account could not access a required file or directory.",
        IOException => "A required backup file or directory could not be accessed.",
        _ => "The backup operation failed. Check the application logs for its execution identifier."
    };

    private static string FailureType(Exception ex) => ex is BackupException known ? known.Code : ex.GetType().Name;
}
