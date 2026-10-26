using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Chat;
using Confast.Web.Features.Customers;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Gages;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Parts;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Playwright;
using Npgsql;

namespace Confast.Web.Tests;

public sealed partial class DeploymentSecurityTests
{
    [IsolatedRehearsalFact]
    public async Task TwoProcesses_RestrictedRuntime_RealBrowserLogin_SharedCookie_Revocation_PrivateChat_AndPdf()
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var username = configuration["BrowserTestUser:Username"];
        var password = configuration["BrowserTestUser:Password"];
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            throw new InvalidOperationException("Configured Development BrowserTestUser credentials are missing; browser authentication was not bypassed.");
        var role = "confast_browser_runtime_" + Guid.NewGuid().ToString("N");
        var output = Path.Combine(Repository, "artifacts", "phase3g-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var checks = new List<string>();
        var errors = new List<string>();
        await using var ownerServices = Services(database.ConnectionString);
        var users = ownerServices.GetRequiredService<UserManager<ApplicationUser>>();
        var browserUser = new ApplicationUser { UserName = username, DisplayName = "Browser Test User", Email = "browser@disposable.invalid", IsActive = true };
        Assert.True((await users.CreateAsync(browserUser, password)).Succeeded);
        Assert.True((await users.AddToRolesAsync(browserUser, [AppRoles.Administrator, AppRoles.Quality, AppRoles.Production])).Succeeded);
        var targetUser = new ApplicationUser { UserName = "browser.assignment.target", DisplayName = "Browser Assignment Target", Email = "assignment@disposable.invalid", IsActive = true };
        Assert.True((await users.CreateAsync(targetUser)).Succeeded);
        Assert.NotEqual(root, browserUser.Id);
        var customers = new CustomerService(database, session.Guard);
        var customer = await customers.CreateCustomerAsync(new() { Name = "Browser Rehearsal Customer" });
        var plant = Assert.Single(await customers.GetPlantsAsync(customer));
        var part = (await new PartService(database, session.Guard).CreatePartAsync(new() { CustomerId = customer, PartNumber = "PHASE3G-1", PlantIds = [plant.Id] })).Id!.Value;
        var criteria = new InspectionCriteriaService(database, session.Guard);
        var revision = (await criteria.CreateDraftRevisionAsync(part, "Rehearsal criteria")).RevisionId!.Value;
        var gages = new GageService(database, session.Guard);
        await gages.SaveGageTypeAsync(new() { Name = "Rehearsal Caliper" });
        var gage = Assert.Single(await gages.GetGageTypesAsync());
        await criteria.AddCriterionAsync(part, revision, new() { Name = "Length", GageTypeId = gage.Id, Minimum = "1", MaximumOrTolerance = "2" });
        Assert.Equal(CriteriaOperationStatus.Succeeded, (await criteria.PublishRevisionAsync(part, revision, (await criteria.GetRevisionAsync(part, revision))!.Version)).Status);
        var inspection = (await session.Inspections().CreateInspectionAsync(new() { PartId = part, LotNumber = "REHEARSAL", InspectionDate = new(2026, 10, 26) })).InspectionId!.Value;
        var roles = ownerServices.GetRequiredService<RoleAdministrationService>();
        var editable = await roles.CreateAsync(new("Browser Lower Role", "Original", AppRoles.AdministratorId, (await roles.GetRolesAsync()).Version));
        var chat = new ChatService(database, session.Guard, session.Evaluator, new Microsoft.AspNetCore.Http.HttpContextAccessor(),
            new(NullLogger<ChatNotifications>.Instance), TimeProvider.System);
        var channel = await chat.CreateChannelAsync("Rehearsal Private", ChannelVisibility.Private, [browserUser.Id]);
        await chat.SendAsync(channel, "Private rehearsal body", attachmentFileName: "proof.txt", attachmentContent: [65, 66]);
        long attachment;
        await using (var db = database.CreateDbContext()) attachment = await db.ChatAttachments.Select(x => x.Id).SingleAsync();
        await Sql(database.ConnectionString, $"CREATE ROLE {role} LOGIN NOINHERIT");
        Process? a = null, b = null;
        try
        {
            await Psql(database.ConnectionString, "runtime-grants.sql", ("database", "confast_phase3g_test"), ("runtime_role", role));
            var runtime = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Username = role }.ConnectionString;
            var originA = "http://127.0.0.1:" + FreePort(); var originB = "http://127.0.0.1:" + FreePort();
            a = StartApplication(originA, runtime, output, "A"); b = StartApplication(originB, runtime, output, "B");
            using var http = new HttpClient();
            await WaitReady(http, originA, a); await WaitReady(http, originB, b);
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            await using var desktop = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
            await using var tablet = await browser.NewContextAsync(new() { ViewportSize = new() { Width = 834, Height = 1194 }, IsMobile = true, HasTouch = true, DeviceScaleFactor = 2 });
            var pageA = await desktop.NewPageAsync(); var pageB = await tablet.NewPageAsync();
            pageA.PageError += (_, error) => errors.Add(error); pageB.PageError += (_, error) => errors.Add(error);
            foreach (var page in new[] { pageA, pageB })
                page.Console += (_, message) => { if (message.Type == "error"
                    && (message.Text.Contains("unhandled", StringComparison.OrdinalIgnoreCase) || message.Text.Contains("circuit", StringComparison.OrdinalIgnoreCase))) errors.Add(message.Text); };
            async Task Login(IPage page, string origin)
            {
                await page.GotoAsync(origin + "/login");
                await page.GetByLabel("Username", new() { Exact = true }).FillAsync(username);
                await page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
                await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
                await page.WaitForURLAsync(origin + "/");
            }
            await Login(pageA, originA); await Login(pageB, originB); checks.Add("Real login UI on both processes; configured BrowserTestUser; desktop and Chromium tablet");
            // A's real authentication cookie is independently decrypted/validated by B.
            Assert.Equal(200, (await desktop.APIRequest.GetAsync(originB + "/customers", new() { MaxRedirects = 0 })).Status);
            checks.Add("Shared key ring authenticates cookie issued by the other process");
            foreach (var path in new[] { "/customers", "/parts", "/inspections", "/container-tracking", "/production-scheduling", "/production-tracking", "/admin/users", "/admin/roles" })
            {
                var response = await pageA.GotoAsync(originA + path);
                Assert.Equal(200, response!.Status);
                Assert.DoesNotContain("not authorized", (await pageA.Locator("body").InnerTextAsync()).ToLowerInvariant());
                checks.Add("Authenticated desktop route " + path);
            }
            await pageA.GotoAsync(originA + "/admin/roles");
            await pageA.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^Browser Lower Role Enabled") }).ClickAsync();
            await pageA.Locator("#role-description").FillAsync("Browser reviewed change");
            await pageA.GetByRole(AriaRole.Button, new() { Name = "Preview Changes", Exact = true }).ClickAsync();
            await pageA.GetByRole(AriaRole.Button, new() { Name = "Save Changes", Exact = true }).ClickAsync();
            await pageA.WaitForFunctionAsync("() => document.body.innerText.includes('Role changes saved') || document.querySelector('#role-description')?.value === 'Browser reviewed change'");
            // Query actual persistence instead of treating the browser's own field value as proof.
            for (var attempt = 0; attempt < 50; attempt++)
            {
                await using var db = database.CreateDbContext();
                if (await db.Roles.AnyAsync(x => x.Id == editable && x.Description == "Browser reviewed change")) break;
                if (attempt == 49) Assert.Fail("Role save did not persist.");
                await Task.Delay(100);
            }
            checks.Add("Delegated lower-role matrix, impact preview and Save commit");
            await pageB.GotoAsync(originB + "/admin/roles");
            await pageB.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^Browser Lower Role Enabled") }).ClickAsync();
            await pageB.Locator("#role-description").FillAsync("Preserved stale proposal");
            await pageB.GetByRole(AriaRole.Button, new() { Name = "Preview Changes", Exact = true }).ClickAsync();
            var item = (await roles.GetRolesAsync()).Roles.Single(x => x.Id == editable);
            await roles.UpdateAsync(new(item.Id, item.Name, "Concurrent operator change", item.IsEnabled, item.ConcurrencyStamp, (await roles.GetRolesAsync()).Version));
            await pageB.GetByRole(AriaRole.Button, new() { Name = "Save Changes", Exact = true }).ClickAsync();
            await pageB.Locator(".alert-error").WaitForAsync();
            Assert.Equal("Preserved stale proposal", await pageB.Locator("#role-description").InputValueAsync());
            await using (var db = database.CreateDbContext())
                Assert.Equal("Concurrent operator change", await db.Roles.Where(x => x.Id == editable).Select(x => x.Description).SingleAsync());
            checks.Add("Cross-process stale role edit is rejected and proposal retained");
            await pageB.ScreenshotAsync(new() { Path = Path.Combine(output, "tablet-stale-role.png"), FullPage = true });
            await pageA.ScreenshotAsync(new() { Path = Path.Combine(output, "desktop-role.png"), FullPage = true });
            await pageB.GetByRole(AriaRole.Button, new() { Name = "Reload And Reapply", Exact = true }).ClickAsync();
            await pageB.GetByRole(AriaRole.Button, new() { Name = "Preview Changes", Exact = true }).ClickAsync();
            await pageB.GetByRole(AriaRole.Button, new() { Name = "Save Changes", Exact = true }).ClickAsync();
            await pageB.GetByText("Changes Saved. Permissions And Provenance Refreshed.", new() { Exact = true }).WaitForAsync();
            checks.Add("Tablet reload/reapply, review and Save recover from stale change");
            await pageA.GotoAsync(originA + "/admin/users");
            await pageA.Locator("tr").Filter(new() { HasText = "Browser Assignment Target" }).GetByRole(AriaRole.Button, new() { Name = "Edit", Exact = true }).ClickAsync();
            await pageA.Locator("#edit-username").WaitForAsync();
            Assert.Equal(targetUser.UserName, await pageA.Locator("#edit-username").InputValueAsync());
            await pageA.GetByRole(AriaRole.Checkbox, new() { Name = "Browser Lower Role", Exact = true }).CheckAsync();
            await pageA.GetByText("Effective Access And Assignment Impact", new() { Exact = true }).ClickAsync();
            await pageA.GetByRole(AriaRole.Button, new() { Name = "Preview Assignment Changes", Exact = true }).ClickAsync();
            await pageA.Locator(".user-effective-access .permission-matrix").WaitForAsync();
            Assert.Contains("Browser Lower Role", await pageA.Locator(".user-effective-access").InnerTextAsync());
            await using (var db = database.CreateDbContext())
                Assert.False(await db.UserRoles.AnyAsync(x => x.UserId == targetUser.Id && x.RoleId == editable));
            await pageA.GetByRole(AriaRole.Button, new() { Name = "Save Changes", Exact = true }).ClickAsync();
            await pageA.GetByRole(AriaRole.Button, new() { Name = "Save Changes", Exact = true }).WaitForAsync();
            for (var attempt = 0; attempt < 50; attempt++)
            {
                await using var db = database.CreateDbContext();
                if (await db.UserRoles.AnyAsync(x => x.UserId == targetUser.Id && x.RoleId == editable)) break;
                if (attempt == 49)
                {
                    await pageA.ScreenshotAsync(new() { Path = Path.Combine(output, "user-assignment-failure.png"), FullPage = true });
                    Assert.Fail("User role assignment did not persist: " + string.Join("; ", await pageA.Locator(".alert-error,.validation-message,.validation-errors").AllTextContentsAsync()));
                }
                await Task.Delay(100);
            }
            checks.Add("Browser role checkbox remains local until explicit Save; assignment commits");
            await pageA.GotoAsync(originA + "/customers");
            // SSR controls precede the Interactive Server circuit on a full navigation.
            await pageA.WaitForTimeoutAsync(700);
            await pageA.GetByRole(AriaRole.Button, new() { Name = "Open chat", Exact = true }).ClickAsync();
            await pageA.Locator(".chat-panel").WaitForAsync();
            await pageA.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^Channels") }).ClickAsync();
            await pageA.Locator($"[data-chat-item-kind='channel'][data-chat-drag-id='{channel}'] .chat-conversation").ClickAsync();
            await pageA.GetByRole(AriaRole.Button, new() { Name = "Moderation History", Exact = true }).ClickAsync();
            await pageA.GetByText("No Moderation History.", new() { Exact = true }).WaitForAsync();
            await pageA.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
            checks.Add("Private channel opens for member; moderation history UI uses membership boundary");
            await pageA.Locator("#chat-message-body").FillAsync("Browser scheduled cancellation");
            await pageA.GetByRole(AriaRole.Button, new() { Name = "Add to message", Exact = true }).ClickAsync();
            await pageA.GetByRole(AriaRole.Menuitem, new() { NameRegex = new System.Text.RegularExpressions.Regex("^Schedule Message") }).ClickAsync();
            await pageA.GetByRole(AriaRole.Menuitem, new() { Name = "Custom...", Exact = true }).ClickAsync();
            await pageA.Locator("#message-schedule-date").FillAsync(DateTime.UtcNow.AddDays(2).ToString("yyyy-MM-dd"));
            await pageA.Locator("#message-schedule-time").FillAsync("12:00");
            await pageA.GetByRole(AriaRole.Button, new() { Name = "Set Schedule", Exact = true }).ClickAsync();
            await pageA.Locator(".chat-schedule-summary").WaitForAsync();
            await pageA.Locator("#chat-message-body").PressAsync("Enter");
            await pageA.GetByRole(AriaRole.Button, new() { Name = "Inbox", Exact = true }).ClickAsync();
            await pageA.GetByRole(AriaRole.Tab, new() { Name = "Scheduled", Exact = true }).ClickAsync();
            await pageA.Locator("#inbox-content").GetByText("Browser scheduled cancellation", new() { Exact = true }).WaitForAsync();
            await pageA.GetByRole(AriaRole.Button, new() { Name = "Cancel Scheduled Message", Exact = true }).ClickAsync();
            await pageA.Locator("#inbox-content").GetByText("Browser scheduled cancellation", new() { Exact = true }).WaitForAsync(new() { State = WaitForSelectorState.Hidden });
            checks.Add("Browser custom schedule submission and Inbox cancellation");
            await pageA.Locator("#inbox-content").PressAsync("Escape");
            await pageA.Locator(".inbox-backdrop").WaitForAsync(new() { State = WaitForSelectorState.Hidden });
            await pageA.GetByRole(AriaRole.Button, new() { Name = "Close chat", Exact = true }).ClickAsync();
            // Both actual hosted workers are running against the same queue.
            var work = await chat.SendWithAttachmentsAsync(channel, "Two-process worker race", [], scheduledAtUtc: DateTime.UtcNow.AddSeconds(2));
            for (var attempt = 0; attempt < 150; attempt++)
            {
                await using var db = database.CreateDbContext();
                if (await db.Set<ChatScheduledMessage>().AnyAsync(x => x.Id == work && x.State == ScheduledChatState.Delivered))
                {
                    Assert.Equal(1, await db.ChatMessages.CountAsync(x => x.Body == "Two-process worker race"));
                    break;
                }
                if (attempt == 149) Assert.Fail("Hosted workers did not deliver the due instruction.");
                await Task.Delay(100);
            }
            checks.Add("Two separate hosted workers deliver one message and one terminal receipt");
            var pdf = await desktop.APIRequest.GetAsync(originA + $"/inspections/{inspection}/print/download", new() { Headers = new Dictionary<string, string> { ["Host"] = "attacker.invalid", ["X-Forwarded-Host"] = "attacker.invalid", ["X-Forwarded-Proto"] = "https" } });
            Assert.Equal(200, pdf.Status); Assert.StartsWith("%PDF", System.Text.Encoding.ASCII.GetString((await pdf.BodyAsync())[..4]));
            await File.WriteAllBytesAsync(Path.Combine(output, "restricted-runtime-inspection.pdf"), await pdf.BodyAsync());
            checks.Add("Actual cookieless Chromium PDF render under runtime credentials; forged Host/forwarding cannot redirect capability");
            Assert.Equal(200, (await desktop.APIRequest.GetAsync(originA + $"/chat/attachments/{attachment}/download")).Status);
            Assert.Equal(200, (await tablet.APIRequest.GetAsync(originB + $"/chat/attachments/{attachment}/download")).Status);
            await chat.RemovePrivateMemberAsync(channel, browserUser.Id);
            Assert.Equal(404, (await desktop.APIRequest.GetAsync(originA + $"/chat/attachments/{attachment}/download")).Status);
            Assert.Equal(404, (await tablet.APIRequest.GetAsync(originB + $"/chat/attachments/{attachment}/download")).Status);
            checks.Add("Private membership removal denies content on both processes");
            await using (var db = database.CreateDbContext())
                await db.RolePermissions.Where(x => x.PermissionKey == Permissions.Chat.Access).ExecuteDeleteAsync();
            Assert.Equal(403, (await desktop.APIRequest.GetAsync(originA + $"/chat/attachments/{attachment}/download")).Status);
            Assert.Equal(403, (await tablet.APIRequest.GetAsync(originB + $"/chat/attachments/{attachment}/download")).Status);
            checks.Add("Chat.Access revocation denies next operation in both independent sessions/caches");
            Assert.Empty(errors);
            await File.WriteAllTextAsync(Path.Combine(output, "results.json"), JsonSerializer.Serialize(new { checks, errors, processes = new[] { a.Id, b.Id } }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            foreach (var process in new[] { a, b })
                if (process is not null) { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } process.Dispose(); }
            NpgsqlConnection.ClearAllPools();
            await Sql(database.ConnectionString, $"DROP OWNED BY {role}; DROP ROLE {role}");
            var keys = Path.Combine(output, "keys");
            if (Directory.Exists(keys)) Directory.Delete(keys, true);
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port;
    }
    private static Process StartApplication(string origin, string connection, string output, string instance)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = Path.Combine(Repository, "src/Confast.Web"),
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(Repository, "src/Confast.Web/bin/Release/net10.0/Confast.Web.dll"));
        foreach (var pair in new Dictionary<string, string> {
            ["ASPNETCORE_ENVIRONMENT"] = "Development", ["ASPNETCORE_URLS"] = origin,
            ["ConnectionStrings__Confast"] = connection, ["BootstrapAdmin__Username"] = "", ["BootstrapAdmin__Email"] = "", ["BootstrapAdmin__Password"] = "",
            ["BrowserTestUser__Username"] = "", ["BrowserTestUser__Password"] = "", ["DataProtection__KeyRingPath"] = Path.Combine(output, "keys"),
            ["DataProtection__ApplicationName"] = "ConfastDB.Phase3G.Rehearsal", ["Logging__LogLevel__Microsoft.AspNetCore"] = "Warning" }) start.Environment[pair.Key] = pair.Value;
        var process = new Process { StartInfo = start };
        var log = Path.Combine(output, "instance-" + instance + ".log");
        var sync = new object();
        void Write(string? line) { if (line is not null) lock (sync) File.AppendAllText(log, line + Environment.NewLine); }
        process.OutputDataReceived += (_, e) => Write(e.Data); process.ErrorDataReceived += (_, e) => Write(e.Data);
        process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine(); return process;
    }
    private static async Task WaitReady(HttpClient http, string origin, Process process)
    {
        for (var attempt = 0; attempt < 150; attempt++)
        {
            if (process.HasExited) throw new InvalidOperationException("Rehearsal application exited; inspect its retained instance log.");
            try { if ((await http.GetAsync(origin + "/login")).IsSuccessStatusCode) return; } catch (HttpRequestException) { }
            await Task.Delay(200);
        }
        throw new TimeoutException("Rehearsal application did not become ready.");
    }
}
