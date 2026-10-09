namespace Confast.Web.Features.Authorization;

public sealed class PermissionManifest
{
    public string Key { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Category { get; set; } = "";
    public PermissionKind Kind { get; set; }
    public PermissionAuthority Authority { get; set; }
    public bool AllowedInBaseline { get; set; }
    public string CatalogVersion { get; set; } = "";
}

public sealed class RolePermission
{
    public string RoleId { get; set; } = "";
    public string PermissionKey { get; set; } = "";
}

public sealed class RoleInheritance
{
    public string ChildRoleId { get; set; } = "";
    public string ParentRoleId { get; set; } = "";
}

public enum AuthorizationReadiness { PendingRoot, Ready }

public sealed class AuthorizationState
{
    public int Id { get; set; } = 1;
    public string? RootUserId { get; set; }
    public string BaselineRoleId { get; set; } = Identity.AppRoles.ReadOnlyId;
    public string RootRoleId { get; set; } = Identity.AppRoles.RootId;
    public long GlobalEpoch { get; set; }
    public Guid InstallationGeneration { get; set; }
    public string CatalogVersion { get; set; } = "";
    public AuthorizationReadiness Readiness { get; set; }
}

public enum AuthorizationActorKind { Human, InstallationOperator }
public enum AuthorizationChangeKind { Role, Assignment, Grant, Inheritance, Provisioning, Account, PasswordDelegation }

public sealed class AuthorizationChangeHistory
{
    public long Id { get; set; }
    public Guid OperationId { get; set; }
    public AuthorizationActorKind ActorKind { get; set; }
    // ID snapshots intentionally survive deletion of ordinary actors/targets.
    public string? ActorUserId { get; set; }
    public string Purpose { get; set; } = "";
    public string Reason { get; set; } = "";
    public DateTime OccurredAtUtc { get; set; }
    public long PreviousEpoch { get; set; }
    public long ResultingEpoch { get; set; }
    public Guid InstallationGeneration { get; set; }
    public string? DirectAnchorRoleId { get; set; }
    public List<AuthorizationChangeDetail> Details { get; set; } = [];
}

public sealed class AuthorizationChangeDetail
{
    public long Id { get; set; }
    public long HistoryId { get; set; }
    public AuthorizationChangeKind Kind { get; set; }
    public string? RoleId { get; set; }
    public string? UserId { get; set; }
    public string? PermissionKey { get; set; }
    public string? ParentRoleId { get; set; }
    public bool? WasPresent { get; set; }
    public bool? IsPresent { get; set; }
    public string? PreviousName { get; set; }
    public string? ResultingName { get; set; }
    public bool? WasEnabled { get; set; }
    public bool? IsEnabled { get; set; }
    public string? FieldName { get; set; }
    public string? PreviousValue { get; set; }
    public string? ResultingValue { get; set; }
}
