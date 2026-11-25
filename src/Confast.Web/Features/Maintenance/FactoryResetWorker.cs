using Confast.Web.Data;
using Confast.Web.Features.Backups;
using Confast.Web.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Confast.Web.Features.Maintenance;

internal sealed class FactoryResetExecutor(FactoryResetStore store, MaintenanceGate gate,
    BackupStorage backups, BackupCoordinator backupCoordinator, FactoryResetStorage storage,
    IOptionsMonitor<DatabaseResetOptions> options, IServiceScopeFactory scopes, IConfiguration configuration,
    DevelopmentDateOverrideTimeProvider developmentClock, ILogger<FactoryResetExecutor> logger)
{
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        if (!store.Configured || (await gate.ReadAsync(ct)).Phase is null) return;
        await using var connection = await store.OpenAsync(ct);
        await using (var command = new NpgsqlCommand(MaintenanceGate.TryExclusive, connection))
        {
            if (await command.ExecuteScalarAsync(ct) is not true) return;
        }
        var operation = await FactoryResetStore.ActiveAsync(connection, ct);
        if (operation is null || operation.DeploymentId != backups.DeploymentId) return;
        await using var lease = await AcquireBackupLeaseAsync(connection, operation, ct);
        if (lease is null) return;
        var token = lease.Token;

        try
        {
            using var files = storage.TryLockStorage();
            if (files is null) return;
            if (operation.Phase == FactoryResetPhase.Draining)
            {
                await StartAsync(connection, operation, lease);
            }
            else if (operation.Phase is FactoryResetPhase.BackingUp or FactoryResetPhase.Resetting)
            {
                await AbortAsync(connection, operation, "InterruptedBeforeCommit", token);
                return;
            }
            else
            {
                storage.Log(operation, operation.Phase.ToString(), "ResumingAfterDatabaseCommit");
            }

            var committed = await FactoryResetStore.ActiveAsync(connection, token)
                ?? throw new InvalidOperationException("The committed Factory Reset journal is missing.");
            await FinishAsync(connection, committed, lease);
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            var failure = ex switch
            {
                BackupException backup => backup.Code,
                FactoryResetException known => known.Code,
                PostgresException postgres => postgres.SqlState,
                _ => ex.GetType().Name
            };
            logger.LogWarning("Factory Reset {ResetId} encountered {Failure}. Checking the durable commit state.", operation.Id, failure);

            // The durable phase, on the same exclusive connection, resolves even a
            // lost commit response. A lost connection leaves the decision to recovery.
            var current = await FactoryResetStore.ActiveAsync(connection, token);
            if (current?.Id != operation.Id) return;
            if (current.Phase is FactoryResetPhase.Draining or FactoryResetPhase.BackingUp or FactoryResetPhase.Resetting)
                await AbortAsync(connection, current, failure, token);
            else
                storage.Log(current, current.Phase.ToString(), failure);
        }
    }

    private async Task<BackupLease?> AcquireBackupLeaseAsync(NpgsqlConnection connection, FactoryResetOperation operation, CancellationToken ct)
    {
        try { return await backupCoordinator.TryAcquireAsync(ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested && BackupStorage.IsFilesystemError(ex) &&
            operation.Phase is FactoryResetPhase.Draining or FactoryResetPhase.BackingUp or FactoryResetPhase.Resetting)
        {
            await AbortAsync(connection, operation, ex is BackupException backup ? backup.Code : ex.GetType().Name, ct);
            return null;
        }
    }

    private async Task StartAsync(NpgsqlConnection connection, FactoryResetOperation operation, BackupLease lease)
    {
        var ct = lease.Token;
        storage.Log(operation, nameof(FactoryResetPhase.Draining));
        DemandEnabled();
        var storageId = storage.Prepare();
        await FactoryResetStore.AdvanceAsync(connection, operation, FactoryResetPhase.BackingUp, ct, storageId);
        operation = operation with { Phase = FactoryResetPhase.BackingUp, StorageId = storageId };
        storage.Log(operation, nameof(FactoryResetPhase.BackingUp));

        await using var scope = scopes.CreateAsyncScope();
        await using var backupDb = OpenBackupContext();
        var plan = FactoryResetPlan.Create(backupDb);
        var processor = scope.ServiceProvider.GetRequiredService<BackupProcessor>();
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await FactoryResetStore.ResetDatabaseAsync(connection, transaction, operation, plan, prepare: true, ct);
        var set = await processor.CreatePreResetAsync(backupDb, lease, operation.BackupId, operation.ActorId,
            operation.Generation, operation.RequestedAt, operation.Id);
        storage.Log(operation, "BackupVerifiedAndPinned", set.Manifest.Id.ToString("D"));

        DemandEnabled();
        await lease.CheckAsync();
        await using (var progress = await store.OpenAsync(ct))
            await FactoryResetStore.AdvanceAsync(progress, operation, FactoryResetPhase.Resetting, ct);
        storage.Log(operation, nameof(FactoryResetPhase.Resetting));
        await FactoryResetStore.ResetDatabaseAsync(connection, transaction, operation, plan, prepare: false, ct);
        await lease.CheckAsync();
        await transaction.CommitAsync(ct);
    }

    private async Task FinishAsync(NpgsqlConnection connection, FactoryResetOperation operation, BackupLease lease)
    {
        var ct = lease.Token;
        await lease.CheckAsync();
        if (operation.Phase == FactoryResetPhase.CleaningStorage)
        {
            storage.Log(operation, nameof(FactoryResetPhase.CleaningStorage), "DatabaseCommitted");
            storage.Clear(operation);
            await FactoryResetStore.AdvanceAsync(connection, operation, FactoryResetPhase.Verifying, ct);
            operation = operation with { Phase = FactoryResetPhase.Verifying };
        }

        storage.VerifyEmpty(operation);
        var status = await gate.ReadAsync(ct);
        if (status.Generation == operation.Generation)
            throw new InvalidOperationException("The installation generation did not change.");

        await using var db = OpenBackupContext();
        if (await db.Users.CountAsync(ct) != 1 ||
            !await db.Users.AnyAsync(x => x.Id == operation.ActorId && x.IsActive, ct))
            throw new InvalidOperationException("Root account verification failed.");
        var backup = await db.BackupExecutions.AsNoTracking().SingleAsync(x => x.Id == operation.BackupId, ct);
        if (backup is not { DirectoryName: { } name, Pinned: true, Outcome: BackupOutcome.Completed, Verification: BackupVerification.Verified })
            throw new InvalidOperationException("The pinned pre-reset backup is absent from the catalog.");
        var set = backups.ReadSet(backups.RequireLocation(backup.Destination, backup.DestinationId), name);
        if (!set.Manifest.Pinned || !set.Manifest.FullArchiveRead || set.Manifest.Id != operation.BackupId)
            throw new InvalidOperationException("The pre-reset backup manifest does not match the verified catalog entry.");
        await BackupCatalog.VerifyChecksumsAsync(set, ct);
        if (operation.Phase == FactoryResetPhase.Verifying)
        {
            storage.Log(operation, nameof(FactoryResetPhase.Verifying), "DatabaseDefaultsStorageAndBackupVerified");
            await FactoryResetStore.AdvanceAsync(connection, operation, FactoryResetPhase.Finalizing, ct);
            operation = operation with { Phase = FactoryResetPhase.Finalizing };
        }

        developmentClock.UseSystemDate();
        await lease.CheckAsync();
        storage.Log(operation, nameof(FactoryResetPhase.Completed), "SessionsInvalidated");
        await FactoryResetStore.AdvanceAsync(connection, operation, FactoryResetPhase.Completed, ct);
        logger.LogInformation("Factory Reset {ResetId} completed with pinned backup {BackupId}.", operation.Id, operation.BackupId);
    }

    private async Task AbortAsync(NpgsqlConnection connection, FactoryResetOperation operation, string failure, CancellationToken ct)
    {
        try { storage.Log(operation, nameof(FactoryResetPhase.Aborted), failure); }
        catch (Exception ex) when (BackupStorage.IsFilesystemError(ex))
        {
            logger.LogWarning("Factory Reset {ResetId} aborted before commit ({Failure}). The operational log location was unavailable.",
                operation.Id, failure);
        }
        await FactoryResetStore.AdvanceAsync(connection, operation, FactoryResetPhase.Aborted, ct);
    }

    // This context uses the ordinary limited runtime login for backup catalog
    // access. It participates in the worker's existing exclusive lease.
    private AppDbContext OpenBackupContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(configuration.GetConnectionString("Confast")).Options);

    private void DemandEnabled()
    {
        if (!options.CurrentValue.Enabled)
            throw new FactoryResetException("Disabled", "Factory Reset is disabled by deployment configuration.");
    }
}

internal sealed class FactoryResetWorker(FactoryResetExecutor executor, ILogger<FactoryResetWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await executor.RunOnceAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Factory Reset coordination could not finish ({FailureType}).", ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
