using Confast.Web.Data;
using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.ContainerTracking;

public sealed record TrackingPermissions(bool CanEdit, bool IsAdministrator);

// Resolve roles from the database at the service boundary, including account deactivation.
public sealed class TrackingAccess(IDbContextFactory<AppDbContext> contextFactory, ICurrentUser currentUser)
{
    public async Task<TrackingPermissions> GetAsync(CancellationToken cancellationToken = default)
    {
        var userId = await currentUser.GetUserIdAsync();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (userId is null || !await db.Users.AnyAsync(x => x.Id == userId && x.IsActive, cancellationToken))
            throw new UnauthorizedAccessException("Sign in with an active account to use Container Tracking.");
        var roles = await (from assignment in db.UserRoles
            join role in db.Roles on assignment.RoleId equals role.Id
            where assignment.UserId == userId
            select role.Name).ToListAsync(cancellationToken);
        var admin = roles.Contains(AppRoles.Administrator);
        return new(admin || roles.Contains(AppRoles.Production) || roles.Contains(AppRoles.Quality), admin);
    }

    public async Task<TrackingPermissions> RequireEditAsync(CancellationToken cancellationToken = default)
    {
        var permissions = await GetAsync(cancellationToken);
        if (!permissions.CanEdit)
            throw new UnauthorizedAccessException("Administrator, Production, or Quality access is required to edit these records.");
        return permissions;
    }
}
