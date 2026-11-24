using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Backups;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Keys = Confast.Web.Features.Authorization.Permissions.DatabaseBackups;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class BackupManagerTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly BackupTestHost host = new();
    private readonly AuthorizationTestSession session = new(database);
    private BackupManager manager = null!;
    private BackupProcessor processor = null!;
    private BackupCoordinator coordinator = null!;
    private BackupCatalog catalog = null!;

    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        await session.SignInAsync(await session.ProvisionRootAsync());
        (manager, processor, coordinator, catalog) = host.Services(database, database.ConnectionString, session);
    }

    public Task DisposeAsync()
    {
        session.Dispose();
        host.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DirectCallsRequireCurrentPermissionsAndOrdinaryAdminHasNoBackupGrants()
    {
        var root = await session.Evaluator.EvaluateCurrentAsync();
        Assert.All(PermissionCatalog.All.Where(x => x.Category == "Database Backups"), item => Assert.True(root.Has(item.Key)));
        Assert.False((await manager.GetOverviewAsync()).Settings.Enabled);
        var user = await AddUserAsync(AppRoles.AdministratorId);
        await session.SignInAsync(user);
        var id = Guid.NewGuid();
        Func<Task>[] operations =
        [
            () => manager.GetOverviewAsync(),
            () => manager.GetHistoryAsync(new()),
            () => manager.GetDetailsAsync(id),
            () => manager.TestDestinationAsync("primary"),
            () => manager.SaveSettingsAsync(new(Guid.Empty, "primary", false, true, true, 14, 0, [], null, null, default)),
            () => manager.RequestBackupAsync(false, null, null),
            () => manager.SetPinnedAsync(id, Guid.Empty, true, false),
            () => manager.EditMetadataAsync(id, Guid.Empty, "label", "notes"),
            () => manager.RequestVerificationAsync(id),
            () => manager.DeleteAsync(id, Guid.Empty, true, true),
            () => manager.RequestReconciliationAsync()
        ];
        foreach (var call in operations) await Assert.ThrowsAsync<AuthorizationDeniedException>(call);
    }

    [Fact]
    public async Task BackupNotificationsRouteEachNoticeAndTrackDeliveryPerRecipient()
    {
        const string failureOnly = "operator@example.test";
        const string failureAndSuccess = "owner@example.test";
        var draft = new BackupSettingsDraft(Guid.Empty, "primary", false, true, true, 14, 0,
            [new(failureOnly, BackupNotice.Failure), new(failureAndSuccess, BackupNotice.Failure | BackupNotice.Success)],
            null, null, host.Clock.GetUtcNow());
        await manager.SaveSettingsAsync(draft);
        var saved = (await manager.GetOverviewAsync()).Settings;
        Assert.Equal(new[] { failureAndSuccess }, BackupCompletion.For(Guid.NewGuid(), saved.Recipients)!.Recipients);
        await Assert.ThrowsAsync<BackupException>(() => manager.SaveSettingsAsync(draft with
        {
            ExpectedRevision = saved.Revision,
            Recipients = [new(failureOnly, BackupNotice.Failure), new(failureOnly.ToUpperInvariant(), BackupNotice.Success)]
        }));

        var now = host.Clock.GetUtcNow();
        var warnings = new Dictionary<BackupNotice, string>
        {
            [BackupNotice.Failure] = "The backup failed.",
            [BackupNotice.Capacity] = "Storage is low."
        };
        await using var db = database.CreateDbContext();
        var pending = await BackupNotifications.ClaimAlertsAsync(db, saved, warnings, now, default);
        Assert.Equal(2, pending.Count);
        Assert.All(pending.Values, claims => Assert.Equal(BackupNotice.Failure, Assert.Single(claims).Kind));
        await db.SaveChangesAsync();

        // The operator received the failure notice. The owner's delivery is still pending.
        var delivered = await db.BackupAlerts.SingleAsync(x => x.RecipientEmail == failureOnly);
        delivered.LastNotifiedMessage = warnings[BackupNotice.Failure];
        delivered.DeliveryClaim = null;
        delivered.NextDeliveryAtUtc = now.AddDays(1);
        await db.SaveChangesAsync();
        Assert.Empty(await BackupNotifications.ClaimAlertsAsync(db, saved, warnings, now.AddMinutes(1), default));
        var retry = await BackupNotifications.ClaimAlertsAsync(db, saved, warnings, now.AddHours(1), default);
        Assert.Equal(failureAndSuccess, Assert.Single(retry).Key);
        await db.SaveChangesAsync();
        warnings.Clear();
        var resolved = await BackupNotifications.ClaimAlertsAsync(db, saved, warnings, now.AddHours(1), default);
        Assert.Null(Assert.Single(Assert.Single(resolved).Value).Message);
        Assert.Equal(failureOnly, Assert.Single(resolved).Key);
        await db.SaveChangesAsync();

        await manager.SaveSettingsAsync(draft with
        {
            ExpectedRevision = saved.Revision, Recipients = [new(failureAndSuccess, BackupNotice.Success)]
        });
        db.ChangeTracker.Clear();
        Assert.False(await db.BackupAlerts.AnyAsync(x => x.RecipientEmail == failureOnly));
    }

    [Fact]
    public async Task SavesPersistedServerRuleAndAnUneditedScheduleSurvivesADifferentBrowserZone()
    {
        var date = host.Clock.GetUtcNow();
        var local = new BackupSchedule(BackupFrequency.Weekly, new TimeOnly(0, 30), Weekdays: 2, HourAnchor: new(2000, 1, 1));
        await SaveAsync(enabled: true, schedule: local, zone: "Asia/Tokyo");
        var first = await SettingsAsync();
        Assert.Equal(local.InZone(TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo"), TimeZoneInfo.Local, date), first.Schedule);
        Assert.Equal(first.Schedule.Next(date, TimeZoneInfo.Local), first.NextDueAtUtc);
        await SaveAsync(enabled: true, retain: 7, zone: "America/Los_Angeles");
        var updated = await SettingsAsync();
        Assert.NotEqual(first.Revision, updated.Revision);
        Assert.Equal(first.Schedule, updated.Schedule);
        Assert.Equal(first.NextDueAtUtc, updated.NextDueAtUtc);
        await Assert.ThrowsAsync<BackupException>(() => manager.SaveSettingsAsync(new(first.Revision, "primary", true,
            true, true, 14, 0, [], null, null, date)));
        await manager.RequestBackupAsync(false, "manual", null);
        await processor.RunOnceAsync(default);
        Assert.Equal(first.NextDueAtUtc, (await SettingsAsync()).NextDueAtUtc);
        Assert.Equal(BackupOutcome.Failed, Assert.Single((await manager.GetHistoryAsync(new())).Records).Outcome);
    }

    [Fact]
    public async Task RestartConsolidatesMissedOccurrencesAndRetriesOnlyOnce()
    {
        await SaveAsync(enabled: true, schedule: new(BackupFrequency.Hourly, new(0, 20)));
        host.Clock.Advance(TimeSpan.FromHours(12));
        var now = host.Clock.GetUtcNow();
        var simultaneous = host.Services(database, database.ConnectionString, session).Processor;
        await Task.WhenAll(processor.RunOnceAsync(default), simultaneous.RunOnceAsync(default));
        var first = Assert.Single((await manager.GetHistoryAsync(new())).Records);
        Assert.Equal(BackupOutcome.Failed, first.Outcome);
        Assert.Equal(1, first.Attempt);
        Assert.Equal((await SettingsAsync()).Schedule.Previous(now, TimeZoneInfo.Local), first.ScheduledForUtc);
        var normal = (await SettingsAsync()).NextDueAtUtc;
        await processor.RunOnceAsync(default);
        Assert.Single((await manager.GetHistoryAsync(new())).Records);
        host.Clock.Advance(BackupOccurrences.RetryDelay.Add(TimeSpan.FromSeconds(1)));
        await simultaneous.RunOnceAsync(default);
        await processor.RunOnceAsync(default);
        var rows = (await manager.GetHistoryAsync(new())).Records;
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(first.ScheduledForUtc, row.ScheduledForUtc));
        Assert.Equal(2, rows.Max(row => row.Attempt));
        Assert.Equal(normal, (await SettingsAsync()).NextDueAtUtc);
    }

    [Fact]
    public async Task RetentionExpiresOnlyOldestRecognizedRollingSetsAfterPublication()
    {
        await SaveAsync(retain: 4);
        var pinned = await SeedSetAsync(pinned: true);
        var oldest = await SeedSetAsync(pinned: false, destination: "archive");
        var middle = await SeedSetAsync(pinned: false);
        var newest = await SeedSetAsync(pinned: false);
        await SaveAsync(retain: 1);
        Assert.True(Directory.Exists(oldest.Path));
        var unrelated = Path.Combine(host.Primary.Path!, "README.operator");
        File.WriteAllText(unrelated, "keep");
        await RetainAsync(newest.Manifest.Id);
        Assert.True(Directory.Exists(pinned.Path));
        Assert.False(Directory.Exists(oldest.Path));
        Assert.False(Directory.Exists(middle.Path));
        Assert.True(Directory.Exists(newest.Path));
        Assert.Equal("keep", File.ReadAllText(unrelated));
        Assert.Equal(BackupAvailability.Expired, (await manager.GetDetailsAsync(oldest.Manifest.Id)).Execution.Availability);
        Assert.Contains((await manager.GetDetailsAsync(middle.Manifest.Id)).Events, x => x.Action == "RetentionExpired");
        await manager.SetPinnedAsync(pinned.Manifest.Id, pinned.Manifest.MetadataVersion, pin: false, confirmedLatest: false);
        Assert.True(Directory.Exists(pinned.Path));
        await RetainAsync(newest.Manifest.Id);
        Assert.False(Directory.Exists(pinned.Path));
    }

    [Fact]
    public async Task FailedCreationStorageLossAndInsufficientCapacityPreserveAllExistingSets()
    {
        await SaveAsync(retain: 1, reserve: 10995116277760L);
        var first = await SeedSetAsync(pinned: false);
        var second = await SeedSetAsync(pinned: true);
        var pending = await manager.RequestBackupAsync(false, "too little space", null);
        await processor.RunOnceAsync(default);
        var failed = (await manager.GetDetailsAsync(pending)).Execution;
        Assert.Equal(BackupOutcome.Failed, failed.Outcome);
        Assert.Contains("insufficient free space", failed.Diagnostic);
        Assert.Null(failed.RetentionDiagnostic);
        await SaveAsync(retain: 1);
        var marker = Path.Combine(host.Primary.Path!, ".confast-backup-owner");
        File.Delete(marker);
        var lostMount = await manager.RequestBackupAsync(false, "missing mount", null);
        await processor.RunOnceAsync(default, forceReconcile: true);
        Assert.Contains("identity", (await manager.GetDetailsAsync(lostMount)).Execution.Diagnostic);
        Assert.False(File.Exists(marker));
        Assert.True(Directory.Exists(first.Path));
        Assert.True(Directory.Exists(second.Path));
        Assert.Equal(BackupAvailability.Unavailable, (await manager.GetDetailsAsync(first.Manifest.Id)).Execution.Availability);
        BackupTestHost.WriteMarker(host.Primary, host.Options.DeploymentId);
        await processor.RunOnceAsync(default, forceReconcile: true);
        Assert.Equal(BackupAvailability.Available, (await manager.GetDetailsAsync(first.Manifest.Id)).Execution.Availability);
    }

    [Fact]
    public async Task ReconciliationProtectsOrphansAndPinChangesAndQuarantinesAmbiguity()
    {
        await SaveAsync(retain: 1);
        var known = await SeedSetAsync(pinned: false);
        BackupStorage.WriteManifest(known.Path, known.Manifest with { Pinned = true, MetadataVersion = Guid.NewGuid() });
        var missing = await SeedSetAsync(pinned: false);
        Directory.Move(missing.Path, Path.Combine(host.Root, "offline"));
        var revision = (await SettingsAsync()).Revision;
        var orphan = await host.WriteSetAsync(revision, pinned: true, "archive");
        var duplicate = await host.WriteSetAsync(revision, pinned: false);
        await host.WriteSetAsync(revision, pinned: false, destination: "archive", id: duplicate.Manifest.Id);
        var invalid = await host.WriteSetAsync(revision, false, deploymentId: Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(host.Primary.Path!, ".stage_interrupted"));
        await manager.RequestReconciliationAsync();
        await processor.RunOnceAsync(default);
        Assert.True((await manager.GetDetailsAsync(known.Manifest.Id)).Execution.Pinned);
        Assert.True((await manager.GetDetailsAsync(orphan.Manifest.Id)).Execution.Pinned);
        Assert.Equal(BackupAvailability.Missing, (await manager.GetDetailsAsync(missing.Manifest.Id)).Execution.Availability);
        Assert.True(host.Storage.ReadPolicy()!.RecoveryHold);
        Assert.Contains("duplicate", (await SettingsAsync()).CatalogIssue);
        Assert.Contains("staging", (await SettingsAsync()).CatalogIssue);
        Assert.Contains("foreign", (await SettingsAsync()).CatalogIssue);
        await Assert.ThrowsAsync<BackupException>(() => manager.GetDetailsAsync(duplicate.Manifest.Id));
        await Assert.ThrowsAsync<BackupException>(() => RetainAsync(known.Manifest.Id));
        Assert.True(Directory.Exists(invalid.Path));
    }

    [Fact]
    public async Task VerificationDetectsSameLengthChangesAndDeletionProtectsTheLatestPinnedSet()
    {
        await SaveAsync();
        var set = await SeedSetAsync(pinned: true);
        var id = set.Manifest.Id;
        await Assert.ThrowsAsync<BackupException>(() => manager.DeleteAsync(id, set.Manifest.MetadataVersion, true, true));
        Assert.Equal("ConfirmLatest", (await Assert.ThrowsAsync<BackupException>(() =>
            manager.SetPinnedAsync(id, set.Manifest.MetadataVersion, false, false))).Code);
        await manager.SetPinnedAsync(id, set.Manifest.MetadataVersion, false, true);
        var file = Path.Combine(set.Path, BackupStorage.ArchiveName);
        var bytes = await File.ReadAllBytesAsync(file);
        bytes[^1] ^= 1;
        await File.WriteAllBytesAsync(file, bytes);
        await manager.RequestVerificationAsync(id);
        await processor.RunOnceAsync(default);
        var details = await manager.GetDetailsAsync(id);
        Assert.Equal(BackupVerification.Failed, details.Execution.Verification);
        Assert.Equal(BackupAvailability.Corrupted, details.Execution.Availability);
        Assert.Contains("SHA-256", details.Execution.VerificationDiagnostic);
        Assert.Contains(details.Events, x => x.Action == "VerificationFailed");
        Assert.Equal("ConfirmDeletion", (await Assert.ThrowsAsync<BackupException>(() =>
            manager.DeleteAsync(id, details.Execution.MetadataVersion, false, true))).Code);
        await manager.DeleteAsync(id, details.Execution.MetadataVersion, true, true);
        Assert.Equal(BackupAvailability.Deleted, (await manager.GetDetailsAsync(id)).Execution.Availability);
        Assert.False(Directory.Exists(set.Path));
    }

    [Fact]
    public async Task DestinationChangeKeepsPreviousSetsVisibleAndVerificationReportsMissingFiles()
    {
        await SaveAsync();
        var original = await SeedSetAsync(pinned: false);
        var previous = await SettingsAsync();
        await manager.SaveSettingsAsync(new(previous.Revision, "archive", false, true, true,
            14, 0, [], null, null, host.Clock.GetUtcNow()));
        Assert.Equal("archive", (await SettingsAsync()).Destination);
        Assert.Equal("primary", (await manager.GetDetailsAsync(original.Manifest.Id)).Execution.Destination);
        Assert.True(Directory.Exists(original.Path));
        var anotherFile = Path.Combine(original.Path, BackupStorage.ArchiveName);
        File.Delete(anotherFile);
        await manager.RequestVerificationAsync(original.Manifest.Id);
        await processor.RunOnceAsync(default);
        var row = (await manager.GetDetailsAsync(original.Manifest.Id)).Execution;
        Assert.Equal(BackupVerification.Failed, row.Verification);
        Assert.Equal(BackupAvailability.Corrupted, row.Availability);
        Assert.Contains("missing", row.VerificationDiagnostic);
        Assert.True(Directory.Exists(original.Path));
    }

    [Fact]
    public async Task CoordinatorFencesMutationsDuringOperationsAndCancelsWhenItsDatabaseSessionIsLost()
    {
        await SaveAsync();
        var set = await SeedSetAsync(pinned: false);
        await using (var lease = await coordinator.TryAcquireAsync(default) ?? throw new InvalidOperationException("Test lease missing."))
        {
            Assert.Null(await host.Services(database, database.ConnectionString, session).Coordinator.TryAcquireAsync(default));
            Assert.Equal("Busy", (await Assert.ThrowsAsync<BackupException>(() => manager.RequestVerificationAsync(set.Manifest.Id))).Code);
            Assert.Equal("Busy", (await Assert.ThrowsAsync<BackupException>(() =>
                manager.SetPinnedAsync(set.Manifest.Id, set.Manifest.MetadataVersion, true, false))).Code);
            Assert.Equal("Busy", (await Assert.ThrowsAsync<BackupException>(() =>
                manager.DeleteAsync(set.Manifest.Id, set.Manifest.MetadataVersion, true, true))).Code);
            await using var db = database.CreateDbContext();
            var terminated = await db.Database.SqlQueryRaw<bool>("""
                SELECT pg_terminate_backend(pid) AS "Value" FROM pg_stat_activity
                WHERE application_name = 'confastDB Backup Coordinator' AND datname = current_database()
                """).SingleAsync();
            Assert.True(terminated);
            var lost = await Record.ExceptionAsync(lease.CheckAsync);
            Assert.True(lost is BackupException { Code: "OwnershipLost" } or OperationCanceledException);
            Assert.True(lease.Token.IsCancellationRequested);
            Assert.Null(await coordinator.TryAcquireAsync(default)); // A dead DB lock did not release file ownership.
        }
        Assert.True(Directory.Exists(set.Path));
        await using var recovered = await coordinator.TryAcquireAsync(default);
        Assert.NotNull(recovered);
    }

    [Fact]
    public async Task RestartMarksUnpublishedRunningAttemptsInterruptedAndRejectsRevokedRequests()
    {
        await SaveAsync();
        var id = await manager.RequestBackupAsync(false, null, null);
        await using (var db = database.CreateDbContext())
        {
            var row = await db.BackupExecutions.SingleAsync(x => x.Id == id);
            row.Outcome = BackupOutcome.Running;
            row.Stage = BackupStage.DumpingDatabase;
            row.StartedAtUtc = host.Clock.GetUtcNow();
            await db.SaveChangesAsync();
        }
        var running = await manager.GetOverviewAsync();
        Assert.Equal(id, running.Status.Active?.Id);
        await processor.RunOnceAsync(default);
        Assert.Equal(BackupOutcome.Interrupted, (await manager.GetDetailsAsync(id)).Execution.Outcome);

        var rootId = session.ActorId;
        var user = await AddUserAsync(AppRoles.ReadOnlyId, Keys.View, Keys.Create);
        await session.SignInAsync(user);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => manager.RequestBackupAsync(true, null, null));
        var delegated = await manager.RequestBackupAsync(false, null, null);
        await using (var db = database.CreateDbContext())
        {
            await db.RolePermissions.Where(x => x.PermissionKey == Keys.Create).ExecuteDeleteAsync();
        }
        await session.SignInAsync(rootId);
        await processor.RunOnceAsync(default);
        Assert.Contains("no longer has", (await manager.GetDetailsAsync(delegated)).Execution.Diagnostic);
    }

    private async Task RetainAsync(Guid publishedId)
    {
        await using var lease = await coordinator.TryAcquireAsync(default) ?? throw new InvalidOperationException("Test lease missing.");
        await using var db = database.CreateDbContext();
        await catalog.ApplyRetentionAsync(db, await db.BackupSettings.SingleAsync(), await db.BackupExecutions.FindAsync(publishedId)
            ?? throw new InvalidOperationException("Fixture missing."), lease);
    }

    private async Task<BackupSet> SeedSetAsync(bool pinned, string? destination = null)
    {
        var set = await host.WriteSetAsync((await SettingsAsync()).Revision, pinned, destination);
        await using var db = database.CreateDbContext();
        var row = new BackupExecution { Id = set.Manifest.Id, DeploymentId = host.Options.DeploymentId, DestinationId = set.Location.Id };
        BackupCatalog.Register(row, set);
        row.Stage = BackupStage.Finished;
        db.BackupExecutions.Add(row);
        await db.SaveChangesAsync();
        return set;
    }

    private async Task SaveAsync(bool enabled = false, int retain = 14, long reserve = 0, BackupSchedule? schedule = null, string? zone = null)
    {
        var previous = await SettingsAsync();
        await manager.SaveSettingsAsync(new(previous.Revision, "primary", enabled, true, true,
            retain, reserve, [], schedule, zone, host.Clock.GetUtcNow()));
    }

    private async Task<BackupSettings> SettingsAsync()
    {
        await using var db = database.CreateDbContext();
        return await db.BackupSettings.AsNoTracking().SingleAsync();
    }

    private async Task<string> AddUserAsync(string initialRoleId, params string[] permissions)
    {
        await using var db = database.CreateDbContext();
        var user = new ApplicationUser { UserName = "backup-test-" + Guid.NewGuid().ToString("N"), IsActive = true,
            DisplayName = "Backup Test Actor", SecurityStamp = Guid.NewGuid().ToString("N") };
        db.Users.Add(user);
        if (initialRoleId != AppRoles.ReadOnlyId)
            db.UserRoles.Add(new IdentityUserRole<string> { RoleId = initialRoleId, UserId = user.Id });
        if (permissions.Length > 0)
        {
            var role = new ApplicationRole { Name = "Backup test role", NormalizedName = "BACKUP TEST ROLE", IsEnabled = true };
            db.Roles.Add(role);
            db.UserRoles.Add(new IdentityUserRole<string> { RoleId = role.Id, UserId = user.Id });
            db.RolePermissions.AddRange(permissions.Select(key => new RolePermission { RoleId = role.Id, PermissionKey = key }));
        }
        await db.SaveChangesAsync();
        return user.Id;
    }
}
