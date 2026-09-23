using System.Security.Claims;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;

namespace Confast.Web.Tests;

public sealed class CurrentUserTests
{
    [Fact]
    public async Task CircuitIdentityTakesPrecedenceOverAnotherRequestAmbientContext()
    {
        var circuitUserId = Guid.NewGuid().ToString();
        var ambientHttpUserId = Guid.NewGuid().ToString();
        var httpContext = new DefaultHttpContext
        {
            User = Principal(ambientHttpUserId)
        };
        var authenticationState = new TestAuthenticationStateProvider(Principal(circuitUserId));
        var currentUser = new CurrentUser(new HttpContextAccessor { HttpContext = httpContext }, authenticationState);

        Assert.Equal(circuitUserId, await currentUser.GetUserIdAsync());
    }

    [Fact]
    public async Task HttpContextRemainsAFallbackWhenCircuitIsUnauthenticated()
    {
        var httpUserId = Guid.NewGuid().ToString();
        var authenticationState = new TestAuthenticationStateProvider(new ClaimsPrincipal(new ClaimsIdentity()));
        var currentUser = new CurrentUser(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = Principal(httpUserId) } },
            authenticationState);

        Assert.Equal(httpUserId, await currentUser.GetUserIdAsync());
    }

    private static ClaimsPrincipal Principal(string userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test"));

    private sealed class TestAuthenticationStateProvider(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(principal));
    }
}
