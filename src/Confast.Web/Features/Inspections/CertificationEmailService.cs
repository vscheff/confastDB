using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Inspections;

public interface ICertificationEmailService
{
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Applies recipients required for every customer certification email without
/// storing them on an individual customer or plant.
/// </summary>
internal sealed class CertificationEmailService(
    IEmailService emailService,
    IDbContextFactory<AppDbContext> contextFactory,
    ApplicationAuthorization authorization) : ICertificationEmailService
{
    public async Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        using var operation = await authorization.BeginAsync("Certifications.SendEmail", new("CertificationEmail"),
            [Permissions.Certifications.SendEmail], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var actor = await db.Users.AsNoTracking().Where(x => x.Id == operation.ActorUserId)
            .Select(x => new { x.DisplayName, x.UserName, x.Email, x.JobTitle }).SingleAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(actor.Email))
            throw new EmailDeliveryException("Your user account needs an email address before certification packages can be sent.");
        var sender = new EmailSenderIdentity(string.IsNullOrWhiteSpace(actor.DisplayName)
            ? actor.UserName ?? actor.Email : actor.DisplayName, actor.Email, actor.JobTitle);
        message = message with { Sender = sender };
        // Delivery consumes one specific setting under this authorized operation, not the editor API.
        var implicitCc = await db.CertificationEmailSettings.AsNoTracking().Where(x => x.Id == 1)
            .Select(x => x.ImplicitCcAddress).SingleOrDefaultAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(implicitCc))
        {
            await emailService.SendAsync(message, cancellationToken);
            return;
        }

        var ccRecipients = message.Cc
            .Append(implicitCc)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        await emailService.SendAsync(message with { Cc = ccRecipients }, cancellationToken);
    }
}
