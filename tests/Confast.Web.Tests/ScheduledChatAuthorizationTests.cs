using System.Data.Common;
using System.Security.Claims;
using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Chat;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ScheduledChatAuthorizationTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly TestClock clock = new();
    private readonly ChatNotifications notifications = new(NullLogger<ChatNotifications>.Instance);
    private AuthorizationTestSession actor = null!, worker = null!, member = null!;
    private long channel, thread;
    private ChatService Chat(AuthorizationTestSession session, IDbContextFactory<AppDbContext>? factory = null, EffectivePermissionService? evaluator = null) =>
        new(factory ?? database, new(evaluator ?? session.Evaluator, new HttpContextAccessor()), evaluator ?? session.Evaluator,
            new HttpContextAccessor(), notifications, clock);
    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        actor = new(database); worker = new(database); member = new(database);
        await actor.ProvisionRootAsync();
        await using var db = database.CreateDbContext();
        var sender = new ApplicationUser { UserName = "scheduled.sender", NormalizedUserName = "SCHEDULED.SENDER", DisplayName = "Sender", SecurityStamp = Guid.NewGuid().ToString() };
        var recipient = new ApplicationUser { UserName = "scheduled.member", DisplayName = "Member", SecurityStamp = Guid.NewGuid().ToString() };
        db.Users.AddRange(sender, recipient); await db.SaveChangesAsync();
        await actor.SignInAsync(sender.Id); await member.SignInAsync(recipient.Id);
        var role = AppRoles.QualityId;
        db.UserRoles.AddRange(new Microsoft.AspNetCore.Identity.IdentityUserRole<string> { UserId = sender.Id, RoleId = role },
            new Microsoft.AspNetCore.Identity.IdentityUserRole<string> { UserId = recipient.Id, RoleId = role });
        await db.SaveChangesAsync();
        channel = await Chat(actor).CreateChannelAsync("Scheduled private", ChannelVisibility.Private, [recipient.Id]);
        thread = await Chat(actor).CreateChannelThreadAsync(channel, "Thread", "Initial");
    }
    public Task DisposeAsync() { actor.Dispose(); worker.Dispose(); member.Dispose(); return Task.CompletedTask; }
    private Task<long> Schedule(string body = "Delayed intent", long? threadId = null) =>
        Chat(actor).SendWithAttachmentsAsync(channel, body, [new("fixture.txt", [65, 66])], channelThreadId: threadId, scheduledAtUtc: clock.Now.AddMinutes(1).UtcDateTime);
    private async Task<ChatScheduledMessage> Receipt(long id)
    {
        await using var db = database.CreateDbContext();
        return await db.Set<ChatScheduledMessage>().Include(x => x.Attachments).SingleAsync(x => x.Id == id);
    }

    [Fact]
    public async Task StoredIntent_FreshAnonymousWorker_OneEvaluation_AtomicReceipt_AndIdempotentRestart()
    {
        var id = await Schedule(threadId: thread);
        var draft = await Receipt(id);
        Assert.NotEqual(Guid.Empty, draft.RequestId); Assert.Equal(actor.ActorId, draft.SenderUserId);
        Assert.Equal(ChatScheduledMessage.DeliveryPurpose, draft.Purpose); Assert.Equal(thread, draft.ChannelThreadId);
        Assert.Equal(clock.Now.UtcDateTime, draft.CreatedAtUtc); Assert.Equal(draft.ScheduledAtUtc.AddDays(7), draft.ValidUntilUtc);
        Assert.Equal(ScheduledChatState.Pending, draft.State);
        clock.Now += TimeSpan.FromDays(2); // Application outage: still inside the delivery window.
        var evaluator = worker.Evaluator;
        Assert.False(worker.Principal.Identity!.IsAuthenticated);
        Assert.Equal(1, await Chat(worker, evaluator: evaluator).DeliverDueMessagesAsync());
        Assert.Equal(1, evaluator.SnapshotCount);
        Assert.Equal(0, await Chat(new AuthorizationTestSession(database)).DeliverDueMessagesAsync());
        var receipt = await Receipt(id);
        Assert.Equal(ScheduledChatState.Delivered, receipt.State); Assert.Equal(1, receipt.AttemptCount);
        Assert.NotNull(receipt.DeliveredMessageId); Assert.NotNull(receipt.CompletedAtUtc);
        Assert.Single(receipt.Attachments); Assert.Empty(await Chat(actor).GetScheduledMessagesAsync());
        var message = (await Chat(member).GetThreadAsync(channel, channelThreadId: thread)).Messages.Single(x => x.Id == receipt.DeliveredMessageId);
        Assert.Equal(actor.ActorId, message.SenderUserId); Assert.Equal("Delayed intent", message.Body);
        Assert.Single(message.Attachments);
    }

    [Theory]
    [InlineData(Permissions.Chat.Access)][InlineData(Permissions.Chat.SendMessages)][InlineData(Permissions.Chat.ScheduleMessages)]
    [InlineData("inactive")][InlineData("membership")][InlineData("conversation")][InlineData("generation")]
    public async Task RevokedAuthorityOrDestination_FailsPermanently_AndRegrantCannotResurrectIt(string change)
    {
        var id = await Schedule(threadId: thread);
        await using var db = database.CreateDbContext();
        if (change.StartsWith("Chat.")) await db.RolePermissions.Where(x => x.RoleId == AppRoles.QualityId && x.PermissionKey == change).ExecuteDeleteAsync();
        else if (change == "inactive") await db.Users.Where(x => x.Id == actor.ActorId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        else if (change == "membership") await db.ChatConversationMembers.Where(x => x.ConversationId == channel && x.UserId == actor.ActorId).ExecuteDeleteAsync();
        else if (change == "conversation") await Chat(actor).DeleteChannelAsync(channel);
        else
        {
            // Offline recovery simulation on the disposable owner connection only.
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE authorization_state DISABLE TRIGGER USER");
            await db.Database.ExecuteSqlRawAsync("UPDATE authorization_state SET installation_generation = '12345678-1111-2222-3333-123456789012'");
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE authorization_state ENABLE TRIGGER USER");
            await tx.CommitAsync();
        }
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(0, await Chat(worker).DeliverDueMessagesAsync());
        var receipt = await Receipt(id); Assert.Equal(ScheduledChatState.Failed, receipt.State); Assert.NotNull(receipt.Failure);
        Assert.Equal(1, receipt.AttemptCount); Assert.Null(receipt.DeliveredMessageId);
        if (change.StartsWith("Chat.")) { db.RolePermissions.Add(new() { RoleId = AppRoles.QualityId, PermissionKey = change }); await db.SaveChangesAsync(); }
        clock.Now += TimeSpan.FromDays(1);
        Assert.Equal(0, await Chat(worker).DeliverDueMessagesAsync());
        Assert.Equal(1, (await Receipt(id)).AttemptCount);
        Assert.False(await db.ChatMessages.IgnoreQueryFilters().AnyAsync(x => x.Body == "Delayed intent"));
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task CredentialRotation_DoesNotCancelPersistedIntent(bool changePassword)
    {
        var id = await Schedule();
        await using var db = database.CreateDbContext();
        var user = await db.Users.SingleAsync(x => x.Id == actor.ActorId);
        user.SecurityStamp = Guid.NewGuid().ToString();
        if (changePassword) user.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<ApplicationUser>().HashPassword(user, "Changed-Test-Password-9!");
        await db.SaveChangesAsync();
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(1, await Chat(worker).DeliverDueMessagesAsync());
        Assert.Equal(ScheduledChatState.Delivered, (await Receipt(id)).State);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => Chat(actor).SendAsync(channel, "Stale session"));
    }

    [Fact]
    public async Task Cancellation_IsOwned_Idempotent_AndDoesNotNeedNewSendAuthority()
    {
        var id = await Schedule();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Chat(member).CancelScheduledMessageAsync(id));
        await using var db = database.CreateDbContext();
        await db.RolePermissions.Where(x => x.RoleId == AppRoles.QualityId && x.PermissionKey == Permissions.Chat.ScheduleMessages).ExecuteDeleteAsync();
        await Chat(actor).CancelScheduledMessageAsync(id); await Chat(actor).CancelScheduledMessageAsync(id);
        clock.Now += TimeSpan.FromHours(2);
        Assert.Equal(0, await Chat(worker).DeliverDueMessagesAsync());
        Assert.Equal(ScheduledChatState.Canceled, (await Receipt(id)).State);
        Assert.Empty(await Chat(actor).GetScheduledMessagesAsync());
    }

    [Fact]
    public async Task ExpirationAndRestrictiveReferences_PreserveSafeFailureAndHistoricalIdentity()
    {
        var id = await Schedule();
        await using var db = database.CreateDbContext();
        db.Users.Remove(await db.Users.SingleAsync(x => x.Id == actor.ActorId));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        clock.Now += TimeSpan.FromDays(8);
        Assert.Equal(0, await Chat(worker).DeliverDueMessagesAsync());
        Assert.Equal("Expired", (await Receipt(id)).FailureCode);
    }

    [Fact]
    public async Task ImmutableIntent_CannotRedirectOrImpersonate_AndTerminalReceiptsCannotReopen()
    {
        var id = await Schedule();
        await using var db = database.CreateDbContext();
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_scheduled_messages SET sender_user_id = {member.ActorId} WHERE id = {id}"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_scheduled_messages SET channel_thread_id = {thread} WHERE id = {id}"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_scheduled_messages SET body = 'Redirected' WHERE id = {id}"));
        await Chat(actor).CancelScheduledMessageAsync(id);
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_scheduled_messages SET state = 0, completed_at_utc = NULL, failure = NULL WHERE id = {id}"));
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM chat_scheduled_messages WHERE id = {id}"));
    }

    [Fact]
    public async Task PostgreSqlSkipLocked_AllowsIndependentWorkers_WithoutDuplicateDelivery()
    {
        var first = await Schedule("First"); var second = await Schedule("Second");
        clock.Now += TimeSpan.FromMinutes(2);
        await using (var locked = database.CreateDbContext())
        {
            await using var tx = await locked.Database.BeginTransactionAsync();
            await locked.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM chat_scheduled_messages WHERE id = {first} FOR UPDATE");
            Assert.Equal(1, await Chat(worker).DeliverDueMessagesAsync());
            Assert.Equal(ScheduledChatState.Pending, (await Receipt(first)).State);
            Assert.Equal(ScheduledChatState.Delivered, (await Receipt(second)).State);
            await tx.CommitAsync();
        }
        var results = await Task.WhenAll(Chat(worker).DeliverDueMessagesAsync(), Chat(worker).DeliverDueMessagesAsync());
        Assert.Equal(1, results.Sum()); Assert.Equal(0, await Chat(worker).DeliverDueMessagesAsync());
        await using var verify = database.CreateDbContext();
        Assert.Equal(2, await verify.ChatMessages.CountAsync(x => x.Body == "First" || x.Body == "Second"));
    }

    [Theory][InlineData(1)][InlineData(5)]
    public async Task TransientInfrastructureFailure_RollsBackAndRetriesWithBoundedPersistedOwnership(int failures)
    {
        var id = await Schedule(); clock.Now += TimeSpan.FromMinutes(2);
        var factory = new FailingFactory(database.ConnectionString, new InsertFailure(failures));
        for (var attempt = 1; attempt <= failures; attempt++)
        {
            Assert.Equal(0, await Chat(worker, factory).DeliverDueMessagesAsync());
            var draft = await Receipt(id); Assert.Equal(attempt, draft.AttemptCount);
            Assert.Equal(0, await Chat(worker, factory).DeliverDueMessagesAsync()); // Not due for retry / terminal.
            clock.Now += TimeSpan.FromMinutes(5);
        }
        if (failures == 5)
        {
            Assert.Equal(0, await Chat(worker, factory).DeliverDueMessagesAsync());
            Assert.Equal("RetryExhausted", (await Receipt(id)).FailureCode);
        }
        else Assert.Equal(1, await Chat(worker, factory).DeliverDueMessagesAsync());
        await using var db = database.CreateDbContext();
        Assert.Equal(failures == 5 ? 0 : 1, await db.ChatMessages.CountAsync(x => x.Body == "Delayed intent"));
    }

    [Fact]
    public async Task InvalidThreadIsRejectedBeforeQueuing_AndPermanentValidationFailureRollsBackDelivery()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Schedule(threadId: long.MaxValue));
        var id = await Schedule();
        await using var db = database.CreateDbContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE chat_scheduled_attachments SET file_name = 'invalid' || chr(10) WHERE scheduled_message_id = {id}");
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(0, await Chat(worker).DeliverDueMessagesAsync());
        var receipt = await Receipt(id); Assert.Equal("InvalidData", receipt.FailureCode);
        Assert.Equal(1, receipt.AttemptCount); Assert.Equal(ScheduledChatState.Failed, receipt.State);
        Assert.False(await db.ChatMessages.AnyAsync(x => x.Body == "Delayed intent"));
    }

    [Fact]
    public async Task ScheduledPollIsCreatedNow_AndTimeOnlyChangesVotingEligibility()
    {
        var start = clock.Now.AddMinutes(1).UtcDateTime;
        var id = await Chat(actor).CreatePollAsync(channel, "Already created", [new("Yes"), new("No")], startsAtUtc: start, durationHours: 1);
        await using var db = database.CreateDbContext();
        Assert.Empty(await db.Set<ChatScheduledMessage>().ToListAsync());
        var answer = await db.Set<ChatPollAnswer>().Where(x => x.PollMessageId == id).Select(x => x.Id).FirstAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Chat(member).TogglePollVoteAsync(id, answer));
        await db.Users.Where(x => x.Id == actor.ActorId).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        clock.Now += TimeSpan.FromMinutes(2);
        await Chat(member).TogglePollVoteAsync(id, answer); // Existing poll, not a new delegated send.
        Assert.Equal(0, await Chat(worker).DeliverDueMessagesAsync());
        clock.Now += TimeSpan.FromHours(1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Chat(member).TogglePollVoteAsync(id, answer));
        Assert.False((await Chat(member).GetThreadAsync(channel)).Messages.Single(x => x.Id == id).Poll!.IsOpen);
    }

    [Fact]
    public async Task MigrationPreservesMultipleLegacyDraftsAndAttachments_WithoutInventingAuthorization_AndRefusesUnsafeRollback()
    {
        await using var db = database.CreateDbContext();
        var migrator = db.GetService<IMigrator>();
        try
        {
            await migrator.MigrateAsync("20261015184700_AddChatModerationHistory");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO chat_scheduled_messages (conversation_id, sender_user_id, body, scheduled_at_utc)
                VALUES ({channel}, {actor.ActorId}, 'Legacy one', {clock.Now.UtcDateTime}),
                       ({channel}, {actor.ActorId}, 'Legacy two', {clock.Now.UtcDateTime});
                INSERT INTO chat_scheduled_attachments (scheduled_message_id, file_name, content)
                SELECT id, 'legacy.txt', decode('4142', 'hex') FROM chat_scheduled_messages;
                """);
            await migrator.MigrateAsync();
            var rows = await db.Set<ChatScheduledMessage>().AsNoTracking().Include(x => x.Attachments).OrderBy(x => x.Id).ToArrayAsync();
            Assert.Equal(new[] { "Legacy one", "Legacy two" }, rows.Select(x => x.Body));
            Assert.Equal(2, rows.Select(x => x.RequestId).Distinct().Count());
            Assert.All(rows, row => { Assert.Equal(ScheduledChatState.Failed, row.State); Assert.Equal("UnverifiedIntent", row.FailureCode);
                Assert.Null(row.InstallationGeneration); Assert.Equal(new byte[] { 65, 66 }, Assert.Single(row.Attachments).Content); });
            Assert.Equal(0, await Chat(worker).DeliverDueMessagesAsync());
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync("20261015184700_AddChatModerationHistory"));
            Assert.Equal(2, await db.Set<ChatScheduledMessage>().CountAsync());
        }
        finally
        {
            // A refused older downgrade can already have removed newer migrations.
            await migrator.MigrateAsync();
        }
    }
    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class FailingFactory(string connection, DbCommandInterceptor interceptor) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).AddInterceptors(interceptor).Options);
    }
    private sealed class InsertFailure(int failures) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO chat_messages", StringComparison.Ordinal) && failures-- > 0)
                throw new Npgsql.NpgsqlException("Injected transport outage", new IOException());
            return ValueTask.FromResult(result);
        }
    }
}

