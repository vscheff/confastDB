using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Confast.Web.Features.Maintenance;

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
        && TryGetCurrentOptions(request, renderTokenService, inspectionId, out _);

    internal static bool CanAuthorizePrint(HttpContext? context, ClaimsPrincipal principal)
    {
        if (context is null || principal.Identity?.AuthenticationType != SchemeName
            || context.User.Identity?.AuthenticationType != SchemeName
            || principal.FindFirstValue(ClaimTypes.NameIdentifier) != "inspection-print-renderer"
            || !TryGetInspectionId(context.Request.Path, out var id)) return false;
        var tokens = context.RequestServices.GetRequiredService<InspectionPrintRenderTokenService>();
        return TryGetCurrentOptions(context.Request, tokens, id, out var options)
            && options.SecondPageNotes == string.Equals(context.Request.Query["notesPage"], "second", StringComparison.OrdinalIgnoreCase);
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!TryGetInspectionId(Request.Path, out var inspectionId)
            || !TryGetCurrentOptions(Request, renderTokenService, inspectionId, out _))
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

    private static bool TryGetCurrentOptions(HttpRequest request, InspectionPrintRenderTokenService tokens,
        long inspectionId, out InspectionPrintRenderOptions options)
    {
        options = default;
        // Inspection IDs can be reused after Factory Reset. The HTTP maintenance
        // lease pins the installation generation until the print response finishes.
        return request.HttpContext.RequestServices.GetService<MaintenanceGate>()?.CurrentGeneration is { } generation
            && tokens.TryGetOptions(request.Query["renderToken"], inspectionId, out options, generation);
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
