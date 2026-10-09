namespace Confast.Web.Features.Identity;

public sealed class PasswordResetDelegation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IssuerUserId { get; set; } = "";
    public string TargetUserId { get; set; } = "";
    public string IssuerSecurityStamp { get; set; } = "";
    public Guid InstallationGeneration { get; set; }
    public string TokenDigest { get; set; } = "";
    public DateTime IssuedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? ConsumedAtUtc { get; set; }
}
