using System.Formats.Tar;
using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using MimeKit;
using Keys = Confast.Web.Features.Authorization.Permissions.DatabaseBackups;

namespace Confast.Web.Features.Backups;

internal sealed record BackupDestinationChoice(string Name, string? ServerPath);
internal sealed record BackupOverview(BackupSettings Settings, BackupStatus Status, IReadOnlyList<BackupDestinationChoice> Destinations, bool Configured);
internal sealed record BackupRecipientDraft(string Email, BackupNotice Notices);
internal sealed record BackupSettingsDraft(Guid ExpectedRevision, string? Destination, bool Enabled, bool CatchUp,
    bool FullVerification, int Retain, long ReserveBytes, IReadOnlyList<BackupRecipientDraft> Recipients,
    BackupSchedule? EditedSchedule, string? BrowserZone, DateTimeOffset ScheduleReferenceUtc);

internal sealed record BackupHistoryFilter(string? Search = null, BackupOutcome? Outcome = null, BackupTrigger? Trigger = null,
    bool? Pinned = null, BackupAvailability? Availability = null, DateTimeOffset? FromUtc = null, DateTimeOffset? UntilUtc = null,
    BackupHistorySort Sort = BackupHistorySort.Newest, int Page = 0);
internal sealed record BackupHistory(IReadOnlyList<BackupExecution> Records, int Total)
{
    internal const int PageSize = 30;
}
internal sealed record BackupDetails(BackupExecution Execution, IReadOnlyList<BackupAuditEntry> Events, bool LatestAvailable, string? RequesterName);

internal sealed class BackupManager(
    IDbContextFactory<AppDbContext> factory,
    ApplicationAuthorization authorization,
    BackupStorage storage,
    BackupCatalog catalog,
    BackupCoordinator coordinator,
    BackupHealth health,
    TimeProvider clock,
    IOptions<BackupHostOptions> options)
{
    private static readonly AuthorizationScope Area = new("DatabaseBackups");
    private static AuthorizationScope For(Guid id) => new("DatabaseBackups", id.ToString("D"));

    internal async Task<BackupOverview> GetOverviewAsync(CancellationToken ct = default)
    {
        using var operation = await authorization.BeginAsync(Keys.View, Area, [Keys.View], ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var settingsQuery = db.BackupSettings.AsNoTracking();
        if (operation.Snapshot.Has(Keys.Configure)) settingsQuery = settingsQuery.Include(x => x.Recipients);
        var settings = await settingsQuery.SingleAsync(ct);
        var destinations = storage.Destinations.Select(name =>
            new BackupDestinationChoice(name, operation.Snapshot.Has(Keys.Configure) ? storage.Approved(name).Path : null)).ToArray();
        var configured = storage.Configured && settings.DeploymentId == storage.DeploymentId && settings.DestinationId.HasValue;
        return new(settings, await health.ReadAsync(db, settings, ct), destinations, configured);
    }

    internal async Task<BackupHistory> GetHistoryAsync(BackupHistoryFilter filter, CancellationToken ct = default)
    {
        using var operation = await authorization.BeginAsync(Keys.View, Area, [Keys.View], ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var rows = db.BackupExecutions.AsNoTracking().Where(x => x.DeploymentId == storage.DeploymentId);
        if (filter.Outcome.HasValue) rows = rows.Where(x => x.Outcome == filter.Outcome);
        if (filter.Trigger.HasValue) rows = rows.Where(x => x.Trigger == filter.Trigger);
        if (filter.Pinned.HasValue) rows = rows.Where(x => x.Pinned == filter.Pinned && x.Outcome == BackupOutcome.Completed);
        if (filter.Availability.HasValue) rows = rows.Where(x => x.Availability == filter.Availability);
        if (filter.FromUtc.HasValue) rows = rows.Where(x => x.RequestedAtUtc >= filter.FromUtc);
        if (filter.UntilUtc.HasValue) rows = rows.Where(x => x.RequestedAtUtc < filter.UntilUtc);
        if (filter.Search?.Trim() is { Length: > 0 } input)
        {
            if (input.Length > 160) throw new BackupException("SearchLength", "Use up to 160 characters to search backup history.");
            var pattern = "%" + input.Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_") + "%";
            if (Guid.TryParse(input, out var id)) rows = rows.Where(x => x.Id == id);
            else rows = rows.Where(x => EF.Functions.ILike(x.Label ?? "", pattern) ||
                EF.Functions.ILike(x.Notes ?? "", pattern) || EF.Functions.ILike(x.Destination, pattern));
        }
        var count = await rows.CountAsync(ct);
        var sorted = filter.Sort switch
        {
            BackupHistorySort.Oldest => rows.OrderBy(x => x.RequestedAtUtc),
            BackupHistorySort.Largest => rows.OrderByDescending(x => x.TotalBytes ?? 0).ThenByDescending(x => x.RequestedAtUtc),
            BackupHistorySort.Longest => rows.OrderByDescending(x => x.DurationMilliseconds ?? 0).ThenByDescending(x => x.RequestedAtUtc),
            _ => rows.OrderByDescending(x => x.RequestedAtUtc)
        };
        var page = Math.Clamp(filter.Page, 0, Math.Max(0, (count - 1) / BackupHistory.PageSize));
        var items = await sorted.ThenBy(x => x.Id).Skip(page * BackupHistory.PageSize).Take(BackupHistory.PageSize).ToListAsync(ct);
        return new(items, count);
    }

    internal async Task<BackupDetails> GetDetailsAsync(Guid id, CancellationToken ct = default)
    {
        using var operation = await authorization.BeginAsync(Keys.View, For(id), [Keys.View], ct);
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await FindAsync(db, id, ct);
        var events = await db.BackupAudit.AsNoTracking().Where(x => x.DeploymentId == storage.DeploymentId && x.ExecutionId == id)
            .OrderByDescending(x => x.OccurredAtUtc).ThenByDescending(x => x.Id).Take(100).ToListAsync(ct);
        var requester = row.RequestedBy is null ? null :
            await db.Users.Where(x => x.Id == row.RequestedBy).Select(x => x.DisplayName).SingleOrDefaultAsync(ct);
        return new(row, events, await IsLatestAsync(db, id, ct), requester);
    }

    internal async Task<BackupDestinationTest> TestDestinationAsync(string destination, CancellationToken ct = default)
    {
        using var operation = await authorization.BeginAsync(Keys.Configure, Area, [Keys.Configure], ct);
        ct.ThrowIfCancellationRequested();
        return storage.Test(destination);
    }

    internal async Task SaveSettingsAsync(BackupSettingsDraft draft, CancellationToken ct = default)
    {
        using var operation = await authorization.BeginAsync(Keys.Configure, Area, [Keys.Configure], ct);
        if (draft.Retain is < 1 or > 1000 || draft.ReserveBytes is < 0 or > 10995116277760L)
            throw new BackupException("RetentionSettings", "Enter a retention count of 1–1,000 and a reserve between 0 and 10 TiB.");
        var recipients = ValidateRecipients(draft.Recipients);
        await using var lease = await RequireLeaseAsync(ct);
        await using var db = await factory.CreateDbContextAsync(lease.Token);
        var settings = await db.BackupSettings.Include(x => x.Recipients).SingleAsync(lease.Token);
        if (settings.Revision != draft.ExpectedRevision)
            throw new BackupException("SettingsConflict", "The backup configuration changed. Reload it before saving your edits.");
        var now = clock.GetUtcNow();
        var location = draft.Destination is { Length: > 0 } name ? storage.Approved(name) : null;
        if (draft.Enabled && location is null)
            throw new BackupException("DestinationRequired", "Select a server destination before enabling automatic backups.");
        var before = SettingsAudit(settings);
        if (location is not null && (settings.DestinationId != location.Id || draft.Enabled || settings.DeploymentId != storage.DeploymentId))
            storage.Test(location.Name);
        var schedule = settings.Schedule;
        if (draft.EditedSchedule is { } edited)
        {
            edited.Validate();
            if (draft.ScheduleReferenceUtc.Year < 1900 || draft.ScheduleReferenceUtc.Year > 9900)
                throw new BackupException("Schedule", "Reload the schedule preview before saving.");
            var browser = BackupSchedule.BrowserZone(draft.BrowserZone, TimeZoneInfo.Local, out _);
            schedule = edited.InZone(browser, TimeZoneInfo.Local, draft.ScheduleReferenceUtc);
        }
        schedule.Validate();
        if (settings.Schedule != schedule || settings.Enabled != draft.Enabled || settings.ServerZoneId != TimeZoneInfo.Local.Id ||
            settings.DeploymentId != storage.DeploymentId)
        {
            settings.ScheduleStartedAtUtc = draft.Enabled ? now : null;
            settings.NextDueAtUtc = draft.Enabled ? schedule.Next(now, TimeZoneInfo.Local) : null;
        }

        settings.Revision = Guid.NewGuid();
        settings.DeploymentId = storage.DeploymentId;
        settings.Destination = location?.Name;
        settings.DestinationId = location?.Id;
        settings.Enabled = draft.Enabled;
        settings.CatchUp = draft.CatchUp;
        settings.FullVerification = draft.FullVerification;
        settings.Retain = draft.Retain;
        settings.ReserveBytes = draft.ReserveBytes;
        var previousRecipients = settings.Recipients.ToDictionary(x => x.Email, StringComparer.OrdinalIgnoreCase);
        foreach (var (email, notices) in recipients)
        {
            if (previousRecipients.Remove(email, out var existing)) existing.Notices = notices;
            else settings.Recipients.Add(new() { Email = email, Notices = notices });
        }
        foreach (var removed in previousRecipients.Values) settings.Recipients.Remove(removed);
        settings.Schedule = schedule;
        settings.ServerZoneId = TimeZoneInfo.Local.Id;
        settings.RetentionIssue = null;
        BackupMapping.Audit(db, storage.DeploymentId, now, "ConfigurationSaved", operation.ActorUserId,
            before: before, after: SettingsAudit(settings));

        await lease.CheckAsync();
        var generation = await db.AuthorizationState.Select(x => x.InstallationGeneration).SingleAsync(lease.Token);
        if (generation != operation.Snapshot.Version.Generation)
            throw new AuthorizationDeniedException(AuthorizationDenial.InvalidState);
        // Commit the external authority first. A failure on either side leaves
        // distinct revisions and therefore blocks automatic deletion.
        storage.WritePolicy(new(storage.DeploymentId, generation, settings.Revision, location?.Id ?? Guid.Empty,
            settings.Retain, RecoveryHold: false));
        await db.SaveChangesAsync(lease.Token);
    }

    internal async Task<Guid> RequestBackupAsync(bool pinned, string? label, string? notes, CancellationToken ct = default)
    {
        using var operation = await authorization.BeginAsync(Keys.Create, Area, CreatePermissions(pinned), ct);
        (label, notes) = ValidateMetadata(label, notes);
        if (!storage.Configured) throw new BackupException("StorageConfiguration", "Backup storage has not been provisioned.");
        await using var db = await factory.CreateDbContextAsync(ct);
        var settings = await db.BackupSettings.AsNoTracking().SingleAsync(ct);
        if (settings.DeploymentId != storage.DeploymentId || settings.Destination is null || settings.DestinationId is null)
            throw new BackupException("DestinationRequired", "Save a backup destination before creating a backup.");
        var now = clock.GetUtcNow();
        var row = NewRequest(settings, now, operation.ActorUserId, operation.Snapshot.Version.Generation);
        (row.Pinned, row.Label, row.Notes) = (pinned, label, notes);
        db.BackupExecutions.Add(row);
        BackupMapping.Audit(db, storage.DeploymentId, now, "BackupRequested", operation.ActorUserId, row.Id,
            after: new { row.Pinned, row.Label, row.Notes, row.Destination });
        await db.SaveChangesAsync(ct);
        return row.Id;
    }

    internal async Task RequestReconciliationAsync(CancellationToken ct = default)
    {
        using var operation = await authorization.BeginAsync(Keys.Reconcile, Area, [Keys.Reconcile], ct);
        await using var lease = await RequireLeaseAsync(ct);
        await using var db = await factory.CreateDbContextAsync(lease.Token);
        var settings = await db.BackupSettings.SingleAsync(lease.Token);
        settings.ReconcileRequestedBy = operation.ActorUserId;
        settings.ReconcileGeneration = operation.Snapshot.Version.Generation;
        BackupMapping.Audit(db, storage.DeploymentId, clock.GetUtcNow(), "ReconciliationRequested", operation.ActorUserId);
        await db.SaveChangesAsync(lease.Token);
    }

    internal async Task RequestVerificationAsync(Guid id, CancellationToken ct = default)
    {
        using var operation = await authorization.BeginAsync(Keys.Verify, For(id), [Keys.Verify], ct);
        await using var lease = await RequireLeaseAsync(ct);
        await using var db = await factory.CreateDbContextAsync(lease.Token);
        var row = await FindAsync(db, id, lease.Token);
        if (row.Outcome != BackupOutcome.Completed || row.IsRetired)
            throw new BackupException("NoBackupSet", "There is no managed backup set to verify.");
        if (row.VerifyRequestedBy is not null) return;
        row.VerifyRequestedBy = operation.ActorUserId;
        row.VerifyGeneration = operation.Snapshot.Version.Generation;
        BackupMapping.Audit(db, storage.DeploymentId, clock.GetUtcNow(), "VerificationRequested", operation.ActorUserId, id);
        await db.SaveChangesAsync(lease.Token);
    }

    internal async Task SetPinnedAsync(Guid id, Guid version, bool pin, bool confirmedLatest, CancellationToken ct = default)
    {
        using var operation = await authorization.BeginAsync(Keys.Pin, For(id), [Keys.Pin], ct);
        await using var lease = await RequireLeaseAsync(ct);
        await using var db = await factory.CreateDbContextAsync(lease.Token);
        var row = await FindAsync(db, id, lease.Token);
        if (!pin && !confirmedLatest && await IsLatestAsync(db, id, lease.Token)) throw ConfirmLatest();
        await catalog.UpdateMetadataAsync(db, row, lease, operation.ActorUserId, version, pin, null, null);
    }

    internal async Task EditMetadataAsync(Guid id, Guid version, string? label, string? notes, CancellationToken ct = default)
    {
        using var operation = await authorization.BeginAsync(Keys.EditMetadata, For(id), [Keys.EditMetadata], ct);
        (label, notes) = ValidateMetadata(label, notes);
        await using var lease = await RequireLeaseAsync(ct);
        await using var db = await factory.CreateDbContextAsync(lease.Token);
        var row = await FindAsync(db, id, lease.Token);
        await catalog.UpdateMetadataAsync(db, row, lease, operation.ActorUserId, version, null, label, notes);
    }

    internal async Task DeleteAsync(Guid id, Guid version, bool confirmed, bool confirmedLatest, CancellationToken ct = default)
    {
        using var operation = await authorization.BeginAsync(Keys.Delete, For(id), [Keys.Delete], ct);
        if (!confirmed) throw new BackupException("ConfirmDeletion", "Confirm that you want to permanently delete this backup.");
        await using var lease = await RequireLeaseAsync(ct);
        await using var db = await factory.CreateDbContextAsync(lease.Token);
        var row = await FindAsync(db, id, lease.Token);
        if (row.MetadataVersion != version) throw BackupCatalog.MetadataConflict();
        if (!confirmedLatest && await IsLatestAsync(db, id, lease.Token)) throw ConfirmLatest();
        await catalog.RemoveAsync(db, row, lease, automatic: false, operation.ActorUserId);
    }

    private async Task DownloadAsync(Guid id, HttpContext http)
    {
        var operation = BusinessHttpAuthorization.Require(http, Keys.Download, For(id), Keys.Download);
        using var timeLimit = CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted);
        timeLimit.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(options.Value.DownloadTimeoutMinutes, 1, 240)));
        try
        {
            await using var lease = await RequireLeaseAsync(timeLimit.Token);
            await using var db = await factory.CreateDbContextAsync(lease.Token);
            var row = await FindAsync(db, id, lease.Token);
            if (row.Availability != BackupAvailability.Available)
                throw new BackupException("UnavailableSet", "The backup set is not available for download.");
            var set = catalog.RequireSet(row);
            BackupStorage.CheckSetFiles(set);
            await lease.CheckAsync();
            BackupMapping.Audit(db, storage.DeploymentId, clock.GetUtcNow(), "DownloadStarted", operation.ActorUserId, id);
            await db.SaveChangesAsync(lease.Token);
            http.Response.ContentType = "application/x-tar";
            http.Response.Headers.CacheControl = "private, no-store";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            var disposition = new ContentDispositionHeaderValue("attachment");
            disposition.SetHttpFileName(set.Manifest.DirectoryName + ".tar");
            http.Response.Headers.ContentDisposition = disposition.ToString();
            await using (var tar = new TarWriter(http.Response.Body, TarEntryFormat.Pax, leaveOpen: true))
            {
                foreach (var file in set.Manifest.Files.Select(x => x.Name).Append(BackupStorage.ManifestName))
                {
                    await using var data = new FileStream(Path.Combine(set.Path, file), FileMode.Open, FileAccess.Read,
                        FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, file)
                    {
                        DataStream = data, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite
                    }, lease.Token);
                }
            }
            BackupMapping.Audit(db, storage.DeploymentId, clock.GetUtcNow(), "DownloadStreamed", operation.ActorUserId, id);
            await db.SaveChangesAsync(lease.Token);
        }
        catch (Exception ex) when (BackupStorage.IsFilesystemError(ex) || ex is OperationCanceledException)
        {
            if (http.Response.HasStarted || http.RequestAborted.IsCancellationRequested) { http.Abort(); return; }
            await Results.Problem(statusCode: ex is BackupException { Code: "NotFound" } ? 404 : 409,
                detail: ex is BackupException known ? known.Message : "The backup set could not be read.").ExecuteAsync(http);
        }
    }

    internal static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/admin/backups/{id:guid}/download", (Guid id, HttpContext context, BackupManager manager) =>
            manager.DownloadAsync(id, context))
            .RequireAuthorization(AuthorizationRegistration.PolicyName(Keys.Download))
            .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = IdentityConstants.ApplicationScheme })
            .WithMetadata(new BusinessHttpBoundary(Keys.Download, Keys.Download,
                context => For(Guid.Parse((string)context.Request.RouteValues["id"]!))));
    }

    internal static BackupExecution NewRequest(BackupSettings settings, DateTimeOffset now, string? requester, Guid? generation) =>
        new()
        {
            Id = Guid.NewGuid(), DeploymentId = settings.DeploymentId!.Value, Destination = settings.Destination!,
            DestinationId = settings.DestinationId!.Value, SettingsRevision = settings.Revision, RequestedAtUtc = now,
            RequestedBy = requester, RequestedGeneration = generation, Trigger = BackupTrigger.Manual,
            Outcome = BackupOutcome.Pending, Stage = BackupStage.Queued
        };

    internal static string[] CreatePermissions(bool pinned) => pinned ? [Keys.Create, Keys.Pin] : [Keys.Create];

    private async Task<BackupLease> RequireLeaseAsync(CancellationToken ct) =>
        await coordinator.TryAcquireAsync(ct) ?? throw new BackupException("Busy", "Another backup operation is in progress. Try again when it finishes.");

    private async Task<BackupExecution> FindAsync(AppDbContext db, Guid id, CancellationToken ct) =>
        await db.BackupExecutions.SingleOrDefaultAsync(x => x.Id == id && x.DeploymentId == storage.DeploymentId, ct)
        ?? throw new BackupException("NotFound", "The backup execution could not be found.");

    private async Task<bool> IsLatestAsync(AppDbContext db, Guid id, CancellationToken ct) =>
        id == await db.BackupExecutions.Where(x => x.DeploymentId == storage.DeploymentId &&
                x.Outcome == BackupOutcome.Completed && x.Availability == BackupAvailability.Available)
            .OrderByDescending(x => x.FinishedAtUtc).ThenByDescending(x => x.Id)
            .Select(x => (Guid?)x.Id).FirstOrDefaultAsync(ct);

    private static BackupException ConfirmLatest() =>
        new("ConfirmLatest", "This is the latest available backup. Confirm that you want to change its protection.");

    private static (string? Label, string? Notes) ValidateMetadata(string? label, string? notes)
    {
        label = string.IsNullOrWhiteSpace(label) ? null : label.Trim();
        notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (label?.Length > 160 || notes?.Length > 2000 ||
            label?.Any(char.IsControl) == true || notes?.Contains('\0') == true)
            throw new BackupException("Metadata", "Use a label up to 160 characters and notes up to 2,000 characters.");
        return (label, notes);
    }

    private static Dictionary<string, BackupNotice> ValidateRecipients(IReadOnlyList<BackupRecipientDraft> recipients)
    {
        if (recipients.Count > 20) throw new BackupException("Recipients", "Enter up to 20 email recipients.");
        var addresses = new Dictionary<string, BackupNotice>(StringComparer.OrdinalIgnoreCase);
        foreach (var recipient in recipients)
        {
            var email = recipient.Email.Trim();
            if (email.Length is 0 or > 254 || !MailboxAddress.TryParse(email, out var mailbox) || mailbox.Address != email)
                throw new BackupException("Recipients", "Enter a valid email address for each backup recipient.");
            if ((recipient.Notices & ~BackupNotice.All) != 0)
                throw new BackupException("Notices", "Select valid notification types for each backup recipient.");
            if (!addresses.TryAdd(email, recipient.Notices))
                throw new BackupException("Recipients", "Enter each backup recipient’s email address once.");
        }
        return addresses;
    }

    private static object SettingsAudit(BackupSettings value) => new
    {
        value.DeploymentId, value.Destination, value.DestinationId, value.Enabled, value.CatchUp, value.FullVerification,
        value.Retain, value.ReserveBytes, Recipients = value.Recipients.OrderBy(x => x.Email, StringComparer.OrdinalIgnoreCase)
            .Select(x => new { x.Email, Notices = x.Notices.ToString() }).ToArray(),
        Frequency = value.Frequency.ToString(), value.LocalTime, value.Hours, value.Weekdays, value.MonthDay, value.MonthShift,
        value.HourAnchor, value.ServerZoneId
    };
}
