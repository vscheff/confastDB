using System.Net;
using System.Text.RegularExpressions;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Confast.Web.Features.Inspections;
using Confast.Web.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class AuthorizationEndpointTests(PostgresTestDatabase database)
{
    [Fact]
    public async Task UnauthenticatedUser_IsRedirectedToLogin()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.GetAsync("/customers");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task LoginPage_IsAnonymousAndRendersAntiforgeryForm()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.GetAsync("/login");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("action=\"/account/login\"", content);
        Assert.Contains("name=\"Username\"", content);
        Assert.Contains("__RequestVerificationToken", content);
    }

    [Fact]
    public async Task UnauthenticatedSessionProbe_ReturnsUnauthorized()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.GetAsync("/account/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AuthenticatedSessionProbe_ReturnsNoContent()
    {
        await using var factory = CreateAuthenticatedFactory(AppRoles.Quality);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.GetAsync("/account/session");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task AnonymousPrintRequest_IsRedirectedToLogin()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.GetAsync("/inspections/1/print");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login", response.Headers.Location?.AbsolutePath);
    }

    [Fact]
    public async Task ValidPrintRenderToken_IsAcceptedByTheAuthenticationPipeline()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });
        var token = factory.Services
            .GetRequiredService<InspectionPrintRenderTokenService>()
            .Create(1);

        var response = await client.GetAsync($"/inspections/1/print?renderToken={Uri.EscapeDataString(token)}");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("inspection-print-page", content);
    }

    [Fact]
    public async Task NormalUser_CannotAccessUserAdministration()
    {
        await using var factory = CreateAuthenticatedFactory(AppRoles.Quality);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.GetAsync("/admin/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task LegacyAdministratorClaimWithoutTrustedSession_CannotAccessUserAdministration()
    {
        await using var factory = CreateAuthenticatedFactory(AppRoles.Administrator);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            BaseAddress = new Uri("https://localhost")
        });

        var response = await client.GetAsync("/admin/users");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualPasswordResetEndpoint_RejectsUntrackedOrdinaryAndRootTokens(bool designateRoot)
    {
        await database.ResetAsync();
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        string id, token, hash;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var name = Guid.NewGuid().ToString();
            var user = new ApplicationUser { UserName = name, Email = name + "@example.test", DisplayName = "Reset test", IsActive = true };
            var created = await users.CreateAsync(user, "Old-Password-Test-9!");
            Assert.True(created.Succeeded, string.Join("; ", created.Errors.Select(x => x.Description)));
            id = user.Id;
            hash = user.PasswordHash!;
            token = await users.GeneratePasswordResetTokenAsync(user);
        }
        if (designateRoot)
        {
            await using var db = database.CreateDbContext();
            var epoch = await db.AuthorizationState.Select(x => x.GlobalEpoch).SingleAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT provision_confast_root({id}, {epoch}, {"Reset boundary regression"}, {"excluded-browser-test"})");
        }
        var page = await client.GetStringAsync($"/reset-password?userId={Uri.EscapeDataString(id)}&token={Uri.EscapeDataString(token)}");
        var antiforgery = WebUtility.HtmlDecode(Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(antiforgery);
        var response = await client.PostAsync("/account/reset-password", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["UserId"] = id, ["Token"] = token, ["Password"] = "New-Password-Test-9!", ["ConfirmPassword"] = "New-Password-Test-9!",
            ["__RequestVerificationToken"] = antiforgery
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/reset-password", response.Headers.Location!.OriginalString.Split('?')[0]);
        await using var verify = database.CreateDbContext();
        var after = await verify.Users.SingleAsync(x => x.Id == id);
        Assert.Equal(hash, after.PasswordHash);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadyDatabaseAuthorityAllowsRelevantAdministrationRoutes_WithoutManufacturingRootAdministratorClaim(bool root)
    {
        await database.ResetAsync(); using var session = new AuthorizationTestSession(database);
        var rootId = await session.ProvisionRootAsync();
        ApplicationUser actor;
        await using (var db = database.CreateDbContext())
        {
            if (root) actor = await db.Users.SingleAsync(x => x.Id == rootId);
            else
            {
                actor = new() { UserName = "actual.admin", NormalizedUserName = "ACTUAL.ADMIN", DisplayName = "Actual Admin",
                    SecurityStamp = Guid.NewGuid().ToString(), ConcurrencyStamp = Guid.NewGuid().ToString() };
                db.Users.Add(actor); await db.SaveChangesAsync();
                db.UserRoles.Add(new() { UserId = actor.Id, RoleId = AppRoles.AdministratorId }); await db.SaveChangesAsync();
            }
        }
        await using var factory = CreateAuthenticatedFactory(root ? AppRoles.ReadOnly : AppRoles.Administrator, actor.Id, actor.SecurityStamp!);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/roles")).StatusCode);
        if (root)
        {
            await using var verify = database.CreateDbContext();
            Assert.False(await verify.UserRoles.AnyAsync(x => x.UserId == actor.Id && x.RoleId == AppRoles.AdministratorId));
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/admin/nominal-tolerance")).StatusCode);
        }
    }

    [Fact]
    public async Task ActualAnonymousResetEndpointAcceptsTrackedDelegation_AndRejectsReplay()
    {
        await database.ResetAsync(); using var session = new AuthorizationTestSession(database);
        await session.SignInAsync(await session.ProvisionRootAsync());
        await using var factory = CreateFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services => {
            services.RemoveAll<AuthenticationStateProvider>(); services.AddSingleton<AuthenticationStateProvider>(session);
        }));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        string id, token;
        using (var scope = factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = new ApplicationUser { UserName = "tracked.reset", Email = "tracked.reset@example.test", DisplayName = "Tracked Reset" };
            Assert.True((await users.CreateAsync(user, "Endpoint-Original-9!")).Succeeded); id = user.Id;
            var administration = scope.ServiceProvider.GetRequiredService<UserAdministrationService>();
            var edit = (await administration.GetUserForEditAsync(id))!;
            token = (await administration.GeneratePasswordResetTokenAsync(id, edit.Version, edit.ConcurrencyStamp)).Token!;
        }
        session.Principal = new(new ClaimsIdentity());
        var page = await client.GetStringAsync($"/reset-password?userId={Uri.EscapeDataString(id)}&token={Uri.EscapeDataString(token)}");
        var antiforgery = WebUtility.HtmlDecode(Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        async Task<HttpResponseMessage> Submit() => await client.PostAsync("/account/reset-password", new FormUrlEncodedContent(new Dictionary<string, string> {
            ["UserId"] = id, ["Token"] = token, ["Password"] = "Endpoint-New-Password-9!", ["ConfirmPassword"] = "Endpoint-New-Password-9!", ["__RequestVerificationToken"] = antiforgery
        }));
        var accepted = await Submit(); Assert.Equal("/login", accepted.Headers.Location!.OriginalString.Split('?')[0]);
        var replay = await Submit(); Assert.Equal("/reset-password", replay.Headers.Location!.OriginalString.Split('?')[0]);
        await using var verify = database.CreateDbContext(); Assert.NotNull((await verify.PasswordResetDelegations.SingleAsync()).ConsumedAtUtc);
    }

    private WebApplicationFactory<Program> CreateAuthenticatedFactory(string role, string actorId = "test-user", string stamp = "untrusted-stamp") =>
        CreateFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultForbidScheme = TestAuthenticationHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
                        TestAuthenticationHandler.SchemeName,
                        _ => { });
                services.AddSingleton(new TestUserRole(role, actorId, stamp));
            });
        });

    private WebApplicationFactory<Program> CreateFactory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Confast"] = database.ConnectionString,
                    ["BootstrapAdmin:Username"] = null,
                    ["BootstrapAdmin:Email"] = null,
                    ["BootstrapAdmin:Password"] = null
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<AppDbContext>();
                services.RemoveAll<DbContextOptions<AppDbContext>>();
                services.RemoveAll<IDbContextFactory<AppDbContext>>();
                services.AddDbContextFactory<AppDbContext>(options =>
                    options.UseNpgsql(database.ConnectionString));
                services.RemoveAll<IDataProtectionProvider>();
                services.RemoveAll<IKeyManager>();
                for (var index = services.Count - 1; index >= 0; index--)
                {
                    if (services[index].ServiceType == typeof(IHostedService)
                        && services[index].ImplementationType?.FullName
                            == "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService")
                    {
                        services.RemoveAt(index);
                    }
                }

                services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
            });
        });

    private sealed record TestUserRole(string Role, string UserId, string SecurityStamp);

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        TestUserRole userRole)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userRole.UserId),
                new Claim(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType, userRole.SecurityStamp),
                new Claim(ClaimTypes.Name, "Test User"),
                new Claim(ClaimTypes.Role, userRole.Role)
            ], SchemeName);
            return Task.FromResult(
                AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }
}
