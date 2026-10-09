using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresCollection : ICollectionFixture<PostgresTestDatabase>
{
    public const string Name = "PostgreSQL integration";
}

public sealed class PostgresTestDatabase : IAsyncLifetime, IDbContextFactory<AppDbContext>
{
    private DbContextOptions<AppDbContext> options = null!;
    private Npgsql.NpgsqlConnection? suiteLockConnection;

    public string ConnectionString { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        ConnectionString = Environment.GetEnvironmentVariable("CONFAST_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Set CONFAST_TEST_CONNECTION_STRING to a disposable PostgreSQL test database.");

        var connectionBuilder = new Npgsql.NpgsqlConnectionStringBuilder(ConnectionString);
        if (string.IsNullOrWhiteSpace(connectionBuilder.Database)
            || !connectionBuilder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The test database name must contain 'test' to guard against destructive cleanup.");
        }

        options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;

        suiteLockConnection = new Npgsql.NpgsqlConnection(ConnectionString);
        try
        {
            await suiteLockConnection.OpenAsync();
            await using var command = suiteLockConnection.CreateCommand();
            command.CommandText = "SELECT pg_try_advisory_lock(hashtext('confast_integration_test_suite'))";
            if (await command.ExecuteScalarAsync() is not bool acquired || !acquired)
            {
                throw new InvalidOperationException(
                    "Another Confast integration-test runner is already using this PostgreSQL test database. " +
                    "Wait for that run to finish; do not start a second dotnet test process.");
            }

            await using var db = CreateDbContext();
            await db.Database.MigrateAsync();
        }
        catch
        {
            await DisposeAsync();
            throw;
        }
    }

    public async Task DisposeAsync()
    {
        if (suiteLockConnection is not { } connection) return;

        suiteLockConnection = null;
        await connection.DisposeAsync();
    }

    public AppDbContext CreateDbContext() => new(options);

    public Task<AppDbContext> CreateDbContextAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    public async Task ResetAsync()
    {
        await using var db = CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Disposable database OWNER reset, never a runtime bypass flag. The suite lock and
        // test-name guard were acquired before migration/reset. Restore triggers atomically.
        string[] securityTables = ["authorization_state", "identity_roles", "identity_users", "identity_user_roles",
            "role_permissions", "role_inheritance", "authorization_change_history", "authorization_change_details", "password_reset_delegations"];
        // These identifiers are a fixed source-controlled list, never input.
#pragma warning disable EF1002
        foreach (var table in securityTables)
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE {table} DISABLE TRIGGER USER");
        await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE password_reset_delegations, authorization_state, role_permissions, role_inheritance, authorization_change_details, authorization_change_history RESTART IDENTITY CASCADE");
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE chat_gif_api_requests, chat_gif_search_pages, chat_gif_metadata, shipments, suppliers, identity_users, certification_documents, inspection_certifications, inspection_certification_requirements, inspection_secondary_processes, inspection_results, inspections, revision_certification_requirements, secondary_process_requirements, inspection_criteria, inspection_criteria_revisions, gages, gage_types, part_plants, parts, plants, customers RESTART IDENTITY CASCADE");
        var seedIds = AppRoles.Seeds.Select(x => x.Id).ToArray();
        await db.Roles.Where(x => !seedIds.Contains(x.Id)).ExecuteDeleteAsync();
        foreach (var role in AppRoles.Seeds)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO identity_roles (id, name, normalized_name, concurrency_stamp, system_kind, system_key, is_enabled)
                VALUES ({role.Id}, {role.Name}, {role.NormalizedName}, {role.ConcurrencyStamp}, {(int)role.SystemKind}, {role.SystemKey}, true)
                ON CONFLICT (id) DO UPDATE SET name = EXCLUDED.name, normalized_name = EXCLUDED.normalized_name,
                    concurrency_stamp = EXCLUDED.concurrency_stamp, system_kind = EXCLUDED.system_kind,
                    system_key = EXCLUDED.system_key, is_enabled = true, description = NULL
                """);
        db.AuthorizationState.Add(new() { InstallationGeneration = Guid.NewGuid(), CatalogVersion = PermissionCatalog.Version });
        db.RolePermissions.AddRange(AuthorizationSeeds.Grants);
        db.RoleInheritance.AddRange(AuthorizationSeeds.Edges);
        await db.SaveChangesAsync();
        foreach (var table in securityTables)
            await db.Database.ExecuteSqlRawAsync($"ALTER TABLE {table} ENABLE TRIGGER USER");
#pragma warning restore EF1002
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO certification_email_templates (template_type, subject_template, html_body_template)
            VALUES
                (0, 'Certification package for {{CustomerName}} - {{PartNumber}}, Lot {{LotNumber}}', '<p>Attached is the certification package for <strong>{{PartNumber}}</strong>, lot <strong>{{LotNumber}}</strong>.</p><p>Ship date: {{ShipDate}}.</p>'),
                (1, 'Certification package for {{CustomerName}} - {{PartNumber}}', '<p>Attached is the certification package for <strong>{{PartNumber}}</strong>.</p><p>Lots: {{LotNumbers}}</p><p>Ship date: {{ShipDate}}.</p>'),
                (2, 'Certification package for {{CustomerName}}', '<p>Attached is the certification package for the following parts and lots.</p>{{PartLotSummary}}<p>Ship date: {{ShipDate}}.</p>')
            ON CONFLICT (template_type) DO NOTHING;
            """);
        await transaction.CommitAsync();
    }
}
