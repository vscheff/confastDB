using System.Net;
using System.Text.RegularExpressions;
using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Backups;
using Confast.Web.Features.Chat;
using Confast.Web.Features.Customers;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.Maintenance;
using Confast.Web.Features.Navigation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Confast.Web.Tests;

public sealed partial class DeploymentSecurityTests
{
    [IsolatedRehearsalFact]
    public async Task FactoryResetRequiresRootAndFreshConfirmationAndMatchesAProvisionedInstallation()
    {
        using var files = new BackupTestHost();
        var suffix = Guid.NewGuid().ToString("N");
        var runtimeRole = "confast_reset_app_" + suffix;
        var resetRole = "confast_reset_exec_" + suffix;
        var backupRole = "confast_reset_reader_" + suffix;
        var freshName = "confast_reset_fresh_test_" + suffix;
        var restoreName = "confast_reset_restore_test_" + suffix;
        var runtime = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = runtimeRole }.ConnectionString;
        var executor = new NpgsqlConnectionStringBuilder(runtime) { Username = resetRole }.ConnectionString;
        var artwork = ChatGifCategoryBackgrounds.StoragePath(files.ContentRoot);
        var keys = files.NewDirectory("keys");
        files.SetClientTools(backupRole);

        await Sql(MaintenanceConnection, $"CREATE ROLE {runtimeRole} LOGIN NOINHERIT; CREATE ROLE {resetRole} LOGIN NOINHERIT; CREATE ROLE {backupRole} LOGIN NOINHERIT");
        try
        {
            await Psql(database.ConnectionString, "runtime-grants.sql", ("database", "confast_phase3g_test"), ("runtime_role", runtimeRole));
            await Psql(database.ConnectionString, "backup-grants.sql", ("database", "confast_phase3g_test"), ("backup_role", backupRole));
            await Psql(database.ConnectionString, "factory-reset-grants.sql", ("database", "confast_phase3g_test"), ("reset_role", resetRole));
            Assert.False(await Scalar<bool>(runtime, "SELECT has_function_privilege(current_user, 'confast_maintenance.execute_reset(uuid,text[],text[],jsonb,text,text)', 'EXECUTE')"));
            Assert.False(await Scalar<bool>(executor, "SELECT has_table_privilege(current_user, 'public.identity_users', 'SELECT,UPDATE,DELETE,TRUNCATE')"));

            var original = await SeedBackupDocumentsAsync(files);
            var chatClock = new RehearsalClock();
            var chat = new ChatService(database, session.Guard, session.Evaluator, new HttpContextAccessor(),
                new ChatNotifications(Microsoft.Extensions.Logging.Abstractions.NullLogger<ChatNotifications>.Instance), chatClock);
            await chat.SendWithAttachmentsAsync(original.ConversationId, "Scheduled before reset", [],
                scheduledAtUtc: chatClock.Now.AddMinutes(5).UtcDateTime);
            var orphan = Path.Combine(Path.GetDirectoryName(artwork)!, BackupStorage.ArtworkName + ".tmp");
            await File.WriteAllTextAsync(orphan, "An interrupted artwork write");
            var before = await ArmBackupSettingsAsync(files);
            var memberId = await CreateResetTestUsersAsync();
            await session.SignInAsync(root);
            var prior = await session.Evaluator.EvaluateCurrentAsync();
            using var export = await session.Guard.BeginAsync("InspectionSheets.Export",
                new("Inspection", original.InspectionId.ToString()), [Permissions.InspectionSheets.Export]);
            Assert.DoesNotContain(NavigationCatalog.GetVisibleGroups(prior).SelectMany(g => g.Items), i => i.Route == FactoryResetService.Route);
            Assert.Contains(NavigationCatalog.GetVisibleGroups(prior, true).SelectMany(g => g.Items), i => i.Route == FactoryResetService.Route);

            // Both authorization and configuration are checked on the routed page
            // and again when the request service is called directly.
            await using (var disabled = ResetHost(runtime, executor, files, keys, enabled: null))
            {
                using var browser = ResetClient(disabled);
                await SignInResetTestAsync(browser, "offline.test.root");
                Assert.Equal("/access-denied", (await browser.GetAsync(FactoryResetService.Route)).Headers.Location?.AbsolutePath);
                await AssertDirectResetDeniedAsync(disabled, session.Principal);
            }

            await using var host = ResetHost(runtime, executor, files, keys, enabled: true);
            using var rootClient = ResetClient(host);
            using var administrator = ResetClient(host);
            await SignInResetTestAsync(rootClient, "offline.test.root");
            await SignInResetTestAsync(administrator, "factory.reset.administrator");
            var originalKeys = Directory.GetFiles(keys, "key-*.xml").ToDictionary(path => Path.GetFileName(path), File.ReadAllText);
            Assert.NotEmpty(originalKeys);
            var printToken = host.Services.GetRequiredService<InspectionPrintRenderTokenService>()
                .CreateForExport(export, original.InspectionId);
            Assert.Equal("/access-denied", (await administrator.GetAsync(FactoryResetService.Route)).Headers.Location?.AbsolutePath);
            await using (var db = database.CreateDbContext())
                await AssertDirectResetDeniedAsync(host, AuthorizationTestSession.UserPrincipal(await db.Users.SingleAsync(u => u.Id == memberId)));
            var missingToken = await rootClient.PostAsync(FactoryResetService.ExecuteRoute,
                new FormUrlEncodedContent(new Dictionary<string, string>()));
            Assert.True(missingToken.StatusCode == HttpStatusCode.BadRequest, await missingToken.Content.ReadAsStringAsync());

            var token = ExtractResetToken(await rootClient.GetStringAsync(FactoryResetService.Route));
            Assert.Contains("error=Confirmation", (await SubmitResetAsync(rootClient, token, phrase: "reset database")).Headers.Location?.OriginalString);
            Assert.Contains("error=Confirmation", (await SubmitResetAsync(rootClient, token, confirmed: false)).Headers.Location?.OriginalString);
            Assert.Contains("error=Confirmation", (await SubmitResetAsync(rootClient, token, acknowledged: false)).Headers.Location?.OriginalString);
            Assert.Contains("error=Password", (await SubmitResetAsync(rootClient, token, password: "Wrong password")).Headers.Location?.OriginalString);
            Assert.Equal(0L, await Scalar<long>(database.ConnectionString, "SELECT count(*) FROM confast_maintenance.factory_resets"));

            // A real tool failure aborts without touching data or the source files.
            var executorService = host.Services.GetRequiredService<FactoryResetExecutor>();
            var failurePasswordFile = files.Options.PassFile + ".disabled";
            File.Move(files.Options.PassFile!, failurePasswordFile);
            Assert.Equal("/maintenance", (await SubmitResetAsync(rootClient, token)).Headers.Location?.OriginalString);
            await executorService.RunOnceAsync(default);
            File.Move(failurePasswordFile, files.Options.PassFile!);
            Assert.Equal("Aborted", await Scalar<string>(database.ConnectionString, "SELECT phase FROM confast_maintenance.factory_resets"));
            Assert.True(File.Exists(artwork));
            Assert.Equal(1L, await Scalar<long>(database.ConnectionString, "SELECT count(*) FROM parts"));
            Assert.Equal(prior.Version.Generation, await Scalar<Guid>(database.ConnectionString, "SELECT installation_generation FROM authorization_state"));

            var validArtwork = await File.ReadAllBytesAsync(artwork);
            await File.WriteAllBytesAsync(artwork, []);
            Assert.Equal("/maintenance", (await SubmitResetAsync(rootClient, token)).Headers.Location?.OriginalString);
            await executorService.RunOnceAsync(default);
            Assert.Equal("Aborted", (await host.Services.GetRequiredService<MaintenanceGate>().ReadAsync(default)).LastOutcome);
            Assert.Contains("artwork map has an unexpected size", await Scalar<string>(database.ConnectionString,
                "SELECT diagnostic FROM backup_executions WHERE label = 'Pre-reset backup' ORDER BY requested_at_utc DESC LIMIT 1"));
            Assert.Equal(1L, await Scalar<long>(database.ConnectionString, "SELECT count(*) FROM parts"));
            await File.WriteAllBytesAsync(artwork, validArtwork);

            // A migration or unrelated public table is fenced before backup/reset.
            await Sql(database.ConnectionString, "CREATE TABLE public.unexpected_factory_scope (id int); INSERT INTO public.unexpected_factory_scope VALUES (42)");
            Assert.Equal("/maintenance", (await SubmitResetAsync(rootClient, token)).Headers.Location?.OriginalString);
            await executorService.RunOnceAsync(default);
            Assert.Equal(42, await Scalar<int>(database.ConnectionString, "SELECT id FROM public.unexpected_factory_scope"));
            await Sql(database.ConnectionString, "DROP TABLE public.unexpected_factory_scope");

            await Sql(database.ConnectionString, $"""
                CREATE SCHEMA unrelated_factory_fixture;
                CREATE TABLE unrelated_factory_fixture.data (value int);
                INSERT INTO unrelated_factory_fixture.data VALUES (42);
                GRANT USAGE ON SCHEMA unrelated_factory_fixture TO {backupRole};
                GRANT SELECT ON unrelated_factory_fixture.data TO {backupRole};
                """);

            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = host.Services.GetRequiredService<MaintenanceGate>();
            var oldWork = gate.RunAsync(async () => { inFlight.SetResult(); await release.Task; }, default);
            await inFlight.Task;
            try
            {
                // The durable job outlives the client that submitted it.
                using (var initiator = ResetClient(host))
                {
                    await SignInResetTestAsync(initiator, "offline.test.root");
                    var submittingToken = ExtractResetToken(await initiator.GetStringAsync(FactoryResetService.Route));
                    Assert.Equal("/maintenance", (await SubmitResetAsync(initiator, submittingToken)).Headers.Location?.OriginalString);
                }
                Assert.Equal(HttpStatusCode.ServiceUnavailable, (await rootClient.GetAsync("/")).StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await rootClient.GetAsync("/maintenance/status")).StatusCode);
                await executorService.RunOnceAsync(default);
                Assert.Equal("Draining", (await gate.ReadAsync(default)).Phase);
                await Assert.ThrowsAsync<MaintenanceUnavailableException>(() => new MaintenanceGate(
                    host.Services.GetRequiredService<IConfiguration>()).RunAsync(() => Task.CompletedTask, default));
                await Assert.ThrowsAsync<PostgresException>(() => Sql(database.ConnectionString,
                    "UPDATE public.\"__EFMigrationsHistory\" SET \"ProductVersion\" = \"ProductVersion\""));
            }
            finally { release.SetResult(); await oldWork; }

            var previousRoot = await ReadResetRootAsync();
            await Task.WhenAll(executorService.RunOnceAsync(default), executorService.RunOnceAsync(default));
            Assert.Null((await gate.ReadAsync(default)).Phase);
            Assert.True(prior.Version.Generation != (await gate.ReadAsync(default)).Generation, ResetLogs(files));
            Assert.Equal(HttpStatusCode.Unauthorized, (await rootClient.GetAsync("/account/session")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await administrator.GetAsync("/account/session")).StatusCode);
            using (var unauthenticated = ResetClient(host))
            {
                var oldPrint = await unauthenticated.GetAsync(
                    $"/inspections/{original.InspectionId}/print?renderToken={Uri.EscapeDataString(printToken)}");
                Assert.Equal("/login", oldPrint.Headers.Location?.AbsolutePath);
            }
            await Assert.ThrowsAsync<MaintenanceUnavailableException>(() => gate.RunAsync(() => Task.CompletedTask, default, prior.Version.Generation));
            Assert.False(File.Exists(artwork));
            Assert.False(File.Exists(orphan));
            Assert.Equal(42, await Scalar<int>(database.ConnectionString, "SELECT value FROM unrelated_factory_fixture.data"));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(artwork)!, "infrastructure-secret.txt")));
            foreach (var key in originalKeys)
                Assert.Equal(key.Value, await File.ReadAllTextAsync(Path.Combine(keys, key.Key!)));

            var after = await ReadResetRootAsync();
            Assert.Equal(previousRoot.Id, after.Id);
            Assert.Equal(previousRoot.UserName, after.UserName);
            Assert.Equal(previousRoot.PasswordHash, after.PasswordHash);
            Assert.NotEqual(previousRoot.SecurityStamp, after.SecurityStamp);
            Assert.Equal(1L, await Scalar<long>(database.ConnectionString, "SELECT count(*) FROM identity_users"));
            await AssertBackupConfigurationAndRestoreAsync(before, original);

            var fresh = await CreateFreshAsync(freshName, "confasTFresh");
            await CompareFactoryResetToFreshAsync(database.ConnectionString, fresh);
            chatClock.Now += TimeSpan.FromMinutes(10);
            Assert.Equal(0, await chat.DeliverDueMessagesAsync());

            // Restore normal service and allocate a new business identity after the reset.
            await SignInResetTestAsync(rootClient, "offline.test.root");
            Assert.Equal(HttpStatusCode.BadRequest, (await SubmitResetAsync(rootClient, token)).StatusCode);
            await session.SignInAsync(root);
            var customer = await new CustomerService(database, session.Guard).CreateCustomerAsync(new() { Name = "After reset" });
            Assert.Equal(1, customer);
            token = ExtractResetToken(await rootClient.GetStringAsync(FactoryResetService.Route));

            // Simulate losing the worker after BackingUp was committed. A new
            // worker aborts the old intent and preserves the current installation.
            Assert.Equal("/maintenance", (await SubmitResetAsync(rootClient, token)).Headers.Location?.OriginalString);
            await using (var lost = await host.Services.GetRequiredService<FactoryResetStore>().OpenAsync(default))
            {
                await using var acquire = new NpgsqlCommand(MaintenanceGate.TryExclusive, lost);
                Assert.Equal(true, await acquire.ExecuteScalarAsync());
                var job = (await FactoryResetStore.ActiveAsync(lost, default))!;
                var storageId = host.Services.GetRequiredService<FactoryResetStorage>().Prepare();
                await FactoryResetStore.AdvanceAsync(lost, job, FactoryResetPhase.BackingUp, default, storageId);
            }
            await executorService.RunOnceAsync(default);
            Assert.Equal("Aborted", (await gate.ReadAsync(default)).LastOutcome);
            Assert.Equal(1L, await Scalar<long>(database.ConnectionString, "SELECT count(*) FROM customers"));

            // Block the first cleanup attempt after database commit. A separate
            // host finishes the durable job even after disabling new reset requests.
            await File.WriteAllTextAsync(artwork, """{"Hello":"https://media.giphy.com/media/example/giphy.gif"}""");
            Assert.Equal("/maintenance", (await SubmitResetAsync(rootClient, token)).Headers.Location?.OriginalString);
            var activeId = await Scalar<Guid>(database.ConnectionString, "SELECT id FROM confast_maintenance.factory_resets WHERE finished_at IS NULL");
            var collision = artwork + $".reset-{activeId:N}";
            await File.WriteAllTextAsync(collision, "Simulated conflicting quarantine file");
            await executorService.RunOnceAsync(default);
            Assert.Equal("CleaningStorage", (await gate.ReadAsync(default)).Phase);
            Assert.Equal(0L, await Scalar<long>(database.ConnectionString, "SELECT count(*) FROM customers"));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await rootClient.GetAsync("/")).StatusCode);
            File.Delete(collision);
            await using (var recovered = ResetHost(runtime, executor, files, keys, enabled: false, runWorker: true))
            {
                _ = recovered.Services;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while ((await gate.ReadAsync(deadline.Token)).Phase is not null)
                    await Task.Delay(250, deadline.Token);
            }

            Assert.Null((await gate.ReadAsync(default)).Phase);
            Assert.False(File.Exists(artwork));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(artwork)!, "*.reset-*"));
            Assert.Equal(2L, await Scalar<long>(database.ConnectionString, "SELECT count(*) FROM confast_maintenance.factory_resets WHERE phase = 'Completed'"));
            Assert.Equal(2L, await Scalar<long>(database.ConnectionString, "SELECT count(*) FROM backup_executions WHERE label = 'Pre-reset backup' AND pinned AND outcome = 2"));
            await CompareFactoryResetToFreshAsync(database.ConnectionString, fresh);
        }
        finally
        {
            // All targets and roles were generated above inside the private rehearsal cluster.
            await Sql(MaintenanceConnection, $"DROP DATABASE IF EXISTS {freshName} WITH (FORCE)");
            await Sql(MaintenanceConnection, $"DROP DATABASE IF EXISTS {restoreName} WITH (FORCE)");
            await Sql(MaintenanceConnection, $"""
                DELETE FROM confast_maintenance.factory_resets;
                DROP TABLE IF EXISTS public.unexpected_factory_scope;
                DROP TABLE IF EXISTS unrelated_factory_fixture.data;
                DROP SCHEMA IF EXISTS unrelated_factory_fixture;
                DROP OWNED BY {runtimeRole}, {resetRole}, {backupRole};
                DROP ROLE {runtimeRole}, {resetRole}, {backupRole};
                """);
        }

        async Task AssertBackupConfigurationAndRestoreAsync(BackupSet before, RecoveryDocuments original)
        {
            await using var db = database.CreateDbContext();
            var settings = await db.BackupSettings.Include(x => x.Recipients).SingleAsync();
            Assert.Equal(files.Options.DeploymentId, settings.DeploymentId);
            Assert.Equal("primary", settings.Destination);
            Assert.Equal(6, settings.Retain);
            Assert.False(settings.FullVerification);
            Assert.Equal("ops@disposable.example.test", Assert.Single(settings.Recipients).Email);
            var row = await db.BackupExecutions.SingleAsync(x => x.Label == "Pre-reset backup" && x.Pinned && x.Outcome == BackupOutcome.Completed);
            Assert.Equal(BackupVerification.Verified, row.Verification);
            Assert.Equal(root, row.RequestedBy);
            Assert.True(File.Exists(Path.Combine(before.Path, BackupStorage.ManifestName)));
            var set = files.Storage.ReadSet(files.Storage.RequireLocation("primary"), row.DirectoryName!);
            Assert.Equal(original.ArtworkSha, set.Manifest.Files.Single(x => x.Name == BackupStorage.ArtworkName).Sha256);
            await BackupCatalog.VerifyChecksumsAsync(set, default);
            Assert.Contains("DatabaseDefaultsStorageAndBackupVerified", string.Join("\n",
                Directory.GetFiles(files.Coordination, "factory-reset-*.jsonl").Select(File.ReadAllText)));

            await Sql(MaintenanceConnection, $"CREATE DATABASE {restoreName} TEMPLATE template0");
            var restored = new NpgsqlConnectionStringBuilder(MaintenanceConnection) { Database = restoreName }.ConnectionString;
            await RunPostgres("pg_restore", restored, ["--exit-on-error", "--single-transaction", "--no-owner", "--no-privileges",
                Path.Combine(set.Path, BackupStorage.ArchiveName)]);
            Assert.Equal(0L, await Scalar<long>(restored, "SELECT count(*) FROM confast_maintenance.factory_resets"));
            await Psql(restored, "runtime-grants.sql", ("database", restoreName), ("runtime_role", runtimeRole));
            var restoredRuntime = new NpgsqlConnectionStringBuilder(restored) { Username = runtimeRole }.ConnectionString;
            Assert.False(await Scalar<bool>(restoredRuntime,
                "SELECT has_function_privilege(current_user, 'confast_maintenance.execute_reset(uuid,text[],text[],jsonb,text,text)', 'EXECUTE')"));
            using var restoredIdentity = new AuthorizationTestSession(new Factory(restored));
            await restoredIdentity.SignInAsync(root);
            await AssertRestoredDocumentsAsync(new Factory(restored), restoredIdentity, original);
        }
    }

    private async Task<BackupSet> ArmBackupSettingsAsync(BackupTestHost files)
    {
        await using var db = database.CreateDbContext();
        var settings = await db.BackupSettings.SingleAsync();
        settings.DeploymentId = files.Options.DeploymentId;
        settings.Destination = "primary";
        settings.DestinationId = files.Primary.Id;
        settings.Retain = 6;
        settings.ReserveBytes = 0;
        settings.FullVerification = false;
        settings.Recipients.Add(new() { Email = "ops@disposable.example.test", Notices = BackupNotice.Failure });
        await db.SaveChangesAsync();
        var existing = await files.WriteSetAsync(settings.Revision, pinned: true);
        var row = BackupManager.NewRequest(settings, files.Clock.GetUtcNow(), root, null);
        row.Id = existing.Manifest.Id;
        BackupCatalog.Register(row, existing);
        row.Stage = BackupStage.Finished;
        db.BackupExecutions.Add(row);
        db.BackupExecutions.Add(BackupManager.NewRequest(settings, files.Clock.GetUtcNow(), root, null));
        await db.SaveChangesAsync();
        return existing;
    }

    private async Task<string> CreateResetTestUsersAsync()
    {
        await using var db = database.CreateDbContext();
        var owner = await db.Users.SingleAsync(u => u.Id == root);
        owner.EmailConfirmed = true;
        owner.LockoutEnabled = true;
        owner.JobTitle = "Temporary Title";
        owner.StatusMessage = "Temporary Status";
        db.UserRoles.Add(new() { UserId = root, RoleId = AppRoles.AdministratorId });
        (await db.Roles.SingleAsync(role => role.Id == AppRoles.ReadOnlyId)).Description = "Temporary role description";
        db.Set<NavigationPreference>().Add(new() { UserId = root, AccentColor = "#deadbe" });
        var administrator = new ApplicationUser
        {
            UserName = "factory.reset.administrator", NormalizedUserName = "FACTORY.RESET.ADMINISTRATOR",
            DisplayName = "Test Administrator", Email = "admin@disposable.example.test", NormalizedEmail = "ADMIN@DISPOSABLE.EXAMPLE.TEST",
            SecurityStamp = Guid.NewGuid().ToString(), IsActive = true, EmailConfirmed = true
        };
        administrator.PasswordHash = new Microsoft.AspNetCore.Identity.PasswordHasher<ApplicationUser>()
            .HashPassword(administrator, "Disposable-Test-Root-9!");
        db.Users.Add(administrator);
        await db.SaveChangesAsync();
        db.UserRoles.Add(new() { UserId = administrator.Id, RoleId = AppRoles.AdministratorId });
        db.Roles.Add(new ApplicationRole("Temporary Role") { NormalizedName = "TEMPORARY ROLE" });
        await db.SaveChangesAsync();
        return administrator.Id;
    }

    private async Task<ApplicationUser> ReadResetRootAsync()
    {
        await using var db = database.CreateDbContext();
        return await db.Users.AsNoTracking().SingleAsync(x => x.Id == root);
    }

    private async Task<string> CreateFreshAsync(string name, string username)
    {
        await Sql(MaintenanceConnection, $"CREATE DATABASE {name} TEMPLATE template0");
        var connection = new NpgsqlConnectionStringBuilder(MaintenanceConnection) { Database = name }.ConnectionString;
        await using var db = new Factory(connection).CreateDbContext();
        await db.Database.MigrateAsync();
        await using var services = new ServiceCollection()
            .AddLogging().AddOptions().Configure<BootstrapAdminOptions>(o =>
            {
                o.Username = username;
                o.Password = "Disposable-Test-Root-9!";
                o.Email = "owner@disposable.example.test";
                o.DisplayName = "Test Installation Owner";
            })
            .AddDbContext<AppDbContext>(o => o.UseNpgsql(connection))
            .AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<AppDbContext>()
            .Services.BuildServiceProvider();
        await IdentityBootstrapper.CreateInitialAdministratorAsync(services);
        var user = await db.Users.SingleAsync();
        var epoch = await db.AuthorizationState.Select(s => s.GlobalEpoch).SingleAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT provision_confast_root({user.Id}, {epoch}, {"Fresh installation test"}, {"excluded-browser-user"})");
        return connection;
    }

    private static async Task CompareFactoryResetToFreshAsync(string reset, string fresh)
    {
        var tables = await Scalar<string[]>(fresh,
            "SELECT array_agg(tablename::text ORDER BY tablename) FROM pg_tables WHERE schemaname = 'public'");
        Assert.Equal(tables, await Scalar<string[]>(reset,
            "SELECT array_agg(tablename::text ORDER BY tablename) FROM pg_tables WHERE schemaname = 'public'"));
        foreach (var table in tables)
        {
            // Backup infrastructure is operational state. The clean installation has an
            // offline-provisioning audit event, while Factory Reset clears audit history.
            var ignored = table switch
            {
                "backup_settings" or "backup_notification_recipients" or "backup_executions" or
                    "authorization_change_history" or "authorization_change_details" => null,
                "authorization_state" => new[] { "root_user_id", "global_epoch", "installation_generation" },
                "identity_users" => new[] { "id", "user_name", "normalized_user_name", "password_hash", "security_stamp", "concurrency_stamp" },
                "identity_user_roles" => ["user_id"],
                "certification_email_templates" => ["updated_at_utc"],
                _ => Array.Empty<string>()
            };
            if (ignored is null) continue;
            var columns = string.Join(", ", ignored.Select(column => $"'{column}'"));
            var quoted = "\"" + table.Replace("\"", "\"\"") + "\"";
            var snapshot = $"SELECT coalesce(jsonb_agg(row ORDER BY row::text)::text, '[]') FROM " +
                $"(SELECT to_jsonb(t) - ARRAY[{columns}]::text[] AS row FROM public.{quoted} t) rows";
            Assert.True(await Scalar<string>(fresh, snapshot) == await Scalar<string>(reset, snapshot), $"Reset differs from fresh installation in {table}.");
        }
        Assert.Equal(0L, await Scalar<long>(reset, "SELECT count(*) FROM authorization_change_history"));
        Assert.Equal(0L, await Scalar<long>(reset, "SELECT count(*) FROM authorization_change_details"));
    }

    private static WebApplicationFactory<Program> ResetHost(string runtime, string executor, BackupTestHost files, string keys,
        bool? enabled, bool runWorker = false)
    {
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, settings) => settings.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Confast"] = runtime, ["ConnectionStrings:DatabaseReset"] = executor,
                ["Logging:LogLevel:Microsoft.EntityFrameworkCore"] = "Warning",
                ["DatabaseReset:Enabled"] = enabled?.ToString(),
                ["BootstrapAdmin:Username"] = null, ["BootstrapAdmin:Email"] = null, ["BootstrapAdmin:Password"] = null,
                ["BrowserTestUser:Username"] = null, ["BrowserTestUser:Password"] = null,
                ["DataProtection:KeyRingPath"] = keys, ["DataProtection:ProtectWithDpapi"] = "false",
                ["Backups:DeploymentId"] = files.Options.DeploymentId.ToString("D"),
                ["Backups:CoordinationDirectory"] = files.Coordination, ["Backups:Destinations:primary:Id"] = files.Primary.Id.ToString("D"),
                ["Backups:Destinations:primary:Path"] = files.Primary.Path, ["Backups:PostgresBinDirectory"] = files.Options.PostgresBinDirectory,
                ["Backups:UserName"] = files.Options.UserName, ["Backups:PassFile"] = files.Options.PassFile,
                ["ManagedFiles:Directory"] = Path.GetDirectoryName(ChatGifCategoryBackgrounds.StoragePath(files.ContentRoot))
            }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IDataProtectionProvider>();
                services.AddSingleton<IDataProtectionProvider>(DataProtectionProvider.Create(new DirectoryInfo(keys)));
                services.RemoveAll<AppDbContext>();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<IDbContextFactory<AppDbContext>>();
                services.AddDbContextFactory<AppDbContext>((sp, o) =>
                    o.UseNpgsql(runtime).AddInterceptors(sp.GetRequiredService<MaintenanceConnections>()));
                foreach (var worker in services.Where(d => d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType is { } type && ((!runWorker && type == typeof(FactoryResetWorker)) || type == typeof(BackupWorker) ||
                    type == typeof(ChatMessageDeliveryWorker))).ToArray())
                    services.Remove(worker);
            });
        });
    }

    private static HttpClient ResetClient(WebApplicationFactory<Program> host) =>
        host.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });

    private static string ResetLogs(BackupTestHost files) =>
        string.Join(Environment.NewLine, Directory.GetFiles(files.Coordination, "factory-reset-*.jsonl").Select(File.ReadAllText));

    private static async Task SignInResetTestAsync(HttpClient client, string username)
    {
        var token = ExtractResetToken(await client.GetStringAsync("/login"));
        var response = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = username, ["Password"] = "Disposable-Test-Root-9!",
            ["__RequestVerificationToken"] = token, ["ReturnUrl"] = "/"
        }));
        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    private static string ExtractResetToken(string html) => WebUtility.HtmlDecode(Regex.Match(html,
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);

    private static Task<HttpResponseMessage> SubmitResetAsync(HttpClient client, string token,
        string phrase = FactoryResetService.Phrase, bool acknowledged = true, bool confirmed = true,
        string password = "Disposable-Test-Root-9!") =>
        client.PostAsync(FactoryResetService.ExecuteRoute, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["Password"] = password, ["Phrase"] = phrase,
            ["Acknowledged"] = acknowledged ? "true" : "false", ["Confirmed"] = confirmed ? "true" : "false"
        }));

    private static async Task AssertDirectResetDeniedAsync(WebApplicationFactory<Program> host, System.Security.Claims.ClaimsPrincipal principal)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext { User = principal, RequestServices = scope.ServiceProvider };
        try
        {
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => scope.ServiceProvider.GetRequiredService<FactoryResetService>()
                .RequestAsync("Disposable-Test-Root-9!", FactoryResetService.Phrase, acknowledged: true, confirmed: true, default));
        }
        finally { accessor.HttpContext = null; }
    }
}
