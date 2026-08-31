using Microsoft.Playwright;

namespace Confast.Web.Features.Inspections;

public sealed class InspectionPdfRenderException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

/// <summary>
/// Creates PDFs by asking Chromium to print the same route the user sees in the
/// printable preview. The preview and download therefore share one layout.
/// </summary>
public sealed class InspectionPdfRenderer : IAsyncDisposable
{
    private const float RenderTimeoutMilliseconds = 30_000;
    private readonly object browserSync = new();
    private Task<IBrowser>? browserTask;
    private IPlaywright? playwright;

    public async Task<byte[]> RenderAsync(string previewUrl, CancellationToken cancellationToken = default)
    {
        var browser = await GetBrowserAsync(cancellationToken);
        await using var context = await browser.NewContextAsync(new BrowserNewContextOptions
        {
            IgnoreHTTPSErrors = true
        });
        var page = await context.NewPageAsync();

        await page.GotoAsync(
            previewUrl,
            new PageGotoOptions
            {
                WaitUntil = WaitUntilState.DOMContentLoaded,
                Timeout = RenderTimeoutMilliseconds
            });
        var expectedPath = new Uri(previewUrl, UriKind.Absolute).AbsolutePath;
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var renderedUrl)
            || !string.Equals(renderedUrl.AbsolutePath, expectedPath, StringComparison.OrdinalIgnoreCase))
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

    private Task<IBrowser> GetBrowserAsync(CancellationToken cancellationToken)
    {
        lock (browserSync)
        {
            browserTask ??= LaunchBrowserAsync(cancellationToken);
            return browserTask;
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
