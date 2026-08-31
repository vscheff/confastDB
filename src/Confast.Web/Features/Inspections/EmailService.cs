using System.ComponentModel.DataAnnotations;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Confast.Web.Features.Inspections;

public sealed class EmailOptions
{
    [Required]
    public string? Host { get; set; }

    [Range(1, 65535)]
    public int Port { get; set; } = 465;

    public bool UseSsl { get; set; } = true;

    [Required, EmailAddress]
    public string? UserName { get; set; }

    [Required]
    public string? Password { get; set; }

    [Required, EmailAddress]
    public string? DefaultFrom { get; set; }

    [MaxLength(200)]
    public string? DefaultDisplayName { get; set; }

    public EmailSenderMode SenderMode { get; set; } = EmailSenderMode.LoggedInUser;

    public bool UseLoggedInUserAsEnvelopeSender { get; set; } = true;

    [EmailAddress]
    public string? TestRecipient { get; set; }
}

public enum EmailSenderMode { LoggedInUser, ApplicationMailbox }

public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true);
        if (string.IsNullOrWhiteSpace(options.Host)
            || string.IsNullOrWhiteSpace(options.UserName)
            || string.IsNullOrWhiteSpace(options.Password)
            || string.IsNullOrWhiteSpace(options.DefaultFrom))
        {
            results.Add(new ValidationResult("Email host, username, password, and default sender address are required."));
        }
        if (!Enum.IsDefined(options.SenderMode))
        {
            results.Add(new ValidationResult("Email sender mode is invalid."));
        }
        return results.Count == 0 ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(results.Select(x => x.ErrorMessage ?? "Invalid Email configuration."));
    }
}

public sealed record EmailSenderIdentity(string DisplayName, string EmailAddress, string? JobTitle = null);

public sealed record EmailMessage(
    EmailSenderIdentity Sender,
    IReadOnlyCollection<string> To,
    IReadOnlyCollection<string> Cc,
    string Subject,
    string Body,
    string AttachmentName,
    byte[] AttachmentContent);

public sealed class EmailDeliveryException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public interface IEmailService
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

public sealed class SmtpEmailService(
    IOptions<EmailOptions> configuredOptions,
    ILogger<SmtpEmailService> logger) : IEmailService
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        EmailOptions options;
        try
        {
            options = configuredOptions.Value;
        }
        catch (OptionsValidationException exception)
        {
            logger.LogError(exception, "Certification email configuration is invalid.");
            throw new EmailDeliveryException("Email delivery is not configured correctly. Contact an administrator.", exception);
        }
        MimeMessage mimeMessage;
        try
        {
            mimeMessage = CreateMimeMessage(message, options);
        }
        catch (EmailDeliveryException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new EmailDeliveryException("The email message could not be prepared.", exception);
        }

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(options.Host!, options.Port,
                options.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls,
                cancellationToken);
            await client.AuthenticateAsync(options.UserName!, options.Password!, cancellationToken);
            var envelopeSender = options.UseLoggedInUserAsEnvelopeSender
                && options.SenderMode == EmailSenderMode.LoggedInUser
                ? message.Sender.EmailAddress
                : options.DefaultFrom;
            var envelopeRecipients = mimeMessage.To.Mailboxes.Concat(mimeMessage.Cc.Mailboxes).ToArray();
            await client.SendAsync(
                mimeMessage,
                ToMailbox(envelopeSender, null, "The SMTP envelope sender address is invalid."),
                envelopeRecipients,
                cancellationToken,
                progress: null);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception exception) when (exception is SmtpCommandException
            or SmtpProtocolException or AuthenticationException or IOException)
        {
            logger.LogError(exception, "SMTP delivery failed for certification email to {RecipientCount} recipients.", message.To.Count);
            throw new EmailDeliveryException("The email could not be sent. Verify the recipient and try again; contact an administrator if the problem continues.", exception);
        }
    }

    public static MimeMessage CreateMimeMessage(EmailMessage message, EmailOptions options)
    {
        if (message.To.Count == 0)
        {
            throw new EmailDeliveryException("At least one To recipient is required.");
        }
        if (message.AttachmentContent.Length == 0 || string.IsNullOrWhiteSpace(message.AttachmentName))
        {
            throw new EmailDeliveryException("The certification package attachment is missing.");
        }

        var sender = ToMailbox(message.Sender.EmailAddress, message.Sender.DisplayName, "The logged-in user's email address is invalid.");
        var configuredSender = ToMailbox(options.DefaultFrom, options.DefaultDisplayName, "The application sender address is invalid.");
        var mimeMessage = new MimeMessage();
        mimeMessage.From.Add(options.SenderMode == EmailSenderMode.LoggedInUser ? sender : configuredSender);
        mimeMessage.ReplyTo.Add(sender);
        AddRecipients(mimeMessage.To, message.To, "To");
        AddRecipients(mimeMessage.Cc, message.Cc, "Cc");
        mimeMessage.Subject = message.Subject;
        var body = new BodyBuilder { HtmlBody = message.Body };
        body.Attachments.Add(message.AttachmentName, message.AttachmentContent, ContentType.Parse("application/pdf"));
        mimeMessage.Body = body.ToMessageBody();
        return mimeMessage;
    }

    private static MailboxAddress ToMailbox(string? emailAddress, string? displayName, string errorMessage)
    {
        if (string.IsNullOrWhiteSpace(emailAddress) || !MailboxAddress.TryParse(emailAddress, out var address))
        {
            throw new EmailDeliveryException(errorMessage);
        }
        return new MailboxAddress(displayName?.Trim() ?? string.Empty, address.Address);
    }

    private static void AddRecipients(InternetAddressList target, IEnumerable<string> recipients, string fieldName)
    {
        foreach (var recipient in recipients)
        {
            if (!MailboxAddress.TryParse(recipient, out var address))
            {
                throw new EmailDeliveryException($"The {fieldName} recipient address '{recipient}' is invalid.");
            }
            target.Add(address);
        }
    }
}
