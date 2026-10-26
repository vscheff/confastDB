using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Confast.Web.Tests;

public sealed class ChatMappingTests
{
    [Fact]
    public void Model_DoesNotWarnAboutFilteredRequiredNavigations()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql()
            .ConfigureWarnings(warnings => warnings.Throw(
                CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning))
            .Options;
        using var db = new AppDbContext(options);

        // Force model validation without connecting to a database.
        Assert.NotEmpty(db.Model.GetEntityTypes());
    }
}
