using Confast.Web.Data;
using Confast.Web.Features.Gages;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class IdentityTests(PostgresTestDatabase database) : IAsyncLifetime
{
    public async Task InitializeAsync() => await database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RoleAssignment_RoundTripsThroughUserAdministration()
    {
        long caliperId;
        await using (var db = database.CreateDbContext())
        {
            var type = new GageType { Name = "Digital Caliper" };
            db.GageTypes.Add(type);
            await db.SaveChangesAsync();
            var caliper = new Gage { GageTypeId = type.Id, GageNumber = "CAL-001" };
            db.Gages.Add(caliper);
            await db.SaveChangesAsync();
            caliperId = caliper.Id;
        }

        await using var services = CreateServices();
        var administration = services.GetRequiredService<UserAdministrationService>();

        var createResult = await administration.CreateUserAsync(new CreateUserInput
        {
            Username = "quality.person",
            DisplayName = "Quality Person",
            JobTitle = "Quality Engineer",
            Email = "quality@example.com",
            CaliperId = caliperId,
            Roles = [AppRoles.Quality, AppRoles.ReadOnly]
        });

        Assert.True(createResult.Succeeded, string.Join(" ", createResult.Errors));
        var edit = await administration.GetUserForEditAsync(createResult.UserId!);
        Assert.NotNull(edit);
        Assert.Equal("quality.person", edit.Username);
        Assert.Equal("Quality Engineer", edit.JobTitle);
        Assert.Equal(caliperId, edit.CaliperId);
        Assert.Equal(
            [AppRoles.Quality, AppRoles.ReadOnly],
            edit.Roles.Order(StringComparer.Ordinal));

        edit.Roles = [AppRoles.Production];
        var updateResult = await administration.UpdateUserAsync(edit);

        Assert.True(updateResult.Succeeded, string.Join(" ", updateResult.Errors));
        var updated = await administration.GetUserForEditAsync(createResult.UserId!);
        Assert.Equal([AppRoles.Production], updated!.Roles);
    }

    [Fact]
    public async Task QualityUserDisplayNames_IncludeOnlyQualityUsersInDisplayNameOrder()
    {
        await using var services = CreateServices();
        var administration = services.GetRequiredService<UserAdministrationService>();

        var qualityUser = await administration.CreateUserAsync(new CreateUserInput
        {
            Username = "zeta.quality",
            DisplayName = "Zeta Quality",
            Email = "zeta.quality@example.com",
            Roles = [AppRoles.Quality]
        });
        var secondQualityUser = await administration.CreateUserAsync(new CreateUserInput
        {
            Username = "alpha.quality",
            DisplayName = "Alpha Quality",
            Email = "alpha.quality@example.com",
            Roles = [AppRoles.Quality, AppRoles.ReadOnly]
        });
        var productionUser = await administration.CreateUserAsync(new CreateUserInput
        {
            Username = "production.person",
            DisplayName = "Production Person",
            Email = "production.person@example.com",
            Roles = [AppRoles.Production]
        });

        Assert.True(qualityUser.Succeeded);
        Assert.True(secondQualityUser.Succeeded);
        Assert.True(productionUser.Succeeded);

        var displayNames = await administration.GetQualityUserDisplayNamesAsync();

        Assert.Equal(["Alpha Quality", "Zeta Quality"], displayNames);
    }

    [Fact]
    public async Task BrowserTestUserProvisioning_CreatesQualityUserWithoutAdministratorRole()
    {
        await using var services = CreateServices(new BrowserTestUserOptions
        {
            Username = "browser-test",
            Password = "Browser-test1!"
        });

        await IdentityBootstrapper.EnsureBrowserTestUserAsync(services);

        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.FindByNameAsync("browser-test");
        Assert.NotNull(user);
        Assert.True(await userManager.CheckPasswordAsync(user, "Browser-test1!"));
        Assert.True(await userManager.IsInRoleAsync(user, AppRoles.Quality));
        Assert.False(await userManager.IsInRoleAsync(user, AppRoles.Administrator));
    }

    [Fact]
    public async Task BrowserTestUserProvisioning_SkipsWhenCredentialsAreMissing()
    {
        await using var services = CreateServices(new BrowserTestUserOptions());

        await IdentityBootstrapper.EnsureBrowserTestUserAsync(services);

        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        Assert.Null(await userManager.FindByNameAsync("browser-test"));
    }

    [Fact]
    public async Task BrowserTestUserProvisioning_RejectsPartialCredentials()
    {
        await using var services = CreateServices(new BrowserTestUserOptions
        {
            Username = "browser-test"
        });

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => IdentityBootstrapper.EnsureBrowserTestUserAsync(services));

        Assert.Contains("BrowserTestUser requires both Username and Password", exception.Message);
    }

    [Fact]
    public async Task CaliperChoices_OnlyIncludeActiveDigitalCalipers()
    {
        await using (var db = database.CreateDbContext())
        {
            var digitalType = new GageType { Name = "Digital Calipers" };
            var otherType = new GageType { Name = "Micrometer" };
            db.GageTypes.AddRange(digitalType, otherType);
            await db.SaveChangesAsync();
            db.Gages.AddRange(
                new Gage { GageTypeId = digitalType.Id, GageNumber = "CAL-001", IsActive = true },
                new Gage { GageTypeId = digitalType.Id, GageNumber = "CAL-002", IsActive = false },
                new Gage { GageTypeId = otherType.Id, GageNumber = "MIC-001", IsActive = true });
            await db.SaveChangesAsync();
        }

        await using var services = CreateServices();
        var administration = services.GetRequiredService<UserAdministrationService>();

        var choices = await administration.GetDigitalCaliperChoicesAsync();

        Assert.Single(choices);
        Assert.Equal("CAL-001", choices[0].GageNumber);
    }

    [Fact]
    public async Task DisabledUser_CannotPassPasswordSignInCheck()
    {
        await using var services = CreateServices();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var signInManager = services.GetRequiredService<SignInManager<ApplicationUser>>();
        var administration = services.GetRequiredService<UserAdministrationService>();
        var user = new ApplicationUser
        {
            UserName = "disabled.person",
            Email = "disabled@example.com",
            EmailConfirmed = true,
            DisplayName = "Disabled Person",
            IsActive = true
        };
        var createResult = await userManager.CreateAsync(user, "Valid-password1!");
        Assert.True(createResult.Succeeded, string.Join(" ", createResult.Errors.Select(x => x.Description)));
        Assert.True((await signInManager.CheckPasswordSignInAsync(user, "Valid-password1!", true)).Succeeded);

        var edit = (await administration.GetUserForEditAsync(user.Id))!;
        edit.IsActive = false;
        var updateResult = await administration.UpdateUserAsync(edit);

        Assert.True(updateResult.Succeeded, string.Join(" ", updateResult.Errors));
        var disabledUser = (await userManager.FindByIdAsync(user.Id))!;
        var signInResult = await signInManager.CheckPasswordSignInAsync(
            disabledUser,
            "Valid-password1!",
            lockoutOnFailure: true);
        Assert.False(signInResult.Succeeded);
        Assert.True(signInResult.IsNotAllowed);
    }

    [Fact]
    public async Task LastActiveAdministrator_CannotBeDeactivated()
    {
        await using var services = CreateServices();
        var administration = services.GetRequiredService<UserAdministrationService>();
        var createResult = await administration.CreateUserAsync(new CreateUserInput
        {
            Username = "only.admin",
            DisplayName = "Only Admin",
            Email = "admin@example.com",
            Roles = [AppRoles.Administrator]
        });
        var edit = (await administration.GetUserForEditAsync(createResult.UserId!))!;
        edit.IsActive = false;

        var result = await administration.UpdateUserAsync(edit);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, x => x.Contains("last active administrator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task NormalizedEmail_IsUniqueInTheDatabase()
    {
        await using var db = database.CreateDbContext();
        db.Users.AddRange(
            new ApplicationUser
            {
                Id = Guid.NewGuid().ToString(),
                UserName = "first@example.com",
                NormalizedUserName = "FIRST@EXAMPLE.COM",
                Email = "same@example.com",
                NormalizedEmail = "SAME@EXAMPLE.COM",
                DisplayName = "First User",
                SecurityStamp = Guid.NewGuid().ToString(),
                ConcurrencyStamp = Guid.NewGuid().ToString()
            },
            new ApplicationUser
            {
                Id = Guid.NewGuid().ToString(),
                UserName = "second@example.com",
                NormalizedUserName = "SECOND@EXAMPLE.COM",
                Email = "same@example.com",
                NormalizedEmail = "SAME@EXAMPLE.COM",
                DisplayName = "Second User",
                SecurityStamp = Guid.NewGuid().ToString(),
                ConcurrencyStamp = Guid.NewGuid().ToString()
            });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task Administrator_CanDeleteAnotherUserButNotTheirOwnAccount()
    {
        await using var services = CreateServices();
        var administration = services.GetRequiredService<UserAdministrationService>();
        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        var administrator = await administration.CreateUserAsync(new CreateUserInput
        {
            Username = "deleting.admin",
            DisplayName = "Deleting Admin",
            Email = "deleting.admin@example.com",
            Roles = [AppRoles.Administrator]
        });
        var target = await administration.CreateUserAsync(new CreateUserInput
        {
            Username = "delete.target",
            DisplayName = "Delete Target",
            Email = "delete.target@example.com",
            Roles = [AppRoles.Quality]
        });

        var selfDeleteResult = await administration.DeleteUserAsync(
            administrator.UserId!,
            administrator.UserId);
        var deleteResult = await administration.DeleteUserAsync(
            target.UserId!,
            administrator.UserId);

        Assert.False(selfDeleteResult.Succeeded);
        Assert.Contains(selfDeleteResult.Errors, x => x.Contains("own account", StringComparison.OrdinalIgnoreCase));
        Assert.True(deleteResult.Succeeded, string.Join(" ", deleteResult.Errors));
        Assert.Null(await userManager.FindByIdAsync(target.UserId!));
        Assert.NotNull(await userManager.FindByIdAsync(administrator.UserId!));
    }

    private ServiceProvider CreateServices(BrowserTestUserOptions? browserTestUser = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<BrowserTestUserOptions>().Configure(options =>
        {
            if (browserTestUser is null)
            {
                return;
            }

            options.Username = browserTestUser.Username;
            options.Password = browserTestUser.Password;
        });
        services.AddHttpContextAccessor();
        services.AddAuthentication();
        services.AddDataProtection();
        services.AddDbContextFactory<AppDbContext>(options => options.UseNpgsql(database.ConnectionString));
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 12;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
            })
            .AddRoles<IdentityRole>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddSignInManager<ApplicationSignInManager>()
            .AddDefaultTokenProviders();
        services.AddScoped<UserAdministrationService>();
        return services.BuildServiceProvider();
    }
}
