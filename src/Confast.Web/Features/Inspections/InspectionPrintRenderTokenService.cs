using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Confast.Web.Features.Inspections;

/// <summary>
/// Grants the headless PDF renderer short-lived access to one printable inspection.
/// It is deliberately separate from a user's authentication cookie because the
/// renderer runs in a fresh browser context.
/// </summary>
public sealed class InspectionPrintRenderTokenService(IDataProtectionProvider dataProtectionProvider)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector(
        "Confast.Web.Inspections.InspectionPrintRenderToken.v1");

    public string Create(long inspectionId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inspectionId);
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(Lifetime).Ticks;
        return protector.Protect(string.Concat(
            inspectionId.ToString(CultureInfo.InvariantCulture),
            ":",
            expiresAtUtc.ToString(CultureInfo.InvariantCulture)));
    }

    public bool IsValid(string? token, long inspectionId)
    {
        if (string.IsNullOrWhiteSpace(token) || inspectionId <= 0)
        {
            return false;
        }

        try
        {
            var values = protector.Unprotect(token).Split(':');
            return values.Length == 2
                && long.TryParse(values[0], CultureInfo.InvariantCulture, out var tokenInspectionId)
                && long.TryParse(values[1], CultureInfo.InvariantCulture, out var expirationTicks)
                && tokenInspectionId == inspectionId
                && DateTimeOffset.UtcNow <= new DateTimeOffset(expirationTicks, TimeSpan.Zero);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
