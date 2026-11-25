using System.Text.Json;
using Confast.Web.Features.Backups;
using Confast.Web.Features.Maintenance;

namespace Confast.Web.Features.Chat;

// Category artwork is application configuration, shared across users and retained
// across restarts. Search results and sent GIFs continue to resolve normally.
internal sealed class ChatGifCategoryBackgrounds(IWebHostEnvironment environment, IConfiguration? configuration = null,
    MaintenanceGate? maintenance = null)
{
    internal const string StorageFileName = "chat-gif-category-backgrounds.json";
    internal static string StoragePath(string contentRoot) => Path.Combine(contentRoot, "App_Data", StorageFileName);
    internal static string StorageDirectory(string contentRoot, IConfiguration? configuration) =>
        configuration?["ManagedFiles:Directory"] is { Length: > 0 } path
            ? BackupStorage.CanonicalPath(path) : Path.GetDirectoryName(StoragePath(contentRoot))!;

    public static readonly IReadOnlyList<string> Categories =
        ["Hello", "LOL", "Love", "Happy Birthday", "Celebrate", "Thank You", "Facepalm", "Wow"];

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string cachePath = Path.Combine(StorageDirectory(environment.ContentRootPath, configuration), StorageFileName);

    public async Task<string?> GetAsync(string category, Func<Task<string?>> resolve)
    {
        if (!Categories.Contains(category, StringComparer.Ordinal))
            throw new ArgumentException("Unknown GIF category.", nameof(category));

        if (maintenance is null) return await ReadOrResolveAsync(category, resolve);
        string? result = null;
        await maintenance.RunAsync(async () => { result = await ReadOrResolveAsync(category, resolve); }, CancellationToken.None);
        return result;
    }

    private async Task<string?> ReadOrResolveAsync(string category, Func<Task<string?>> resolve)
    {
        // The lock includes the client lookup so simultaneous picker openings
        // cannot all spend an API request initializing the same category.
        await gate.WaitAsync();
        try
        {
            using var files = await LockStorageAsync();
            if (maintenance is not null && (await maintenance.ReadAsync(CancellationToken.None)).Generation != maintenance.CurrentGeneration)
                throw new MaintenanceUnavailableException();
            Dictionary<string, string> backgrounds = [];
            if (BackupStorage.CheckPath(cachePath, directory: false, allowMissing: true))
            {
                await using var input = File.OpenRead(cachePath);
                backgrounds = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(input) ?? [];
                foreach (var storedUrl in backgrounds.Values) ValidateUrl(storedUrl);
            }
            if (backgrounds.TryGetValue(category, out var saved)) return saved;
            var url = await resolve();
            if (url is null) return null;
            ValidateUrl(url);
            var updated = new Dictionary<string, string>(backgrounds) { [category] = url };
            var temporaryPath = cachePath + ".tmp";
            BackupStorage.CheckPath(temporaryPath, directory: false, allowMissing: true);
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(updated));
            File.Move(temporaryPath, cachePath, overwrite: true);
            return url;
        }
        finally { gate.Release(); }
    }

    private async Task<FileStream> LockStorageAsync()
    {
        var directory = Path.GetDirectoryName(cachePath)!;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (FactoryResetStorage.TryLockDirectory(directory) is { } lease) return lease;
            await Task.Delay(50);
        }
        throw new IOException("Managed artwork storage is busy.");
    }

    private static void ValidateUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
            || !(uri.Host.Equals("giphy.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.EndsWith(".giphy.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("GIF category artwork must use a GIPHY HTTPS image URL.");
    }
}
