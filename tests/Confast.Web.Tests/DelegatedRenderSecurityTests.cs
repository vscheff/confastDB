using Confast.Web.Features.Inspections;
using Microsoft.AspNetCore.DataProtection;

namespace Confast.Web.Tests;

public sealed class DelegatedRenderSecurityTests
{
    [Fact]
    public void ExpiryIsExact_PurposeIsProtected_AndInFlightCapabilityDoesNotBorrowHumanSession()
    {
        var clock = new TestClock(); var provider = new EphemeralDataProtectionProvider();
        var service = new InspectionPrintRenderTokenService(provider, clock);
        using var operation = PrintTestCapability.Operation("InspectionSheets.Export", 42, Confast.Web.Features.Authorization.Permissions.InspectionSheets.Export);
        var token = service.CreateForExport(operation, 42);
        operation.Dispose(); // Frame/session lifetime does not revoke an issued two-minute capability.
        clock.Now += TimeSpan.FromSeconds(119); Assert.True(service.IsValid(token, 42));
        clock.Now += TimeSpan.FromSeconds(1); Assert.False(service.IsValid(token, 42));
        var wrongPurpose = provider.CreateProtector("Different.Purpose").Protect("42:999999999999999999:0:0");
        Assert.False(service.IsValid(wrongPurpose, 42));
    }

    [Theory]
    [InlineData("https://renderer.test/inspections/42/print?renderToken=secret", "document", "GET", true)]
    [InlineData("https://renderer.test/inspections/43/print?renderToken=secret", "document", "GET", false)]
    [InlineData("https://other.test/inspections/42/print?renderToken=secret", "document", "GET", false)]
    [InlineData("https://renderer.test/inspections/42/print?renderToken=secret&notesPage=second", "document", "GET", false)]
    [InlineData("https://renderer.test/inspections/42/print/download", "document", "GET", false)]
    [InlineData("https://renderer.test/chat/attachments/1", "image", "GET", false)]
    [InlineData("https://renderer.test/admin/users", "fetch", "GET", false)]
    [InlineData("https://renderer.test/_blazor/negotiate", "fetch", "POST", false)]
    [InlineData("https://renderer.test/images/conformance-logo.png", "image", "GET", true)]
    [InlineData("https://renderer.test/app.abc.css", "stylesheet", "GET", true)]
    [InlineData("https://other.test/font.woff2", "font", "GET", false)]
    [InlineData("https://renderer.test/inspections/42/print?renderToken=secret", "document", "POST", false)]
    public void RendererNetworkPolicyAllowsOnlyExactDocumentAndLocalStaticAssets(string url, string type, string method, bool allowed)
        => Assert.Equal(allowed, InspectionRenderRequestPolicy.IsAllowed(new("https://renderer.test/inspections/42/print?renderToken=secret"), new(url), type, method));

    private sealed class TestClock : TimeProvider
    {
        internal DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
