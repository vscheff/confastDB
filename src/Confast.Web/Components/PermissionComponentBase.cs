using Confast.Web.Features.Authorization;
using Microsoft.AspNetCore.Components;

namespace Confast.Web.Components;

// Advisory display state only. Every service call starts its own trusted operation.
public abstract class PermissionComponentBase : ComponentBase
{
    [Inject] protected EffectivePermissionService PermissionEvaluator { get; set; } = null!;
    private EffectivePermissionSnapshot? displayPermissions;
    [CascadingParameter(Name = "AuthorizationRevision")] public long AuthorizationRevision { get; set; }
    private Dictionary<string, object?> previousParameters = [];
    private bool receivedParameters;
    protected virtual Task OnPermissionsChangedAsync() => Task.CompletedTask;

    protected bool Can(string key) => displayPermissions is not null
        && PermissionCatalog.ExpandRequirements([key]).All(displayPermissions.Has);

    protected async Task RefreshPermissionsAsync()
    {
        try { displayPermissions = await PermissionEvaluator.EvaluateCurrentAsync(); }
        catch (AuthorizationDeniedException) { displayPermissions = null; }
    }

    public override async Task SetParametersAsync(ParameterView parameters)
    {
        // Consume renderer-owned parameters synchronously, retaining cascading/query provenance.
        var values = parameters.ToDictionary().ToDictionary(x => x.Key, x => x.Value);
        var oldRevision = AuthorizationRevision;
        var advisoryOnly = receivedParameters && values.Where(x => x.Key != nameof(AuthorizationRevision))
            .All(x => previousParameters.TryGetValue(x.Key, out var previous) && Equals(previous, x.Value))
            && previousParameters.Keys.Where(x => x != nameof(AuthorizationRevision)).All(values.ContainsKey);
        parameters.SetParameterProperties(this);
        previousParameters = values;
        receivedParameters = true;
        var previousVersion = displayPermissions?.Version;
        await RefreshPermissionsAsync();
        if (advisoryOnly)
        {
            // A cascade and its parent render may deliver the same parameters twice. Neither may replace an open model.
            if (oldRevision != AuthorizationRevision || previousVersion != displayPermissions?.Version)
                await OnPermissionsChangedAsync();
            StateHasChanged();
            return;
        }
        await base.SetParametersAsync(ParameterView.Empty);
    }
}
