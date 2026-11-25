using Confast.Web.Features.Authorization;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace Confast.Web.Features.Maintenance;

internal static class FactoryResetWeb
{
    internal static void AddConfastMaintenance(this IServiceCollection services)
    {
        services.AddOptions<DatabaseResetOptions>().BindConfiguration("DatabaseReset");
        services.AddSingleton<MaintenanceGate>();
        services.AddSingleton<MaintenanceConnections>();
        services.AddSingleton<IAntiforgeryAdditionalDataProvider, InstallationAntiforgeryData>();
        services.AddScoped<CircuitHandler, MaintenanceCircuitHandler>();
        services.AddScoped<FactoryResetService>();
        services.AddScoped<IAuthorizationHandler, FactoryResetAuthorizationHandler>();
        services.AddAuthorization(options => options.AddPolicy(FactoryResetService.Policy,
            policy => policy.RequireAuthenticatedUser().AddRequirements(new FactoryResetRequirement())));
        services.AddSingleton<FactoryResetStore>();
        services.AddSingleton<FactoryResetStorage>();
        services.AddSingleton<FactoryResetExecutor>();
        services.AddHostedService<FactoryResetWorker>();
    }

    internal static void UseConfastMaintenance(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var gate = context.RequestServices.GetRequiredService<MaintenanceGate>();
            try
            {
                if (HttpMethods.IsGet(context.Request.Method))
                {
                    if (context.Request.Path.Equals("/maintenance/status", StringComparison.OrdinalIgnoreCase))
                    {
                        var status = await gate.ReadAsync(context.RequestAborted);
                        context.Response.Headers.CacheControl = "no-store";
                        await context.Response.WriteAsJsonAsync(new
                        {
                            status.Generation, status.Phase, status.LastOutcome, Message = StageDescription(status)
                        }, context.RequestAborted);
                        return;
                    }
                    if (context.Request.Path.Equals("/maintenance", StringComparison.OrdinalIgnoreCase))
                    {
                        var status = await gate.ReadAsync(context.RequestAborted);
                        await WriteMaintenancePageAsync(context, status);
                        return;
                    }
                }

                // A SignalR transport can live for hours. The scoped circuit handler
                // leases individual activities instead of holding the transport open.
                if (context.Request.Path.StartsWithSegments("/_blazor"))
                    await next(context);
                else
                    await gate.RunAsync(() => next(context), context.RequestAborted);
            }
            catch (MaintenanceUnavailableException) when (!context.Response.HasStarted)
            {
                var status = await gate.ReadAsync(context.RequestAborted);
                await WriteMaintenancePageAsync(context, status);
            }
        });
    }

    internal static void MapEndpoints(WebApplication app)
    {
        app.MapPost(FactoryResetService.ExecuteRoute, async (HttpContext context, IAntiforgery antiforgery,
            FactoryResetService service, ILogger<FactoryResetService> logger) =>
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
                var form = await context.Request.ReadFormAsync(context.RequestAborted);
                await service.RequestAsync(form["Password"], form["Phrase"], form["Acknowledged"] == "true",
                    form["Confirmed"] == "true", context.RequestAborted);
                return Results.LocalRedirect("/maintenance");
            }
            catch (AntiforgeryValidationException) { return Results.BadRequest(); }
            catch (AuthorizationDeniedException) { return Results.Forbid(); }
            catch (FactoryResetException ex) { return Results.LocalRedirect(FactoryResetService.Route + "?error=" + ex.Code); }
            catch (NpgsqlException)
            {
                logger.LogWarning("Factory Reset submission could not reach the configured database boundary.");
                return Results.LocalRedirect(FactoryResetService.Route + "?error=Database");
            }
        }).RequireAuthorization(FactoryResetService.Policy)
          .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = IdentityConstants.ApplicationScheme });
    }

    private static Task WriteMaintenancePageAsync(HttpContext context, MaintenanceStatus status)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.RetryAfter = "5";
        return context.Response.WriteAsync($"""
            <!doctype html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>ConfastDB Maintenance</title>
              <script src="/theme.js"></script>
              <link rel="stylesheet" href="/theme.css">
              <link rel="stylesheet" href="/app.css">
              <script src="/factory-reset.js" defer></script>
            </head>
            <body>
              <main class="account-page" data-maintenance-page>
                <section class="account-card" aria-labelledby="maintenance-title">
                  <h1 id="maintenance-title">ConfastDB Maintenance</h1>
                  <p id="maintenance-phase" role="status">{StageDescription(status)}</p>
                  <p>The operation runs on the server. You can close this page and return later.</p>
                  <a class="button button-secondary" href="/login">Return to Sign In</a>
                </section>
              </main>
            </body>
            </html>
            """, context.RequestAborted);
    }

    private static string StageDescription(MaintenanceStatus status) => status.Phase switch
    {
        "Draining" => "Waiting for active work to finish.",
        "BackingUp" => "Creating and verifying the pinned pre-reset backup.",
        "Resetting" => "Resetting the application database and restoring installation defaults.",
        "CleaningStorage" => "Clearing managed file storage.",
        "Verifying" => "Verifying the reset database, storage, and pinned backup.",
        "Finalizing" => "Finalizing maintenance and signing out previous sessions.",
        _ when status.LastOutcome == "Aborted" => "Factory Reset stopped before deleting any application data. Check with the deployment operator.",
        _ => "Maintenance is complete. Return to sign in."
    };
}

// A Root account can sign back in with the same Identity key. Invalidate forms from
// the old installation even if another tab has since received the new cookie.
internal sealed class InstallationAntiforgeryData(MaintenanceGate gate) : IAntiforgeryAdditionalDataProvider
{
    public string GetAdditionalData(HttpContext context) => gate.CurrentGeneration?.ToString("D") ?? "";

    public bool ValidateAdditionalData(HttpContext context, string additionalData) =>
        gate.CurrentGeneration is { } generation && string.Equals(additionalData, generation.ToString("D"), StringComparison.Ordinal);
}
