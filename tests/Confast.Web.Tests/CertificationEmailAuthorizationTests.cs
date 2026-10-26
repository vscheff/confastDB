using Confast.Web.Features.Inspections;
using Confast.Web.Features.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CertificationEmailAuthorizationTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly AuthorizationTestSession session = new(database);
    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        await session.SignInAsync(await session.ProvisionRootAsync());
    }
    public Task DisposeAsync() { session.Dispose(); return Task.CompletedTask; }
    private async Task SetImplicitCc(string? address)
    {
        await using var db = database.CreateDbContext();
        await db.CertificationEmailSettings.Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.ImplicitCcAddress, address));
    }
    [Fact]
    public async Task CertificationEmailService_AddsConfiguredAddressAsAnImplicitCc()
    {
        var emailService = new CapturingEmailService();
        await SetImplicitCc("quality@conformancefasteners.com");
        var certificationEmailService = new CertificationEmailService(emailService, database, session.Guard);

        await certificationEmailService.SendAsync(CreateMessage());

        var sentMessage = Assert.IsType<EmailMessage>(emailService.Message);
        Assert.Equal(
            ["quality-contact@example.com", "quality@conformancefasteners.com"],
            sentMessage.Cc);
    }

    [Fact]
    public async Task CertificationEmailService_DoesNotDuplicateTheImplicitCc()
    {
        var emailService = new CapturingEmailService();
        await SetImplicitCc("quality@conformancefasteners.com");
        var certificationEmailService = new CertificationEmailService(emailService, database, session.Guard);

        await certificationEmailService.SendAsync(CreateMessage() with
        {
            Cc = ["QUALITY@CONFORMANCEFASTENERS.COM"]
        });

        var sentMessage = Assert.IsType<EmailMessage>(emailService.Message);
        Assert.Equal(["QUALITY@CONFORMANCEFASTENERS.COM"], sentMessage.Cc);
    }

    [Fact]
    public async Task CertificationEmailService_DoesNotAddAnImplicitCcWhenTheSettingIsBlank()
    {
        var emailService = new CapturingEmailService();
        await SetImplicitCc(null);
        var certificationEmailService = new CertificationEmailService(emailService, database, session.Guard);

        await certificationEmailService.SendAsync(CreateMessage());

        var sentMessage = Assert.IsType<EmailMessage>(emailService.Message);
        Assert.Equal(["quality-contact@example.com"], sentMessage.Cc);
    }

    private static EmailMessage CreateMessage() => new(new("Forged sender", "forged@example.test"),
        ["customer@example.test"], ["quality-contact@example.com"], "Certifications", "Attached", "package.pdf", [1,2,3]);
    private sealed class CapturingEmailService : IEmailService
    {
        public EmailMessage? Message { get; private set; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Message = message;
            return Task.CompletedTask;
        }
    }

}
