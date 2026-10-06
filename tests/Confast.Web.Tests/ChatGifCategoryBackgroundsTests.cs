using Confast.Web.Features.Chat;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;

namespace Confast.Web.Tests;

public sealed class ChatGifCategoryBackgroundsTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "confast-gif-cache-" + Guid.NewGuid().ToString("N"));
    private ChatGifCategoryBackgrounds Cache() => new(new TestEnvironment { ContentRootPath = root });

    [Fact]
    public async Task Backgrounds_ResolveOnceAcrossConcurrentPickersAndSurviveRestart()
    {
        var cache = Cache();
        var calls = 0;
        const string url = "https://media.giphy.com/media/abc123/giphy.gif?keep=original";
        async Task<string?> Resolve()
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(10);
            return url;
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => cache.GetAsync("Hello", Resolve)));
        Assert.All(results, value => Assert.Equal(url, value));
        Assert.Equal(1, calls);
        Assert.Equal(url, await Cache().GetAsync("Hello", () => throw new InvalidOperationException("Must not query again")));
    }

    [Fact]
    public async Task Backgrounds_RetryMissingResultsAndRejectForeignUrls()
    {
        var cache = Cache();
        Assert.Null(await cache.GetAsync("Love", () => Task.FromResult<string?>(null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetAsync("Love", () => Task.FromResult<string?>("https://example.com/file.gif")));
        const string url = "https://media.giphy.com/media/love/giphy.gif";
        Assert.Equal(url, await cache.GetAsync("Love", () => Task.FromResult<string?>(url)));
        await Assert.ThrowsAsync<ArgumentException>(() => cache.GetAsync("Unknown", () => Task.FromResult<string?>(url)));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Confast.Web.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
