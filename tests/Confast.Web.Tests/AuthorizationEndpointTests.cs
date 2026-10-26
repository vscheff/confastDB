using System.Net;
using Microsoft.AspNetCore.Builder;
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
        using var operation = PrintTestCapability.Operation("InspectionSheets.Export", 1, Permissions.InspectionSheets.Export);
        var token = factory.Services
            .GetRequiredService<InspectionPrintRenderTokenService>()
            .CreateForExport(operation, 1);

        var response = await client.GetAsync($"/inspections/1/print?renderToken={Uri.EscapeDataString(token)}");
        var content = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("inspection-print-page", content);
        Assert.DoesNotContain("Open chat", content);
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
            if (root)
            {
                actor = await db.Users.SingleAsync(x => x.Id == rootId);
                await db.UserRoles.Where(x => x.UserId == rootId && x.RoleId == AppRoles.ReadOnlyId).ExecuteDeleteAsync();
            }
            else
            {
                actor = new() { UserName = "actual.admin", NormalizedUserName = "ACTUAL.ADMIN", DisplayName = "Actual Admin",
                    SecurityStamp = Guid.NewGuid().ToString(), ConcurrencyStamp = Guid.NewGuid().ToString() };
                db.Users.Add(actor); await db.SaveChangesAsync();
                db.UserRoles.Add(new() { UserId = actor.Id, RoleId = AppRoles.AdministratorId }); await db.SaveChangesAsync();
            }
        }
        await using var factory = CreateAuthenticatedFactory(root ? AppRoles.Root : AppRoles.Administrator, actor.Id, actor.SecurityStamp!);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/roles")).StatusCode);
        if (root)
        {
            await using var verify = database.CreateDbContext();
            Assert.False(await verify.UserRoles.AnyAsync(x => x.UserId == actor.Id && x.RoleId == AppRoles.AdministratorId));
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/nominal-tolerance")).StatusCode);
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

    [Fact]
    public async Task PrintCapabilityCannotAuthenticateInteractiveChatAdminOrNestedDownloads_AndOptionsAreBound()
    {
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        var service = factory.Services.GetRequiredService<InspectionPrintRenderTokenService>();
        using var operation = PrintTestCapability.Operation("InspectionSheets.Export", 1, Permissions.InspectionSheets.Export);
        var token = Uri.EscapeDataString(service.CreateForExport(operation, 1));
        foreach (var path in new[] { "/customers", "/admin/users", "/chat/attachments/1", "/inspections/1/print/download", "/inspections/2/print" })
        {
            var response = await client.GetAsync($"{path}?renderToken={token}");
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Equal("/login", response.Headers.Location?.AbsolutePath);
            Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync($"/account/session?renderToken={token}")).StatusCode);
        var changedOptions = await client.GetAsync($"/inspections/1/print?renderToken={token}&notesPage=second");
        Assert.Equal(HttpStatusCode.Redirect, changedOptions.StatusCode);
        Assert.Equal("/access-denied", changedOptions.Headers.Location?.AbsolutePath);
        var valid = await client.GetAsync($"/inspections/1/print?renderToken={token}");
        Assert.Equal(HttpStatusCode.OK, valid.StatusCode);
        Assert.Equal("no-referrer", valid.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task IssuedPrintCapabilitySurvivesGrantRevocationOnlyUntilItsOriginalExpiry()
    {
        await database.ResetAsync(); using var session = new AuthorizationTestSession(database);
        await session.ProvisionRootAsync();
        ApplicationUser actor;
        await using (var db = database.CreateDbContext())
        {
            actor = new() { UserName = "inflight.fixture", DisplayName = "Fixture", SecurityStamp = Guid.NewGuid().ToString() };
            var role = new ApplicationRole { Name = "Export fixture", NormalizedName = "EXPORT FIXTURE" };
            db.Users.Add(actor); db.Roles.Add(role);
            db.RolePermissions.AddRange(PermissionCatalog.ExpandRequirements([Permissions.InspectionSheets.Export])
                .Select(key => new RolePermission { RoleId = role.Id, PermissionKey = key }));
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = actor.Id, RoleId = role.Id }); await db.SaveChangesAsync();
        }
        await session.SignInAsync(actor.Id);
        var clock = new CapabilityTestClock();
        await using var factory = CreateFactory().WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(clock)));
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        using var operation = await session.Guard.BeginAsync("InspectionSheets.Export", new("Inspection", "1"), [Permissions.InspectionSheets.Export]);
        var token = factory.Services.GetRequiredService<InspectionPrintRenderTokenService>().CreateForExport(operation, 1);
        await using (var db = database.CreateDbContext()) await db.UserRoles.Where(x => x.UserId == actor.Id).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => session.Guard.BeginAsync("InspectionSheets.Export", new("Inspection", "1"), [Permissions.InspectionSheets.Export]));
        var url = $"/inspections/1/print?renderToken={Uri.EscapeDataString(token)}";
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
        clock.Now += TimeSpan.FromMinutes(2);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync(url)).StatusCode);
    }

    private sealed class CapabilityTestClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task DevelopmentEmailDiagnostic_IsExcludedFromProduction_RequiresCurrentPermissionAndAntiforgery()
    {
        await database.ResetAsync(); using var session = new AuthorizationTestSession(database);
        await session.ProvisionRootAsync();
        ApplicationUser actor;
        await using (var db = database.CreateDbContext())
        {
            actor = new() { UserName = "diagnostic.fixture", DisplayName = "Fixture", Email = "sender@fixture.invalid", SecurityStamp = Guid.NewGuid().ToString() };
            db.Users.Add(actor); await db.SaveChangesAsync();
            await db.UserRoles.Where(x => x.UserId == actor.Id).ExecuteDeleteAsync();
        }
        await using (var production = CreateAuthenticatedFactory(AppRoles.Administrator, actor.Id, actor.SecurityStamp!))
        {
            using var client = production.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/development/smtp-test")).StatusCode);
            var endpoints = production.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>().SelectMany(x => x.Endpoints);
            Assert.DoesNotContain(endpoints, x => x is Microsoft.AspNetCore.Routing.RouteEndpoint route && route.RoutePattern.RawText == "/development/smtp-test");
            Assert.Null(production.Services.GetService<DevelopmentUtilities>());
        }
        await using var development = CreateAuthenticatedFactory(AppRoles.Administrator, actor.Id, actor.SecurityStamp!, "Development")
            .WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddSingleton<IStartupFilter, LoopbackTestFilter>()));
        using var devClient = development.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        Assert.Equal(HttpStatusCode.Forbidden, (await devClient.PostAsync("/development/smtp-test", new StringContent(""))).StatusCode);
        await using (var db = database.CreateDbContext())
        {
            var role = new ApplicationRole { Name = "Development diagnostic fixture", NormalizedName = "DEVELOPMENT DIAGNOSTIC FIXTURE" };
            db.Roles.Add(role);
            db.RolePermissions.AddRange(PermissionCatalog.ExpandRequirements([Permissions.Certifications.SendEmail])
                .Select(key => new RolePermission { RoleId = role.Id, PermissionKey = key }));
            db.UserRoles.Add(new IdentityUserRole<string> { UserId = actor.Id, RoleId = role.Id }); await db.SaveChangesAsync();
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await devClient.PostAsync("/development/smtp-test", new StringContent(""))).StatusCode);
        var login = await devClient.GetStringAsync("/login");
        var antiforgery = WebUtility.HtmlDecode(Regex.Match(login, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var response = await devClient.PostAsync("/development/smtp-test", new FormUrlEncodedContent(new Dictionary<string,string> { ["__RequestVerificationToken"] = antiforgery }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("fixture@browser-test.invalid", body); Assert.Contains("No SMTP connection", body);
        await using var verify = database.CreateDbContext();
        Assert.Null((await verify.AuthorizationState.SingleAsync()).RootUserId == actor.Id ? actor.Id : null);
    }

    private sealed class LoopbackTestFilter : IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, proceed) => { context.Connection.RemoteIpAddress = IPAddress.Loopback; await proceed(); });
            next(app);
        };
    }

    private WebApplicationFactory<Program> CreateAuthenticatedFactory(string role, string actorId = "test-user", string stamp = "untrusted-stamp", string environment = "Production") =>
        CreateFactory(environment).WithWebHostBuilder(builder =>
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

    private WebApplicationFactory<Program> CreateFactory(string environment = "Production") =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Confast"] = database.ConnectionString,
                    ["BootstrapAdmin:Username"] = null,
                    ["BootstrapAdmin:Email"] = null,
                    ["BootstrapAdmin:Password"] = null,
                    ["BrowserTestUser:Username"] = null,
                    ["BrowserTestUser:Password"] = null
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BusinessDocumentAndExportUrlsRejectLegacyClaimsWithoutEffectiveGrants(bool authenticated)
    {
        await database.ResetAsync();
        using var session = new AuthorizationTestSession(database); await session.ProvisionRootAsync();
        ApplicationUser actor;
        await using (var db = database.CreateDbContext())
        {
            actor = new() { UserName = "http.no.roles", NormalizedUserName = "HTTP.NO.ROLES", DisplayName = "No Roles", SecurityStamp = Guid.NewGuid().ToString(), ConcurrencyStamp = Guid.NewGuid().ToString() };
            db.Users.Add(actor); await db.SaveChangesAsync();
            await db.UserRoles.Where(x => x.UserId == actor.Id).ExecuteDeleteAsync();
        }
        await using var factory = authenticated ? CreateAuthenticatedFactory(AppRoles.Administrator, actor.Id, actor.SecurityStamp!) : CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        foreach (var path in new[] { "/parts/1/inspection-criteria/1/master-print", "/inspections/1/certifications/documents/1",
            "/inspections/1/certifications/documents/1/preview", "/inspections/1/certifications/documents/1/download",
            "/inspections/1/print/download", "/inspections/1/inspection-sheet/download" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Redirect, response.StatusCode);
            Assert.NotEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
            if (authenticated) Assert.Equal("text/plain", response.Content.Headers.ContentType?.MediaType);
        }
        foreach (var path in new[] { "/customers", "/parts", "/gages", "/gage-types", "/inspections", "/suppliers", "/admin/certification-email-templates", "/admin/certification-email-settings", "/admin/nominal-tolerance" })
            Assert.Equal(authenticated ? HttpStatusCode.Forbidden : HttpStatusCode.Redirect, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task DirectCertificationDownloadUsesCurrentHttpActor_AndRevocationStopsTheNextRequest()
    {
        await database.ResetAsync(); using var session = new AuthorizationTestSession(database);
        await session.SignInAsync(await session.ProvisionRootAsync());
        var customers = new Confast.Web.Features.Customers.CustomerService(database, session.Guard);
        var parts = new Confast.Web.Features.Parts.PartService(database, session.Guard);
        var criteria = new Confast.Web.Features.InspectionCriteria.InspectionCriteriaService(database, session.Guard);
        var inspections = session.Inspections();
        var customerId = await customers.CreateCustomerAsync(new() { Name = "HTTP Fixture" });
        var partId = (await parts.CreatePartAsync(new() { CustomerId = customerId, PartNumber = "HTTP" })).Id!.Value;
        var revisionId = (await criteria.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        var gages = new Confast.Web.Features.Gages.GageService(database, session.Guard);
        await gages.SaveGageTypeAsync(new() { Name = "HTTP Caliper" });
        var gageTypeId = Assert.Single(await gages.GetGageTypesAsync()).Id;
        await criteria.AddCriterionAsync(partId, revisionId, new() { Name = "Length", GageTypeId = gageTypeId, Minimum = "1", MaximumOrTolerance = "2" });
        var revision = (await criteria.GetRevisionAsync(partId, revisionId))!;
        await criteria.PublishRevisionAsync(partId, revisionId, revision.Version);
        var inspectionId = (await inspections.CreateInspectionAsync(new() { PartId = partId, LotNumber = "HTTP", InspectionDate = new(2026,10,14) })).InspectionId!.Value;
        ApplicationUser actor; long documentId;
        var pdf = "%PDF-1.4\nHTTP authorization fixture\n%%EOF"u8.ToArray();
        await using (var db = database.CreateDbContext())
        {
            var typeId = await db.CertificationTypes.Where(x => x.Name != "Inspection Sheet").Select(x => x.Id).FirstAsync();
            await inspections.UploadCertificationDocumentAsync(inspectionId, typeId, "protected.pdf", pdf);
            documentId = await db.CertificationDocuments.Select(x => x.Id).SingleAsync();
            actor = new() { UserName = "http.documents", NormalizedUserName = "HTTP.DOCUMENTS", SecurityStamp = Guid.NewGuid().ToString(), ConcurrencyStamp = Guid.NewGuid().ToString() };
            var role = new ApplicationRole { Name = "Document Reader", NormalizedName = "DOCUMENT READER" };
            db.Users.Add(actor); db.Roles.Add(role); db.RolePermissions.Add(new() { RoleId = role.Id, PermissionKey = Permissions.Certifications.Read });
            db.UserRoles.Add(new() { UserId = actor.Id, RoleId = role.Id }); await db.SaveChangesAsync();
            await db.UserRoles.Where(x => x.UserId == actor.Id && x.RoleId == AppRoles.ReadOnlyId).ExecuteDeleteAsync();
        }
        await using var factory = CreateAuthenticatedFactory("forged-role", actor.Id, actor.SecurityStamp!);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        var url = $"/inspections/{inspectionId}/certifications/documents/{documentId}/download";
        var response = await client.GetAsync(url); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(pdf, await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/inspections/{inspectionId + 1}/certifications/documents/{documentId}/download")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/inspections/{inspectionId}/print/download")).StatusCode);
        await using (var db = database.CreateDbContext()) await db.UserRoles.Where(x => x.UserId == actor.Id).ExecuteDeleteAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task ChatMedia_UsesTheHttpActorAndCurrentAccess_AndNeverBypassesMembership()
    {
        await database.ResetAsync();
        using var rootSession = new AuthorizationTestSession(database);
        var rootId = await rootSession.ProvisionRootAsync();
        ApplicationUser member, owner, third;
        await using (var db = database.CreateDbContext())
        {
            ApplicationUser User(string name) => new() { UserName = name, NormalizedUserName = name.ToUpperInvariant(),
                DisplayName = name, SecurityStamp = Guid.NewGuid().ToString(), IsActive = true };
            owner = User("chat.http.owner"); member = User("chat.http.member"); third = User("chat.http.third");
            member.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(member, "Disposable-Chat-Http-9!");
            db.Users.AddRange(owner, member, third); await db.SaveChangesAsync();
        }
        using var ownerSession = ChatTestAuthorization.Session(database, owner.Id);
        using var memberSession = ChatTestAuthorization.Session(database, member.Id);
        var chat = new Confast.Web.Features.Chat.ChatService(database, ownerSession.Guard, ownerSession.Evaluator,
            new Microsoft.AspNetCore.Http.HttpContextAccessor(), new(Microsoft.Extensions.Logging.Abstractions.NullLogger<Confast.Web.Features.Chat.ChatNotifications>.Instance), TimeProvider.System);
        var channelId = await chat.CreateChannelAsync("HTTP private", Confast.Web.Features.Chat.ChannelVisibility.Private, [member.Id]);
        var png = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10, 1, 2, 3, 4 };
        await chat.SendAsync(channelId, "Private image", attachmentFileName: "private.png", attachmentContent: png);
        var groupId = await chat.CreateGroupAsync([member.Id, third.Id]);
        await chat.UpdateGroupAsync(groupId, "Private icon", new(png));
        await new UserProfilePictureService(database, ownerSession.Guard, new Microsoft.AspNetCore.Http.HttpContextAccessor(),
            new(Microsoft.Extensions.Logging.Abstractions.NullLogger<Confast.Web.Features.Chat.ChatNotifications>.Instance)).SaveAsync(png);
        long attachmentId;
        await using (var db = database.CreateDbContext()) attachmentId = await db.ChatAttachments.Select(x => x.Id).SingleAsync();
        string[] paths = [$"/chat/attachments/{attachmentId}", $"/chat/attachments/{attachmentId}/download", $"/chat/group-icons/{groupId}", $"/profile-pictures/{owner.Id}"];
        await using var factory = CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        async Task Login(HttpClient target, string username, string password)
        {
            var page = await target.GetStringAsync("/login");
            var token = WebUtility.HtmlDecode(Regex.Match(page, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
            var response = await target.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Username"] = username, ["Password"] = password, ["__RequestVerificationToken"] = token, ["ReturnUrl"] = "/"
            }));
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.DoesNotContain("/login", response.Headers.Location!.OriginalString);
        }
        await Login(client, member.UserName!, "Disposable-Chat-Http-9!");
        foreach (var path in paths)
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(png, await response.Content.ReadAsByteArrayAsync());
            Assert.True(response.Headers.CacheControl?.NoStore);
            Assert.Contains("nosniff", response.Headers.GetValues("X-Content-Type-Options"));
        }
        // Designated Root has all keys and still cannot read these private resources.
        await using var rootFactory = CreateFactory();
        using var rootClient = rootFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        await Login(rootClient, "offline.test.root", "Disposable-Test-Root-9!");
        Assert.Equal(HttpStatusCode.NotFound, (await rootClient.GetAsync(paths[0])).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await rootClient.GetAsync(paths[2])).StatusCode);
        await using (var db = database.CreateDbContext()) await db.UserRoles.Where(x => x.UserId == member.Id).ExecuteDeleteAsync();
        foreach (var path in paths) Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        await using (var db = database.CreateDbContext())
        {
            var roleId = await db.Roles.Where(x => x.Name == AppRoles.Quality).Select(x => x.Id).SingleAsync();
            db.UserRoles.Add(new() { UserId = member.Id, RoleId = roleId }); await db.SaveChangesAsync();
            await db.ChatConversationMembers.Where(x => x.UserId == member.Id && x.ConversationId == channelId).ExecuteDeleteAsync();
        }
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(paths[0])).StatusCode);
    }

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
