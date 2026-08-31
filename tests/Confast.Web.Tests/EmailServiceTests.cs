using Confast.Web.Features.Inspections;
using MimeKit;

namespace Confast.Web.Tests;

public sealed class EmailServiceTests
{
    [Fact]
    public void CreateMimeMessage_UsesLoggedInUserForFromAndReplyTo()
    {
        var message = SmtpEmailService.CreateMimeMessage(CreateMessage(), CreateOptions());

        var from = Assert.IsType<MailboxAddress>(Assert.Single(message.From));
        var replyTo = Assert.IsType<MailboxAddress>(Assert.Single(message.ReplyTo));
        Assert.Equal("Quality Person", from.Name);
        Assert.Equal("quality.person@example.com", from.Address);
        Assert.Equal("quality.person@example.com", replyTo.Address);
        Assert.Equal(["customer@example.com"], message.To.Mailboxes.Select(x => x.Address));
        Assert.Equal(["quality-contact@example.com"], message.Cc.Mailboxes.Select(x => x.Address));
    }

    [Fact]
    public void CreateMimeMessage_UsesApplicationMailboxWhenFallbackIsConfigured()
    {
        var options = CreateOptions();
        options.SenderMode = EmailSenderMode.ApplicationMailbox;

        var message = SmtpEmailService.CreateMimeMessage(CreateMessage(), options);

        var from = Assert.IsType<MailboxAddress>(Assert.Single(message.From));
        var replyTo = Assert.IsType<MailboxAddress>(Assert.Single(message.ReplyTo));
        Assert.Equal("Confast Certifications", from.Name);
        Assert.Equal("certifications@example.com", from.Address);
        Assert.Equal("quality.person@example.com", replyTo.Address);
    }

    [Fact]
    public void CreateMimeMessage_PreservesAttachmentNameAndContent()
    {
        var message = SmtpEmailService.CreateMimeMessage(CreateMessage(), CreateOptions());
        var attachment = Assert.IsType<MimePart>(Assert.Single(message.Attachments));
        using var content = new MemoryStream();
        Assert.NotNull(attachment.Content);
        attachment.Content.DecodeTo(content);

        Assert.Equal("package.pdf", attachment.FileName);
        Assert.Equal([1, 2, 3], content.ToArray());
    }

    [Fact]
    public void CreateMimeMessage_PreservesAnHtmlBody()
    {
        var message = SmtpEmailService.CreateMimeMessage(CreateMessage() with { Body = "<p><strong>Edited</strong> body</p>" }, CreateOptions());

        Assert.Equal("<p><strong>Edited</strong> body</p>", message.HtmlBody);
    }

    [Fact]
    public void CreateMimeMessage_RejectsMissingUserEmail()
    {
        var email = CreateMessage() with { Sender = new EmailSenderIdentity("Quality Person", "") };

        var exception = Assert.Throws<EmailDeliveryException>(() => SmtpEmailService.CreateMimeMessage(email, CreateOptions()));

        Assert.Contains("logged-in user's email", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CreateMimeMessage_RejectsInvalidCustomerRecipient()
    {
        var email = CreateMessage() with { To = ["not an address"] };

        var exception = Assert.Throws<EmailDeliveryException>(() => SmtpEmailService.CreateMimeMessage(email, CreateOptions()));

        Assert.Contains("To recipient", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmailOptionsValidator_RejectsIncompleteSmtpConfiguration()
    {
        var result = new EmailOptionsValidator().Validate(null, new EmailOptions { Host = "secure.emailsrvr.com", Port = 465 });

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task CertificationEmailService_AddsConfiguredAddressAsAnImplicitCc()
    {
        var emailService = new CapturingEmailService();
        var certificationEmailService = new CertificationEmailService(emailService, new StubTemplateService("quality@conformancefasteners.com"));

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
        var certificationEmailService = new CertificationEmailService(emailService, new StubTemplateService("quality@conformancefasteners.com"));

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
        var certificationEmailService = new CertificationEmailService(emailService, new StubTemplateService(null));

        await certificationEmailService.SendAsync(CreateMessage());

        var sentMessage = Assert.IsType<EmailMessage>(emailService.Message);
        Assert.Equal(["quality-contact@example.com"], sentMessage.Cc);
    }

    private static EmailMessage CreateMessage() => new(
        new EmailSenderIdentity("Quality Person", "quality.person@example.com"),
        ["customer@example.com"],
        ["quality-contact@example.com"],
        "Certification package",
        "Attached is the package.",
        "package.pdf",
        [1, 2, 3]);

    private static EmailOptions CreateOptions() => new()
    {
        Host = "secure.emailsrvr.com",
        Port = 465,
        UseSsl = true,
        UserName = "certifications@example.com",
        Password = "test-secret",
        DefaultFrom = "certifications@example.com",
        DefaultDisplayName = "Confast Certifications"
    };

    private sealed class CapturingEmailService : IEmailService
    {
        public EmailMessage? Message { get; private set; }

        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            Message = message;
            return Task.CompletedTask;
        }
    }

    private sealed class StubTemplateService(string? implicitCcAddress) : ICertificationEmailTemplateService
    {
        public Task<IReadOnlyList<CertificationEmailTemplateEditModel>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CertificationEmailTemplateEditModel>>([]);

        public Task SaveAsync(CertificationEmailTemplateEditModel model, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<CertificationEmailSettingsEditModel> GetSettingsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new CertificationEmailSettingsEditModel(implicitCcAddress));

        public Task SaveSettingsAsync(CertificationEmailSettingsEditModel model, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
