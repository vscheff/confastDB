using Confast.Web.Features.Chat;
using Confast.Web.Features.Identity;
using Microsoft.Extensions.Logging.Abstractions;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class UserPresenceServiceTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly ChatNotifications notifications = new(NullLogger<ChatNotifications>.Instance);

    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private UserPresenceService Service(string userId) =>
        new(database, new TestCurrentUser(userId), notifications, TimeProvider.System);

    private async Task<(string First, string Second)> UsersAsync()
    {
        var first = Guid.NewGuid().ToString();
        var second = Guid.NewGuid().ToString();
        await using var db = database.CreateDbContext();
        db.Users.AddRange(
            new ApplicationUser { Id = first, UserName = "first", DisplayName = "First", IsActive = true },
            new ApplicationUser { Id = second, UserName = "second", DisplayName = "Second", IsActive = true });
        await db.SaveChangesAsync();
        return (first, second);
    }

    [Fact]
    public async Task SessionActivity_DerivesIdleAndOfflineAcrossMultipleClients()
    {
        var (first, second) = await UsersAsync();
        var service = Service(first);
        var viewer = Service(second);
        var idleClient = Guid.NewGuid();
        var activeClient = Guid.NewGuid();

        Assert.Equal(UserPresenceState.Offline, (await viewer.GetVisibleAsync())[first].State);
        await service.HeartbeatAsync(idleClient, 360);
        Assert.Equal(UserPresenceState.Idle, (await viewer.GetVisibleAsync())[first].State);
        await service.HeartbeatAsync(activeClient, 0);
        Assert.Equal(UserPresenceState.Online, (await viewer.GetVisibleAsync())[first].State);
        await service.EndSessionAsync(activeClient);
        Assert.Equal(UserPresenceState.Idle, (await viewer.GetVisibleAsync())[first].State);
        await service.EndSessionAsync(idleClient);
        Assert.Equal(UserPresenceState.Offline, (await viewer.GetVisibleAsync())[first].State);
    }

    [Fact]
    public async Task HiddenClient_UsesFiveMinuteIdleThresholdAndExpiresOffline()
    {
        var (first, second) = await UsersAsync();
        var service = Service(first);
        var viewer = Service(second);
        var sessionId = Guid.NewGuid();
        await service.HeartbeatAsync(sessionId, 0);
        Assert.Equal(UserPresenceState.Online, (await viewer.GetVisibleAsync())[first].State);

        await service.HeartbeatAsync(sessionId, 360);
        Assert.Equal(UserPresenceState.Idle, (await viewer.GetVisibleAsync())[first].State);

        await using (var db = database.CreateDbContext())
        {
            var session = await db.UserPresenceSessions.FindAsync(sessionId);
            session!.LastHeartbeatAtUtc = DateTime.UtcNow.AddMinutes(-2);
            await db.SaveChangesAsync();
        }
        Assert.Equal(UserPresenceState.Offline, (await viewer.GetVisibleAsync())[first].State);
    }

    [Fact]
    public async Task Invisible_HidesPresenceAndCustomStatusFromOtherUsers()
    {
        var (first, second) = await UsersAsync();
        var service = Service(first);
        var viewer = Service(second);
        await service.HeartbeatAsync(Guid.NewGuid(), 0);
        await service.SetCustomStatusAsync("😊", "Working on inspections");
        await service.SetPreferenceAsync(UserPresencePreference.Invisible);

        var self = (await service.GetVisibleAsync())[first];
        Assert.Equal(UserPresenceState.Invisible, self.State);
        Assert.Equal("😊", self.StatusEmoji);
        Assert.Equal("Working on inspections", self.StatusMessage);
        var seenByOther = (await viewer.GetVisibleAsync())[first];
        Assert.Equal(UserPresenceState.Offline, seenByOther.State);
        Assert.Null(seenByOther.StatusEmoji);
        Assert.Null(seenByOther.StatusMessage);

        await service.SetPreferenceAsync(UserPresencePreference.DoNotDisturb);
        Assert.Equal(UserPresenceState.DoNotDisturb, (await viewer.GetVisibleAsync())[first].State);
        Assert.Equal("Working on inspections", (await viewer.GetVisibleAsync())[first].StatusMessage);
    }

    [Fact]
    public async Task OnlyOnlineDndAndInvisibleCanBeSelected_AndStatusIsValidated()
    {
        var (first, _) = await UsersAsync();
        var service = Service(first);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetPreferenceAsync((UserPresencePreference)3));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetPreferenceAsync((UserPresencePreference)5));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetCustomStatusAsync("not an emoji", "Valid text"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetCustomStatusAsync(null, new string('x', 141)));
        await service.SetCustomStatusAsync("😊", "  In a meeting  ");
        Assert.Equal("In a meeting", (await service.GetCurrentAsync()).StatusMessage);
        await service.SetCustomStatusAsync(null, null);
        Assert.Null((await service.GetCurrentAsync()).StatusEmoji);
    }

    [Theory]
    [InlineData(UserPresencePreference.Idle)]
    [InlineData(UserPresencePreference.DoNotDisturb)]
    [InlineData(UserPresencePreference.Invisible)]
    public async Task TimedPreference_ExpiresToAutomaticPresence(UserPresencePreference preference)
    {
        var (first, second) = await UsersAsync();
        var clock = new PresenceClock();
        var service = new UserPresenceService(database, new TestCurrentUser(first), notifications, clock);
        var viewer = new UserPresenceService(database, new TestCurrentUser(second), notifications, clock);
        var sessionId = Guid.NewGuid();
        await service.SetCustomStatusAsync("😊", "Testing expiry");
        await service.HeartbeatAsync(sessionId, 0);
        await service.SetPreferenceAsync(preference, TimeSpan.FromMinutes(15));
        Assert.Equal(preference, (await service.GetCurrentAsync()).Preference);
        await using (var db = database.CreateDbContext())
        {
            Assert.Equal(clock.GetUtcNow().UtcDateTime.AddMinutes(15),
                (await db.Users.FindAsync(first))!.PresencePreferenceExpiresAtUtc);
        }

        clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        await service.HeartbeatAsync(sessionId, 0);
        Assert.Equal(preference, (await service.GetCurrentAsync()).Preference);
        clock.Advance(TimeSpan.FromSeconds(1));
        var expired = (await viewer.GetVisibleAsync())[first];
        Assert.Equal(UserPresenceState.Online, expired.State);
        Assert.Equal("Testing expiry", expired.StatusMessage);
        Assert.Equal(UserPresencePreference.Online, (await service.GetCurrentAsync()).Preference);
        await service.HeartbeatAsync(sessionId, 360);
        Assert.Equal(UserPresenceState.Idle, (await viewer.GetVisibleAsync())[first].State);
        await service.EndSessionAsync(sessionId);
        Assert.Equal(UserPresenceState.Offline, (await viewer.GetVisibleAsync())[first].State);
    }

    [Fact]
    public async Task ChangingPreference_ReplacesOrClearsExpiry_AndValidatesDuration()
    {
        var (first, _) = await UsersAsync();
        var clock = new PresenceClock();
        var service = new UserPresenceService(database, new TestCurrentUser(first), notifications, clock);
        foreach (var duration in new[] { TimeSpan.FromMinutes(15), TimeSpan.FromHours(1),
                     TimeSpan.FromHours(8), TimeSpan.FromHours(24), TimeSpan.FromDays(3) })
        {
            await service.SetPreferenceAsync(UserPresencePreference.Idle, duration);
            await using var db = database.CreateDbContext();
            Assert.Equal(clock.GetUtcNow().UtcDateTime + duration,
                (await db.Users.FindAsync(first))!.PresencePreferenceExpiresAtUtc);
        }
        await service.SetPreferenceAsync(UserPresencePreference.Invisible);
        clock.Advance(TimeSpan.FromDays(4));
        Assert.Equal(UserPresencePreference.Invisible, (await service.GetCurrentAsync()).Preference);
        await using (var db = database.CreateDbContext())
            Assert.Null((await db.Users.FindAsync(first))!.PresencePreferenceExpiresAtUtc);

        await service.SetPreferenceAsync(UserPresencePreference.Idle, TimeSpan.FromHours(1));
        await service.SetPreferenceAsync(UserPresencePreference.Online);
        await using (var db = database.CreateDbContext())
            Assert.Null((await db.Users.FindAsync(first))!.PresencePreferenceExpiresAtUtc);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetPreferenceAsync(UserPresencePreference.Idle, TimeSpan.FromMinutes(2)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SetPreferenceAsync(UserPresencePreference.Online, TimeSpan.FromHours(1)));
    }

    private sealed class PresenceClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
    }

    private sealed class TestCurrentUser(string userId) : ICurrentUser
    {
        public ValueTask<string?> GetUserIdAsync() => ValueTask.FromResult<string?>(userId);
    }
}
