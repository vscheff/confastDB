using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Confast.Web.Features.Identity;

public static class IdentityBootstrapper
{
    public static async Task EnsureBrowserTestUserAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        // Check here as well as at composition: this utility cannot provision on a Production host.
        if (!provider.GetRequiredService<IHostEnvironment>().IsDevelopment()) return;
        var options = provider.GetRequiredService<IOptions<BrowserTestUserOptions>>().Value;
        if (string.IsNullOrWhiteSpace(options.Username) && string.IsNullOrWhiteSpace(options.Password)) return;
        if (string.IsNullOrWhiteSpace(options.Username) || string.IsNullOrWhiteSpace(options.Password))
            throw new InvalidOperationException("BrowserTestUser requires both Username and Password when enabled.");
        var db = provider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockInstallationAsync(db, cancellationToken);
        var users = provider.GetRequiredService<UserManager<ApplicationUser>>();
        // Existing identity, activation, credentials and administrator-selected roles are authoritative.
        if (await users.FindByNameAsync(options.Username.Trim()) is not null) return;
        var user = new ApplicationUser
        {
            UserName = options.Username.Trim(), Email = $"{options.Username.Trim()}@browser-test.invalid",
            EmailConfirmed = true, DisplayName = "Browser Test User", IsActive = true
        };
        ThrowIfFailed(await users.CreateAsync(user, options.Password), "create the browser-test user");
        ThrowIfFailed(await users.AddToRolesAsync(user, [AppRoles.Quality, AppRoles.Production]), "assign initial browser-test roles");
        await transaction.CommitAsync(cancellationToken);
        provider.GetRequiredService<ILoggerFactory>().CreateLogger("IdentityBootstrapper")
            .LogInformation("Created the configured Development browser-test account.");
    }

    public static async Task CreateInitialAdministratorAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var options = provider.GetRequiredService<IOptions<BootstrapAdminOptions>>().Value;
        if (string.IsNullOrWhiteSpace(options.Username) && string.IsNullOrWhiteSpace(options.Email) && string.IsNullOrWhiteSpace(options.Password)) return;
        var db = provider.GetRequiredService<AppDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockInstallationAsync(db, cancellationToken);
        // Normal startup is never security administration, even if bootstrap settings were left behind.
        if (await db.Users.AnyAsync(cancellationToken))
        {
            provider.GetRequiredService<ILoggerFactory>().CreateLogger("IdentityBootstrapper")
                .LogWarning("BootstrapAdmin ignored because accounts already exist. Remove the installation configuration.");
            return;
        }
        if (string.IsNullOrWhiteSpace(options.Username) || string.IsNullOrWhiteSpace(options.Email) || string.IsNullOrWhiteSpace(options.Password))
            throw new InvalidOperationException("BootstrapAdmin requires Username, Email, and Password when enabled.");
        var state = await db.AuthorizationState.AsNoTracking().SingleAsync(cancellationToken);
        if (state.Readiness != AuthorizationReadiness.PendingRoot || state.RootUserId is not null)
            throw new InvalidOperationException("Initial account creation requires an unprovisioned installation.");
        // Canonical roles and the permission manifest are migration-owned. Do not reconstruct them by name.
        var user = new ApplicationUser
        {
            UserName = options.Username.Trim(), Email = options.Email.Trim(), EmailConfirmed = true, IsActive = true,
            DisplayName = string.IsNullOrWhiteSpace(options.DisplayName) ? options.Email.Trim() : options.DisplayName.Trim()
        };
        var users = provider.GetRequiredService<UserManager<ApplicationUser>>();
        ThrowIfFailed(await users.CreateAsync(user, options.Password), "create the initial administrator");
        ThrowIfFailed(await users.AddToRoleAsync(user, AppRoles.Administrator), "assign the initial administrator role");
        await transaction.CommitAsync(cancellationToken);
        provider.GetRequiredService<ILoggerFactory>().CreateLogger("IdentityBootstrapper")
            .LogInformation("Created the initial installation account. Explicit offline Root designation is still required.");
    }

    private static Task LockInstallationAsync(AppDbContext db, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtext('confast_identity_installation'))", ct);

    private static void ThrowIfFailed(IdentityResult result, string operation)
    {
        if (!result.Succeeded) throw new InvalidOperationException($"Could not {operation}: {string.Join(" ", result.Errors.Select(x => x.Description))}");
    }
}
