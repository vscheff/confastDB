using System.Data.Common;
using System.Runtime.CompilerServices;
using Confast.Web.Data;
using Confast.Web.Time;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace Confast.Web.Features.Maintenance;

internal sealed record MaintenanceStatus(Guid Generation, string? Phase, string? LastOutcome);
internal sealed class MaintenanceUnavailableException : Exception;

// Lock ordering is application lease, then backup lease, then database table locks.
// The journal is checked after taking the shared lock, using a fresh SQL snapshot.
internal sealed class MaintenanceGate(IConfiguration configuration, DevelopmentDateOverrideTimeProvider? developmentClock = null)
{
    internal const string TryExclusive = "SELECT pg_try_advisory_lock(1703373, 841538)";
    private const string TryShared = "SELECT pg_try_advisory_lock_shared(1703373, 841538)";
    private const string ReleaseShared = "SELECT pg_advisory_unlock_shared(1703373, 841538)";
    private readonly AsyncLocal<Activity?> ambient = new();
    private readonly object stateLock = new();
    private Guid observedGeneration;
    private readonly string connectionString = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Confast"))
    {
        Enlist = false, Multiplexing = false, NoResetOnClose = false
    }.ConnectionString;

    internal Guid? CurrentGeneration => ambient.Value?.Generation;

    internal async Task<MaintenanceStatus> ReadAsync(CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        return await ReadAsync(connection, ct);
    }

    private static async Task<MaintenanceStatus> ReadAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("SELECT generation, phase, last_outcome FROM confast_maintenance.activity", connection)
        { CommandTimeout = 5 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false)) throw new MaintenanceUnavailableException();
        return new(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    internal async Task RunAsync(Func<Task> work, CancellationToken ct, Guid? expectedGeneration = null)
    {
        var parent = ambient.Value;
        if (parent is { Active: true })
        {
            if (expectedGeneration is { } expected && expected != parent.Generation) throw new MaintenanceUnavailableException();
            await work();
            return;
        }

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        var generation = await AcquireAsync(connection, expectedGeneration, ct);
        var activity = new Activity(generation);
        ambient.Value = activity;
        try { await work(); }
        finally
        {
            activity.Active = false;
            ambient.Value = parent;
            await ReleaseAsync(connection);
        }
    }

    internal async Task<Guid> AcquireAsync(NpgsqlConnection connection, Guid? expectedGeneration, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(TryShared, connection) { CommandTimeout = 5 };
        object? acquired;
        try { acquired = await command.ExecuteScalarAsync(ct).ConfigureAwait(false); }
        catch
        {
            // Cancellation may lose the response after PostgreSQL took the lock.
            NpgsqlConnection.ClearPool(connection);
            throw;
        }
        if (acquired is not true) throw new MaintenanceUnavailableException();
        try
        {
            var status = await ReadAsync(connection, ct).ConfigureAwait(false);
            var parent = ambient.Value;
            if ((status.Phase is not null && parent is not { Active: true }) ||
                (parent is not null && parent.Generation != status.Generation) ||
                (expectedGeneration is { } expected && expected != status.Generation))
                throw new MaintenanceUnavailableException();
            lock (stateLock)
            {
                if (observedGeneration != Guid.Empty && observedGeneration != status.Generation)
                    developmentClock?.UseSystemDate();
                observedGeneration = status.Generation;
            }
            return status.Generation;
        }
        catch
        {
            await ReleaseAsync(connection).ConfigureAwait(false);
            throw;
        }
    }

    internal static async ValueTask ReleaseAsync(NpgsqlConnection connection)
    {
        try
        {
            await using var command = new NpgsqlCommand(ReleaseShared, connection) { CommandTimeout = 5 };
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
        {
            // Keep a session that could still hold its advisory lock out of the pool.
            NpgsqlConnection.ClearPool(connection);
        }
    }

    private sealed class Activity(Guid generation)
    {
        internal Guid Generation { get; } = generation;
        internal volatile bool Active = true;
    }
}

// Cover database connections opened by timers, deferred component callbacks and
// application services as well as ordinary request and circuit entry points.
internal sealed class MaintenanceConnections(MaintenanceGate gate) : DbConnectionInterceptor
{
    private readonly ConditionalWeakTable<DbConnection, object> acquired = new();
    private static readonly object LeaseMarker = new();

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        ConnectionOpenedAsync(connection, eventData).GetAwaiter().GetResult();

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (acquired.TryGetValue(connection, out _)) return;
        var context = (AppDbContext)eventData.Context!;
        var generation = await gate.AcquireAsync((NpgsqlConnection)connection, context.MaintenanceGeneration, cancellationToken)
            .ConfigureAwait(false);
        context.MaintenanceGeneration = generation;
        acquired.Add(connection, LeaseMarker);
    }

    public override InterceptionResult ConnectionClosing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        ReleaseAsync(connection).GetAwaiter().GetResult();
        return result;
    }

    public override async ValueTask<InterceptionResult> ConnectionClosingAsync(DbConnection connection, ConnectionEventData eventData,
        InterceptionResult result)
    {
        await ReleaseAsync(connection).ConfigureAwait(false);
        return result;
    }

    public override InterceptionResult ConnectionDisposing(DbConnection connection, ConnectionEventData eventData, InterceptionResult result) =>
        ConnectionClosing(connection, eventData, result);

    public override ValueTask<InterceptionResult> ConnectionDisposingAsync(DbConnection connection, ConnectionEventData eventData,
        InterceptionResult result) => ConnectionClosingAsync(connection, eventData, result);

    private ValueTask ReleaseAsync(DbConnection connection) => acquired.Remove(connection)
        ? MaintenanceGate.ReleaseAsync((NpgsqlConnection)connection) : ValueTask.CompletedTask;
}

internal sealed class MaintenanceCircuitHandler(MaintenanceGate gate) : CircuitHandler
{
    private Guid? generation;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken) =>
        gate.RunAsync(() =>
        {
            generation ??= gate.CurrentGeneration;
            return Task.CompletedTask;
        }, cancellationToken, generation);

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            try
            {
                await gate.RunAsync(() =>
                {
                    generation ??= gate.CurrentGeneration;
                    return next(context);
                }, CancellationToken.None, generation);
            }
            catch (MaintenanceUnavailableException)
            {
                // The page's independent status request sends connected clients to
                // maintenance and sign-in. Reconnecting cannot reopen a stale circuit.
            }
        };
}
