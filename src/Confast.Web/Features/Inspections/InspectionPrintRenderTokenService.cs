using System.Globalization;
using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Confast.Web.Features.Authorization;

namespace Confast.Web.Features.Inspections;

/// <summary>
/// Grants the headless PDF renderer short-lived access to one printable inspection.
/// It is deliberately separate from a user's authentication cookie because the
/// renderer runs in a fresh browser context.
/// </summary>
public sealed class InspectionPrintRenderTokenService(IDataProtectionProvider dataProtectionProvider, TimeProvider? clock = null)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector(
        "Confast.Web.Inspections.InspectionPrintRenderToken.v2");

    internal string CreateForExport(AuthorizedOperation operation, long inspectionId, bool secondPageNotes = false)
    {
        operation.Require("InspectionSheets.Export", new("Inspection", inspectionId.ToString()), Permissions.InspectionSheets.Export);
        return Protect(inspectionId, false, secondPageNotes);
    }

    internal string CreateForPackage(AuthorizedOperation operation, long activeInspectionId, long inspectionId,
        bool temporarilyCompleteForCertificationPackage)
    {
        operation.Require("Certifications.BuildPackage", new("Inspection", activeInspectionId.ToString()), Permissions.Certifications.BuildPackage);
        if (temporarilyCompleteForCertificationPackage)
            operation.Require(operation.Purpose, operation.Scope, Permissions.Certifications.TemporarilyCompletePackage);
        return Protect(inspectionId, temporarilyCompleteForCertificationPackage, false);
    }

    private string Protect(long inspectionId, bool temporarilyCompleteForCertificationPackage, bool secondPageNotes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inspectionId);
        var expiresAtUtc = (clock ?? TimeProvider.System).GetUtcNow().Add(Lifetime).Ticks;
        return protector.Protect(string.Concat(
            inspectionId.ToString(CultureInfo.InvariantCulture),
            ":",
            expiresAtUtc.ToString(CultureInfo.InvariantCulture),
            ":",
            temporarilyCompleteForCertificationPackage ? "1" : "0", ":", secondPageNotes ? "1" : "0"));
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
            if (values.Length == 4
                && values[2] is "0" or "1" && values[3] is "0" or "1"
                && long.TryParse(values[0], CultureInfo.InvariantCulture, out var tokenInspectionId)
                && long.TryParse(values[1], CultureInfo.InvariantCulture, out var expirationTicks)
                && tokenInspectionId == inspectionId
                && (clock ?? TimeProvider.System).GetUtcNow() < new DateTimeOffset(expirationTicks, TimeSpan.Zero))
            {
                options = new InspectionPrintRenderOptions(values[2] == "1", values[3] == "1");
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
    bool TemporarilyCompleteForCertificationPackage, bool SecondPageNotes = false);
