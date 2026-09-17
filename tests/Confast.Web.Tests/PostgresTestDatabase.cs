using Confast.Web.Data;
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
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE shipments, suppliers, identity_users, certification_documents, inspection_certifications, inspection_certification_requirements, inspection_secondary_processes, inspection_results, inspections, revision_certification_requirements, secondary_process_requirements, inspection_criteria, inspection_criteria_revisions, gages, gage_types, part_plants, parts, plants, customers RESTART IDENTITY CASCADE");
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO certification_email_templates (template_type, subject_template, html_body_template)
            VALUES
                (0, 'Certification package for {{CustomerName}} - {{PartNumber}}, Lot {{LotNumber}}', '<p>Attached is the certification package for <strong>{{PartNumber}}</strong>, lot <strong>{{LotNumber}}</strong>.</p><p>Ship date: {{ShipDate}}.</p>'),
                (1, 'Certification package for {{CustomerName}} - {{PartNumber}}', '<p>Attached is the certification package for <strong>{{PartNumber}}</strong>.</p><p>Lots: {{LotNumbers}}</p><p>Ship date: {{ShipDate}}.</p>'),
                (2, 'Certification package for {{CustomerName}}', '<p>Attached is the certification package for the following parts and lots.</p>{{PartLotSummary}}<p>Ship date: {{ShipDate}}.</p>')
            ON CONFLICT (template_type) DO NOTHING;
            """);
    }
}
