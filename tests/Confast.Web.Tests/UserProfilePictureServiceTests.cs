using Confast.Web.Features.Chat;
using Confast.Web.Features.Identity;
using Microsoft.Extensions.Logging.Abstractions;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class UserProfilePictureServiceTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly ChatNotifications notifications = new(NullLogger<ChatNotifications>.Instance);

    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private UserProfilePictureService Service(string userId) =>
        new(database, new TestCurrentUser(userId), notifications);

    [Fact]
    public async Task Picture_CanBeReplacedViewedByAnotherUserAndRemoved()
    {
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        await using (var db = database.CreateDbContext())
        {
            db.Users.AddRange(
                new ApplicationUser { Id = first, UserName = "picture-first", DisplayName = "First" },
                new ApplicationUser { Id = second, UserName = "picture-second", DisplayName = "Second" });
            await db.SaveChangesAsync();
        }

        var owner = Service(first);
        var viewer = Service(second);
        var png = new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 1 };
        await owner.SaveAsync(png);
        var firstVersion = (await viewer.GetVersionsAsync())[first];
        var firstPicture = await viewer.GetAsync(second, first);
        Assert.NotNull(firstPicture);
        Assert.Equal(png, firstPicture.Value.Data);
        Assert.Equal("image/png", firstPicture.Value.ContentType);

        var jpeg = new byte[] { 0xff, 0xd8, 0xff, 1 };
        await owner.SaveAsync(jpeg);
        Assert.NotEqual(firstVersion, (await viewer.GetVersionsAsync())[first]);
        var replacement = await viewer.GetAsync(second, first);
        Assert.NotNull(replacement);
        Assert.Equal(jpeg, replacement.Value.Data);
        Assert.Equal("image/jpeg", replacement.Value.ContentType);
        Assert.Null(await viewer.GetAsync(second, Guid.NewGuid().ToString()));

        await owner.RemoveAsync();
        Assert.DoesNotContain(first, await viewer.GetVersionsAsync());
        Assert.Null(await viewer.GetAsync(second, first));
    }

    [Fact]
    public async Task Picture_RejectsInvalidContentAndInactiveAccounts()
    {
        var active = Guid.NewGuid().ToString();
        var inactive = Guid.NewGuid().ToString();
        await using (var db = database.CreateDbContext())
        {
            db.Users.AddRange(
                new ApplicationUser { Id = active, UserName = "picture-active", DisplayName = "Active" },
                new ApplicationUser { Id = inactive, UserName = "picture-inactive", DisplayName = "Inactive", IsActive = false });
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(active).SaveAsync("<svg/>"u8.ToArray()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(active).SaveAsync(new byte[UserProfilePictureService.MaximumPictureBytes + 1]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(inactive).SaveAsync(new byte[] { 0xff, 0xd8, 0xff }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(inactive).GetAsync(inactive, active));
    }

    private sealed class TestCurrentUser(string userId) : ICurrentUser
    {
        public ValueTask<string?> GetUserIdAsync() => ValueTask.FromResult<string?>(userId);
    }
}
