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

    public async Task InitializeAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("CONFAST_TEST_CONNECTION_STRING")
            ?? throw new InvalidOperationException(
                "Set CONFAST_TEST_CONNECTION_STRING to a disposable PostgreSQL test database.");

        var connectionBuilder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(connectionBuilder.Database)
            || !connectionBuilder.Database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The test database name must contain 'test' to guard against destructive cleanup.");
        }

        options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public AppDbContext CreateDbContext() => new(options);

    public Task<AppDbContext> CreateDbContextAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult(CreateDbContext());

    public async Task ResetAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE certification_documents, inspection_certifications, inspection_certification_requirements, inspection_secondary_processes, inspection_results, inspections, revision_certification_requirements, secondary_process_requirements, inspection_criteria, inspection_criteria_revisions, gages, gage_types, part_plants, parts, plants, customers RESTART IDENTITY CASCADE");
    }
}
