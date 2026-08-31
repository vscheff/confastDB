namespace Confast.Web.Features.Inspections;

public interface ICertificationEmailService
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies recipients required for every customer certification email without
/// storing them on an individual customer or plant.
/// </summary>
public sealed class CertificationEmailService(
    IEmailService emailService,
    ICertificationEmailTemplateService templateService) : ICertificationEmailService
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        var settings = await templateService.GetSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.ImplicitCcAddress))
        {
            await emailService.SendAsync(message, cancellationToken);
            return;
        }

        var ccRecipients = message.Cc
            .Append(settings.ImplicitCcAddress)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await emailService.SendAsync(message with { Cc = ccRecipients }, cancellationToken);
    }
}
