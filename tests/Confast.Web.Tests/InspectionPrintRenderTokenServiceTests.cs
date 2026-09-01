using Confast.Web.Features.Inspections;
using Microsoft.AspNetCore.DataProtection;

namespace Confast.Web.Tests;

public sealed class InspectionPrintRenderTokenServiceTests
{
    [Fact]
    public void Token_IsValidOnlyForItsInspection()
    {
        var service = new InspectionPrintRenderTokenService(new EphemeralDataProtectionProvider());

        var token = service.Create(42);

        Assert.True(service.IsValid(token, 42));
        Assert.False(service.IsValid(token, 43));
    }

    [Fact]
    public void TamperedToken_IsRejected()
    {
        var service = new InspectionPrintRenderTokenService(new EphemeralDataProtectionProvider());
        var token = service.Create(42);

        Assert.False(service.IsValid($"{token}x", 42));
    }

    [Fact]
    public void TemporaryCompletionOption_IsProtectedWithTheInspectionToken()
    {
        var service = new InspectionPrintRenderTokenService(new EphemeralDataProtectionProvider());
        var token = service.Create(42, temporarilyCompleteForCertificationPackage: true);

        Assert.True(service.TryGetOptions(token, 42, out var options));
        Assert.True(options.TemporarilyCompleteForCertificationPackage);
        Assert.False(service.TryGetOptions(token, 43, out _));
    }
}
