using Confast.Web.Features.Authorization;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Tests;

// Existing behavior regressions opt into the seeded ordinary chat grants explicitly.
// Security-matrix tests construct their own exact grants instead.
internal static class ChatTestAuthorization
{
    internal static AuthorizationTestSession Session(PostgresTestDatabase database, string userId)
    {
        using var db = database.CreateDbContext();
        var user = db.Users.SingleOrDefault(x => x.Id == userId);
        if (user is not null)
        {
            user.SecurityStamp ??= Guid.NewGuid().ToString();
            user.ConcurrencyStamp ??= Guid.NewGuid().ToString();
            var roleId = db.Roles.Where(x => x.Name == AppRoles.Quality).Select(x => x.Id).Single();
            if (!db.UserRoles.Any(x => x.UserId == userId && x.RoleId == roleId))
                db.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = roleId });
            db.SaveChanges();
        }
        return new(database) { Principal = AuthorizationTestSession.UserPrincipal(user
            ?? new ApplicationUser { Id = userId, SecurityStamp = "missing-user" }) };
    }
}
