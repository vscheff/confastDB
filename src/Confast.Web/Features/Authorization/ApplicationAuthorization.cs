using Microsoft.AspNetCore.Authorization;

namespace Confast.Web.Features.Authorization;

public sealed class ApplicationAuthorization(EffectivePermissionService permissions, IHttpContextAccessor http)
{
    public async Task<AuthorizedOperation> BeginAsync(string purpose, AuthorizationScope scope,
        IEnumerable<string> requiredKeys, CancellationToken cancellationToken = default)
    {
        ValidatePurpose(purpose, scope);
        var required = PermissionCatalog.ExpandRequirements(requiredKeys);
        return Create(purpose, scope, required, await permissions.EvaluateCurrentAsync(cancellationToken));
    }

    public async Task<AuthorizedOperation> BeginHttpAsync(string purpose, AuthorizationScope scope,
        IEnumerable<string> requiredKeys, CancellationToken cancellationToken = default)
    {
        ValidatePurpose(purpose, scope);
        var required = PermissionCatalog.ExpandRequirements(requiredKeys);
        var principal = http.HttpContext?.User
            ?? throw new AuthorizationDeniedException(AuthorizationDenial.Unauthenticated);
        return Create(purpose, scope, required, await permissions.EvaluateAsync(principal, cancellationToken));
    }

    private static AuthorizedOperation Create(string purpose, AuthorizationScope scope,
        System.Collections.Immutable.ImmutableArray<string> required, EffectivePermissionSnapshot snapshot)
    {
        foreach (var key in required)
            if (!snapshot.Has(key)) throw new AuthorizationDeniedException(AuthorizationDenial.MissingPermission);
        return new(purpose, scope, required, snapshot);
    }

    private static void ValidatePurpose(string purpose, AuthorizationScope scope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope.Area);
        if (purpose.Length > 200) throw new ArgumentOutOfRangeException(nameof(purpose));
    }
}

public sealed record PermissionRequirement(string PermissionKey) : IAuthorizationRequirement;

public sealed class PermissionAuthorizationHandler(EffectivePermissionService permissions,
    Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Identity.IdentityOptions> identityOptions)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        try
        {
            if (context.Resource is AuthorizedOperation operation)
            {
                var claims = identityOptions.Value.ClaimsIdentity;
                if (context.User.Identity?.IsAuthenticated != true
                    || context.User.FindFirst(claims.UserIdClaimType)?.Value != operation.ActorUserId
                    || context.User.FindFirst(claims.SecurityStampClaimType)?.Value != operation.Snapshot.SecurityStamp)
                { context.Fail(); return; }
                operation.Require(operation.Purpose, operation.Scope, requirement.PermissionKey);
                context.Succeed(requirement);
                return;
            }
            // ASP.NET supplies the principal. Contextual business/privacy checks still belong to the service.
            var snapshot = await permissions.EvaluateAsync(context.User, CancellationToken.None);
            if (PermissionCatalog.ExpandRequirements([requirement.PermissionKey]).All(snapshot.Has))
                context.Succeed(requirement);
            else context.Fail();
        }
        catch (AuthorizationDeniedException) { context.Fail(); }
    }
}

public static class AuthorizationRegistration
{
    public static IServiceCollection AddConfastAuthorization(this IServiceCollection services)
    {
        services.AddSingleton<PermissionCache>();
        services.AddScoped<EffectivePermissionService>();
        services.AddScoped<ApplicationAuthorization>();
        services.AddScoped<RoleAdministrationService>();
        services.AddScoped<InspectorEligibilityService>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddAuthorization(options =>
        {
            foreach (var definition in PermissionCatalog.All)
                options.AddPolicy(PolicyName(definition.Key), policy =>
                    policy.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(definition.Key)));
        });
        return services;
    }
    public static string PolicyName(string key) => "Permission:" + key;
}
