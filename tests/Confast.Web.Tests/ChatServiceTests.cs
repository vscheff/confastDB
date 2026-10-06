using Confast.Web.Features.Chat;
using Confast.Web.Features.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ChatServiceTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly ChatNotifications notifications = new(NullLogger<ChatNotifications>.Instance);

    private static ChatGif CachedGif(string id) => new(id, "Test GIF", $"https://media.giphy.com/{id}/preview.gif",
        $"https://media.giphy.com/{id}/image.gif", $"https://giphy.com/gifs/{id}", "GIPHY");

    private ChatService GifService(string userId, TimeProvider time, int limit = 100, int reserve = 20) =>
        new(database, new TestCurrentUser(userId), notifications, time,
            Microsoft.Extensions.Options.Options.Create(new ChatGifCacheOptions
            { SearchCacheLifetimeHours = 1, HourlyRequestLimit = limit, RefreshRequestReserve = reserve }));

    [Fact]
    public async Task GifCache_RefreshesExpiredPagesAndMetadataButServesFreshPagesWithoutSpendingBudget()
    {
        var (owner, member, _) = await UsersAsync();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var chat = GifService(owner, time);
        await chat.ResolveGifSearchAsync("hello", 0, 24, () => Task.FromResult(new ChatGifResult([CachedGif("abc123")], 1, null)));
        time.Now += TimeSpan.FromMinutes(59);
        await GifService(member, time).ResolveGifSearchAsync("hello", 0, 24, () => throw new Exception("Fresh page fetched"));
        time.Now += TimeSpan.FromMinutes(1);
        var updated = CachedGif("abc123") with { Title = "Refreshed title" };
        var refreshed = await GifService(member, time).ResolveGifSearchAsync("hello", 0, 24,
            () => Task.FromResult(new ChatGifResult([updated, CachedGif("def456")], 2, null)));
        Assert.Equal(2, refreshed.Total);
        Assert.Equal("Refreshed title", (await chat.ResolveGifsAsync(["abc123"], _ => throw new Exception("Cached image fetched"))).Gifs[0].Title);
        await chat.ResolveGifSearchAsync("hello", 0, 24, () => throw new Exception("Refreshed page fetched again"));
        await using var db = database.CreateDbContext();
        var requests = await db.Set<ChatGifApiRequest>().OrderBy(x => x.RequestedAtUtc).ToArrayAsync();
        Assert.Equal(2, requests.Length);
        Assert.False(requests[0].IsRefresh);
        Assert.True(requests[1].IsRefresh);
    }

    [Fact]
    public async Task GifCache_PreservesReserveForMissesAndResumesRefreshingAfterRollingHour()
    {
        var (owner, member, _) = await UsersAsync();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 30, 0, TimeSpan.Zero));
        var chat = GifService(owner, time, limit: 3, reserve: 1);
        await chat.ResolveGifSearchAsync("hello", 0, 24, () => Task.FromResult(new ChatGifResult([CachedGif("abc123")], 1, null)));
        // Age the stored page while retaining a recent request in the rolling window.
        await using (var db = database.CreateDbContext())
            await db.Set<ChatGifSearchPage>().ExecuteUpdateAsync(x => x.SetProperty(p => p.CachedAtUtc, time.Now.UtcDateTime.AddHours(-2)));
        var other = GifService(member, time, limit: 3, reserve: 1);
        await other.RecordGifRequestAsync();
        time.Now += TimeSpan.FromMinutes(31); // Crossing 13:00 does not reset the budget.
        var stale = await chat.ResolveGifSearchAsync("hello", 0, 24, () => throw new Exception("Reserve spent on refresh"));
        Assert.Equal("abc123", Assert.Single(stale.Gifs).Id);
        Assert.Null(stale.Error);
        await other.ResolveGifSearchAsync("new", 0, 24, () => Task.FromResult(new ChatGifResult([], 0, null)));
        await chat.RecordGifRequestAsync();
        var uncached = await chat.ResolveGifSearchAsync("another", 0, 24,
            () => Task.FromResult(new ChatGifResult([CachedGif("def456")], 1, null)));
        Assert.Null(uncached.Error); // Cache misses still fetch after exceeding the hourly budget.
        await chat.ResolveGifSearchAsync("hello", 0, 24, () => throw new Exception("Refresh exceeded budget"));
        time.Now += TimeSpan.FromHours(1); // All recent requests leave the rolling window.
        var refreshed = await chat.ResolveGifSearchAsync("hello", 0, 24,
            () => Task.FromResult(new ChatGifResult([CachedGif("def456")], 1, null)));
        Assert.Equal("def456", Assert.Single(refreshed.Gifs).Id);
    }

    [Fact]
    public async Task GifCache_FailedRefreshRetainsResultsAndOriginalCacheTimestamp()
    {
        var (owner, _, _) = await UsersAsync();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var chat = GifService(owner, time);
        await chat.ResolveGifSearchAsync("hello", 0, 24, () => Task.FromResult(new ChatGifResult([CachedGif("abc123")], 1, null)));
        time.Now += TimeSpan.FromHours(2);
        var fallback = await chat.ResolveGifSearchAsync("hello", 0, 24, () => Task.FromResult(new ChatGifResult([], 0, "429")));
        Assert.Single(fallback.Gifs);
        Assert.Null(fallback.Error);
        Assert.Single((await chat.ResolveGifSearchAsync("hello", 0, 24,
            () => throw new Microsoft.JSInterop.JSException("Disconnected"))).Gifs);
        await using var db = database.CreateDbContext();
        Assert.Equal(time.Now.UtcDateTime.AddHours(-2), (await db.Set<ChatGifSearchPage>().SingleAsync()).CachedAtUtc);
        Assert.Equal(3, await db.Set<ChatGifApiRequest>().CountAsync());
    }

    [Fact]
    public async Task GifBudget_RecordsEveryConcurrentUncachedRequestEvenAboveBudget()
    {
        var (owner, member, _) = await UsersAsync();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
            GifService(i % 2 == 0 ? owner : member, time, limit: 5, reserve: 2).RecordGifRequestAsync()));
        await using var db = database.CreateDbContext();
        Assert.Equal(16, await db.Set<ChatGifApiRequest>().CountAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => GifService("missing", time).RecordGifRequestAsync());
    }

    [Fact]
    public async Task GifBudget_ConcurrentStaleRefreshesCannotConsumeTheReserve()
    {
        var (owner, member, _) = await UsersAsync();
        var time = new MutableTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var chat = GifService(owner, time, limit: 9, reserve: 1);
        for (var i = 0; i < 6; i++)
            await chat.ResolveGifSearchAsync("query" + i, 0, 24,
                () => Task.FromResult(new ChatGifResult([CachedGif("abc123")], 1, null)));
        await using (var db = database.CreateDbContext())
            await db.Set<ChatGifSearchPage>().ExecuteUpdateAsync(x => x.SetProperty(p => p.CachedAtUtc, time.Now.UtcDateTime.AddHours(-2)));
        var refreshes = 0;
        await Task.WhenAll(Enumerable.Range(0, 6).Select(i => GifService(i % 2 == 0 ? owner : member, time, limit: 9, reserve: 1)
            .ResolveGifSearchAsync("query" + i, 0, 24, async () =>
            {
                Interlocked.Increment(ref refreshes);
                await Task.Delay(20);
                return new ChatGifResult([CachedGif("def456")], 1, null);
            })));
        Assert.Equal(2, refreshes);
        await using var finalDb = database.CreateDbContext();
        Assert.Equal(8, await finalDb.Set<ChatGifApiRequest>().CountAsync());
        Assert.Equal(2, await finalDb.Set<ChatGifApiRequest>().CountAsync(x => x.IsRefresh));
    }

    [Fact]
    public async Task GifCache_SharesSearchPagesAndMetadataAcrossUsersAndConcurrentRequests()
    {
        var (owner, member, _) = await UsersAsync();
        var calls = 0;
        async Task<ChatGifResult> Fetch()
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(20);
            return new([CachedGif("abc123"), CachedGif("def456")], 99, null);
        }
        var results = await Task.WhenAll(Service(owner).ResolveGifSearchAsync(" LOL ", 0, 24, Fetch),
            Service(member).ResolveGifSearchAsync("lol", 0, 24, Fetch));
        Assert.Equal(1, calls);
        Assert.All(results, x => Assert.Equal(["abc123", "def456"], x.Gifs.Select(g => g.Id)));
        var page = await Service(member).ResolveGifSearchAsync("LOL", 0, 24, () => throw new Exception("Cache miss"));
        Assert.Equal(99, page.Total);
        var images = await Service(member).ResolveGifsAsync(["def456", "abc123"], _ => throw new Exception("API must not be called"));
        Assert.Equal(["def456", "abc123"], images.Gifs.Select(x => x.Id));
        await Service(owner).ResolveGifSearchAsync("lol", 24, 24, Fetch);
        await Service(owner).ResolveGifSearchAsync("lol", 0, 1, () => Task.FromResult(new ChatGifResult([CachedGif("abc123")], 99, null)));
        Assert.Equal(2, calls);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service("missing-user").ResolveGifSearchAsync("lol", 0, 24, Fetch));
    }

    [Fact]
    public async Task GifCache_DoesNotPersistFailuresOrUntrustedUrlsAndCachesEmptySearches()
    {
        var (owner, _, _) = await UsersAsync();
        var chat = Service(owner);
        await chat.ResolveGifSearchAsync("retry", 0, 24, () => Task.FromResult(new ChatGifResult([], 0, "Rate limit")));
        var invalid = CachedGif("abc123") with { Url = "https://evil.example/test.gif" };
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.ResolveGifSearchAsync("retry", 0, 24,
            () => Task.FromResult(new ChatGifResult([invalid], 1, null))));
        var good = await chat.ResolveGifSearchAsync("retry", 0, 24,
            () => Task.FromResult(new ChatGifResult([CachedGif("abc123")], 1, null)));
        Assert.Single(good.Gifs);
        await chat.ResolveGifSearchAsync("nothing", 0, 24, () => Task.FromResult(new ChatGifResult([], 0, null)));
        Assert.Empty((await chat.ResolveGifSearchAsync("nothing", 0, 24, () => throw new Exception("Empty page wasn't cached"))).Gifs);
    }

    [Fact]
    public async Task GifCache_ResolvesOnlyMissingIdsAndKeepsCachedFavoritesDuringRateLimits()
    {
        var (owner, _, _) = await UsersAsync();
        var chat = Service(owner);
        await chat.ResolveGifsAsync(["abc123"], ids => Task.FromResult(new ChatGifResult([CachedGif(ids[0])], 1, null)));
        var partial = await chat.ResolveGifsAsync(["abc123", "def456"], ids =>
        {
            Assert.Equal(["def456"], ids);
            return Task.FromResult(new ChatGifResult([], 0, "Rate limit"));
        });
        Assert.Equal("abc123", Assert.Single(partial.Gifs).Id);
        Assert.Equal("Rate limit", partial.Error);
        await chat.ResolveGifsAsync(["def456"], ids => Task.FromResult(new ChatGifResult([CachedGif(ids[0])], 1, null)));
        Assert.Equal(2, (await Service(owner).ResolveGifsAsync(["abc123", "def456"], _ => throw new Exception("Cache miss"))).Gifs.Length);
    }

    [Fact]
    public async Task GifFavorites_ArePrivatePersistentAndIdempotent()
    {
        var (owner, member, _) = await UsersAsync();
        await Task.WhenAll(Service(owner).SetGifFavoriteAsync("abc123", true), Service(owner).SetGifFavoriteAsync("abc123", true));
        Assert.Equal(["abc123"], await Service(owner).GetGifFavoritesAsync());
        Assert.Empty(await Service(member).GetGifFavoritesAsync());
        await Service(member).SetGifFavoriteAsync("abc123", true);
        await Service(owner).SetGifFavoriteAsync("abc123", false);
        await Service(owner).SetGifFavoriteAsync("abc123", false);
        Assert.Empty(await Service(owner).GetGifFavoritesAsync());
        Assert.Equal(["abc123"], await Service(member).GetGifFavoritesAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(owner).SetGifFavoriteAsync("https://example.com/file.gif", true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service("missing-user").GetGifFavoritesAsync());
    }

    [Fact]
    public async Task GifMessages_UseNormalPermissionsThreadsRepliesUnreadAndDeletion()
    {
        var (owner, member, outsider) = await UsersAsync();
        var chat = Service(owner);
        var channel = await chat.CreateChannelAsync("GIFs", ChannelVisibility.Private, [member]);
        var reply = await chat.SendAsync(channel, "Reply here");
        var id = await chat.SendGifAsync(channel, "abc123", replyToMessageId: reply);
        var message = Assert.Single((await Service(member).GetThreadAsync(channel)).Messages, x => x.Id == id);
        Assert.Equal("abc123", message.GiphyId);
        Assert.Equal(reply, message.ReplyToMessageId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.EditOwnMessageAsync(id, "Changed"));
        Assert.True((await Service(member).GetConversationsAsync()).Single(x => x.Id == channel).UnreadCount > 0);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).SendGifAsync(channel, "abc123"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendGifAsync(channel, "../invalid"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SendGifAsync(channel, "abc123", channelThreadId: long.MaxValue));
        var thread = await chat.CreateChannelThreadAsync(channel, "GIF thread", "First message");
        var threadMessageId = await chat.SendGifAsync(channel, "def456", channelThreadId: thread);
        Assert.Contains((await Service(member).GetThreadAsync(channel, channelThreadId: thread)).Messages,
            x => x.Id == threadMessageId && x.GiphyId == "def456");
        await chat.DeleteOwnMessageAsync(id);
        Assert.DoesNotContain((await Service(member).GetThreadAsync(channel)).Messages, x => x.Id == id);
        await using var db = database.CreateDbContext();
        var stored = await db.ChatMessages.SingleAsync(x => x.Id == id);
        Assert.NotNull(stored.DeletedAtUtc);
        Assert.Equal("abc123", stored.GiphyId);
    }

    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private ChatService Service(string userId) =>
        new(database, new TestCurrentUser(userId), notifications, TimeProvider.System);

    private ChatService Service(string userId, TimeProvider timeProvider) =>
        new(database, new TestCurrentUser(userId), notifications, timeProvider);

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<(string Owner, string Member, string Outsider)> UsersAsync()
    {
        var ids = (Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), Guid.NewGuid().ToString());
        await using var db = database.CreateDbContext();
        db.Users.AddRange(
            new ApplicationUser { Id = ids.Item1, UserName = "owner", DisplayName = "Owner", IsActive = true },
            new ApplicationUser { Id = ids.Item2, UserName = "member", DisplayName = "Member", IsActive = true },
            new ApplicationUser { Id = ids.Item3, UserName = "outsider", DisplayName = "Outsider", IsActive = true });
        await db.SaveChangesAsync();
        return ids;
    }


    [Fact]
    public async Task ChannelActions_PinsArePersonalAndDuplicationCopiesOnlyConfiguration()
    {
        var (owner, member, outsider) = await UsersAsync();
        var chat = Service(owner);
        var source = await chat.CreateChannelAsync("Source", ChannelVisibility.Private, [member]);
        var category = await chat.CreateChannelGroupAsync("Category", source);
        await chat.SetChannelTopicAsync(source, "Topic");
        await chat.SendAsync(source, "History stays in source");
        await chat.SetChannelPinnedToTopAsync(source, true);
        await chat.SetChannelNotificationModeAsync(source, ChatNotificationMode.Nothing);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).SetChannelPinnedToTopAsync(source, true));
        Assert.NotNull((await chat.GetConversationsAsync()).Single(x => x.Id == source).PinnedToTopAtUtc);
        Assert.Null((await Service(member).GetConversationsAsync()).Single(x => x.Id == source).PinnedToTopAtUtc);
        var copy = await chat.DuplicateChannelAsync(source, "Copy");
        var row = (await chat.GetConversationsAsync()).Single(x => x.Id == copy);
        Assert.Equal(category, row.ChannelGroupId);
        Assert.Equal("Topic", row.Topic);
        Assert.Null(row.PinnedToTopAtUtc);
        Assert.Empty((await chat.GetThreadAsync(copy)).Messages);
        Assert.Contains(await Service(member).GetConversationsAsync(), x => x.Id == copy);
        Assert.DoesNotContain(await Service(outsider).GetConversationsAsync(), x => x.Id == copy);
        Assert.Equal(ChatNotificationMode.CategoryDefault, (await chat.GetNotificationSettingsAsync()).Channels[copy].Mode);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).DuplicateChannelAsync(source, "Forbidden"));
        await chat.SetChannelPinnedToTopAsync(source, false);
        Assert.Null((await chat.GetConversationsAsync()).Single(x => x.Id == source).PinnedToTopAtUtc);
    }

    [Fact]
    public async Task ChannelActions_CategoryEdgesAndDeletionPreserveAuditAndDenyFurtherAccess()
    {
        var (owner, member, outsider) = await UsersAsync();
        var chat = Service(owner);
        var first = await chat.CreateChannelAsync("First", ChannelVisibility.Public, []);
        var category = await chat.CreateChannelGroupAsync("Category", first);
        var second = await chat.CreateChannelAsync("Second", ChannelVisibility.Public, [], category);
        var third = await chat.CreateChannelAsync("Third", ChannelVisibility.Public, [], category);
        await chat.MoveChannelToCategoryEdgeAsync(third, true);
        Assert.Equal([third, first, second], (await chat.GetConversationsAsync()).OrderBy(x => x.ChannelSortOrder).Select(x => x.Id));
        await chat.MoveChannelToCategoryEdgeAsync(third, false);
        Assert.Equal([first, second, third], (await chat.GetConversationsAsync()).OrderBy(x => x.ChannelSortOrder).Select(x => x.Id));
        var message = await chat.SendAsync(second, "Audit record");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(member).DeleteChannelAsync(second));
        await chat.DeleteChannelAsync(second);
        Assert.DoesNotContain(await Service(outsider).GetConversationsAsync(), x => x.Id == second);
        Assert.DoesNotContain(await Service(member).GetConversationsAsync(), x => x.Id == second);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.GetThreadAsync(second));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.SendAsync(second, "Forbidden"));
        await using var db = database.CreateDbContext();
        var deleted = await db.ChatConversations.IgnoreQueryFilters().SingleAsync(x => x.Id == second);
        Assert.Equal(owner, deleted.DeletedByUserId);
        Assert.NotNull(deleted.DeletedAtUtc);
        Assert.True(await db.ChatMessages.AnyAsync(x => x.Id == message));
    }

    [Theory]
    [InlineData(ChatNotificationMode.AllMessages, true, true, 2)]
    [InlineData(ChatNotificationMode.MentionsAndUnreads, false, true, 2)]
    [InlineData(ChatNotificationMode.OnlyMentions, false, true, 1)]
    [InlineData(ChatNotificationMode.Nothing, false, false, 0)]
    public async Task NotificationModes_FilterPopupsAndToastsWithoutChangingUnreadHistory(
        ChatNotificationMode mode, bool ordinaryPopup, bool mentionPopup, int toastCount)
    {
        var (owner, member, _) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var channel = await sender.CreateChannelAsync("Notifications", ChannelVisibility.Private, [member]);
        await recipient.SetChannelNotificationModeAsync(channel, mode);
        var ordinary = await sender.SendAsync(channel, "Ordinary");
        Assert.Equal(mode is ChatNotificationMode.AllMessages or ChatNotificationMode.MentionsAndUnreads ? 1 : 0,
            (await recipient.GetNotificationUnreadCountsAsync())[channel].Count);
        var mentioned = await sender.SendAsync(channel, "Hello @member");
        Assert.Equal(ordinaryPopup, await recipient.GetIncomingMessagePreviewAsync(ordinary) != null);
        Assert.Equal(mentionPopup, await recipient.GetIncomingMessagePreviewAsync(mentioned) != null);
        var summary = await recipient.GetNotificationUnreadSummaryAsync();
        Assert.Equal(toastCount, summary.Count);
        Assert.Equal(summary, (await recipient.GetNotificationUnreadCountsAsync())[channel]);
        Assert.Equal(toastCount > 0, summary.HasAlert);
        Assert.Equal(2, await recipient.GetTotalUnreadCountAsync());
        Assert.Equal(2, (await recipient.GetInboxMessagesAsync()).Count);
        Assert.Equal(ChatNotificationMode.CategoryDefault,
            (await sender.GetNotificationSettingsAsync()).Channels[channel].Mode);
    }

    [Fact]
    public async Task NotificationCategories_DefaultsFollowMovesAndDeletionWhileOverridesRemainPersonal()
    {
        var (owner, member, _) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var channel = await sender.CreateChannelAsync("Inherited", ChannelVisibility.Private, [member]);
        var parent = await sender.CreateChannelGroupAsync("Parent");
        var group = await sender.CreateChannelGroupAsync("Category", channel, parent);
        var defaults = (await recipient.GetNotificationSettingsAsync()).Channels[channel];
        Assert.Equal(ChatNotificationMode.CategoryDefault, defaults.Mode);
        Assert.Equal(ChatNotificationMode.AllMessages, defaults.EffectiveMode);
        await recipient.SetCategoryNotificationModeAsync(group, ChatNotificationMode.OnlyMentions);
        Assert.Equal(ChatNotificationMode.OnlyMentions,
            (await recipient.GetNotificationSettingsAsync()).Channels[channel].EffectiveMode);
        Assert.Equal(ChatNotificationMode.AllMessages,
            (await sender.GetNotificationSettingsAsync()).Channels[channel].EffectiveMode);
        await recipient.SetChannelNotificationModeAsync(channel, ChatNotificationMode.Nothing);
        await recipient.SetCategoryNotificationModeAsync(group, ChatNotificationMode.AllMessages);
        Assert.Equal(ChatNotificationMode.Nothing,
            (await Service(member).GetNotificationSettingsAsync()).Channels[channel].EffectiveMode);
        await recipient.SetChannelNotificationModeAsync(channel, ChatNotificationMode.CategoryDefault);
        await recipient.SetCategoryNotificationModeAsync(group, ChatNotificationMode.Nothing);
        await sender.MoveChannelAsync(channel, null, null);
        Assert.Equal(ChatNotificationMode.AllMessages,
            (await recipient.GetNotificationSettingsAsync()).Channels[channel].EffectiveMode);
        await sender.MoveChannelAsync(channel, group, null);
        await sender.DeleteChannelGroupAsync(group);
        Assert.Null((await recipient.GetConversationsAsync()).Single().ChannelGroupId);
        Assert.DoesNotContain(group, (await recipient.GetNotificationSettingsAsync()).Categories.Keys);
        Assert.Equal(ChatNotificationMode.AllMessages,
            (await recipient.GetNotificationSettingsAsync()).Channels[channel].EffectiveMode);
    }

    [Theory]
    [InlineData(15)]
    [InlineData(60)]
    [InlineData(180)]
    [InlineData(480)]
    [InlineData(1440)]
    [InlineData(null)]
    public async Task NotificationMute_PersistsUntilExactExpiryAndPreservesSelectedMode(int? minutes)
    {
        var (owner, member, _) = await UsersAsync();
        var time = new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var sender = Service(owner, time);
        var recipient = Service(member, time);
        var channel = await sender.CreateChannelAsync("Muted", ChannelVisibility.Private, [member]);
        await recipient.SetChannelNotificationModeAsync(channel, ChatNotificationMode.MentionsAndUnreads);
        await recipient.SetChannelMutedAsync(channel, true, minutes);
        var id = await sender.SendAsync(channel, "@everyone hello");
        var setting = (await Service(member, time).GetNotificationSettingsAsync()).Channels[channel];
        Assert.True(setting.IsMuted);
        Assert.Equal(ChatNotificationMode.MentionsAndUnreads, setting.Mode);
        Assert.Equal(minutes is int duration ? time.Now.UtcDateTime.AddMinutes(duration) : (DateTime?)null, setting.MutedUntilUtc);
        Assert.Null(await recipient.GetIncomingMessagePreviewAsync(id));
        Assert.Equal(0, (await recipient.GetNotificationUnreadSummaryAsync()).Count);
        Assert.Equal(new ChatNotificationUnreadSummary(0, false), (await recipient.GetNotificationUnreadCountsAsync())[channel]);
        Assert.Equal(1, await recipient.GetTotalUnreadCountAsync());
        if (minutes is int value)
        {
            time.Now = time.Now.AddMinutes(value).AddTicks(-10);
            Assert.Null(await recipient.GetIncomingMessagePreviewAsync(id));
            time.Now = time.Now.AddTicks(10);
            Assert.NotNull(await recipient.GetIncomingMessagePreviewAsync(id));
            Assert.False((await recipient.GetNotificationSettingsAsync()).Channels[channel].IsMuted);
            Assert.Equal(1, (await recipient.GetNotificationUnreadSummaryAsync()).Count);
        }
        else
        {
            time.Now = time.Now.AddYears(1);
            Assert.Null(await recipient.GetIncomingMessagePreviewAsync(id));
        }
        await recipient.SetChannelMutedAsync(channel, false);
        Assert.NotNull(await recipient.GetIncomingMessagePreviewAsync(id));
    }

    [Fact]
    public async Task NotificationCategoryMute_IncludesNestedChannelsAndLeavesOtherUsersAndModesAlone()
    {
        var (owner, member, _) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var first = await sender.CreateChannelAsync("First", ChannelVisibility.Private, [member]);
        var second = await sender.CreateChannelAsync("Nested", ChannelVisibility.Private, [member]);
        var other = await sender.CreateChannelAsync("Other", ChannelVisibility.Private, [member]);
        var category = await sender.CreateChannelGroupAsync("Category", first);
        await sender.CreateChannelGroupAsync("Nested Category", second, category);
        await recipient.SetChannelNotificationModeAsync(first, ChatNotificationMode.OnlyMentions);
        await recipient.SetCategoryNotificationModeAsync(category, ChatNotificationMode.MentionsAndUnreads);
        await recipient.SetCategoryMutedAsync(category, true, 60);
        var settings = await recipient.GetNotificationSettingsAsync();
        Assert.True(settings.Categories[category].IsMuted);
        Assert.True(settings.Channels[first].IsMuted);
        Assert.True(settings.Channels[second].IsMuted);
        Assert.False(settings.Channels[other].IsMuted);
        Assert.Equal(ChatNotificationMode.OnlyMentions, settings.Channels[first].Mode);
        Assert.Equal(ChatNotificationMode.MentionsAndUnreads, settings.Categories[category].Mode);
        Assert.False((await sender.GetNotificationSettingsAsync()).Channels[first].IsMuted);
        await recipient.SetChannelMutedAsync(first, false);
        Assert.False((await recipient.GetNotificationSettingsAsync()).Channels[first].IsMuted);
        await recipient.SetCategoryMutedAsync(category, false);
        Assert.False((await recipient.GetNotificationSettingsAsync()).Channels[second].IsMuted);
    }

    [Fact]
    public async Task NotificationBadgeCounts_ApplyCategoryDefaultsAndChannelOverridesWithoutClearingUnread()
    {
        var (owner, member, _) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var inherited = await sender.CreateChannelAsync("Inherited", ChannelVisibility.Private, [member]);
        var explicitAll = await sender.CreateChannelAsync("All", ChannelVisibility.Private, [member]);
        var mentionsOnly = await sender.CreateChannelAsync("Mentions", ChannelVisibility.Private, [member]);
        var category = await sender.CreateChannelGroupAsync("Category", inherited);
        await sender.MoveChannelAsync(explicitAll, category, null);
        await sender.MoveChannelAsync(mentionsOnly, category, null);
        await recipient.SetCategoryNotificationModeAsync(category, ChatNotificationMode.Nothing);
        await recipient.SetChannelNotificationModeAsync(explicitAll, ChatNotificationMode.AllMessages);
        await recipient.SetChannelNotificationModeAsync(mentionsOnly, ChatNotificationMode.OnlyMentions);
        foreach (var channel in new[] { inherited, explicitAll, mentionsOnly })
        {
            await sender.SendAsync(channel, "Ordinary");
            await sender.SendAsync(channel, "Hello @member");
        }
        var counts = await recipient.GetNotificationUnreadCountsAsync();
        Assert.Equal(new ChatNotificationUnreadSummary(0, false), counts[inherited]);
        Assert.Equal(new ChatNotificationUnreadSummary(2, true), counts[explicitAll]);
        Assert.Equal(new ChatNotificationUnreadSummary(1, true), counts[mentionsOnly]);
        Assert.Equal(3, counts.Values.Sum(x => x.Count));
        Assert.Equal(6, await recipient.GetTotalUnreadCountAsync());
        await recipient.SetCategoryMutedAsync(category, true);
        Assert.All((await recipient.GetNotificationUnreadCountsAsync()).Values,
            count => Assert.Equal(new ChatNotificationUnreadSummary(0, false), count));
        Assert.Equal(6, await recipient.GetTotalUnreadCountAsync());
        await recipient.SetCategoryMutedAsync(category, false);
        Assert.Equal(3, (await recipient.GetNotificationUnreadSummaryAsync()).Count);
    }

    [Fact]
    public async Task NotificationSettings_RejectUnauthorizedTargetsAndInvalidSelections()
    {
        var (owner, member, outsider) = await UsersAsync();
        var sender = Service(owner);
        var channel = await sender.CreateChannelAsync("Private", ChannelVisibility.Private, [member]);
        var group = await sender.CreateChannelGroupAsync("Private Category", channel);
        var other = Service(outsider);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other.SetChannelMutedAsync(channel, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other.SetChannelNotificationModeAsync(channel, ChatNotificationMode.Nothing));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other.SetCategoryMutedAsync(group, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other.SetCategoryNotificationModeAsync(group, ChatNotificationMode.Nothing));
        var direct = await sender.OpenDirectAsync(member);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => sender.SetChannelMutedAsync(direct, true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SetChannelNotificationModeAsync(channel, (ChatNotificationMode)99));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SetCategoryNotificationModeAsync(group, ChatNotificationMode.CategoryDefault));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SetChannelMutedAsync(channel, true, 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SetCategoryMutedAsync(group, true, 2));
    }

    [Fact]
    public async Task IncomingPreviews_DoNotDisturbSuppressesCardsUntilExpiryWithoutChangingUnread()
    {
        var (owner, member, _) = await UsersAsync();
        var time = new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var sender = Service(owner, time);
        var recipient = Service(member, time);
        var presence = new UserPresenceService(database, new TestCurrentUser(member), notifications, time);
        var channel = await sender.CreateChannelAsync("Quiet", ChannelVisibility.Private, [member]);
        await presence.SetPreferenceAsync(UserPresencePreference.DoNotDisturb, TimeSpan.FromMinutes(15));
        var id = await sender.SendAsync(channel, "Incoming while busy");
        Assert.Null(await recipient.GetIncomingMessagePreviewAsync(id));
        Assert.Equal(id, Assert.Single(await recipient.GetInboxMessagesAsync()).Id);
        Assert.Equal(1, await recipient.GetTotalUnreadCountAsync());
        time.Now = time.Now.AddMinutes(15);
        Assert.NotNull(await recipient.GetIncomingMessagePreviewAsync(id));
        await presence.SetPreferenceAsync(UserPresencePreference.DoNotDisturb);
        time.Now = time.Now.AddDays(1);
        Assert.Null(await recipient.GetIncomingMessagePreviewAsync(id));
        await presence.SetPreferenceAsync(UserPresencePreference.Online);
        Assert.NotNull(await recipient.GetIncomingMessagePreviewAsync(id));
    }

    [Fact]
    public async Task IncomingPreviews_IdentifySendsAndRespectMembershipDeletionAndSender()
    {
        var (owner, member, outsider) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var channel = await sender.CreateChannelAsync("Previews", ChannelVisibility.Private, [member]);
        var events = new List<long?>();
        using var subscription = notifications.Subscribe(member, id => events.Add(id));
        var id = await sender.SendWithAttachmentsAsync(channel, "Incoming",
            [new("note.txt", System.Text.Encoding.UTF8.GetBytes("Attachment"))]);
        Assert.Equal(id, Assert.Single(events));
        var preview = Assert.IsType<ChatInboxMessage>(await recipient.GetIncomingMessagePreviewAsync(id));
        Assert.Equal("Incoming", preview.Body);
        Assert.Equal("Owner", preview.SenderName);
        Assert.Equal("note.txt", Assert.Single(preview.AttachmentNames));
        Assert.Null(await sender.GetIncomingMessagePreviewAsync(id));
        Assert.Null(await Service(outsider).GetIncomingMessagePreviewAsync(id));
        Assert.Equal(1, await recipient.GetTotalUnreadCountAsync());
        await recipient.MarkMessageReadAsync(channel, id);
        Assert.Null(events.Last());
        Assert.NotNull(await recipient.GetIncomingMessagePreviewAsync(id));
        await sender.DeleteOwnMessageAsync(id);
        Assert.Null(events.Last());
        Assert.Null(await recipient.GetIncomingMessagePreviewAsync(id));
    }

    [Fact]
    public async Task IncomingPreviews_IncludeThreadMessagesPollsAndDeliveredSchedules()
    {
        var (owner, member, _) = await UsersAsync();
        var time = new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var sender = Service(owner, time);
        var recipient = Service(member, time);
        var channel = await sender.CreateChannelAsync("Previews", ChannelVisibility.Private, [member]);
        var events = new List<long?>();
        using var subscription = notifications.Subscribe(member, id => events.Add(id));
        var threadId = await sender.CreateChannelThreadAsync(channel, "Thread", "First message");
        var firstId = Assert.IsType<long>(Assert.Single(events));
        Assert.Equal(threadId, (await recipient.GetIncomingMessagePreviewAsync(firstId))!.ChannelThreadId);
        var reply = await sender.SendAsync(channel, "Thread reply", channelThreadId: threadId);
        Assert.Equal(reply, events.Last());
        Assert.Equal(threadId, (await recipient.GetIncomingMessagePreviewAsync(reply))!.ChannelThreadId);
        var poll = await sender.CreatePollAsync(channel, "Question?", [new("Yes"), new("No")]);
        Assert.Equal(poll, events.Last());
        Assert.Equal("Question?", (await recipient.GetIncomingMessagePreviewAsync(poll))!.Body);
        var count = events.Count;
        var due = time.Now.AddMinutes(1).UtcDateTime;
        await sender.SendWithAttachmentsAsync(channel, "Later", [], scheduledAtUtc: due);
        Assert.Equal(count, events.Count);
        time.Now = new DateTimeOffset(due);
        Assert.Equal(1, await sender.DeliverDueMessagesAsync());
        var deliveredId = Assert.IsType<long>(events.Last());
        Assert.Equal("Later", (await recipient.GetIncomingMessagePreviewAsync(deliveredId))!.Body);
    }

    [Fact]
    public async Task Inbox_PreviewsRespectMembershipMentionsDeletionAndReadState()
    {
        var (owner, member, outsider) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var channel = await sender.CreateChannelAsync("Inbox", ChannelVisibility.Private, [member]);
        var ordinary = await sender.SendAsync(channel, "Ordinary message");
        var tagged = await sender.SendAsync(channel, "Hello @Member");
        var deleted = await sender.SendAsync(channel, "Removed @Member");
        await sender.DeleteOwnMessageAsync(deleted);
        var hidden = await sender.CreateChannelAsync("Hidden", ChannelVisibility.Private, [outsider]);
        await sender.SendAsync(hidden, "Hidden @Outsider");
        Assert.Equal(new[] { tagged, ordinary }, (await recipient.GetInboxMessagesAsync()).Select(x => x.Id));
        Assert.Equal(tagged, Assert.Single(await recipient.GetInboxMessagesAsync(true)).Id);
        Assert.Empty(await sender.GetInboxMessagesAsync());
        Assert.Equal(2, await recipient.GetTotalUnreadCountAsync());
        await recipient.MarkConversationReadAsync(channel);
        Assert.Empty(await recipient.GetInboxMessagesAsync());
        Assert.Empty(await recipient.GetInboxMessagesAsync(true));
        await recipient.MarkUnreadAsync(channel, tagged);
        Assert.Equal(tagged, Assert.Single(await recipient.GetInboxMessagesAsync()).Id);
        await recipient.MarkConversationReadAsync(channel);
        var own = await recipient.SendAsync(channel, "My last message");
        await recipient.MarkUnreadAsync(channel, own);
        Assert.Equal(own, Assert.Single(await recipient.GetInboxMessagesAsync()).Id);
        Assert.Empty(await recipient.GetInboxMessagesAsync(true));
    }

    [Fact]
    public async Task Inbox_ScheduledPreviewsBelongOnlyToSenderAndDisappearAfterDelivery()
    {
        var (owner, member, outsider) = await UsersAsync();
        var time = new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var sender = Service(owner, time);
        var channel = await sender.CreateChannelAsync("Queue", ChannelVisibility.Private, [member]);
        var due = time.Now.AddHours(1).UtcDateTime;
        var queuedId = await sender.SendWithAttachmentsAsync(channel, "Later", [new("note.txt", System.Text.Encoding.UTF8.GetBytes("Private attachment"))], scheduledAtUtc: due);
        var preview = Assert.Single(await sender.GetScheduledMessagesAsync());
        Assert.Equal(queuedId, preview.Id);
        Assert.Equal(due, preview.AtUtc);
        Assert.Equal("note.txt", Assert.Single(preview.AttachmentNames));
        Assert.Empty(await Service(member, time).GetScheduledMessagesAsync());
        Assert.Empty(await Service(outsider, time).GetScheduledMessagesAsync());
        Assert.Empty(await Service(member, time).GetInboxMessagesAsync());
        time.Now = new DateTimeOffset(due);
        Assert.Equal(1, await sender.DeliverDueMessagesAsync());
        Assert.Empty(await sender.GetScheduledMessagesAsync());
        Assert.Equal("Later", Assert.Single(await Service(member, time).GetInboxMessagesAsync()).Body);
    }

    [Fact]
    public async Task Inbox_ReadingOneMessageKeepsEarlierAndLaterMessagesUnreadAndUpdatesAlerts()
    {
        var (owner, member, outsider) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var channel = await sender.CreateChannelAsync("Read individually", ChannelVisibility.Private, [member, outsider]);
        var first = await sender.SendAsync(channel, "First");
        var tagged = await sender.SendAsync(channel, "Hello @Member");
        var last = await sender.SendAsync(channel, "Last");
        Assert.True(await recipient.HasUnreadMentionAsync());
        Assert.True(await recipient.HasUnreadAlertAsync());
        await recipient.MarkMessageReadAsync(channel, tagged);
        var reopened = Service(member);
        Assert.Equal(new[] { last, first }, (await reopened.GetInboxMessagesAsync()).Select(x => x.Id));
        Assert.Empty(await reopened.GetInboxMessagesAsync(true));
        Assert.Equal(2, await reopened.GetTotalUnreadCountAsync());
        var conversation = Assert.Single(await reopened.GetConversationsAsync());
        Assert.Equal(2, conversation.UnreadCount);
        Assert.False(conversation.HasUnreadMention);
        Assert.False(await reopened.HasUnreadMentionAsync());
        Assert.False(await reopened.HasUnreadAlertAsync());
        Assert.Equal(3, await Service(outsider).GetTotalUnreadCountAsync());
        await reopened.MarkMessageReadAsync(channel, last);
        Assert.Equal(first, Assert.Single(await reopened.GetInboxMessagesAsync()).Id);
        Assert.Equal(1, await reopened.GetTotalUnreadCountAsync());
        await reopened.MarkReadAsync(channel, last);
        Assert.Empty(await reopened.GetInboxMessagesAsync());
        await using var db = database.CreateDbContext();
        Assert.Empty(db.Set<ChatMessageRead>());
    }

    [Fact]
    public async Task Inbox_IndividualReadsAuthorizeAndSerializeDuplicatesAndManualUnread()
    {
        var (owner, member, outsider) = await UsersAsync();
        var sender = Service(owner);
        var channel = await sender.CreateChannelAsync("Reads", ChannelVisibility.Private, [member]);
        var first = await sender.SendAsync(channel, "First");
        var last = await sender.SendAsync(channel, "Last @Member");
        var other = await sender.CreateChannelAsync("Other", ChannelVisibility.Private, [member]);
        var elsewhere = await sender.SendAsync(other, "Elsewhere");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).MarkMessageReadAsync(channel, last));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(member).MarkMessageReadAsync(channel, elsewhere));
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Service(member).MarkMessageReadAsync(channel, last)));
        await using (var db = database.CreateDbContext()) Assert.Single(db.Set<ChatMessageRead>());
        Assert.Equal(first, Assert.Single(await Service(member).GetInboxMessagesAsync(), x => x.ConversationId == channel).Id);
        await Service(member).MarkUnreadAsync(channel, last);
        Assert.Equal(last, Assert.Single(await Service(member).GetInboxMessagesAsync(true)).Id);
        await Service(member).MarkMessageReadAsync(channel, last);
        Assert.Empty(await Service(member).GetInboxMessagesAsync(true));
        await Service(member).MarkConversationReadAsync(channel);
        await using (var db = database.CreateDbContext()) Assert.Empty(db.Set<ChatMessageRead>());
        var own = await Service(member).SendAsync(channel, "My message");
        await Service(member).MarkUnreadAsync(channel, own);
        Assert.Equal(own, Assert.Single(await Service(member).GetInboxMessagesAsync(), x => x.ConversationId == channel).Id);
        await Service(member).MarkMessageReadAsync(channel, own);
        Assert.DoesNotContain(await Service(member).GetInboxMessagesAsync(), x => x.ConversationId == channel);
        Assert.Equal(0, (await Service(member).GetConversationsAsync()).Single(x => x.Id == channel).UnreadCount);
        var deleted = await sender.SendAsync(channel, "Delete me");
        await sender.DeleteOwnMessageAsync(deleted);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(member).MarkMessageReadAsync(channel, deleted));
    }

    [Fact]
    public async Task Inbox_ReadAllIncludesMessagesBeyondPreviewLimitAndManualUnreadWithoutAffectingOthersOrSchedules()
    {
        var (owner, member, outsider) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var channel = await sender.CreateChannelAsync("Many unreads", ChannelVisibility.Private, [member, outsider]);
        var other = await sender.CreateChannelAsync("Other unreads", ChannelVisibility.Private, [member]);
        var empty = await sender.CreateChannelAsync("Empty manual unread", ChannelVisibility.Private, [member]);
        await using (var db = database.CreateDbContext())
        {
            db.ChatMessages.AddRange(Enumerable.Range(0, 120).Select(index => new ChatMessage
            {
                ConversationId = channel, SenderUserId = owner, Body = $"Unread {index}", SentAtUtc = DateTime.UtcNow
            }));
            (await db.ChatConversationMembers.FindAsync(empty, member))!.IsManuallyUnread = true;
            await db.SaveChangesAsync();
        }
        var tagged = await sender.SendAsync(channel, "Hello @Member");
        await recipient.MarkMessageReadAsync(channel, tagged);
        await sender.SendAsync(other, "Other @Member");
        var direct = await sender.OpenDirectAsync(member);
        await sender.SendAsync(direct, "Direct unread");
        var self = await recipient.OpenDirectAsync(member);
        var own = await recipient.SendAsync(self, "My manually unread message");
        await recipient.MarkUnreadAsync(self, own);
        await recipient.SendWithAttachmentsAsync(other, "Still scheduled", [], scheduledAtUtc: DateTime.UtcNow.AddHours(1));
        Assert.Equal(100, (await recipient.GetInboxMessagesAsync()).Count);
        Assert.True(await recipient.GetTotalUnreadCountAsync() > 100);
        Assert.True(await recipient.HasUnreadAlertAsync());
        await Task.WhenAll(recipient.MarkAllUnreadReadAsync(), Service(member).MarkAllUnreadReadAsync());
        var reopened = Service(member);
        Assert.Empty(await reopened.GetInboxMessagesAsync());
        Assert.Empty(await reopened.GetInboxMessagesAsync(true));
        Assert.Equal(0, await reopened.GetTotalUnreadCountAsync());
        Assert.False(await reopened.HasUnreadAlertAsync());
        Assert.False(await reopened.HasUnreadMentionAsync());
        Assert.All(await reopened.GetConversationsAsync(), conversation =>
        {
            Assert.Equal(0, conversation.UnreadCount);
            Assert.False(conversation.IsManuallyUnread);
        });
        Assert.Equal(121, await Service(outsider).GetTotalUnreadCountAsync());
        Assert.Single(await reopened.GetScheduledMessagesAsync());
        await using (var db = database.CreateDbContext()) Assert.Empty(db.Set<ChatMessageRead>());
        var arriving = await sender.SendAsync(channel, "Arrived after read all");
        Assert.Equal(arriving, Assert.Single(await reopened.GetInboxMessagesAsync()).Id);
        Assert.Equal(1, await reopened.GetTotalUnreadCountAsync());
    }

    [Fact]
    public async Task ScheduledMessages_StayPrivateUntilDueAndDeliverOnceWithAttachmentsAndReplies()
    {
        var (owner, member, _) = await UsersAsync();
        var time = new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var chat = Service(owner, time);
        var conversation = await chat.OpenDirectAsync(member);
        var original = await chat.SendAsync(conversation, "Original");
        var due = time.Now.AddHours(1).UtcDateTime;
        var before = (await Service(member, time).GetConversationsAsync()).Single(x => x.Id == conversation);
        await chat.SendWithAttachmentsAsync(conversation, "Scheduled @Member",
            [new("note.txt", System.Text.Encoding.UTF8.GetBytes("A scheduled attachment"))],
            replyToMessageId: original, scheduledAtUtc: due);
        Assert.Single((await Service(member, time).GetThreadAsync(conversation)).Messages);
        var after = (await Service(member, time).GetConversationsAsync()).Single(x => x.Id == conversation);
        Assert.Equal(before.UnreadCount, after.UnreadCount);
        Assert.Equal(before.ActivityAtUtc, after.ActivityAtUtc);
        Assert.Equal(0, await chat.DeliverDueMessagesAsync());
        time.Now = new DateTimeOffset(due);
        // New service instances simulate restart and competing delivery workers.
        var counts = await Task.WhenAll(Service(owner, time).DeliverDueMessagesAsync(), Service(owner, time).DeliverDueMessagesAsync());
        Assert.Equal(1, counts.Sum());
        Assert.Equal(0, await chat.DeliverDueMessagesAsync());
        var sent = (await Service(member, time).GetThreadAsync(conversation)).Messages.Last();
        Assert.True(sent.Id > original);
        Assert.Equal(due, sent.SentAtUtc);
        Assert.Equal(original, sent.ReplyToMessageId);
        var file = await Service(member, time).GetAttachmentAsync(Assert.Single(sent.Attachments).Id);
        Assert.Equal("A scheduled attachment", System.Text.Encoding.UTF8.GetString(file!.Content));
    }

    [Fact]
    public async Task ScheduledMessages_ValidateTimeScopeAndSenderAtDelivery()
    {
        var (owner, member, outsider) = await UsersAsync();
        var time = new MutableTimeProvider(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        var chat = Service(owner, time);
        var channel = await chat.CreateChannelAsync("Scheduled", ChannelVisibility.Private, [member]);
        var due = time.Now.AddHours(1).UtcDateTime;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider, time)
            .SendWithAttachmentsAsync(channel, "Forbidden", [], scheduledAtUtc: due));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat
            .SendWithAttachmentsAsync(channel, "Past", [], scheduledAtUtc: time.Now.UtcDateTime));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat
            .SendWithAttachmentsAsync(channel, "Local", [], scheduledAtUtc: DateTime.SpecifyKind(due, DateTimeKind.Unspecified)));
        var thread = await chat.CreateChannelThreadAsync(channel, "Later", "First");
        await chat.SendWithAttachmentsAsync(channel, "Thread delivery", [], channelThreadId: thread, scheduledAtUtc: due);
        await Service(member, time).SendWithAttachmentsAsync(channel, "Inactive", [], scheduledAtUtc: due);
        await using (var db = database.CreateDbContext())
        {
            var user = await db.Users.FindAsync(member);
            user!.IsActive = false;
            await db.SaveChangesAsync();
        }
        time.Now = new DateTimeOffset(due);
        Assert.Equal(1, await chat.DeliverDueMessagesAsync());
        Assert.Contains((await chat.GetThreadAsync(channel, channelThreadId: thread)).Messages, x => x.Body == "Thread delivery");
        Assert.DoesNotContain((await chat.GetThreadAsync(channel)).Messages, x => x.Body == "Inactive");
        await using var check = database.CreateDbContext();
        Assert.NotNull(Assert.Single(check.Set<ChatScheduledMessage>()).Failure);
    }

    [Fact]
    public async Task Polls_EnforceSchedulePermissionsAndSingleChoice()
    {
        var (owner, member, outsider) = await UsersAsync();
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var chat = Service(owner, time);
        var id = await chat.CreateChannelAsync("Polls", ChannelVisibility.Private, [member]);
        var start = time.Now.AddHours(1).UtcDateTime;
        var messageId = await chat.CreatePollAsync(id, "Lunch?", [new("Pizza", "🍕"), new("Soup")], 4, startsAtUtc: start);
        var poll = Assert.Single((await chat.GetThreadAsync(id)).Messages).Poll!;
        Assert.False(poll.IsOpen);
        Assert.Null(poll.Answers[1].Emoji);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider, time).CreatePollAsync(id, "No", [new("A"), new("B")]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider, time).TogglePollVoteAsync(messageId, poll.Answers[0].Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.TogglePollVoteAsync(messageId, poll.Answers[0].Id));
        time.Now = new DateTimeOffset(start);
        await chat.TogglePollVoteAsync(messageId, poll.Answers[0].Id);
        await chat.TogglePollVoteAsync(messageId, poll.Answers[1].Id);
        poll = Assert.Single((await chat.GetThreadAsync(id)).Messages).Poll!;
        Assert.Equal(1, poll.Voters);
        Assert.Equal(0, poll.Answers[0].Votes);
        Assert.Empty(poll.Answers[0].Users);
        Assert.Equal(new ChatUser(owner, "Owner"), Assert.Single(poll.Answers[1].Users));
        Assert.True(poll.Answers[1].Selected);
        await Service(member, time).TogglePollVoteAsync(messageId, poll.Answers[0].Id);
        time.Now = time.Now.AddHours(4);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.TogglePollVoteAsync(messageId, poll.Answers[0].Id));
        poll = Assert.Single((await chat.GetThreadAsync(id)).Messages).Poll!;
        Assert.False(poll.IsOpen);
        Assert.Equal(2, poll.Voters);
        Assert.Equal(new ChatUser(member, "Member"), Assert.Single(poll.Answers[0].Users));
        Assert.Equal(new ChatUser(owner, "Owner"), Assert.Single(poll.Answers[1].Users));
    }

    [Fact]
    public async Task Polls_SupportMultipleVotesAndThreadAndDirectScopes()
    {
        var (owner, member, _) = await UsersAsync();
        var chat = Service(owner);
        var channel = await chat.CreateChannelAsync("Polls", ChannelVisibility.Private, [member]);
        var thread = await chat.CreateChannelThreadAsync(channel, "Plans", "Let's decide");
        var message = await chat.CreatePollAsync(channel, "Which days?", [new("Monday"), new("Tuesday"), new("Friday")], 168, true, channelThreadId: thread);
        Assert.DoesNotContain((await chat.GetThreadAsync(channel)).Messages, x => x.Id == message);
        var row = (await chat.GetThreadAsync(channel, channelThreadId: thread)).Messages.Single(x => x.Id == message);
        await Task.WhenAll(row.Poll!.Answers.Take(2).Select(x => chat.TogglePollVoteAsync(message, x.Id)));
        row = (await chat.GetThreadAsync(channel, channelThreadId: thread)).Messages.Single(x => x.Id == message);
        Assert.Equal(2, row.Poll!.Answers.Count(x => x.Selected));
        Assert.All(row.Poll.Answers.Take(2), answer =>
            Assert.Equal(new ChatUser(owner, "Owner"), Assert.Single(answer.Users)));
        await chat.TogglePollVoteAsync(message, row.Poll.Answers[0].Id);
        Assert.Equal(1, (await chat.GetThreadAsync(channel, channelThreadId: thread)).Messages.Single(x => x.Id == message).Poll!.Answers.Count(x => x.Selected));
        Assert.Equal(2, Assert.Single(await chat.GetChannelThreadsAsync(channel)).MessageCount);
        var direct = await chat.OpenDirectAsync(member);
        var directPoll = await chat.CreatePollAsync(direct, "Coffee?", [new("Yes"), new("No")]);
        var directRow = Assert.Single((await Service(member).GetThreadAsync(direct)).Messages);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.TogglePollVoteAsync(message, directRow.Poll!.Answers[0].Id));
        await Service(member).TogglePollVoteAsync(directPoll, directRow.Poll!.Answers[0].Id);
        Assert.Equal(1, Assert.Single((await chat.GetThreadAsync(direct)).Messages).Poll!.Voters);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.CreatePollAsync(direct, "Wrong thread", [new("A"), new("B")], channelThreadId: thread));
    }

    [Fact]
    public async Task Polls_ValidateInputsAndSerializeSingleChoiceVotes()
    {
        var (owner, member, _) = await UsersAsync();
        var chat = Service(owner);
        var direct = await chat.OpenDirectAsync(member);
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.CreatePollAsync(direct, "", [new("A"), new("B")]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.CreatePollAsync(direct, "Question", [new("A")]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.CreatePollAsync(direct, "Question", [new("A"), new(" ")]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.CreatePollAsync(direct, "Question", [new("A", "abc"), new("B")]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.CreatePollAsync(direct, "Question", [new("A"), new("B")], 2));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.CreatePollAsync(direct, "Question", [new("A"), new("B")], startsAtUtc: DateTime.UtcNow.AddMinutes(-1)));
        var message = await chat.CreatePollAsync(direct, "Question", [new("A"), new("B")]);
        var poll = Assert.Single((await chat.GetThreadAsync(direct)).Messages).Poll!;
        await Task.WhenAll(poll.Answers.Select(x => chat.TogglePollVoteAsync(message, x.Id)));
        Assert.Single(Assert.Single((await chat.GetThreadAsync(direct)).Messages).Poll!.Answers, x => x.Selected);
        await using var db = database.CreateDbContext();
        var selected = Assert.Single(Assert.Single((await chat.GetThreadAsync(direct)).Messages).Poll!.Answers, x => x.Selected);
        db.Add(new ChatPollVote { PollMessageId = message, UserId = owner, AnswerId = poll.Answers.Single(x => x.Id != selected.Id).Id });
        await Assert.ThrowsAsync<Microsoft.EntityFrameworkCore.DbUpdateException>(() => db.SaveChangesAsync());
    }
    [Fact]
    public async Task ChannelThreads_AreScopedToChannelAndKeepPinsSeparate()
    {
        var (owner, member, outsider) = await UsersAsync();
        var chat = Service(owner);
        var channelId = await chat.CreateChannelAsync("Engineering", ChannelVisibility.Private, [member]);
        var otherChannelId = await chat.CreateChannelAsync("Other", ChannelVisibility.Private, [member]);
        var directId = await chat.OpenDirectAsync(member);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(outsider).CreateChannelThreadAsync(channelId, "Forbidden", "No access"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(outsider).GetChannelThreadsAsync(channelId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(outsider).GetThreadAsync(channelId, channelThreadId: 123));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            chat.CreateChannelThreadAsync(directId, "Wrong place", "No channel"));

        var firstId = await chat.CreateChannelThreadAsync(channelId, "First thread", "First message");
        var secondId = await chat.CreateChannelThreadAsync(channelId, "Second thread", "Second message");
        var channelMessageId = await chat.SendAsync(channelId, "In the channel");
        var firstMessageId = await chat.SendAsync(channelId, "In the first thread", channelThreadId: firstId);
        var secondMessageId = await chat.SendAsync(channelId, "In the second thread", channelThreadId: secondId);
        await chat.SetMessagePinnedAsync(channelMessageId, true);
        await chat.SetMessagePinnedAsync(firstMessageId, true);
        await chat.SetMessagePinnedAsync(secondMessageId, true);

        var channel = await Service(member).GetThreadAsync(channelId);
        Assert.Equal(2, channel.Messages.Count(x => x.Type == ChatMessageType.ThreadNotice));
        Assert.DoesNotContain(channel.Messages, x => x.Id == firstMessageId || x.Id == secondMessageId);
        Assert.Equal(channelMessageId, Assert.Single(channel.PinnedMessages).Id);
        var first = await Service(member).GetThreadAsync(channelId, channelThreadId: firstId);
        Assert.Equal("First thread", first.ChannelThread?.Title);
        Assert.Contains(first.Messages, x => x.Id == firstMessageId);
        Assert.DoesNotContain(first.Messages, x => x.Id == secondMessageId);
        Assert.Equal(firstMessageId, Assert.Single(first.PinnedMessages).Id);
        Assert.Equal(secondMessageId, Assert.Single((await chat.GetThreadAsync(channelId,
            channelThreadId: secondId)).PinnedMessages).Id);
        var memberChat = Service(member);
        Assert.Equal((await memberChat.GetConversationsAsync()).Sum(x => x.UnreadCount),
            await memberChat.GetTotalUnreadCountAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chat.SendAsync(channelId, "cross thread", replyToMessageId: secondMessageId,
                channelThreadId: firstId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chat.SendAsync(otherChannelId, "cross channel", channelThreadId: firstId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chat.GetThreadAsync(otherChannelId, channelThreadId: firstId));
    }

    [Fact]
    public async Task ChannelThreads_ExpireAfterThreeDaysAndReviveOnReply()
    {
        var (owner, member, _) = await UsersAsync();
        var time = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var chat = Service(owner, time);
        var channelId = await chat.CreateChannelAsync("Operations", ChannelVisibility.Private, [member]);
        var threadId = await chat.CreateChannelThreadAsync(channelId, "Shift handoff", "Initial handoff");
        Assert.Equal(threadId, Assert.Single(await chat.GetActiveChannelThreadsAsync()).Id);

        time.Now = time.Now.AddDays(3).AddSeconds(1);
        Assert.Empty(await chat.GetActiveChannelThreadsAsync());
        Assert.Equal(threadId, Assert.Single(await chat.GetChannelThreadsAsync(channelId)).Id);
        await Service(member, time).SendAsync(channelId, "New update", channelThreadId: threadId);
        Assert.Equal(threadId, Assert.Single(await chat.GetActiveChannelThreadsAsync()).Id);
    }

    [Fact]
    public async Task ChannelThread_CanStartFromAnExistingChannelReply()
    {
        var (owner, member, _) = await UsersAsync();
        var chat = Service(owner);
        var channelId = await chat.CreateChannelAsync("Production", ChannelVisibility.Private, [member]);
        var originalId = await chat.SendAsync(channelId, "Original");
        var replyId = await chat.SendAsync(channelId, "The follow-up", replyToMessageId: originalId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chat.CreateChannelThreadAsync(channelId, "Invalid source", "Initial message", startedFromMessageId: originalId));
        var threadId = await chat.CreateChannelThreadAsync(channelId, "Follow-up", "More detail",
            startedFromMessageId: replyId);
        var thread = await Service(member).GetThreadAsync(channelId, channelThreadId: threadId);
        Assert.Equal(replyId, thread.ChannelThread?.StartedFromMessageId);
        Assert.Equal("The follow-up", thread.ChannelThread?.StartedFromMessage);
    }

    [Fact]
    public async Task Attachments_AllowMultipleFilesRequireMembershipAndDisappearWhenMessageIsDeleted()
    {
        var (owner, member, outsider) = await UsersAsync();
        var sender = Service(owner);
        var conversationId = await sender.CreateChannelAsync("Files", ChannelVisibility.Private, [member]);
        var bytes = System.Text.Encoding.UTF8.GetBytes("A short note\nSecond line");
        var secondBytes = System.Text.Encoding.UTF8.GetBytes("Another file");
        var messageId = await sender.SendWithAttachmentsAsync(conversationId, "",
        [
            new ChatAttachmentUpload("note.txt", bytes),
            new ChatAttachmentUpload("second.txt", secondBytes)
        ]);

        var message = Assert.Single((await Service(member).GetThreadAsync(conversationId)).Messages);
        Assert.Equal(2, message.Attachments.Count);
        var note = message.Attachments.Single(x => x.FileName == "note.txt");
        var second = message.Attachments.Single(x => x.FileName == "second.txt");
        Assert.Equal(ChatAttachmentKind.Text, note.Kind);
        Assert.Equal("A short note\nSecond line", note.Text);
        Assert.Equal(bytes, (await Service(member).GetAttachmentAsync(note.Id))?.Content);
        Assert.Equal(secondBytes, (await Service(member).GetAttachmentAsync(second.Id))?.Content);
        Assert.Null(await Service(outsider).GetAttachmentAsync(note.Id));
        await sender.DeleteOwnMessageAsync(messageId);
        Assert.Null(await Service(member).GetAttachmentAsync(note.Id));
        Assert.Null(await Service(member).GetAttachmentAsync(second.Id));
    }

    [Fact]
    public async Task DirectConversation_ReusesTheSamePairIncludingConcurrentOpens()
    {
        var (owner, member, outsider) = await UsersAsync();
        var first = Service(owner);
        var second = Service(member);
        var ids = await Task.WhenAll(first.OpenDirectAsync(member), second.OpenDirectAsync(owner));
        Assert.Equal(ids[0], ids[1]);
        Assert.Equal(ids[0], await first.OpenDirectAsync(member));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).GetThreadAsync(ids[0]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).SendAsync(ids[0], "forged"));
        await using var db = database.CreateDbContext();
        Assert.Single(db.ChatConversations);
        Assert.Equal(2, db.ChatConversationMembers.Count());
    }

    [Fact]
    public async Task SelfConversation_ReusesOneMembershipAndKeepsMessagesPrivate()
    {
        var (owner, member, _) = await UsersAsync();
        var chat = Service(owner);
        Assert.Contains(await chat.GetActiveUsersAsync(), user => user.Id == owner);
        var ids = await Task.WhenAll(chat.OpenDirectAsync(owner), Service(owner).OpenDirectAsync(owner));
        Assert.Equal(ids[0], ids[1]);
        Assert.Equal(ids[0], await chat.OpenDirectAsync(owner));
        var messageId = await chat.SendAsync(ids[0], "Note to myself");
        var thread = await chat.GetThreadAsync(ids[0]);
        Assert.Single(thread.Members);
        Assert.Contains(thread.Messages, message => message.Id == messageId && message.Body == "Note to myself");
        var row = Assert.Single(await chat.GetConversationsAsync());
        Assert.Equal(thread.Members[0].Name, row.Name);
        Assert.Equal(owner, row.OtherUserId);
        Assert.Equal(0, row.UnreadCount);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(member).GetThreadAsync(ids[0]));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(member).SendAsync(ids[0], "forged"));
        await using var db = database.CreateDbContext();
        Assert.Single(db.ChatConversationMembers);
    }

    [Fact]
    public async Task PrivateChannel_IsInvisibleAndInaccessibleToNonMembers()
    {
        var (owner, member, outsider) = await UsersAsync();
        var id = await Service(owner).CreateChannelAsync("Quality", ChannelVisibility.Private, [member]);
        var other = Service(outsider);
        Assert.Empty(await other.GetConversationsAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other.GetThreadAsync(id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => other.SendAsync(id, "forged"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(member).AddPrivateMemberAsync(id, outsider));
        Assert.Single(await Service(member).GetConversationsAsync());
    }

    [Fact]
    public async Task ChannelGroups_ExposeOnlyMemberChannelsAndProtectHiddenChannels()
    {
        var (owner, member, outsider) = await UsersAsync();
        var chat = Service(owner);
        var shared = await chat.CreateChannelAsync("Shared", ChannelVisibility.Private, [member]);
        var hidden = await chat.CreateChannelAsync("Hidden", ChannelVisibility.Private, [outsider]);
        var groupId = await chat.CreateChannelGroupAsync("Operations", shared);
        await chat.MoveChannelAsync(hidden, groupId, null);

        var nonMemberId = Guid.NewGuid().ToString();
        await using (var db = database.CreateDbContext())
        {
            db.Users.Add(new ApplicationUser
            {
                Id = nonMemberId, UserName = "nonmember", DisplayName = "Nonmember", IsActive = true
            });
            await db.SaveChangesAsync();
        }
        Assert.Empty(await Service(nonMemberId).GetChannelGroupsAsync());
        var memberGroup = Assert.Single(await Service(member).GetChannelGroupsAsync());
        Assert.Equal(groupId, memberGroup.Id);
        Assert.False(memberGroup.CanManage);
        Assert.Equal([shared], (await Service(member).GetConversationsAsync())
            .Where(x => x.Kind == ConversationKind.Channel).Select(x => x.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(member).RenameChannelGroupAsync(groupId, "Other"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(member).MoveChannelAsync(hidden, null, null));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).MoveChannelAsync(shared, null, null));

        var separate = await Service(nonMemberId).CreateChannelAsync("Separate", ChannelVisibility.Private, []);
        var separateGroup = await Service(nonMemberId).CreateChannelGroupAsync("Operations", separate);
        Assert.NotEqual(groupId, separateGroup);
        Assert.Single(await Service(nonMemberId).GetChannelGroupsAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(member).MoveChannelAsync(shared, separateGroup, null));

        var direct = await chat.OpenDirectAsync(member);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.MoveChannelAsync(direct, groupId, null));
    }

    [Fact]
    public async Task ChannelLayout_PersistsGroupAndChannelOrderAcrossUsers()
    {
        var (owner, member, _) = await UsersAsync();
        var chat = Service(owner);
        var first = await chat.CreateChannelAsync("First", ChannelVisibility.Public, []);
        var second = await chat.CreateChannelAsync("Second", ChannelVisibility.Public, []);
        var third = await chat.CreateChannelAsync("Third", ChannelVisibility.Public, []);
        var groupId = await chat.CreateChannelGroupAsync("Team", second);
        await chat.MoveChannelAsync(third, groupId, second);
        await chat.MoveChannelAsync(first, groupId, null);

        var memberChat = Service(member);
        Assert.Equal([third, second, first], (await memberChat.GetConversationsAsync())
            .Where(x => x.ChannelGroupId == groupId)
            .OrderBy(x => x.ChannelSortOrder).Select(x => x.Id));
        Assert.Equal("Team", Assert.Single(await memberChat.GetChannelGroupsAsync()).Name);

        await memberChat.MoveChannelAsync(first, null, null);
        await memberChat.RenameChannelGroupAsync(groupId, "Work");
        Assert.Equal("Work", Assert.Single(await chat.GetChannelGroupsAsync()).Name);
        var otherGroupId = await chat.CreateChannelGroupAsync("Other", first);
        await memberChat.MoveChannelGroupAsync(otherGroupId, groupId);
        Assert.Equal([otherGroupId, groupId], (await chat.GetChannelGroupsAsync()).Select(x => x.Id));
        await memberChat.DeleteChannelGroupAsync(groupId);
        await memberChat.DeleteChannelGroupAsync(otherGroupId);
        Assert.Empty(await chat.GetChannelGroupsAsync());
        Assert.All((await chat.GetConversationsAsync()).Where(x => x.Kind == ConversationKind.Channel),
            row => Assert.Null(row.ChannelGroupId));

        var oldGroup = await chat.CreateChannelGroupAsync("Old", first);
        var replacement = await chat.CreateChannelGroupAsync("Replacement", first);
        Assert.Equal([oldGroup, replacement], (await chat.GetChannelGroupsAsync()).Select(x => x.Id));
        Assert.Equal(replacement, Assert.Single(await memberChat.GetChannelGroupsAsync()).Id);
    }

    [Fact]
    public async Task EmptyChannelFolder_IsVisibleOnlyToItsCreatorUntilPopulated()
    {
        var (owner, member, _) = await UsersAsync();
        var ownerChat = Service(owner);
        var memberChat = Service(member);
        var channelId = await ownerChat.CreateChannelAsync("Shared", ChannelVisibility.Private, [member]);
        var folderId = await ownerChat.CreateChannelGroupAsync("New folder");

        Assert.True(Assert.Single(await ownerChat.GetChannelGroupsAsync()).CanManage);
        Assert.Empty(await memberChat.GetChannelGroupsAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => memberChat.MoveChannelAsync(channelId, folderId, null));

        await ownerChat.MoveChannelAsync(channelId, folderId, null);
        Assert.Equal(folderId, Assert.Single(await memberChat.GetChannelGroupsAsync()).Id);
        await ownerChat.MoveChannelAsync(channelId, null, null);
        Assert.Empty(await memberChat.GetChannelGroupsAsync());
        Assert.Equal(folderId, Assert.Single(await ownerChat.GetChannelGroupsAsync()).Id);
        await ownerChat.DeleteChannelGroupAsync(folderId);
        Assert.Empty(await ownerChat.GetChannelGroupsAsync());
    }

    [Fact]
    public async Task CreateChannelInFolder_AssignsDestinationAndRequiresFolderAccess()
    {
        var (owner, member, _) = await UsersAsync();
        var ownerChat = Service(owner);
        var folderId = await ownerChat.CreateChannelGroupAsync("Operations");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(member).CreateChannelAsync("Not allowed", ChannelVisibility.Private, [], folderId));
        Assert.Empty(await Service(member).GetConversationsAsync());

        var channelId = await ownerChat.CreateChannelAsync("New channel", ChannelVisibility.Private, [member], folderId);
        Assert.Equal(folderId, (await ownerChat.GetConversationsAsync()).Single(x => x.Id == channelId).ChannelGroupId);
        Assert.Equal(folderId, Assert.Single(await Service(member).GetChannelGroupsAsync()).Id);
    }

    [Fact]
    public async Task NestedFolders_PersistOrderAndRejectCycles()
    {
        var (owner, member, _) = await UsersAsync();
        var chat = Service(owner);
        var first = await chat.CreateChannelAsync("First", ChannelVisibility.Private, [member]);
        var second = await chat.CreateChannelAsync("Second", ChannelVisibility.Private, [member]);
        var parent = await chat.CreateChannelGroupAsync("Parent", first);
        var child = await chat.CreateChannelGroupAsync("Child", parentGroupId: parent);
        var grandchild = await chat.CreateChannelGroupAsync("Grandchild", parentGroupId: child);
        await chat.MoveLayoutItemAsync(false, second, grandchild, null, null);
        await chat.MoveLayoutItemAsync(true, child, parent, false, first);

        var groups = await Service(member).GetChannelGroupsAsync();
        Assert.Equal(parent, groups.Single(x => x.Id == child).ParentGroupId);
        Assert.Equal(child, groups.Single(x => x.Id == grandchild).ParentGroupId);
        Assert.True(groups.Single(x => x.Id == child).SortOrder <
            (await chat.GetConversationsAsync()).Single(x => x.Id == first).ChannelSortOrder);
        Assert.Equal(grandchild, (await chat.GetConversationsAsync()).Single(x => x.Id == second).ChannelGroupId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chat.MoveLayoutItemAsync(true, parent, grandchild, null, null));
        Assert.Null((await chat.GetChannelGroupsAsync()).Single(x => x.Id == parent).ParentGroupId);
    }

    [Fact]
    public async Task NestedFolders_HidePrivateBranchesAndPromoteChildrenOnDelete()
    {
        var (owner, member, outsider) = await UsersAsync();
        var chat = Service(owner);
        var shared = await chat.CreateChannelAsync("Shared", ChannelVisibility.Private, [member]);
        var hidden = await chat.CreateChannelAsync("Hidden", ChannelVisibility.Private, [outsider]);
        var parent = await chat.CreateChannelGroupAsync("Parent");
        var visibleChild = await chat.CreateChannelGroupAsync("Visible", shared, parent);
        var hiddenChild = await chat.CreateChannelGroupAsync("Hidden folder", hidden, parent);

        var memberGroups = await Service(member).GetChannelGroupsAsync();
        Assert.Contains(memberGroups, x => x.Id == parent && !x.CanManage);
        Assert.Contains(memberGroups, x => x.Id == visibleChild);
        Assert.DoesNotContain(memberGroups, x => x.Id == hiddenChild);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Service(member).CreateChannelGroupAsync("Parent"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(member).MoveLayoutItemAsync(true, parent, null, null, null));

        await chat.DeleteChannelGroupAsync(parent);
        var ownerGroups = await chat.GetChannelGroupsAsync();
        Assert.DoesNotContain(ownerGroups, x => x.Id == parent);
        Assert.All(ownerGroups.Where(x => x.Id == visibleChild || x.Id == hiddenChild),
            x => Assert.Null(x.ParentGroupId));
        Assert.Equal(visibleChild, (await chat.GetConversationsAsync()).Single(x => x.Id == shared).ChannelGroupId);
    }

    [Fact]
    public async Task PublicChannel_IncludesAllActiveUsersAndEnrollsNewUsersAutomatically()
    {
        var (owner, member, outsider) = await UsersAsync();
        var id = await Service(owner).CreateChannelAsync("Production", ChannelVisibility.Public, []);
        await using (var db = database.CreateDbContext())
            Assert.Equal(3, db.ChatConversationMembers.Count(x => x.ConversationId == id));
        Assert.Equal(id, Assert.Single(await Service(member).GetConversationsAsync()).Id);
        Assert.Equal(id, Assert.Single(await Service(outsider).GetConversationsAsync()).Id);

        await Service(owner).SendAsync(id, "Earlier message");
        var newUserId = Guid.NewGuid().ToString();
        await using (var db = database.CreateDbContext())
        {
            db.Users.Add(new ApplicationUser
            {
                Id = newUserId, UserName = "newcomer", DisplayName = "Newcomer", IsActive = true
            });
            await db.SaveChangesAsync();
        }
        var newcomer = Service(newUserId);
        Assert.Equal(id, Assert.Single(await newcomer.GetConversationsAsync()).Id);
        Assert.Equal(0, (await newcomer.GetConversationsAsync()).Single().UnreadCount);
        Assert.Equal("Earlier message", (await newcomer.GetThreadAsync(id)).Messages.Single().Body);
        await newcomer.SendAsync(id, "Hello everyone");
        await using (var db = database.CreateDbContext())
            Assert.Equal(4, db.ChatConversationMembers.Count(x => x.ConversationId == id));
    }

    [Fact]
    public async Task PrivateOwner_CanAddAndRemoveMembersAndRemovalRevokesAccess()
    {
        var (owner, member, outsider) = await UsersAsync();
        var ownerService = Service(owner);
        var id = await ownerService.CreateChannelAsync("Issue", ChannelVisibility.Private, [member]);
        await ownerService.AddPrivateMemberAsync(id, outsider);
        await ownerService.AddPrivateMemberAsync(id, outsider);
        Assert.Single(await Service(outsider).GetConversationsAsync());
        await ownerService.RemovePrivateMemberAsync(id, outsider);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).GetThreadAsync(id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ownerService.RemovePrivateMemberAsync(id, owner));
    }

    [Fact]
    public async Task ChannelTopics_PersistNotifyValidateAndRequireOwnerOrAdministrator()
    {
        var (owner, member, outsider) = await UsersAsync();
        var chat = Service(owner);
        var id = await chat.CreateChannelAsync("Topics", ChannelVisibility.Private, [member]);
        var updates = 0;
        using var subscription = notifications.Subscribe(member, () => updates++);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(member).SetChannelTopicAsync(id, "Denied"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).SetChannelTopicAsync(id, "Denied"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => chat.SetChannelTopicAsync(id, new string('x', 4001)));
        Assert.Null((await Service(member).GetThreadAsync(id)).Conversation.Topic);

        await chat.SetChannelTopicAsync(id, "  Current priorities\nSecond line  ");
        Assert.Equal("Current priorities\nSecond line", (await Service(member).GetThreadAsync(id)).Conversation.Topic);
        Assert.Equal(1, updates);
        await chat.RenameChannelAsync(id, "Renamed");
        Assert.Equal("Current priorities\nSecond line", (await Service(member).GetThreadAsync(id)).Conversation.Topic);

        await using (var db = database.CreateDbContext())
        {
            db.UserRoles.Add(new() { UserId = outsider, RoleId = db.Roles.Single(x => x.Name == AppRoles.Administrator).Id });
            await db.SaveChangesAsync();
        }
        await Service(outsider).SetChannelTopicAsync(id, new string('x', 4000));
        Assert.Equal(4000, (await Service(member).GetThreadAsync(id)).Conversation.Topic!.Length);
        await chat.SetChannelTopicAsync(id, " \n ");
        Assert.Null((await Service(member).GetThreadAsync(id)).Conversation.Topic);
        var directId = await chat.OpenDirectAsync(member);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => chat.SetChannelTopicAsync(directId, "Denied"));
    }

    [Fact]
    public async Task PrivateChannelSettings_RequireMembershipAndOwnerForChanges()
    {
        var (owner, member, outsider) = await UsersAsync();
        var ownerService = Service(owner);
        var id = await ownerService.CreateChannelAsync("Initial", ChannelVisibility.Private, [member]);

        Assert.Equal(new[] { owner, member }.OrderBy(x => x), (await Service(member).GetChannelMembersAsync(id))
            .Select(x => x.Id).OrderBy(x => x));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).GetChannelMembersAsync(id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(member).RenameChannelAsync(id, "Wrong"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).RenameChannelAsync(id, "Wrong"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ownerService.RenameChannelAsync(id, " "));

        await ownerService.RenameChannelAsync(id, "  Revised  ");
        Assert.Equal("Revised", (await Service(member).GetConversationsAsync()).Single().Name);
        await ownerService.AddPrivateMemberAsync(id, outsider);
        Assert.Equal(3, (await ownerService.GetChannelMembersAsync(id)).Count);
        await ownerService.RemovePrivateMemberAsync(id, outsider);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).GetChannelMembersAsync(id));

        var directId = await ownerService.OpenDirectAsync(member);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => ownerService.RenameChannelAsync(directId, "Wrong"));
    }

    [Fact]
    public async Task Administrator_CanRenameChannelsWithoutOwningThem()
    {
        var (owner, member, outsider) = await UsersAsync();
        var ownerService = Service(owner);
        var channelId = await ownerService.CreateChannelAsync("First name", ChannelVisibility.Public, []);
        var privateChannelId = await Service(outsider).CreateChannelAsync("Private name", ChannelVisibility.Private, []);
        Assert.False((await Service(member).GetConversationsAsync()).Single().IsOwner);
        Assert.False(await Service(member).IsCurrentUserAdministratorAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(member).RenameChannelAsync(channelId, "Not allowed"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(member).RenameChannelAsync(privateChannelId, "Not allowed"));

        await using (var db = database.CreateDbContext())
        {
            db.UserRoles.Add(new()
            {
                UserId = member,
                RoleId = db.Roles.Single(x => x.Name == AppRoles.Administrator).Id
            });
            await db.SaveChangesAsync();
        }

        Assert.True(await Service(member).IsCurrentUserAdministratorAsync());
        await Service(member).RenameChannelAsync(channelId, "  Updated by administrator  ");
        Assert.Equal("Updated by administrator", (await Service(owner).GetConversationsAsync()).Single().Name);
        await Service(member).RenameChannelAsync(privateChannelId, "Updated private name");
        Assert.Equal("Updated private name", (await Service(outsider).GetConversationsAsync())
            .Single(x => x.Id == privateChannelId).Name);

        await using (var db = database.CreateDbContext())
        {
            db.UserRoles.Remove(db.UserRoles.Single(x => x.UserId == member));
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(member).RenameChannelAsync(channelId, "After role removal"));
    }

    [Fact]
    public async Task Messages_TrackUnreadAndReadProgressAndKeepDeletedAudit()
    {
        var (owner, member, _) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var id = await sender.OpenDirectAsync(member);
        var messageId = await sender.SendAsync(id, "Hello");
        Assert.Equal(1, (await recipient.GetConversationsAsync()).Single().UnreadCount);
        Assert.True(await recipient.HasUnreadAlertAsync());
        Assert.False(await sender.HasUnreadAlertAsync());
        Assert.Equal(0, (await sender.GetConversationsAsync()).Single().UnreadCount);
        Assert.Equal("Hello", (await recipient.GetThreadAsync(id)).Messages.Single().Body);
        await recipient.MarkReadAsync(id, messageId);
        Assert.Equal(0, (await recipient.GetConversationsAsync()).Single().UnreadCount);
        Assert.False(await recipient.HasUnreadAlertAsync());
        var secondMessageId = await sender.SendAsync(id, "Again");
        await Task.WhenAll(
            recipient.MarkReadAsync(id, secondMessageId),
            recipient.MarkReadAsync(id, messageId));
        Assert.Equal(0, (await recipient.GetConversationsAsync()).Single().UnreadCount);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => recipient.DeleteOwnMessageAsync(messageId));
        await sender.DeleteOwnMessageAsync(messageId);
        Assert.DoesNotContain((await recipient.GetThreadAsync(id)).Messages, message => message.Id == messageId);
        await using var db = database.CreateDbContext();
        var stored = await db.ChatMessages.FindAsync(messageId);
        Assert.Equal("Hello", stored!.Body);
        Assert.NotNull(stored.DeletedAtUtc);
        Assert.Equal(owner, stored.DeletedByUserId);
    }

    [Fact]
    public async Task DeletingAnUnreadMessageClearsItsUnreadCountAndAlert()
    {
        var (owner, member, _) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var id = await sender.OpenDirectAsync(member);
        var messageId = await sender.SendAsync(id, "Soon deleted");
        Assert.Equal(1, await recipient.GetTotalUnreadCountAsync());

        await sender.DeleteOwnMessageAsync(messageId);

        Assert.Empty((await recipient.GetThreadAsync(id)).Messages);
        Assert.Equal(0, (await recipient.GetConversationsAsync()).Single().UnreadCount);
        Assert.Equal(0, await recipient.GetTotalUnreadCountAsync());
        Assert.False(await recipient.HasUnreadAlertAsync());
    }

    [Fact]
    public async Task Pinning_PersistsNoticeAndPinnedList_AndDeletionClearsThePin()
    {
        var (owner, member, outsider) = await UsersAsync();
        var ownerChat = Service(owner);
        var memberChat = Service(member);
        var channelId = await ownerChat.CreateChannelAsync("Quality", ChannelVisibility.Private, [member]);
        var messageId = await ownerChat.SendAsync(channelId, "Keep this handy");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).SetMessagePinnedAsync(messageId, true));
        await memberChat.SetMessagePinnedAsync(messageId, true);
        await memberChat.SetMessagePinnedAsync(messageId, true);
        Assert.False((await ownerChat.GetConversationsAsync()).Single(x => x.Id == channelId).HasUnreadMention);

        var thread = await ownerChat.GetThreadAsync(channelId);
        var pinned = Assert.Single(thread.PinnedMessages);
        Assert.Equal(messageId, pinned.Id);
        Assert.Equal("Keep this handy", pinned.Body);
        Assert.NotNull(thread.Messages.Single(x => x.Id == messageId).PinnedAtUtc);
        var notice = Assert.Single(thread.Messages, x => x.Type == ChatMessageType.PinNotice);
        Assert.Equal(member, notice.SenderUserId);
        Assert.Equal(messageId, notice.ReplyToMessageId);
        Assert.Equal("Keep this handy", notice.ReplyToBody);

        await ownerChat.SetMessagePinnedAsync(messageId, false);
        Assert.Empty((await ownerChat.GetThreadAsync(channelId)).PinnedMessages);
        await ownerChat.SetMessagePinnedAsync(messageId, true);
        Assert.Equal(2, (await ownerChat.GetThreadAsync(channelId)).Messages.Count(x => x.Type == ChatMessageType.PinNotice));
        await ownerChat.DeleteOwnMessageAsync(messageId);
        Assert.Empty((await memberChat.GetThreadAsync(channelId)).PinnedMessages);
        await using var db = database.CreateDbContext();
        var stored = await db.ChatMessages.FindAsync(messageId);
        Assert.Null(stored!.PinnedAtUtc);
        Assert.Null(stored.PinnedByUserId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => memberChat.SetMessagePinnedAsync(messageId, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).SetMessagePinnedAsync(messageId, false));
    }

    [Fact]
    public async Task Replies_KeepTheirTargetAndAlertTheOriginalSender()
    {
        var (owner, member, outsider) = await UsersAsync();
        var ownerChat = Service(owner);
        var memberChat = Service(member);
        var channelId = await ownerChat.CreateChannelAsync("Quality", ChannelVisibility.Private, [member]);
        var otherChannelId = await ownerChat.CreateChannelAsync("Other", ChannelVisibility.Private, [member]);
        var originalId = await ownerChat.SendAsync(channelId, "A long original message");
        var otherId = await ownerChat.SendAsync(otherChannelId, "Other channel");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            memberChat.SendAsync(channelId, "Wrong target", replyToMessageId: otherId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(outsider).SendAsync(channelId, "Unauthorized", replyToMessageId: originalId));

        var replyId = await memberChat.SendAsync(channelId, "I checked it", replyToMessageId: originalId);
        var reply = (await ownerChat.GetThreadAsync(channelId)).Messages.Single(x => x.Id == replyId);
        Assert.Equal(originalId, reply.ReplyToMessageId);
        Assert.Equal(owner, reply.ReplyToSenderUserId);
        Assert.Equal("Owner", reply.ReplyToSenderName);
        Assert.Equal("A long original message", reply.ReplyToBody);
        Assert.True((await ownerChat.GetConversationsAsync()).Single(x => x.Id == channelId).HasUnreadMention);
        Assert.True(await ownerChat.HasUnreadAlertAsync());
        await ownerChat.MarkReadAsync(channelId, replyId);
        Assert.False(await ownerChat.HasUnreadAlertAsync());

        await ownerChat.DeleteOwnMessageAsync(originalId);
        reply = (await memberChat.GetThreadAsync(channelId)).Messages.Single(x => x.Id == replyId);
        Assert.Null(reply.ReplyToBody);
        Assert.DoesNotContain((await memberChat.GetThreadAsync(channelId)).Messages, message => message.Id == originalId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            memberChat.SendAsync(channelId, "Deleted target", replyToMessageId: originalId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ownerChat.GetThreadAsync(channelId, throughMessageId: originalId));
    }

    [Fact]
    public async Task MarkUnread_StartsAtSelectedMessageAndCannotCrossConversations()
    {
        var (owner, member, outsider) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var channelId = await sender.CreateChannelAsync("Quality", ChannelVisibility.Private, [member]);
        var otherChannelId = await sender.CreateChannelAsync("Other", ChannelVisibility.Private, [member]);
        var firstId = await sender.SendAsync(channelId, "First");
        var secondId = await sender.SendAsync(channelId, "Second");
        var thirdId = await sender.SendAsync(channelId, "Third");
        var otherId = await sender.SendAsync(otherChannelId, "Other");
        await recipient.MarkReadAsync(channelId, thirdId);

        await recipient.MarkUnreadAsync(channelId, secondId);
        Assert.Equal(2, (await recipient.GetConversationsAsync())
            .Single(x => x.Id == channelId).UnreadCount);
        await recipient.MarkUnreadAsync(channelId, thirdId);
        Assert.Equal(2, (await recipient.GetConversationsAsync())
            .Single(x => x.Id == channelId).UnreadCount);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            recipient.MarkUnreadAsync(channelId, otherId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            Service(outsider).MarkUnreadAsync(channelId, firstId));
        await recipient.MarkUnreadAsync(channelId, firstId);
        Assert.Equal(3, (await recipient.GetConversationsAsync())
            .Single(x => x.Id == channelId).UnreadCount);
        await recipient.MarkReadAsync(channelId, thirdId);
        Assert.Equal(3, (await recipient.GetConversationsAsync())
            .Single(x => x.Id == channelId).UnreadCount);
        await recipient.MarkConversationReadAsync(channelId);
        Assert.Equal(0, (await recipient.GetConversationsAsync())
            .Single(x => x.Id == channelId).UnreadCount);

        await sender.MarkReadAsync(channelId, thirdId);
        await sender.MarkUnreadAsync(channelId, thirdId);
        var ownMessageUnread = (await sender.GetConversationsAsync()).Single(x => x.Id == channelId);
        Assert.True(ownMessageUnread.IsManuallyUnread);
        Assert.Equal(1, ownMessageUnread.UnreadCount);
        Assert.Equal(1, await sender.GetTotalUnreadCountAsync());
        await sender.SendAsync(channelId, "Back in the conversation");
        Assert.False((await sender.GetConversationsAsync()).Single(x => x.Id == channelId).IsManuallyUnread);
        Assert.Equal(0, (await sender.GetConversationsAsync()).Single(x => x.Id == channelId).UnreadCount);

        var directId = await sender.OpenDirectAsync(member);
        var ownDirectMessageId = await sender.SendAsync(directId, "Direct update");
        await sender.MarkReadAsync(directId, ownDirectMessageId);
        await sender.MarkUnreadAsync(directId, ownDirectMessageId);
        Assert.True(await sender.HasUnreadAlertAsync());
        Assert.Equal(1, (await sender.GetConversationsAsync()).Single(x => x.Id == directId).UnreadCount);
    }

    [Fact]
    public async Task ChannelMentions_OnlyTargetMembers_AndUnreadBadgesPreferMentions()
    {
        var (owner, member, outsider) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var channelId = await sender.CreateChannelAsync("Quality", ChannelVisibility.Private, [member]);

        await sender.SendAsync(channelId, "Normal update");
        var row = Assert.Single(await recipient.GetConversationsAsync());
        Assert.Equal(1, row.UnreadCount);
        Assert.False(row.HasUnreadMention);
        Assert.False(await recipient.HasUnreadAlertAsync());

        var taggedMessageId = await sender.SendAsync(channelId, "Please check this, @mEmBeR");
        row = Assert.Single(await recipient.GetConversationsAsync());
        Assert.Equal(2, row.UnreadCount);
        Assert.True(row.HasUnreadMention);
        Assert.True(await recipient.HasUnreadAlertAsync());
        Assert.True(await recipient.HasUnreadMentionAsync());
        Assert.False(await Service(outsider).HasUnreadMentionAsync());
        var tagged = (await recipient.GetThreadAsync(channelId)).Messages.Single(x => x.Id == taggedMessageId);
        Assert.Equal("Please check this, @Member", tagged.Body);
        Assert.Contains(tagged.Parts, part => part.IsTag && part.UserId == member && part.Text == "@Member");

        await recipient.MarkReadAsync(channelId, taggedMessageId);
        Assert.False((await recipient.GetConversationsAsync()).Single().HasUnreadMention);
        Assert.False(await recipient.HasUnreadAlertAsync());
        var outsiderMessageId = await sender.SendAsync(channelId, "@Outsider cannot be tagged here");
        Assert.False((await recipient.GetConversationsAsync()).Single().HasUnreadMention);
        var outsiderMessage = (await recipient.GetThreadAsync(channelId)).Messages.Single(x => x.Id == outsiderMessageId);
        Assert.DoesNotContain(outsiderMessage.Parts, part => part.IsTag);

        var everyoneId = await sender.SendAsync(channelId, "@EvErYoNe please review");
        Assert.Equal("@everyone please review", (await recipient.GetThreadAsync(channelId)).Messages.Single(x => x.Id == everyoneId).Body);
        Assert.True((await recipient.GetConversationsAsync()).Single().HasUnreadMention);
        await sender.DeleteOwnMessageAsync(everyoneId);
        Assert.False((await recipient.GetConversationsAsync()).Single().HasUnreadMention);

        var editableId = await sender.SendAsync(channelId, "Initial text");
        await sender.EditOwnMessageAsync(editableId, "Now tagged: @mEmBeR");
        Assert.Equal("Now tagged: @Member", (await recipient.GetThreadAsync(channelId)).Messages.Single(x => x.Id == editableId).Body);
        Assert.True((await recipient.GetConversationsAsync()).Single().HasUnreadMention);
        await sender.EditOwnMessageAsync(editableId, "No tag now");
        Assert.False((await recipient.GetConversationsAsync()).Single().HasUnreadMention);
    }

    [Fact]
    public async Task HereMentions_UseLiveVisiblePresenceAtSendTime()
    {
        var (owner, member, _) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var channelId = await sender.CreateChannelAsync("Quality", ChannelVisibility.Private, [member]);

        var offlineId = await sender.SendAsync(channelId, "@here offline");
        Assert.False((await recipient.GetConversationsAsync()).Single().HasUnreadMention);
        await recipient.MarkReadAsync(channelId, offlineId);

        await using (var db = database.CreateDbContext())
        {
            var now = DateTime.UtcNow;
            db.UserPresenceSessions.Add(new UserPresenceSession
            {
                Id = Guid.NewGuid(), UserId = member, LastHeartbeatAtUtc = now,
                LastActivityAtUtc = now
            });
            await db.SaveChangesAsync();
        }
        var onlineId = await sender.SendAsync(channelId, "@here online");
        Assert.True((await recipient.GetConversationsAsync()).Single().HasUnreadMention);
        await recipient.MarkReadAsync(channelId, onlineId);

        await using (var db = database.CreateDbContext())
        {
            var user = await db.Users.FindAsync(member);
            user!.PresencePreference = UserPresencePreference.Invisible;
            await db.SaveChangesAsync();
        }
        await sender.SendAsync(channelId, "@here invisible");
        Assert.False((await recipient.GetConversationsAsync()).Single().HasUnreadMention);
    }

    [Fact]
    public async Task SavedTagStillTargetsOriginalUserAfterChannelMembershipAndNamesChange()
    {
        var (owner, member, outsider) = await UsersAsync();
        var sender = Service(owner);
        var channelId = await sender.CreateChannelAsync("Quality", ChannelVisibility.Private, [member]);
        var messageId = await sender.SendAsync(channelId, "Check with @Member");

        await sender.RemovePrivateMemberAsync(channelId, member);
        await using (var db = database.CreateDbContext())
        {
            (await db.Users.FindAsync(member))!.DisplayName = "Renamed";
            (await db.Users.FindAsync(outsider))!.DisplayName = "Member";
            await db.SaveChangesAsync();
        }
        await sender.AddPrivateMemberAsync(channelId, outsider);

        var message = (await sender.GetThreadAsync(channelId)).Messages.Single(x => x.Id == messageId);
        var tag = Assert.Single(message.Parts, x => x.IsTag);
        Assert.Equal("@Member", tag.Text);
        Assert.Equal(member, tag.UserId);
        Assert.Equal("Renamed", tag.Name);
    }

    [Fact]
    public async Task EarlierMentionRecipientsCanStillRenderTheirTag()
    {
        var (owner, member, _) = await UsersAsync();
        var sender = Service(owner);
        var channelId = await sender.CreateChannelAsync("Quality", ChannelVisibility.Private, [member]);
        var messageId = await sender.SendAsync(channelId, "Check with @Member");
        await using (var db = database.CreateDbContext())
        {
            db.ChatMessageTags.RemoveRange(db.ChatMessageTags.Where(x => x.MessageId == messageId));
            await db.SaveChangesAsync();
        }

        var message = (await sender.GetThreadAsync(channelId)).Messages.Single(x => x.Id == messageId);
        Assert.Contains(message.Parts, part => part.IsTag && part.UserId == member);
    }

    [Fact]
    public async Task Reactions_AggregateUsers_AllowMultipleEmoji_AndToggleOnlyTheCurrentUsersVote()
    {
        var (owner, member, outsider) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var conversationId = await sender.OpenDirectAsync(member);
        var messageId = await sender.SendAsync(conversationId, "React here");

        await sender.AddReactionAsync(messageId, "👍");
        await sender.AddReactionAsync(messageId, "👍");
        await sender.AddReactionAsync(messageId, "❤️");
        await recipient.AddReactionAsync(messageId, "👍");
        var reactions = (await sender.GetThreadAsync(conversationId)).Messages.Single().Reactions;
        Assert.Equal(new[] { "👍", "❤️" }, reactions.Select(x => x.Emoji));
        Assert.Equal(new[] { "Owner", "Member" }, reactions[0].Users.Select(x => x.Name));
        Assert.Single(reactions[1].Users);

        await recipient.ToggleReactionAsync(messageId, "👍");
        reactions = (await sender.GetThreadAsync(conversationId)).Messages.Single().Reactions;
        Assert.Equal("Owner", Assert.Single(reactions[0].Users).Name);
        await recipient.ToggleReactionAsync(messageId, "👍");
        Assert.Equal(2, (await sender.GetThreadAsync(conversationId)).Messages.Single().Reactions[0].Users.Count);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(outsider).AddReactionAsync(messageId, "👍"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.AddReactionAsync(messageId, "not emoji"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => sender.AddReactionAsync(messageId, "👍👍"));
        await sender.DeleteOwnMessageAsync(messageId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => recipient.ToggleReactionAsync(messageId, "👍"));
    }

    [Fact]
    public async Task LastReactionEmoji_IsPerUserAndSurvivesRemovalAndMessageDeletion()
    {
        var (owner, member, _) = await UsersAsync();
        var first = Service(owner);
        var second = Service(member);
        var conversationId = await first.OpenDirectAsync(member);
        var messageId = await first.SendAsync(conversationId, "Recent reaction");

        Assert.Null(await first.GetLastReactionEmojiAsync());
        Assert.Null(await second.GetLastReactionEmojiAsync());
        await first.AddReactionAsync(messageId, "👍🏽");
        Assert.Null(await first.GetLastReactionEmojiAsync());
        await first.AddReactionAsync(messageId, "🎉");
        await first.AddReactionAsync(messageId, "👎🏻");
        await first.ToggleReactionAsync(messageId, "👍");
        Assert.Equal("🎉", await first.GetLastReactionEmojiAsync());
        await second.ToggleReactionAsync(messageId, "❤️");
        await second.AddReactionAsync(messageId, "👍🏿");
        Assert.Equal("❤️", await second.GetLastReactionEmojiAsync());

        await first.ToggleReactionAsync(messageId, "🎉");
        await second.ToggleReactionAsync(messageId, "❤️");
        Assert.Equal("🎉", await first.GetLastReactionEmojiAsync());
        Assert.Equal("❤️", await second.GetLastReactionEmojiAsync());

        await first.DeleteOwnMessageAsync(messageId);
        Assert.Equal("🎉", await first.GetLastReactionEmojiAsync());
    }

    [Fact]
    public async Task EmojiTonePreferences_ArePerUserPerEmoji_AndCanReturnToDefault()
    {
        var (owner, member, _) = await UsersAsync();
        var first = Service(owner);
        var second = Service(member);

        Assert.Empty(await first.GetEmojiTonePreferencesAsync());
        await first.SetEmojiTonePreferenceAsync("👍", "👍🏽");
        await first.SetEmojiTonePreferenceAsync("👎", "👎🏻");
        await second.SetEmojiTonePreferenceAsync("👍", "👍🏿");
        Assert.Equal("👍🏽", (await first.GetEmojiTonePreferencesAsync())["👍"]);
        Assert.Equal("👎🏻", (await first.GetEmojiTonePreferencesAsync())["👎"]);
        Assert.Equal("👍🏿", (await second.GetEmojiTonePreferencesAsync())["👍"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            first.SetEmojiTonePreferenceAsync("👍", "👎🏽"));
        await first.SetEmojiTonePreferenceAsync("👍", "👍");
        Assert.False((await first.GetEmojiTonePreferencesAsync()).ContainsKey("👍"));
        Assert.Equal("👍🏿", (await second.GetEmojiTonePreferencesAsync())["👍"]);
    }

    [Fact]
    public async Task GeneratedNeutralEmoji_CanBeUsedAsAReaction()
    {
        var (owner, member, _) = await UsersAsync();
        var service = Service(owner);
        var family = ChatEmojiCatalog.Families.First(x => x.Tones.Count > 1 &&
            !ChatEmojiCatalog.All.Any(option => option.Emoji == x.Default.Emoji));
        var conversationId = await service.OpenDirectAsync(member);
        var messageId = await service.SendAsync(conversationId, "Neutral reaction");

        await service.AddReactionAsync(messageId, family.Default.Emoji);
        Assert.Equal(family.Default.Emoji,
            Assert.Single((await service.GetThreadAsync(conversationId)).Messages.Single().Reactions).Emoji);
    }

    [Fact]
    public async Task ConcurrentReactionAdds_CountEachUserOnce()
    {
        var (owner, member, _) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var conversationId = await sender.OpenDirectAsync(member);
        var messageId = await sender.SendAsync(conversationId, "Concurrent votes");

        await Task.WhenAll(
            sender.AddReactionAsync(messageId, "🎉"),
            sender.AddReactionAsync(messageId, "🎉"),
            recipient.AddReactionAsync(messageId, "🎉"));

        var reaction = Assert.Single((await sender.GetThreadAsync(conversationId)).Messages.Single().Reactions);
        Assert.Equal(2, reaction.Users.Count);
    }

    [Fact]
    public async Task MarkConversationRead_AdvancesThroughMessagesReceivedAfterTheThreadWasLoaded()
    {
        var (owner, member, _) = await UsersAsync();
        var sender = Service(owner);
        var recipient = Service(member);
        var id = await sender.OpenDirectAsync(member);
        var firstMessageId = await sender.SendAsync(id, "First");

        await recipient.MarkReadAsync(id, firstMessageId);
        var arrivingMessageId = await sender.SendAsync(id, "Arrived while open");

        Assert.Equal(1, (await recipient.GetConversationsAsync()).Single().UnreadCount);
        Assert.Equal(arrivingMessageId, await recipient.MarkConversationReadAsync(id));
        Assert.Equal(0, (await recipient.GetConversationsAsync()).Single().UnreadCount);
    }

    [Fact]
    public async Task UnreadCounts_AreScopedToEachUserWhenOneConversationMovesToTheTop()
    {
        var (firstUser, secondUser, _) = await UsersAsync();
        var first = Service(firstUser);
        var second = Service(secondUser);
        var directId = await first.OpenDirectAsync(secondUser);
        var channelId = await first.CreateChannelAsync("Updates", ChannelVisibility.Public, [secondUser]);

        await second.SendAsync(directId, "One");
        await second.SendAsync(directId, "Two");
        await second.SendAsync(directId, "Three");

        await first.SendAsync(channelId, "Channel update");

        var firstUserConversations = await first.GetConversationsAsync();
        var secondUserConversations = await second.GetConversationsAsync();
        Assert.Equal(3, firstUserConversations.Single(x => x.Id == directId).UnreadCount);
        Assert.Equal(0, firstUserConversations.Single(x => x.Id == channelId).UnreadCount);
        Assert.Equal(0, secondUserConversations.Single(x => x.Id == directId).UnreadCount);
        Assert.Equal(1, secondUserConversations.Single(x => x.Id == channelId).UnreadCount);
        Assert.Equal("Member", firstUserConversations.Single(x => x.Id == directId).Name);
        Assert.Equal("Owner", secondUserConversations.Single(x => x.Id == directId).Name);
        Assert.Equal(ConversationKind.Direct, firstUserConversations.Single(x => x.Id == directId).Kind);
        Assert.Equal(ConversationKind.Channel, firstUserConversations.Single(x => x.Id == channelId).Kind);
    }

    private sealed class TestCurrentUser(string userId) : ICurrentUser
    {
        public ValueTask<string?> GetUserIdAsync() => ValueTask.FromResult<string?>(userId);
    }
}
