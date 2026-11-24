using Confast.Web.Data;
using Confast.Web.Features.Inspections;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Backups;

internal sealed class BackupWorker(IServiceScopeFactory scopes, BackupStorage storage, ILogger<BackupWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        storage.Configured
            ? Task.WhenAll(ProcessAsync(stoppingToken), MonitorAsync(stoppingToken))
            : Task.CompletedTask;

    private Task ProcessAsync(CancellationToken ct)
    {
        var first = true;
        return RepeatAsync("execution", TimeSpan.FromSeconds(5), async (services, token) =>
        {
            var completed = await services.GetRequiredService<BackupProcessor>().RunOnceAsync(token, forceReconcile: first);
            first = false;
            if (completed is not null)
                await services.GetRequiredService<BackupNotifications>().SendCompletionAsync(completed, token);
        }, ct);
    }

    private Task MonitorAsync(CancellationToken ct) =>
        RepeatAsync("health monitoring", TimeSpan.FromSeconds(45), (services, token) =>
            services.GetRequiredService<BackupNotifications>().CheckAsync(token), ct);

    private async Task RepeatAsync(string operation, TimeSpan interval, Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken ct)
    {
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await work(scope.ServiceProvider, ct);
                failures = 0;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (++failures == 1 || failures % 12 == 0)
                    logger.LogWarning("Backup {Operation} could not finish ({FailureType}).", operation,
                        ex is BackupException backup ? backup.Code : ex.GetType().Name);
            }
            try { await Task.Delay(failures == 0 ? interval : TimeSpan.FromSeconds(45), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        }
    }
}

internal sealed record BackupAlertClaim(BackupNotice Kind, string? Message, Guid Id);

internal sealed class BackupNotifications(
    IDbContextFactory<AppDbContext> factory,
    BackupStorage storage,
    BackupHealth health,
    SmtpEmailService email,
    TimeProvider clock,
    ILogger<BackupNotifications> logger)
{
    private static readonly Dictionary<BackupNotice, string> Titles = new()
    {
        [BackupNotice.Failure] = "Backup execution",
        [BackupNotice.Integrity] = "Backup integrity",
        [BackupNotice.Storage] = "Storage availability",
        [BackupNotice.Capacity] = "Storage capacity",
        [BackupNotice.Overdue] = "Backup schedule",
        [BackupNotice.Retention] = "Retention",
        [BackupNotice.Catalog] = "Backup catalog"
    };

    internal async Task CheckAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var now = clock.GetUtcNow();
        Dictionary<string, List<BackupAlertClaim>> pending;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            // This lock is independent of the storage lock, so a storage outage
            // can still be reported and two app instances cannot send the same alert.
            if (!await db.Database.SqlQueryRaw<bool>("SELECT pg_try_advisory_xact_lock(1703373, 841537) AS \"Value\"")
                    .SingleAsync(ct))
                return;
            var settings = await db.BackupSettings.AsNoTracking().Include(x => x.Recipients).SingleAsync(ct);
            if (settings.DeploymentId != storage.DeploymentId) return;
            var status = await health.ReadAsync(db, settings, ct);
            pending = await ClaimAlertsAsync(db, settings, status.Warnings, now, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        await Parallel.ForEachAsync(pending, DeliveryOptions(ct), async (delivery, token) =>
        {
            var (recipient, alerts) = delivery;
            var body = string.Join(Environment.NewLine + Environment.NewLine, alerts.Select(x =>
                $"{Titles[x.Kind]} — {x.Message ?? "Resolved."}"));
            if (!await SendAsync("confastDB backup health", body, recipient, token)) return;
            await using var deliveryDb = await factory.CreateDbContextAsync(token);
            foreach (var (kind, message, claim) in alerts)
            {
                var remindAfter = message is null ? (DateTimeOffset?)null : clock.GetUtcNow().AddDays(1);
                await deliveryDb.BackupAlerts.Where(x => x.DeploymentId == storage.DeploymentId && x.RecipientEmail == recipient &&
                        x.Kind == kind && x.DeliveryClaim == claim)
                    .ExecuteUpdateAsync(row => row.SetProperty(x => x.LastNotifiedMessage, message)
                        .SetProperty(x => x.NextDeliveryAtUtc, remindAfter).SetProperty(x => x.DeliveryClaim, (Guid?)null), token);
            }
        });
    }

    // Called inside the monitor's transaction and advisory lock.
    internal static async Task<Dictionary<string, List<BackupAlertClaim>>> ClaimAlertsAsync(AppDbContext db,
        BackupSettings settings, IReadOnlyDictionary<BackupNotice, string> warnings, DateTimeOffset now, CancellationToken ct)
    {
        var deployment = settings.DeploymentId!.Value;
        var entries = await db.BackupAlerts.Where(x => x.DeploymentId == deployment)
            .ToDictionaryAsync(x => (x.RecipientEmail, x.Kind), ct);
        var pending = new Dictionary<string, List<BackupAlertClaim>>();
        foreach (var recipient in settings.Recipients)
        {
            foreach (var kind in Titles.Keys)
            {
                entries.TryGetValue((recipient.Email, kind), out var entry);
                if (!recipient.Notices.HasFlag(kind))
                {
                    if (entry is not null) db.BackupAlerts.Remove(entry);
                    continue;
                }
                warnings.TryGetValue(kind, out var message);
                if (entry is null)
                {
                    if (message is null) continue;
                    entry = new BackupAlert { DeploymentId = deployment, RecipientEmail = recipient.Email, Kind = kind };
                    db.BackupAlerts.Add(entry);
                }
                entry.Message = message;
                var changedSinceDelivery = (entry.LastNotifiedMessage is null) != (message is null);
                if (entry.DeliveryClaim.HasValue && entry.NextDeliveryAtUtc > now ||
                    entry.LastNotifiedMessage is null && message is null ||
                    !changedSinceDelivery && entry.NextDeliveryAtUtc > now)
                    continue;

                var claim = Guid.NewGuid();
                entry.DeliveryClaim = claim;
                entry.NextDeliveryAtUtc = now.AddHours(1);
                if (!pending.TryGetValue(recipient.Email, out var claims)) pending[recipient.Email] = claims = [];
                claims.Add(new(kind, message, claim));
            }
        }
        return pending;
    }

    internal Task SendCompletionAsync(BackupCompletion result, CancellationToken ct) =>
        Parallel.ForEachAsync(result.Recipients, DeliveryOptions(ct), async (recipient, token) =>
        {
            await SendAsync("confastDB backup completed",
                $"Backup {result.Id:D} was published. See Backup Manager for its verification and retention results.",
                recipient, token);
        });

    private static ParallelOptions DeliveryOptions(CancellationToken ct) =>
        new() { CancellationToken = ct, MaxDegreeOfParallelism = 4 };

    private async Task<bool> SendAsync(string subject, string body, string recipient, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await email.SendApplicationNoticeAsync(subject, body, [recipient], timeout.Token);
            return true;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Backup notification could not be sent ({FailureType}).", ex.GetType().Name);
            return false;
        }
    }
}
