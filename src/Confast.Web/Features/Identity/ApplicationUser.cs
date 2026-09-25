using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

using Confast.Web.Features.Gages;
using Confast.Web.Features.Chat;

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

    [MaxLength(32)]
    public string? LastReactionEmoji { get; set; }

    public UserPresencePreference PresencePreference { get; set; } = UserPresencePreference.Online;

    [MaxLength(32)]
    public string? StatusEmoji { get; set; }

    [MaxLength(140)]
    public string? StatusMessage { get; set; }
}
