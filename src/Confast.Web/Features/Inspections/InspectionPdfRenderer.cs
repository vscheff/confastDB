using Microsoft.Playwright;

namespace Confast.Web.Features.Inspections;

public sealed class InspectionPdfRenderException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>
/// Creates PDFs by asking Chromium to print the same route the user sees in the
/// printable preview. The preview and download therefore share one layout.
/// </summary>
public sealed class InspectionPdfRenderer(Microsoft.AspNetCore.Hosting.Server.IServer server,
    IConfiguration configuration, InspectionPrintRenderTokenService tokens) : IAsyncDisposable
{
    private const float RenderTimeoutMilliseconds = 30_000;
    private readonly object browserSync = new();
    private Task<IBrowser>? browserTask;
    private IPlaywright? playwright;

    public async Task<byte[]> RenderAsync(string previewUrl, CancellationToken cancellationToken = default)
    {
        try { return await RenderCoreAsync(previewUrl, cancellationToken); }
        catch (PlaywrightException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Playwright diagnostics include the navigation URL and bearer token.
            throw new InspectionPdfRenderException("The inspection render failed or timed out.");
        }
    }

    private async Task<byte[]> RenderCoreAsync(string previewUrl, CancellationToken cancellationToken)
    {
        var requested = new Uri(previewUrl, UriKind.Absolute);
        // Host headers and component arguments must never choose where a bearer capability is sent.
        var origin = GetCallbackOrigin();
        var document = new UriBuilder(origin) { Path = requested.AbsolutePath, Query = requested.Query }.Uri;
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(document.Query);
        var segments = document.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments is not ["inspections", var id, "print"] || !long.TryParse(id, out var inspectionId)
            || !tokens.TryGetOptions(query.GetValueOrDefault("renderToken"), inspectionId, out var options)
            || query.Keys.Any(key => key is not ("renderToken" or "notesPage"))
            || options.SecondPageNotes != (query.GetValueOrDefault("notesPage") == "second"))
            throw new InspectionPdfRenderException("The print renderer requires an exact inspection capability and options.");
        var browser = await GetBrowserAsync(cancellationToken);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            ServiceWorkers = ServiceWorkerPolicy.Block
        });
        await context.RouteAsync("**/*", route =>
            Uri.TryCreate(route.Request.Url, UriKind.Absolute, out var resource)
                && InspectionRenderRequestPolicy.IsAllowed(document, resource, route.Request.ResourceType, route.Request.Method)
                ? route.ContinueAsync() : route.AbortAsync());
        // Cancel active navigation/rendering as well as the wait for a shared browser launch.
        using var cancellation = cancellationToken.Register(() => _ = context.CloseAsync());
        var page = await context.NewPageAsync();

        var response = await page.GotoAsync(
            document.AbsoluteUri,
            new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = RenderTimeoutMilliseconds
            });
        if (response?.Ok != true || !Uri.TryCreate(page.Url, UriKind.Absolute, out var renderedUrl) || renderedUrl != document)
        {
            throw new InspectionPdfRenderException("The print renderer was redirected before it could load the inspection.");
        }
        await page.WaitForFunctionAsync(
            "() => document.querySelector('.inspection-print-sheet, .inspection-print-authorization-error') !== null",
            null,
            new PageWaitForFunctionOptions { Timeout = RenderTimeoutMilliseconds });
        if (await page.Locator(".inspection-print-authorization-error").First.IsVisibleAsync())
        {
            throw new InspectionPdfRenderException("The print renderer was not authorized to load the inspection.");
        }
        await page.EvaluateAsync("async () => { await document.fonts.ready; await document.fonts.load('20pt \\\"Avengeance Heroic Avenger\\\"'); }");

        cancellationToken.ThrowIfCancellationRequested();
        return await page.PdfAsync(new PagePdfOptions
        {
            DisplayHeaderFooter = false,
            PreferCSSPageSize = true,
            PrintBackground = true,
            Margin = new Margin
            {
                Top = "0",
                Right = "0",
                Bottom = "0",
                Left = "0"
            }
        });
    }

    private Uri GetCallbackOrigin()
    {
        // Reverse proxies may configure a trusted internal HTTPS origin. Otherwise use
        // this server's actual listener, with wildcard binds mapped to local loopback.
        var configured = configuration["InspectionRendering:ApplicationOrigin"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var origin = new Uri(configured, UriKind.Absolute);
            if (origin.Scheme is not ("http" or "https") || origin.UserInfo.Length != 0 || origin.PathAndQuery != "/")
                throw new InspectionPdfRenderException("The configured inspection rendering origin is invalid.");
            return origin;
        }
        var addresses = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses;
        var address = addresses?.OrderBy(x => x.StartsWith("http://", StringComparison.Ordinal) ? 0 : 1).FirstOrDefault();
        if (address is null) throw new InspectionPdfRenderException("Configure the inspection renderer's internal application origin.");
        address = address.Replace("0.0.0.0", "127.0.0.1").Replace("[::]", "[::1]").Replace("*", "127.0.0.1").Replace("+", "127.0.0.1");
        return new Uri(address, UriKind.Absolute);
    }

    private Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken)
    {
        lock (browserSync)
        {
            if (browserTask is null || browserTask.IsFaulted || browserTask.IsCanceled)
                browserTask = LaunchBrowserAsync(CancellationToken.None);
            return browserTask.WaitAsync(cancellationToken);
        }
    }

    private async Task<IBrowser> LaunchBrowserAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        playwright = await Playwright.CreateAsync();
        return await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (browserTask is not null)
        {
            try
            {
                var browser = await browserTask;
                await browser.CloseAsync();
            }
            catch
            {
                // The host is shutting down; there is no useful recovery action.
            }
        }

        playwright?.Dispose();
    }
}
