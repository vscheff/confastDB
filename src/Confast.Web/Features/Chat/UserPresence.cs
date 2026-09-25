using Confast.Web.Features.Identity;

namespace Confast.Web.Features.Chat;

public enum UserPresencePreference { Online = 0, Idle = 1, DoNotDisturb = 2, Invisible = 4 }
public enum UserPresenceState { Online, Idle, DoNotDisturb, Offline, Invisible }

public sealed class UserPresenceSession
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
    public DateTime LastHeartbeatAtUtc { get; set; }
    public DateTime LastActivityAtUtc { get; set; }
}

public sealed record UserPresence(string UserId, UserPresenceState State, UserPresencePreference Preference,
    string? StatusEmoji, string? StatusMessage);
