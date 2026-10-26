using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Chat;
using Confast.Web.Features.Customers;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Inspections;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Confast.Web.Tests;

// These tests need their own empty cluster: they create logins and restored databases.
public sealed class IsolatedRehearsalFactAttribute : FactAttribute
{
    public IsolatedRehearsalFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CONFAST_ISOLATED_REHEARSAL") != "1")
            Skip = "Run scripts/security/start-disposable-rehearsal.ps1 and set CONFAST_ISOLATED_REHEARSAL=1.";
    }
}

[Collection(PostgresCollection.Name)]
public sealed partial class DeploymentSecurityTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly AuthorizationTestSession session = new(database);
    private string root = "";
    private static string Repository => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
    private static string Script(string name) => Path.Combine(Repository, "scripts/security", name);

    public async Task InitializeAsync()
    {
        // Refuse to create cluster-level resources using the normal local test login.
        if (Environment.GetEnvironmentVariable("CONFAST_ISOLATED_REHEARSAL") != "1") return;
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        Assert.Equal("127.0.0.1", connection.Host);
        Assert.Equal("confast_phase3g_test", connection.Database);
        Assert.Equal("confast_rehearsal_operator", connection.Username);
        Assert.NotEqual(5432, connection.Port);
        await database.ResetAsync();
        root = await session.ProvisionRootAsync();
        await session.SignInAsync(root);
    }
    public Task DisposeAsync() { session.Dispose(); return Task.CompletedTask; }

    private sealed class Factory(string connection) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    private ServiceProvider Services(string connection)
    {
        var services = new ServiceCollection();
        services.AddLogging(); services.AddHttpContextAccessor(); services.AddAuthentication();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IDbContextFactory<AppDbContext>>(new Factory(connection));
        services.AddScoped(_ => new Factory(connection).CreateDbContext());
        services.AddSingleton<AuthenticationStateProvider>(session);
        services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
        services.AddIdentityCore<ApplicationUser>().AddRoles<ApplicationRole>().AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
        services.AddConfastAuthorization(); services.AddScoped<UserAdministrationService>();
        return services.BuildServiceProvider();
    }

    [IsolatedRehearsalFact]
    public async Task RestrictedRuntime_IdentityBusinessRoleAdministrationWorkerAndSqlDenials()
    {
        var role = "confast_runtime_test_" + Guid.NewGuid().ToString("N");
        await Sql(database.ConnectionString, $"CREATE ROLE {role} LOGIN NOINHERIT");
        try
        {
            await Psql(database.ConnectionString, "runtime-grants.sql", ("database", "confast_phase3g_test"), ("runtime_role", role));
            var runtime = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = role }.ConnectionString;
            await using var services = Services(runtime);
            var evaluator = services.GetRequiredService<EffectivePermissionService>();
            Assert.True((await evaluator.EvaluateCurrentAsync()).IsRoot);
            var guard = services.GetRequiredService<ApplicationAuthorization>();
            var customers = new CustomerService(new Factory(runtime), guard);
            await customers.CreateCustomerAsync(new() { Name = "Restricted runtime customer" });
            Assert.Single(await customers.GetCustomersAsync());
            var administration = services.GetRequiredService<RoleAdministrationService>();
            var before = await administration.GetRolesAsync();
            var created = await administration.CreateAsync(new("Runtime created role", null, null, before.Version));
            Assert.Contains((await administration.GetRolesAsync()).Roles, x => x.Id == created);
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = "runtime.identity", DisplayName = "Runtime Identity", IsActive = true };
            Assert.True((await users.CreateAsync(user, "Runtime-Identity-9!" )).Succeeded);
            Assert.True(await users.CheckPasswordAsync(user, "Runtime-Identity-9!"));
            var notifications = new ChatNotifications(NullLogger<ChatNotifications>.Instance);
            var clock = new RehearsalClock();
            var chat = new ChatService(new Factory(runtime), guard, evaluator, new HttpContextAccessor(), notifications, clock);
            var conversation = await chat.OpenDirectAsync(root);
            var scheduled = await chat.SendWithAttachmentsAsync(conversation, "Restricted worker", [], scheduledAtUtc: clock.Now.AddMinutes(1).UtcDateTime);
            clock.Now += TimeSpan.FromMinutes(2);
            Assert.Equal(1, await chat.DeliverDueMessagesAsync());
            Assert.Equal(0, await chat.DeliverDueMessagesAsync());
            await using var verify = database.CreateDbContext();
            Assert.Equal(ScheduledChatState.Delivered, (await verify.Set<ChatScheduledMessage>().SingleAsync(x => x.Id == scheduled)).State);
            Assert.True(await verify.AuthorizationChangeHistory.AnyAsync(x => x.Purpose == "Roles.Create"));
            string[] denied = [
                "UPDATE public.authorization_state SET global_epoch = global_epoch + 1",
                "UPDATE public.permissions SET display_name = display_name",
                "DELETE FROM public.authorization_change_history",
                "UPDATE public.authorization_change_details SET field_name = 'forged'",
                "DELETE FROM public.chat_moderation_history",
                "TRUNCATE public.authorization_change_history",
                "ALTER TABLE public.identity_roles DISABLE TRIGGER USER",
                "CREATE TABLE public.runtime_escalation(id int)",
                "SELECT public.provision_confast_root('forged', 0, 'forged', NULL)",
                "SET ROLE confast_rehearsal_operator"
            ];
            foreach (var sql in denied)
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await Assert.ThrowsAsync<PostgresException>(() => Sql(runtime, sql))).SqlState);
            // Protected Root remains structurally guarded even with ordinary Identity DML.
            var rootMutation = await Assert.ThrowsAsync<PostgresException>(() => Sql(runtime, $"UPDATE public.identity_users SET is_active = false WHERE id = '{root}'"));
            Assert.Equal(PostgresErrorCodes.CheckViolation, rootMutation.SqlState);
            // Fixed paths/ownership on every definer; runtime cannot replace them.
            Assert.Equal(0L, await Scalar<long>(runtime, "SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE n.nspname='public' AND p.prosecdef AND (p.proowner = (SELECT oid FROM pg_roles WHERE rolname=current_user) OR NOT ('search_path=pg_catalog, public' = ANY(p.proconfig)))"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Sql(database.ConnectionString, $"DROP OWNED BY {role}; DROP ROLE {role}");
        }
    }

    [IsolatedRehearsalFact]
    public async Task Restore_RotatesGeneration_RejectsOldDelegation_AndRecoversRootOffline()
    {
        var clock = new RehearsalClock();
        var chat = new ChatService(database, session.Guard, session.Evaluator, new HttpContextAccessor(),
            new(NullLogger<ChatNotifications>.Instance), clock);
        var conversation = await chat.OpenDirectAsync(root);
        var draft = await chat.SendWithAttachmentsAsync(conversation, "Retained pre-restore instruction", [], scheduledAtUtc: clock.Now.AddMinutes(1).UtcDateTime);
        // Historical revision/inspection rows supplement identity, roles, manifest, history and chat.
        await using (var db = database.CreateDbContext())
        {
            var customer = new Customer { Name = "Restore customer" };
            var part = new Confast.Web.Features.Parts.Part { Customer = customer, PartNumber = "RESTORE-1" };
            var revision = new Confast.Web.Features.InspectionCriteria.InspectionCriteriaRevision { Part = part,
                RevisionNumber = 1, CreatedAtUtc = DateTimeOffset.UtcNow, PublishedAtUtc = DateTimeOffset.UtcNow };
            db.Add(new Inspection { Part = part, InspectionCriteriaRevision = revision, LotNumber = "HISTORICAL",
                InspectionDate = new(2026, 10, 1), CreatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var restoredName = "confast_restore_test_" + Guid.NewGuid().ToString("N");
        var output = Path.Combine(Repository, "artifacts", restoredName);
        Directory.CreateDirectory(output);
        var dump = Path.Combine(output, "database.dump");
        var restored = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Database = restoredName }.ConnectionString;
        await RunPostgres("pg_dump", database.ConnectionString, ["--format=custom", "--file", dump]);
        await Sql(database.ConnectionString, $"CREATE DATABASE {restoredName} TEMPLATE template0");
        try
        {
            await RunPostgres("pg_restore", restored, ["--exit-on-error", "--dbname", restoredName, dump], includeDatabase: false);
            string[] tables = ["identity_users", "identity_user_roles", "identity_roles", "permissions", "role_permissions", "role_inheritance",
                "authorization_state", "authorization_change_history", "authorization_change_details", "chat_messages", "chat_scheduled_messages", "inspections", "inspection_criteria_revisions"];
            foreach (var table in tables)
            {
                var query = $"SELECT md5(coalesce(string_agg(row_to_json(t)::text, '' ORDER BY row_to_json(t)::text), '')) FROM public.{table} t";
                Assert.Equal(await Scalar<string>(database.ConnectionString, query), await Scalar<string>(restored, query));
            }
            await using var restoredServices = Services(restored);
            var users = restoredServices.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.True(await users.CheckPasswordAsync((await users.FindByIdAsync(root))!, "Disposable-Test-Root-9!"));
            await VerifyRestoredLoginAsync(restored);
            var evaluator = restoredServices.GetRequiredService<EffectivePermissionService>();
            var old = await evaluator.EvaluateCurrentAsync();
            await Psql(restored, "rotate-generation.sql", ("database", restoredName), ("approved", "yes"),
                ("expected_epoch", old.Version.Epoch.ToString()), ("reason", "Isolated restore rehearsal"));
            Assert.NotEqual(old.Version.Generation, (await evaluator.EvaluateCurrentAsync()).Version.Generation);
            clock.Now += TimeSpan.FromMinutes(2);
            var worker = new ChatService(new Factory(restored), new(evaluator, new HttpContextAccessor()), evaluator,
                new HttpContextAccessor(), new(NullLogger<ChatNotifications>.Instance), clock);
            Assert.Equal(0, await worker.DeliverDueMessagesAsync());
            await using var check = new Factory(restored).CreateDbContext();
            Assert.Equal("InstallationChanged", (await check.Set<ChatScheduledMessage>().SingleAsync(x => x.Id == draft)).FailureCode);
            var replacement = new ApplicationUser { UserName = "recovery.test.root", DisplayName = "Recovery Root", IsActive = true };
            Assert.True((await users.CreateAsync(replacement, "Recovered-Root-9!" )).Succeeded);
            Assert.True(await users.CheckPasswordAsync(replacement, "Recovered-Root-9!"));
            var version = await check.AuthorizationState.AsNoTracking().SingleAsync();
            // The expected identity/epoch are explicit; stale approval must leave state untouched.
            await Assert.ThrowsAsync<InvalidOperationException>(() => Psql(restored, "recover-root.sql", ("database", restoredName), ("approved", "yes"),
                ("expected_epoch", "-1"), ("expected_root", root), ("account_id", replacement.Id), ("excluded_browser_id", "browser"), ("reason", "stale")));
            await Psql(restored, "recover-root.sql", ("database", restoredName), ("approved", "yes"),
                ("expected_epoch", version.GlobalEpoch.ToString()), ("expected_root", root), ("account_id", replacement.Id),
                ("excluded_browser_id", "browser"), ("reason", "Approved isolated lost-credential recovery"));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => restoredServices.GetRequiredService<ApplicationAuthorization>()
                .BeginAsync("OldRoot", new("Security"), [Permissions.Authorization.ManageSecurity]));
            session.Principal = AuthorizationTestSession.UserPrincipal(replacement);
            Assert.True((await evaluator.EvaluateCurrentAsync()).IsRoot);
            Assert.Single(await check.UserRoles.Where(x => x.RoleId == AppRoles.RootId && x.UserId == replacement.Id).ToListAsync());
            Assert.True(await check.AuthorizationChangeHistory.AnyAsync(x => x.Purpose == "Recovery.ReplaceRoot"));
            Assert.False(await check.Database.SqlQueryRaw<bool>("SELECT EXISTS (SELECT FROM pg_trigger WHERE tgrelid='public.authorization_state'::regclass AND tgname='auth_guard' AND tgenabled='D') AS \"Value\"").SingleAsync());
            // Deliberate owner-only corruption of this restored disposable copy.
            await Sql(restored, $"BEGIN; ALTER TABLE public.authorization_state DISABLE TRIGGER USER; UPDATE public.authorization_state SET root_user_id='{root}'; ALTER TABLE public.authorization_state ENABLE TRIGGER USER; COMMIT");
            Assert.Equal(AuthorizationDenial.InvalidState, (await Assert.ThrowsAsync<AuthorizationDeniedException>(() => evaluator.EvaluateCurrentAsync())).Denial);
            var corrupted = await check.AuthorizationState.AsNoTracking().SingleAsync();
            await Psql(restored, "recover-root.sql", ("database", restoredName), ("approved", "yes"),
                ("expected_epoch", corrupted.GlobalEpoch.ToString()), ("expected_root", root), ("account_id", replacement.Id),
                ("excluded_browser_id", "browser"), ("reason", "Approved inconsistent-designation recovery"));
            Assert.True((await evaluator.EvaluateCurrentAsync()).IsRoot);
            Assert.Equal(2, await check.AuthorizationChangeHistory.CountAsync(x => x.Purpose == "Recovery.ReplaceRoot"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await Sql(database.ConnectionString, $"DROP DATABASE {restoredName} WITH (FORCE)");
        }
    }

    private static async Task Sql(string connection, string sql)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await using var command = new NpgsqlCommand(sql, db); await command.ExecuteNonQueryAsync();
    }
    private static async Task VerifyRestoredLoginAsync(string connection)
    {
        await using var host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?> {
                ["ConnectionStrings:Confast"] = connection, ["BootstrapAdmin:Username"] = null, ["BootstrapAdmin:Email"] = null,
                ["BootstrapAdmin:Password"] = null, ["BrowserTestUser:Username"] = null, ["BrowserTestUser:Password"] = null,
                ["DataProtection:KeyRingPath"] = null }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<AppDbContext>(); services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<IDbContextFactory<AppDbContext>>();
                services.AddDbContextFactory<AppDbContext>(options => options.UseNpgsql(connection));
            });
        });
        using var client = host.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        var page = await client.GetStringAsync("/login");
        var token = System.Net.WebUtility.HtmlDecode(Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var login = await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["Username"] = "offline.test.root", ["Password"] = "Disposable-Test-Root-9!",
            ["__RequestVerificationToken"] = token, ["ReturnUrl"] = "/" }));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location?.OriginalString);
        Assert.Equal(System.Net.HttpStatusCode.NoContent, (await client.GetAsync("/account/session")).StatusCode);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/admin/roles")).StatusCode);
    }
    private static async Task<T> Scalar<T>(string connection, string sql)
    {
        await using var db = new NpgsqlConnection(connection); await db.OpenAsync();
        await using var command = new NpgsqlCommand(sql, db); return (T)(await command.ExecuteScalarAsync())!;
    }
    private static Task Psql(string connection, string script, params (string Key, string Value)[] variables) =>
        RunPostgres("psql", connection, ["-X", "-v", "ON_ERROR_STOP=1", ..variables.SelectMany(x => new[] { "-v", x.Key + "=" + x.Value }), "-f", Script(script)]);
    private static async Task RunPostgres(string executable, string connection, string[] arguments, bool includeDatabase = true)
    {
        var builder = new NpgsqlConnectionStringBuilder(connection);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var value in new[] { "-h", builder.Host!, "-p", builder.Port.ToString(), "-U", builder.Username! }) start.ArgumentList.Add(value);
        if (includeDatabase) { start.ArgumentList.Add("-d"); start.ArgumentList.Add(builder.Database!); }
        foreach (var value in arguments) start.ArgumentList.Add(value);
        if (builder.Password is { } password) start.Environment["PGPASSWORD"] = password;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(); await output;
        if (process.ExitCode != 0) throw new InvalidOperationException($"{executable} failed ({process.ExitCode}): {await error}");
    }
}

internal sealed class RehearsalClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    public override DateTimeOffset GetUtcNow() => Now;
}
