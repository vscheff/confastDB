using System.Security.Claims;
using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Confast.Web.Features.Navigation;

internal sealed class AppearancePreferenceService(
    IDbContextFactory<AppDbContext> factory,
    AuthenticationStateProvider authentication,
    IOptions<IdentityOptions> identityOptions)
{
    internal async Task<AppearancePreference> LoadAsync() =>
        await LoadAsync((await authentication.GetAuthenticationStateAsync()).User);

    // App.razor uses the authenticated HTTP request for the first paint. Circuit
    // callers use AuthenticationStateProvider exclusively.
    internal async Task<AppearancePreference> LoadAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        var (id, stamp) = RequireAccount(principal);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var preference = await (from user in db.Users
            where user.Id == id && user.IsActive && user.SecurityStamp == stamp
            join settings in db.Set<NavigationPreference>() on user.Id equals settings.UserId into preferences
            from settings in preferences.DefaultIfEmpty()
            select new { settings.AppearanceMode, settings.AccentColor }).SingleOrDefaultAsync(cancellationToken);

        if (preference is null)
            throw new AuthorizationDeniedException(AuthorizationDenial.InvalidSession);

        return AppearancePreference.FromStored(preference.AppearanceMode, preference.AccentColor);
    }

    internal async Task<AppearancePreference> SaveAsync(AppearanceMode? mode, string? accent,
        CancellationToken cancellationToken = default)
    {
        var (id, stamp) = RequireAccount((await authentication.GetAuthenticationStateAsync()).User);
        if (mode is { } value && !Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(mode));
        var storedMode = mode?.ToString().ToLowerInvariant();
        var storedAccent = AppearancePreference.NormalizeAccent(accent);
        if (accent is not null && storedAccent is null)
            throw new ArgumentException("Choose a valid HEX color.", nameof(accent));
        if (mode is null && accent is null)
            return await LoadAsync();

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // The Identity row stabilizes the account/session check during the write.
        // The upsert composes independent edits with the latest preference row,
        // including a first write racing Quick Access or another device.
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO navigation_preferences (user_id, appearance_mode, accent_color)
            SELECT id, {storedMode}, {storedAccent}
            FROM identity_users
            WHERE id = {id} AND is_active AND security_stamp = {stamp}
            FOR SHARE
            ON CONFLICT (user_id) DO UPDATE SET
                appearance_mode = COALESCE(EXCLUDED.appearance_mode, navigation_preferences.appearance_mode),
                accent_color = COALESCE(EXCLUDED.accent_color, navigation_preferences.accent_color)
            """, cancellationToken);
        if (updated == 0)
            throw new AuthorizationDeniedException(AuthorizationDenial.InvalidSession);

        var preference = await db.Set<NavigationPreference>().AsNoTracking()
            .Where(settings => settings.UserId == id)
            .Select(settings => new { settings.AppearanceMode, settings.AccentColor })
            .SingleAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AppearancePreference.FromStored(preference.AppearanceMode, preference.AccentColor);
    }

    private (string Id, string Stamp) RequireAccount(ClaimsPrincipal principal)
    {
        var claimTypes = identityOptions.Value.ClaimsIdentity;
        var id = principal.FindFirstValue(claimTypes.UserIdClaimType);
        var stamp = principal.FindFirstValue(claimTypes.SecurityStampClaimType);
        if (principal.Identity?.IsAuthenticated != true || string.IsNullOrEmpty(id))
            throw new AuthorizationDeniedException(AuthorizationDenial.Unauthenticated);
        if (string.IsNullOrEmpty(stamp))
            throw new AuthorizationDeniedException(AuthorizationDenial.InvalidSession);
        return (id, stamp);
    }
}
