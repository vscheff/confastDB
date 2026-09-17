namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PostgresTestDatabaseTests(PostgresTestDatabase database)
{
    [Fact]
    public async Task SecondRunnerIsRejectedBeforeItCanResetTheSharedDatabase()
    {
        _ = database;
        var competingDatabase = new PostgresTestDatabase();

        try
        {
            var exception = await Assert.ThrowsAsync<InvalidOperationException>(competingDatabase.InitializeAsync);

            Assert.Contains("already using this PostgreSQL test database", exception.Message);
        }
        finally
        {
            await competingDatabase.DisposeAsync();
        }
    }
}
