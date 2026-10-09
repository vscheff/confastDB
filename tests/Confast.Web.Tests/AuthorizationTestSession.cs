using System.Security.Claims;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Confast.Web.Tests;

// Explicit disposable-database setup. Reset remains PendingRoot; individual tests opt in.
internal sealed class AuthorizationTestSession(PostgresTestDatabase database) : AuthenticationStateProvider, IDisposable
{
    private readonly PermissionCache cache = new();
    internal ClaimsPrincipal Principal { get; set; } = new(new ClaimsIdentity());
    internal string ActorId => Principal.FindFirstValue(ClaimTypes.NameIdentifier)!;
    internal EffectivePermissionService Evaluator => new(database, this, Options.Create(new IdentityOptions()), cache,
        NullLogger<EffectivePermissionService>.Instance);
    internal ApplicationAuthorization Guard => new(Evaluator, new HttpContextAccessor());
    public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(Principal));

    internal async Task<string> ProvisionRootAsync()
    {
        await using var db = database.CreateDbContext();
        var state = await db.AuthorizationState.AsNoTracking().SingleAsync();
        if (state.RootUserId is not null) return state.RootUserId;
        var user = new ApplicationUser { UserName = "offline.test.root", NormalizedUserName = "OFFLINE.TEST.ROOT",
            DisplayName = "Test Installation Owner", Email = "owner@disposable.example.test", NormalizedEmail = "OWNER@DISPOSABLE.EXAMPLE.TEST",
            SecurityStamp = Guid.NewGuid().ToString(), ConcurrencyStamp = Guid.NewGuid().ToString(), IsActive = true };
        user.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(user, "Disposable-Test-Root-9!");
        db.Users.Add(user); await db.SaveChangesAsync();
        var epoch = await db.AuthorizationState.Select(x => x.GlobalEpoch).SingleAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT provision_confast_root({user.Id}, {epoch}, {"Explicit disposable-test owner provisioning"}, {"excluded-development-browser-account"})");
        return user.Id;
    }

    internal async Task SignInAsync(string id)
    {
        await using var db = database.CreateDbContext();
        var user = await db.Users.SingleAsync(x => x.Id == id);
        if (string.IsNullOrEmpty(user.SecurityStamp))
        { user.SecurityStamp = Guid.NewGuid().ToString(); user.ConcurrencyStamp ??= Guid.NewGuid().ToString(); await db.SaveChangesAsync(); }
        Principal = UserPrincipal(user);
    }
    internal static ClaimsPrincipal UserPrincipal(ApplicationUser user) => new(new ClaimsIdentity([
        new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(new IdentityOptions().ClaimsIdentity.SecurityStampClaimType, user.SecurityStamp!)
    ], IdentityConstants.ApplicationScheme));
    public void Dispose() => cache.Dispose();
}
