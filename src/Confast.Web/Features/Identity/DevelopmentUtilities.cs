using System.Net;
using Confast.Web.Data;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Inspections;
using Confast.Web.Time;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Identity;

// Registered only in Development. No production email options, credentials or transport.
internal sealed class DevelopmentUtilities(IHostEnvironment environment, ApplicationAuthorization authorization,
    IDbContextFactory<AppDbContext> factory, DevelopmentDateOverrideTimeProvider clock)
{
    internal async Task SetDateAsync(DateOnly? date, CancellationToken ct = default)
    {
        RequireDevelopment();
        using var operation = await authorization.BeginAsync("Development.BusinessDate", new("Development"),
            [Permissions.Authorization.ManageSecurity], ct);
        if (date is DateOnly value) clock.SetDate(value); else clock.UseSystemDate();
    }

    internal async Task<object> PrepareEmailAsync(HttpContext context, CancellationToken ct)
    {
        RequireDevelopment();
        if (context.Connection.RemoteIpAddress is not { } ip || !IPAddress.IsLoopback(ip))
            throw new AuthorizationDeniedException(AuthorizationDenial.WrongOperation);
        var operation = BusinessHttpAuthorization.Require(context, "Development.EmailDiagnostic", new("Development"), Permissions.Certifications.SendEmail);
        await using var db = await factory.CreateDbContextAsync(ct);
        var sender = await db.Users.Where(x => x.Id == operation.ActorUserId)
            .Select(x => new EmailSenderIdentity(x.DisplayName, x.Email ?? "")).SingleAsync(ct);
        // Exercise MIME/sender validation without reading Email:* or opening a connection.
        using var mime = SmtpEmailService.CreateMimeMessage(new(sender, ["fixture@browser-test.invalid"], [],
            "Confast Development email diagnostic", "Development fixture message.", "fixture.pdf", [37, 80, 68, 70]),
            new EmailOptions { DefaultFrom = "application@browser-test.invalid", SenderMode = EmailSenderMode.LoggedInUser });
        using var capture = new MemoryStream();
        await mime.WriteToAsync(capture, ct);
        return new { message = "Email prepared using an in-memory test transport. No SMTP connection was made.",
            recipient = "fixture@browser-test.invalid", bytes = capture.Length };
    }

    private void RequireDevelopment()
    {
        if (!environment.IsDevelopment()) throw new AuthorizationDeniedException(AuthorizationDenial.WrongOperation);
    }
}
