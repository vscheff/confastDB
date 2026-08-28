using System.Net;
using System.Net.Mail;

namespace Confast.Web.Features.Inspections;

public sealed class EmailOptions
{
    public string? Host { get; set; }
    public int Port { get; set; } = 25;
    public string? UserName { get; set; }
    public string? Password { get; set; }
    public string? From { get; set; }
}

public sealed record EmailMessage(
    IReadOnlyCollection<string> To,
    IReadOnlyCollection<string> Cc,
    string Subject,
    string Body,
    string AttachmentName,
    byte[] AttachmentContent);

public interface IEmailService
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

public sealed class SmtpEmailService(EmailOptions options) : IEmailService
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        if (message.To.Count == 0)
        {
            throw new InvalidOperationException("At least one To recipient is required.");
        }
        if (string.IsNullOrWhiteSpace(options.Host) || string.IsNullOrWhiteSpace(options.From))
        {
            throw new InvalidOperationException("Email delivery is not configured.");
        }

        using var mail = new MailMessage { From = new MailAddress(options.From), Subject = message.Subject, Body = message.Body };
        foreach (var address in message.To) mail.To.Add(address);
        foreach (var address in message.Cc) mail.CC.Add(address);
        mail.Attachments.Add(new Attachment(new MemoryStream(message.AttachmentContent, writable: false), message.AttachmentName, "application/pdf"));
        using var client = new SmtpClient(options.Host, options.Port);
        if (!string.IsNullOrWhiteSpace(options.UserName)) client.Credentials = new NetworkCredential(options.UserName, options.Password);
        await client.SendMailAsync(mail, cancellationToken);
    }
}
