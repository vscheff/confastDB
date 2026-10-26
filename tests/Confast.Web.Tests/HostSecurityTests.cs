using System.Net;
using System.Security.Cryptography;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Inspections;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Confast.Web.Tests;

public sealed class HostSecurityTests
{
    private static WebApplicationBuilder Builder(string? keyPath = null, string application = "ConfastDB")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["DataProtection:KeyRingPath"] = keyPath, ["DataProtection:ApplicationName"] = application });
        return builder;
    }

    [Fact]
    public async Task PersistentKeys_ShareCapabilities_RotateWithoutBreakingInflight_AndIsolatePurposeAndApplication()
    {
        var path = Path.Combine(Path.GetTempPath(), "confast-key-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var first = Builder(path); first.AddConfastHostSecurity();
            await using var hostA = first.Build();
            var second = Builder(path); second.AddConfastHostSecurity();
            await using var hostB = second.Build();
            var providerA = hostA.Services.GetRequiredService<IDataProtectionProvider>();
            var providerB = hostB.Services.GetRequiredService<IDataProtectionProvider>();
            var clock = new RehearsalClock();
            var a = new InspectionPrintRenderTokenService(providerA, clock);
            var b = new InspectionPrintRenderTokenService(providerB, clock);
            using var operation = PrintTestCapability.Operation("InspectionSheets.Export", 7, Confast.Web.Features.Authorization.Permissions.InspectionSheets.Export);
            var token = a.CreateForExport(operation, 7);
            Assert.True(b.IsValid(token, 7));
            Assert.False(b.IsValid(token, 8));
            hostA.Services.GetRequiredService<IKeyManager>().CreateNewKey(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(90));
            Assert.True(b.IsValid(token, 7));
            Assert.Throws<CryptographicException>(() => providerB.CreateProtector("other-purpose").Unprotect(token));
            var third = Builder(path, "DifferentInstallation"); third.AddConfastHostSecurity();
            await using var hostC = third.Build();
            Assert.False(new InspectionPrintRenderTokenService(hostC.Services.GetRequiredService<IDataProtectionProvider>(), clock).IsValid(token, 7));
            Assert.False(new InspectionPrintRenderTokenService(new EphemeralDataProtectionProvider(), clock).IsValid(token, 7));
            clock.Now += TimeSpan.FromMinutes(2);
            Assert.False(b.IsValid(token, 7));
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Theory]
    [InlineData("192.0.2.20", "http")]
    [InlineData("127.0.0.1", "https")]
    public async Task Forwarding_OnlyAcceptsExplicitProxy_AndNeverForwardedHost(string peer, string expectedScheme)
    {
        var builder = Builder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["ReverseProxy:KnownProxies:0"] = "127.0.0.1" });
        builder.WebHost.UseTestServer(); builder.AddConfastHostSecurity();
        await using var app = builder.Build();
        app.Use((context, next) => { context.Connection.RemoteIpAddress = IPAddress.Parse(peer); return next(context); });
        app.UseForwardedHeaders();
        app.MapGet("/", (HttpContext context) => $"{context.Request.Scheme}|{context.Request.Host}");
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Host = "localhost";
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-Host", "attacker.invalid");
        Assert.Equal(expectedScheme + "|localhost", await (await client.SendAsync(request)).Content.ReadAsStringAsync());
    }
}
