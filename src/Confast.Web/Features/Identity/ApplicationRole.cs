using Microsoft.AspNetCore.Identity;

namespace Confast.Web.Features.Identity;

public enum SystemRoleKind { Ordinary, Baseline, Root }

public sealed class ApplicationRole : IdentityRole
{
    public ApplicationRole() { }
    public ApplicationRole(string name) : base(name) { }
    public string? Description { get; set; }
    public SystemRoleKind SystemKind { get; set; }
    public string? SystemKey { get; set; }
    public bool IsEnabled { get; set; } = true;
}
