using Confast.Web.Features.Authorization;
using Confast.Web.Features.Backups;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Confast.Web.Features.Maintenance;

internal sealed class FactoryResetService(IOptionsMonitor<DatabaseResetOptions> options, EffectivePermissionService permissions,
    IHttpContextAccessor http, UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn,
    BackupStorage backups, FactoryResetStore store)
{
    internal const string Policy = "FactoryReset";
    internal const string Route = "/admin/factory-reset";
    internal const string ExecuteRoute = Route + "/execute";
    internal const string Phrase = "RESET DATABASE";

    internal async Task<Guid> RequestAsync(string? password, string? phrase, bool acknowledged, bool confirmed, CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled)
            throw new AuthorizationDeniedException(AuthorizationDenial.MissingPermission);
        var principal = http.HttpContext?.User ?? throw new AuthorizationDeniedException(AuthorizationDenial.Unauthenticated);
        var actor = await permissions.EvaluateAsync(principal, ct);
        if (!actor.IsRoot) throw new AuthorizationDeniedException(AuthorizationDenial.MissingPermission);
        if (!acknowledged || !confirmed || !string.Equals(phrase, Phrase, StringComparison.Ordinal) ||
            password is not { Length: > 0 and <= 4096 })
            throw new FactoryResetException("Confirmation", "Enter your current password and complete both confirmations.");
        if (!backups.Configured || !store.Configured)
            throw new FactoryResetException("Configuration", "Factory Reset requires the deployment's backup and reset database configuration.");

        var user = await users.FindByIdAsync(actor.ActorUserId);
        if (user is null || !user.IsActive || !(await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true)).Succeeded)
            throw new FactoryResetException("Password", "Current password authentication failed.");

        // A changed password, Root designation, catalog or epoch cannot authorize a
        // queued operation. No password or credential stamp is stored in the journal.
        actor = await permissions.EvaluateAsync(principal, ct);
        if (!options.CurrentValue.Enabled || !actor.IsRoot)
            throw new AuthorizationDeniedException(AuthorizationDenial.MissingPermission);
        try { return await store.RequestAsync(actor, backups.DeploymentId, ct); }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.LockNotAvailable)
        {
            throw new FactoryResetException("Busy", "Another Factory Reset is already in progress.");
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.NoDataFound)
        {
            throw new AuthorizationDeniedException(AuthorizationDenial.InvalidState);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.ObjectNotInPrerequisiteState)
        {
            throw new FactoryResetException("BackupDestination", "Set up a backup destination before starting a Factory Reset.");
        }
    }
}

internal sealed class FactoryResetRequirement : IAuthorizationRequirement;

internal sealed class FactoryResetAuthorizationHandler(IOptionsMonitor<DatabaseResetOptions> options,
    EffectivePermissionService permissions) : AuthorizationHandler<FactoryResetRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, FactoryResetRequirement requirement)
    {
        if (!options.CurrentValue.Enabled || context.User.Identity?.IsAuthenticated != true) return;
        try
        {
            if ((await permissions.EvaluateAsync(context.User, CancellationToken.None)).IsRoot)
                context.Succeed(requirement);
        }
        catch (AuthorizationDeniedException) { }
    }
}
