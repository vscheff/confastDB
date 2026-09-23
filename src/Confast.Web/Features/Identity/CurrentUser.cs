using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace Confast.Web.Features.Identity;

public interface ICurrentUser
{
    ValueTask<string?> GetUserIdAsync();
}

public sealed class CurrentUser(
    IHttpContextAccessor httpContextAccessor,
    AuthenticationStateProvider authenticationStateProvider) : ICurrentUser
{
    public async ValueTask<string?> GetUserIdAsync()
    {
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        if (state.User.Identity?.IsAuthenticated == true)
        {
            var circuitUserId = state.User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (circuitUserId is not null) return circuitUserId;
        }

        // Circuit callbacks can inherit another request's ambient HttpContext when a shared
        // notification service schedules work on a different circuit. Prefer the circuit's
        // AuthenticationState above; use HttpContext only for ordinary HTTP request callers.
        var httpPrincipal = httpContextAccessor.HttpContext?.User;
        return httpPrincipal?.Identity?.IsAuthenticated == true
            ? httpPrincipal.FindFirstValue(ClaimTypes.NameIdentifier)
            : null;
    }
}
