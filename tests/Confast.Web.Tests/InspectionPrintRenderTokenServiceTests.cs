using Confast.Web.Features.Inspections;
using Confast.Web.Features.Authorization;
using Microsoft.AspNetCore.DataProtection;

namespace Confast.Web.Tests;

public sealed class InspectionPrintRenderTokenServiceTests
{
    [Fact]
    public void Token_IsValidOnlyForItsInspection()
    {
        var service = new InspectionPrintRenderTokenService(new EphemeralDataProtectionProvider());

        using var operation = PrintTestCapability.Operation("InspectionSheets.Export", 42, Permissions.InspectionSheets.Export);
        var token = service.CreateForExport(operation, 42);

        Assert.True(service.IsValid(token, 42));
        Assert.False(service.IsValid(token, 43));
    }

    [Fact]
    public void TamperedToken_IsRejected()
    {
        var service = new InspectionPrintRenderTokenService(new EphemeralDataProtectionProvider());
        using var operation = PrintTestCapability.Operation("InspectionSheets.Export", 42, Permissions.InspectionSheets.Export);
        var token = service.CreateForExport(operation, 42);

        Assert.False(service.IsValid($"{token}x", 42));
    }

    [Fact]
    public void TemporaryCompletionOption_IsProtectedWithTheInspectionToken()
    {
        var service = new InspectionPrintRenderTokenService(new EphemeralDataProtectionProvider());
        using var operation = PrintTestCapability.Operation("Certifications.BuildPackage", 42, Permissions.Certifications.TemporarilyCompletePackage);
        var token = service.CreateForPackage(operation, 42, 42, true);

        Assert.True(service.TryGetOptions(token, 42, out var options));
        Assert.True(options.TemporarilyCompleteForCertificationPackage);
        Assert.False(service.TryGetOptions(token, 43, out _));
    }

    [Fact]
    public void IssuanceRejectsWrongPurposeScopePermissionAndClosedFrames()
    {
        var service = new InspectionPrintRenderTokenService(new EphemeralDataProtectionProvider());
        using var read = PrintTestCapability.Operation("InspectionSheets.Export", 42, Permissions.Inspections.Read);
        Assert.Throws<AuthorizationDeniedException>(() => service.CreateForExport(read, 42));
        using var export = PrintTestCapability.Operation("InspectionSheets.Export", 42, Permissions.InspectionSheets.Export);
        Assert.Throws<AuthorizationDeniedException>(() => service.CreateForExport(export, 43));
        Assert.Throws<AuthorizationDeniedException>(() => service.CreateForPackage(export, 42, 42, false));
        export.Dispose();
        Assert.Throws<AuthorizationDeniedException>(() => service.CreateForExport(export, 42));
        using var build = PrintTestCapability.Operation("Certifications.BuildPackage", 42, Permissions.Certifications.BuildPackage);
        Assert.Throws<AuthorizationDeniedException>(() => service.CreateForPackage(build, 42, 42, true));
    }

    [Fact]
    public void ExtendedNotesAreBoundToTheCapability()
    {
        var service = new InspectionPrintRenderTokenService(new EphemeralDataProtectionProvider());
        using var operation = PrintTestCapability.Operation("InspectionSheets.Export", 42, Permissions.InspectionSheets.Export);
        var token = service.CreateForExport(operation, 42, true);
        Assert.True(service.TryGetOptions(token, 42, out var options));
        Assert.True(options.SecondPageNotes);
    }
}
