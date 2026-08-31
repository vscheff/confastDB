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
        var httpPrincipal = httpContextAccessor.HttpContext?.User;
        if (httpPrincipal?.Identity?.IsAuthenticated == true)
        {
            return httpPrincipal.FindFirstValue(ClaimTypes.NameIdentifier);
        }

        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        return state.User.FindFirstValue(ClaimTypes.NameIdentifier);
    }
}
