using Confast.Web.Features.Authorization;
using Confast.Web.Features.Identity;
using Npgsql;
using NpgsqlTypes;
using System.Security.Cryptography;
using System.Text;

namespace Confast.Web.Features.Maintenance;

internal sealed class DatabaseResetOptions
{
    public bool Enabled { get; set; }
}

internal enum FactoryResetPhase { Draining, BackingUp, Resetting, CleaningStorage, Verifying, Finalizing, Completed, Aborted }

internal sealed record FactoryResetOperation(Guid Id, string ActorId, string ActorName, Guid Generation,
    Guid DeploymentId, Guid BackupId, Guid? StorageId, DateTimeOffset RequestedAt, FactoryResetPhase Phase);

internal sealed class FactoryResetStore(IConfiguration configuration)
{
    internal bool Configured => !string.IsNullOrWhiteSpace(configuration.GetConnectionString("DatabaseReset"));

    internal async Task<NpgsqlConnection> OpenAsync(CancellationToken ct)
    {
        var runtime = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Confast"));
        var executor = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("DatabaseReset"));
        if (string.IsNullOrWhiteSpace(executor.Username) || executor.Username == runtime.Username ||
            string.IsNullOrWhiteSpace(runtime.Database) || runtime.Database != executor.Database ||
            !string.Equals(runtime.Host, executor.Host, StringComparison.OrdinalIgnoreCase) || runtime.Port != executor.Port ||
            string.IsNullOrWhiteSpace(runtime.Host) || runtime.Host.Contains(','))
            throw new FactoryResetException("ResetConnection", "Configure a separate Factory Reset login on the application database.");

        executor.Pooling = false;
        executor.Multiplexing = false;
        executor.Enlist = false;
        executor.ApplicationName = "confastDB Factory Reset";
        executor.Timeout = 5;
        var connection = new NpgsqlConnection(executor.ConnectionString);
        try
        {
            await connection.OpenAsync(ct);
            await using var command = new NpgsqlCommand("SET synchronous_commit = on", connection);
            await command.ExecuteNonQueryAsync(ct);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    internal async Task<Guid> RequestAsync(EffectivePermissionSnapshot actor, Guid deployment, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        await using var connection = await OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT confast_maintenance.request_reset(@id, @actor, @proof, @generation, @epoch, @deployment, @backup)
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("actor", actor.ActorUserId);
        command.Parameters.AddWithValue("proof", SHA256.HashData(Encoding.UTF8.GetBytes(actor.SecurityStamp + id.ToString("D"))));
        command.Parameters.AddWithValue("generation", actor.Version.Generation);
        command.Parameters.AddWithValue("epoch", actor.Version.Epoch);
        command.Parameters.AddWithValue("deployment", deployment);
        command.Parameters.AddWithValue("backup", Guid.NewGuid());
        await command.ExecuteNonQueryAsync(ct);
        return id;
    }

    internal static async Task<FactoryResetOperation?> ActiveAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT id, actor_id, actor_name, generation, deployment_id, backup_id, storage_id, requested_at, phase
            FROM confast_maintenance.factory_resets WHERE finished_at IS NULL
            """, connection);
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetGuid(3), reader.GetGuid(4),
            reader.GetGuid(5), reader.IsDBNull(6) ? null : reader.GetGuid(6), reader.GetFieldValue<DateTimeOffset>(7),
            Enum.Parse<FactoryResetPhase>(reader.GetString(8)));
    }

    internal static async Task AdvanceAsync(NpgsqlConnection connection, FactoryResetOperation operation,
        FactoryResetPhase phase, CancellationToken ct, Guid? storageId = null)
    {
        await using var command = new NpgsqlCommand("SELECT confast_maintenance.advance_reset(@id, @from, @to, @storage)", connection);
        command.Parameters.AddWithValue("id", operation.Id);
        command.Parameters.AddWithValue("from", operation.Phase.ToString());
        command.Parameters.AddWithValue("to", phase.ToString());
        command.Parameters.AddWithValue("storage", NpgsqlDbType.Uuid, (object?)storageId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(ct);
    }

    internal static async Task ResetDatabaseAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        FactoryResetOperation operation, FactoryResetPlan plan, bool prepare, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(prepare
            ? "SELECT confast_maintenance.prepare_reset(@id, @tables, @migrations)"
            : "SELECT confast_maintenance.execute_reset(@id, @tables, @migrations, @seeds, @bootstrap, @catalog)",
            connection, transaction)
        { CommandTimeout = prepare ? 60 : 600 };
        command.Parameters.AddWithValue("id", operation.Id);
        command.Parameters.AddWithValue("tables", plan.Tables);
        command.Parameters.AddWithValue("migrations", plan.Migrations);
        if (!prepare)
        {
            command.Parameters.AddWithValue("seeds", NpgsqlDbType.Jsonb, plan.Seeds);
            command.Parameters.AddWithValue("bootstrap", AppRoles.AdministratorId);
            command.Parameters.AddWithValue("catalog", PermissionCatalog.Version);
        }
        await command.ExecuteNonQueryAsync(ct);
    }
}

internal sealed class FactoryResetException(string code, string message) : Exception(message)
{
    internal string Code { get; } = code;
}
