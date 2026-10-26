using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace Confast.Web.Tests;

public sealed class RoleNavigationBrowserFactAttribute : FactAttribute
{
    public RoleNavigationBrowserFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CONFAST_ROLE_BROWSER_TESTS") != "1")
            Skip = "Set CONFAST_ROLE_BROWSER_TESTS=1 to run the authenticated Chromium role-navigation regression.";
    }
}

public sealed partial class DelegatedAdministrationTests
{
    [RoleNavigationBrowserFact]
    public async Task ScrolledRoleNavigationShowsModalAndPreservesDraftUntilDiscarded()
    {
        var configuration = new ConfigurationBuilder().AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().Build();
        var username = configuration["BrowserTestUser:Username"];
        var password = configuration["BrowserTestUser:Password"];
        Assert.False(string.IsNullOrWhiteSpace(username), "Development BrowserTestUser username is required.");
        Assert.False(string.IsNullOrWhiteSpace(password), "Development BrowserTestUser password is required.");
        var manager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var user = new ApplicationUser { UserName = username, DisplayName = "Browser Test User", IsActive = true };
        Assert.True((await manager.CreateAsync(user, password)).Succeeded);
        Assert.True((await manager.AddToRoleAsync(user, AppRoles.Administrator)).Succeeded);
        await Roles.CreateAsync(new("ZZ Navigation Source", "Original source", AppRoles.AdministratorId, await Version()));
        await Roles.CreateAsync(new("ZZ Navigation Target", "Original target", AppRoles.AdministratorId, await Version()));

        var repository = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var output = Path.Combine(repository, "artifacts", "role-navigation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var origin = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.Combine(repository, "src/Confast.Web"),
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Confast.Web.dll"));
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_URLS"] = origin;
        start.Environment["ConnectionStrings__Confast"] = database.ConnectionString;
        start.Environment["BootstrapAdmin__Username"] = "";
        start.Environment["BootstrapAdmin__Password"] = "";
        start.Environment["BrowserTestUser__Username"] = "";
        start.Environment["BrowserTestUser__Password"] = "";
        start.Environment["Logging__LogLevel__Default"] = "Warning";
        using var app = Process.Start(start)!;
        var stdout = app.StandardOutput.ReadToEndAsync();
        var stderr = app.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient();
            for (var attempt = 0; ; attempt++)
            {
                Assert.False(app.HasExited, "Browser-test application exited before startup.");
                try { if ((await http.GetAsync(origin + "/login")).IsSuccessStatusCode) break; }
                catch (HttpRequestException) { }
                Assert.True(attempt < 100, "Browser-test application did not become ready.");
                await Task.Delay(200);
            }
            using var playwright = await Playwright.CreateAsync();
            await using var browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
            foreach (var (width, height) in new[] { (1440, 700), (834, 1194) })
            {
                await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height }, HasTouch = width == 834 });
                var page = await context.NewPageAsync();
                var errors = new List<string>();
                page.PageError += (_, error) => errors.Add(error);
                await page.GotoAsync(origin + "/login");
                await page.GetByLabel("Username", new() { Exact = true }).FillAsync(username);
                await page.GetByLabel("Password", new() { Exact = true }).FillAsync(password);
                await page.GetByRole(AriaRole.Button, new() { Name = "Log in", Exact = true }).ClickAsync();
                await page.WaitForURLAsync(origin + "/");
                await page.GotoAsync(origin + "/admin/roles");
                var source = page.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^ZZ Navigation Source Enabled") });
                var target = page.GetByRole(AriaRole.Button, new() { NameRegex = new System.Text.RegularExpressions.Regex("^ZZ Navigation Target Enabled") });
                await source.ClickAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('#role-description')?.value === 'Original source'");
                await page.Locator("#role-description").FillAsync("Unsaved navigation proposal");
                await page.Locator("#role-description").PressAsync("Tab");
                await target.ClickAsync();
                var dialog = page.GetByRole(AriaRole.Dialog, new() { Name = "Unsaved Changes", Exact = true });
                await dialog.WaitForAsync();
                Assert.True(await page.EvaluateAsync<bool>("() => window.scrollY > 100 || document.querySelector('.role-list').scrollTop > 100"));
                Assert.True(await dialog.EvaluateAsync<bool>("element => element.matches(':modal')"));
                var bounds = (await dialog.BoundingBoxAsync())!;
                Assert.InRange(bounds.Y, 0, height - bounds.Height);
                Assert.InRange(bounds.X, 0, width - bounds.Width);
                Assert.Contains("ZZ Navigation Source", await dialog.InnerTextAsync());
                Assert.Contains("ZZ Navigation Target", await dialog.InnerTextAsync());
                Assert.Equal("Keep Editing", await page.EvaluateAsync<string>("() => document.activeElement.textContent.trim()"));
                await page.ScreenshotAsync(new() { Path = Path.Combine(output, $"dialog-{width}.png") });
                await page.Keyboard.PressAsync("Shift+Tab");
                await page.Keyboard.PressAsync("Shift+Tab");
                Assert.True(await page.EvaluateAsync<bool>("() => document.querySelector('dialog').contains(document.activeElement)"));
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Keep Editing", Exact = true }).ClickAsync();
                await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached });
                Assert.Equal("Unsaved navigation proposal", await page.Locator("#role-description").InputValueAsync());
                await target.ClickAsync();
                await dialog.WaitForAsync();
                await page.Keyboard.PressAsync("Escape");
                await dialog.WaitForAsync(new() { State = WaitForSelectorState.Detached });
                Assert.Equal("Unsaved navigation proposal", await page.Locator("#role-description").InputValueAsync());
                await target.ClickAsync();
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Discard And Continue", Exact = true }).ClickAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('#role-description')?.value === 'Original target'");
                Assert.Contains("ZZ Navigation Target", await page.Locator(".role-card.selected").InnerTextAsync());
                await source.ClickAsync();
                await page.WaitForFunctionAsync("() => document.querySelector('#role-description')?.value === 'Original source'");
                await page.Locator("#role-description").FillAsync("New role navigation proposal");
                await page.GetByRole(AriaRole.Button, new() { Name = "New Role", Exact = true }).ClickAsync();
                await dialog.WaitForAsync();
                Assert.Contains("to a new role", await dialog.InnerTextAsync());
                await dialog.GetByRole(AriaRole.Button, new() { Name = "Discard And Continue", Exact = true }).ClickAsync();
                await page.GetByRole(AriaRole.Heading, new() { Name = "Create Role", Exact = true }).WaitForAsync();
                Assert.Empty(errors);
            }
        }
        finally
        {
            if (!app.HasExited) app.Kill(entireProcessTree: true);
            await app.WaitForExitAsync();
            await File.WriteAllTextAsync(Path.Combine(output, "app.log"), await stdout + await stderr);
        }
    }
}
