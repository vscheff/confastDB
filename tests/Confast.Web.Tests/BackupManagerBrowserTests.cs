using System.Formats.Tar;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Backups;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Npgsql;
using static Microsoft.Playwright.Assertions;
using Keys = Confast.Web.Features.Authorization.Permissions.DatabaseBackups;

namespace Confast.Web.Tests;

public sealed class BackupManagerBrowserFactAttribute : FactAttribute
{
    public BackupManagerBrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CONFAST_BACKUP_BROWSER_TESTS") != "1")
            Skip = "Set CONFAST_BACKUP_BROWSER_TESTS=1 to run the authenticated Backup Manager browser acceptance.";
    }
}

public sealed partial class DelegatedAdministrationTests
{
    [BackupManagerBrowserFact]
    public async Task BackupManagerShowsLocalPreviewRunsAndDownloadsABackupAndHonorsTabletPermissions()
    {
        using var files = new BackupTestHost();
        var pg = new NpgsqlConnectionStringBuilder(database.ConnectionString);
        files.SetClientTools(pg.Username!, pg.Password);
        var browserRole = new ApplicationRole { Name = "Backup browser permissions", NormalizedName = "BACKUP BROWSER PERMISSIONS", IsEnabled = true };
        await WithNavigationBrowserAsync("backup-manager-browser", async (browser, origin, user, password, _, output) =>
        {
            await using var desktop = await browser.NewContextAsync(new()
            {
                ViewportSize = new() { Width = 1440, Height = 940 }, ColorScheme = ColorScheme.Light,
                TimezoneId = "Asia/Tokyo", AcceptDownloads = true
            });
            var page = await desktop.NewPageAsync();
            var errors = ObserveErrors(page);
            await LoginForNavigationAsync(page, origin, user, password);
            await page.GotoAsync(origin + "/admin/backups", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Backup Manager", Exact = true })).ToBeVisibleAsync();
            await Expect(page.Locator(".backup-checks label[title]:not([title=''])")).ToHaveCountAsync(3);
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");
            await page.GetByLabel("Server Destination", new() { Exact = true }).SelectOptionAsync("primary");

            var marker = Path.Combine(files.Primary.Path!, ".confast-backup-owner");
            File.Delete(marker);
            await page.GetByRole(AriaRole.Button, new() { Name = "Test Destination" }).ClickAsync();
            await Expect(page.GetByText("Storage is unavailable or its provisioned identity does not match.")).ToBeVisibleAsync();
            BackupTestHost.WriteMarker(files.Primary, files.Options.DeploymentId);
            await page.GetByRole(AriaRole.Button, new() { Name = "Test Destination" }).ClickAsync();
            await Expect(page.GetByText("Destination passed the read, write, rename, and delete checks.", new() { Exact = false })).ToBeVisibleAsync();

            await page.GetByLabel("Frequency", new() { Exact = true }).SelectOptionAsync("Weekly");
            await Expect(page.GetByRole(AriaRole.Group, new() { Name = "Days of Week" })).ToBeVisibleAsync();
            await page.GetByLabel("Frequency", new() { Exact = true }).SelectOptionAsync("Monthly");
            await page.GetByLabel("Backup Time", new() { Exact = true }).FillAsync("03:15");
            await page.GetByLabel("Free Space Reserve (GiB)").FillAsync("0");
            var preview = page.GetByRole(AriaRole.Complementary, new() { Name = "Next Five Executions" });
            await Expect(preview.GetByRole(AriaRole.Listitem)).ToHaveCountAsync(5);
            await Expect(preview.GetByRole(AriaRole.Listitem).First)
                .ToHaveTextAsync(new System.Text.RegularExpressions.Regex(@"^\d{4}-\d{2}-\d{2} 03:15$"));
            await page.GetByRole(AriaRole.Button, new() { Name = "Save Configuration" }).ClickAsync();
            await Expect(page.GetByText("Backup configuration saved.")).ToBeVisibleAsync();

            await using var blocker = database.CreateDbContext();
            await using var locked = await blocker.Database.BeginTransactionAsync();
            await blocker.Database.ExecuteSqlRawAsync("LOCK TABLE public.customers IN ACCESS EXCLUSIVE MODE");
            await page.GetByRole(AriaRole.Button, new() { Name = "Create Backup", Exact = true }).ClickAsync();
            var dialog = page.GetByRole(AriaRole.Dialog);
            await dialog.GetByLabel("Label", new() { Exact = true }).FillAsync("Browser Fixture");
            await dialog.GetByLabel("Notes", new() { Exact = true }).FillAsync("Backed up through the real worker");
            await dialog.GetByLabel("Pin This Backup").CheckAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Queue Backup" }).ClickAsync();
            await Expect(page.GetByText("Backup queued.", new() { Exact = false })).ToBeVisibleAsync();
            var row = page.GetByRole(AriaRole.Row).Filter(new() { HasTextString = "Browser Fixture" });
            await Expect(row).ToContainTextAsync("Dumping Database", new() { Timeout = 20000 });
            await locked.RollbackAsync();
            await Expect(row).ToContainTextAsync("Completed", new() { Timeout = 60000 });
            await Expect(row).ToContainTextAsync("Pinned");

            await page.GetByLabel("Search", new() { Exact = true }).FillAsync("no record matches");
            await page.GetByRole(AriaRole.Button, new() { Name = "Apply Filters" }).ClickAsync();
            await Expect(page.GetByText("No backup executions match the current filters.")).ToBeVisibleAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Clear Filters" }).ClickAsync();
            await row.GetByRole(AriaRole.Button, new() { Name = "Details" }).ClickAsync();
            await Expect(dialog.GetByText("Backed up through the real worker")).ToBeVisibleAsync();
            var download = await page.RunAndWaitForDownloadAsync(() => dialog.GetByRole(AriaRole.Link, new() { Name = "Download" }).ClickAsync());
            await using (var archive = File.OpenRead(await download.PathAsync()))
            await using (var reader = new TarReader(archive))
            {
                var names = new List<string>();
                while (await reader.GetNextEntryAsync() is { } entry) names.Add(entry.Name);
                Assert.Equal(BackupStorage.ArchiveName, names[0]);
                Assert.Equal(BackupStorage.ManifestName, names[^1]);
                Assert.All(names.Skip(1).SkipLast(1), name => Assert.Equal(BackupStorage.ArtworkName, name));
            }
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Unpin", Exact = true }).ClickAsync();
            await Expect(dialog.GetByText("This is the latest known-good backup.", new() { Exact = false })).ToBeVisibleAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Unpin Backup", Exact = true }).ClickAsync();
            await Expect(dialog.GetByText("Unpinned", new() { Exact = true })).ToBeVisibleAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Pin", Exact = true }).ClickAsync();
            await Expect(dialog.GetByText("Pinned", new() { Exact = true })).ToBeVisibleAsync();
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "backup-manager-desktop-light.png"), FullPage = true });

            await OpenAppearanceAsync(page);
            await ModeButton(page, "Dark").ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Purple", Exact = true }).ClickAsync();
            await page.GotoAsync(origin + "/admin/backups");
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-accent-color", "#7C3AED");
            var action = page.GetByRole(AriaRole.Button, new() { Name = "Create Backup", Exact = true });
            await Expect(action).ToBeEnabledAsync();
            Assert.True(await action.EvaluateAsync<bool>("""
                button => {
                    const swatch = document.createElement('span');
                    swatch.style.color = 'var(--accent)';
                    document.body.appendChild(swatch);
                    const result = getComputedStyle(button).backgroundColor === getComputedStyle(swatch).color;
                    swatch.remove();
                    return result;
                }
                """));

            await page.GetByText("Notifications", new() { Exact = true }).ClickAsync();
            var addRecipient = page.GetByRole(AriaRole.Button, new() { Name = "Add Recipient" });
            await addRecipient.ClickAsync();
            await addRecipient.ClickAsync();
            var recipientCards = page.GetByRole(AriaRole.Group, new() { Name = "Notification Recipient" });
            await Expect(recipientCards.First.Locator(".backup-checks label[title]:not([title=''])")).ToHaveCountAsync(8);
            await recipientCards.Nth(0).GetByLabel("Email Address").FillAsync("operator@example.test");
            await recipientCards.Nth(1).GetByLabel("Email Address").FillAsync("owner@example.test");
            await recipientCards.Nth(1).GetByLabel("Successful Backups").CheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Save Configuration" }).ClickAsync();
            await Expect(page.GetByText("Backup configuration saved.")).ToBeVisibleAsync();
            await page.ReloadAsync();
            await page.GetByText("Notifications", new() { Exact = true }).ClickAsync();
            await Expect(recipientCards.Nth(0).GetByLabel("Email Address")).ToHaveValueAsync("operator@example.test");
            await Expect(recipientCards.Nth(0).GetByLabel("Successful Backups")).Not.ToBeCheckedAsync();
            await Expect(recipientCards.Nth(1).GetByLabel("Email Address")).ToHaveValueAsync("owner@example.test");
            await Expect(recipientCards.Nth(1).GetByLabel("Successful Backups")).ToBeCheckedAsync();
            await recipientCards.Nth(1).GetByLabel("Successful Backups").UncheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await Expect(recipientCards.Nth(1).GetByLabel("Successful Backups")).ToBeCheckedAsync();
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "backup-recipient-preferences.png") });

            await using (var db = database.CreateDbContext())
                await db.RolePermissions.Where(x => x.RoleId == browserRole.Id && x.PermissionKey != Keys.View).ExecuteDeleteAsync();

            await using var tablet = await browser.NewContextAsync(new()
            {
                ViewportSize = new() { Width = 834, Height = 1194 }, HasTouch = true, IsMobile = true,
                ColorScheme = ColorScheme.Dark, TimezoneId = "America/Los_Angeles"
            });
            var touch = await tablet.NewPageAsync();
            var touchErrors = ObserveErrors(touch);
            await LoginForNavigationAsync(touch, origin, user, password);
            await touch.GotoAsync(origin + "/admin/backups");
            await Expect(touch.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
            await Expect(touch.Locator("html")).ToHaveAttributeAsync("data-accent-color", "#7C3AED");
            await Expect(touch.GetByRole(AriaRole.Button, new() { Name = "Create Backup" })).ToHaveCountAsync(0);
            await Expect(touch.GetByRole(AriaRole.Button, new() { Name = "Test Destination" })).ToHaveCountAsync(0);
            Assert.True(await touch.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= innerWidth"));
            await touch.GetByRole(AriaRole.Button, new() { Name = "Details", Exact = true }).TapAsync();
            await Expect(touch.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Link, new() { Name = "Download" })).ToHaveCountAsync(0);
            await Expect(touch.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Button, new() { Name = "Unpin" })).ToHaveCountAsync(0);
            Guid id;
            await using (var db = database.CreateDbContext()) id = await db.BackupExecutions.Select(x => x.Id).SingleAsync();
            Assert.Equal(403, (await tablet.APIRequest.GetAsync($"{origin}/admin/backups/{id:D}/download")).Status);
            await touch.ScreenshotAsync(new() { Path = Path.Combine(output, "backup-manager-tablet-dark.png"), FullPage = true });
            Assert.Empty(errors);
            Assert.Empty(touchErrors);
        }, async (account, start) =>
        {
            await using var db = database.CreateDbContext();
            db.Roles.Add(browserRole);
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = account.Id, RoleId = browserRole.Id });
            db.RolePermissions.AddRange(new[] { Keys.View, Keys.Configure, Keys.Create, Keys.Pin, Keys.Download }.Select(
                key => new RolePermission { RoleId = browserRole.Id, PermissionKey = key }));
            await db.SaveChangesAsync();
            files.ConfigureProcess(start);
            start.Environment["Email__Host"] = "";
        }, Environment.GetEnvironmentVariable("CONFAST_BACKUP_BROWSER_CHANNEL"));
    }
}
