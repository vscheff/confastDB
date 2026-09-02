using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

using Confast.Web.Features.Gages;

namespace Confast.Web.Features.Identity;

public sealed class ApplicationUser : IdentityUser
{
    [MaxLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    [MaxLength(200)]
    public string? JobTitle { get; set; }

    public long? CaliperId { get; set; }

    public Gage? Caliper { get; set; }

    public bool IsActive { get; set; } = true;
}
