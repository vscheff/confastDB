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

    public string Create(long inspectionId, bool temporarilyCompleteForCertificationPackage = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inspectionId);
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(Lifetime).Ticks;
        return protector.Protect(string.Concat(
            inspectionId.ToString(CultureInfo.InvariantCulture),
            ":",
            expiresAtUtc.ToString(CultureInfo.InvariantCulture),
            ":",
            temporarilyCompleteForCertificationPackage ? "1" : "0"));
    }

    public bool IsValid(string? token, long inspectionId)
        => TryGetOptions(token, inspectionId, out _);

    public bool TryGetOptions(string? token, long inspectionId, out InspectionPrintRenderOptions options)
    {
        options = default;
        if (string.IsNullOrWhiteSpace(token) || inspectionId <= 0)
        {
            return false;
        }

        try
        {
            var values = protector.Unprotect(token).Split(':');
            if (values.Length is (2 or 3)
                && long.TryParse(values[0], CultureInfo.InvariantCulture, out var tokenInspectionId)
                && long.TryParse(values[1], CultureInfo.InvariantCulture, out var expirationTicks)
                && tokenInspectionId == inspectionId
                && DateTimeOffset.UtcNow <= new DateTimeOffset(expirationTicks, TimeSpan.Zero))
            {
                options = new InspectionPrintRenderOptions(values.Length == 3 && values[2] == "1");
                return true;
            }
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return false;
    }
}

public readonly record struct InspectionPrintRenderOptions(
    bool TemporarilyCompleteForCertificationPackage);
