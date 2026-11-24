using Npgsql;

namespace Confast.Web.Features.Backups;

internal sealed class BackupCoordinator(BackupStorage storage, IConfiguration configuration)
{
    internal async Task<BackupLease?> TryAcquireAsync(CancellationToken ct)
    {
        var file = storage.TryLockControl();
        if (file is null) return null;
        NpgsqlConnection? connection = null;
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Confast"))
            {
                Pooling = false, Enlist = false, Timeout = 5, CommandTimeout = 5, CancellationTimeout = 1000,
                ApplicationName = "confastDB Backup Coordinator"
            };
            connection = new(builder.ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand("SELECT pg_try_advisory_lock(1703373, 841536)", connection);
            if (await command.ExecuteScalarAsync(ct) is not true) return null;
            var lease = new BackupLease(storage, file, connection, ct);
            connection = null;
            file = null;
            return lease;
        }
        finally
        {
            try
            {
                if (connection is not null) await connection.DisposeAsync();
            }
            finally { file?.Dispose(); }
        }
    }
}

// The shared file lock fences filesystem changes even if the database drops its
// session lock first. Never resume a lost lease or publish from another connection.
internal sealed class BackupLease : IAsyncDisposable
{
    private readonly BackupStorage storage;
    private readonly FileStream file;
    private readonly NpgsqlConnection connection;
    private readonly CancellationTokenSource cancelled;
    private readonly SemaphoreSlim commands = new(1);
    private readonly Task heartbeat;

    internal BackupLease(BackupStorage storage, FileStream file, NpgsqlConnection connection, CancellationToken ct)
    {
        this.storage = storage;
        this.file = file;
        this.connection = connection;
        cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        heartbeat = WatchAsync();
    }

    internal CancellationToken Token => cancelled.Token;

    internal async Task CheckAsync()
    {
        await commands.WaitAsync(Token);
        try
        {
            storage.CheckControl();
            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(Token);
            Token.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or IOException)
        {
            await cancelled.CancelAsync();
            throw new BackupException("OwnershipLost", "Backup execution ownership was lost.");
        }
        finally { commands.Release(); }
    }

    private async Task WatchAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(Token)) await CheckAsync();
        }
        catch (OperationCanceledException) when (Token.IsCancellationRequested) { }
        catch (BackupException) when (Token.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await cancelled.CancelAsync();
        try
        {
            await heartbeat;
            await connection.DisposeAsync();
        }
        finally
        {
            await file.DisposeAsync();
            commands.Dispose();
            cancelled.Dispose();
        }
    }
}
