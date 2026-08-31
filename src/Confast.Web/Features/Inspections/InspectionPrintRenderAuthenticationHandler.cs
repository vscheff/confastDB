using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Confast.Web.Features.Inspections;

public sealed class InspectionPrintRenderAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    InspectionPrintRenderTokenService renderTokenService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "InspectionPrintRender";

    public static bool CanAuthenticate(HttpRequest request, InspectionPrintRenderTokenService renderTokenService) =>
        TryGetInspectionId(request.Path, out var inspectionId)
        && renderTokenService.IsValid(request.Query["renderToken"], inspectionId);

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryGetInspectionId(Request.Path, out var inspectionId)
            || !renderTokenService.IsValid(Request.Query["renderToken"], inspectionId))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "inspection-print-renderer"),
            new Claim(ClaimTypes.Name, "Inspection print renderer")
        ], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    private static bool TryGetInspectionId(PathString path, out long inspectionId)
    {
        inspectionId = 0;
        var segments = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments is ["inspections", var id, "print"]
            && long.TryParse(id, CultureInfo.InvariantCulture, out inspectionId)
            && inspectionId > 0;
    }
}
