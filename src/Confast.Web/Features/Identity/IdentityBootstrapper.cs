using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Confast.Web.Features.Identity;

public static class IdentityBootstrapper
{
    public static async Task EnsureBrowserTestUserAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var options = scope.ServiceProvider
            .GetRequiredService<IOptions<BrowserTestUserOptions>>()
            .Value;
        var hasUsername = !string.IsNullOrWhiteSpace(options.Username);
        var hasPassword = !string.IsNullOrWhiteSpace(options.Password);
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("IdentityBootstrapper");

        if (!hasUsername && !hasPassword)
        {
            logger.LogInformation(
                "Browser-test user provisioning skipped because BrowserTestUser credentials are not configured.");
            return;
        }

        if (!hasUsername || !hasPassword)
        {
            throw new InvalidOperationException(
                "BrowserTestUser requires both Username and Password when enabled.");
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        if (!await roleManager.RoleExistsAsync(AppRoles.Quality))
        {
            var roleResult = await roleManager.CreateAsync(new IdentityRole(AppRoles.Quality));
            ThrowIfFailed(roleResult, $"create the {AppRoles.Quality} role");
        }

        var username = options.Username!.Trim();
        var existingUser = await userManager.FindByNameAsync(username);
        if (existingUser is not null)
        {
            if (!existingUser.IsActive)
            {
                existingUser.IsActive = true;
                ThrowIfFailed(
                    await userManager.UpdateAsync(existingUser),
                    "reactivate the browser-test user");
            }

            if (!await userManager.IsInRoleAsync(existingUser, AppRoles.Quality))
            {
                ThrowIfFailed(
                    await userManager.AddToRoleAsync(existingUser, AppRoles.Quality),
                    $"assign the {AppRoles.Quality} role to the browser-test user");
            }

            logger.LogInformation("Browser-test user {Username} is available.", username);
            return;
        }

        var user = new ApplicationUser
        {
            UserName = username,
            Email = $"{username}@browser-test.invalid",
            EmailConfirmed = true,
            DisplayName = "Browser Test User",
            IsActive = true
        };

        ThrowIfFailed(
            await userManager.CreateAsync(user, options.Password!),
            "create the browser-test user");
        ThrowIfFailed(
            await userManager.AddToRoleAsync(user, AppRoles.Quality),
            $"assign the {AppRoles.Quality} role to the browser-test user");

        logger.LogInformation("Created browser-test user {Username}.", username);
    }

    public static async Task CreateInitialAdministratorAsync(
        IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var options = scope.ServiceProvider
            .GetRequiredService<IOptions<BootstrapAdminOptions>>()
            .Value;

        var hasUsername = !string.IsNullOrWhiteSpace(options.Username);
        var hasEmail = !string.IsNullOrWhiteSpace(options.Email);
        var hasPassword = !string.IsNullOrWhiteSpace(options.Password);
        if (!hasUsername && !hasEmail && !hasPassword)
        {
            return;
        }

        if (!hasUsername || !hasEmail || !hasPassword)
        {
            throw new InvalidOperationException(
                "BootstrapAdmin requires Username, Email, and Password when enabled.");
        }

        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("IdentityBootstrapper");

        foreach (var roleName in AppRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                var roleResult = await roleManager.CreateAsync(new IdentityRole(roleName));
                ThrowIfFailed(roleResult, $"create the {roleName} role");
            }
        }

        var existingUser = await userManager.FindByNameAsync(options.Username!.Trim());
        if (existingUser is not null)
        {
            if (!await userManager.IsInRoleAsync(existingUser, AppRoles.Administrator))
            {
                var roleResult = await userManager.AddToRoleAsync(existingUser, AppRoles.Administrator);
                ThrowIfFailed(roleResult, "assign the bootstrap administrator role");
            }

            return;
        }

        if (await userManager.Users.AnyAsync(cancellationToken))
        {
            logger.LogWarning(
                "Bootstrap administrator configuration was ignored because users already exist. Remove the BootstrapAdmin configuration.");
            return;
        }

        var username = options.Username!.Trim();
        var email = options.Email!.Trim();
        var user = new ApplicationUser
        {
            UserName = username,
            Email = email,
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(options.DisplayName)
                ? email
                : options.DisplayName.Trim(),
            IsActive = true
        };

        var createResult = await userManager.CreateAsync(user, options.Password!);
        ThrowIfFailed(createResult, "create the bootstrap administrator");
        var addRoleResult = await userManager.AddToRoleAsync(user, AppRoles.Administrator);
        ThrowIfFailed(addRoleResult, "assign the bootstrap administrator role");

        logger.LogInformation(
            "Created the initial administrator account {Username} for {Email}.",
            username,
            email);
    }

    private static void ThrowIfFailed(IdentityResult result, string operation)
    {
        if (result.Succeeded)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Could not {operation}: {string.Join(" ", result.Errors.Select(x => x.Description))}");
    }
}
