using System.Text.Json;

namespace Confast.Web.Features.Chat;

// Category artwork is application configuration, shared across users and retained
// across restarts. Search results and sent GIFs continue to resolve normally.
public sealed class ChatGifCategoryBackgrounds(IWebHostEnvironment environment)
{
    public static readonly IReadOnlyList<string> Categories =
        ["Hello", "LOL", "Love", "Happy Birthday", "Celebrate", "Thank You", "Facepalm", "Wow"];

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string cachePath = Path.Combine(environment.ContentRootPath, "App_Data", "chat-gif-category-backgrounds.json");
    private Dictionary<string, string>? backgrounds;

    public async Task<string?> GetAsync(string category, Func<Task<string?>> resolve)
    {
        if (!Categories.Contains(category, StringComparer.Ordinal))
            throw new ArgumentException("Unknown GIF category.", nameof(category));

        // The lock includes the client lookup so simultaneous picker openings
        // cannot all spend an API request initializing the same category.
        await gate.WaitAsync();
        try
        {
            if (backgrounds is null)
            {
                if (File.Exists(cachePath))
                {
                    await using var input = File.OpenRead(cachePath);
                    var stored = await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(input) ?? [];
                    foreach (var storedUrl in stored.Values) ValidateUrl(storedUrl);
                    backgrounds = stored;
                }
                else backgrounds = [];
            }
            if (backgrounds.TryGetValue(category, out var saved)) return saved;
            var url = await resolve();
            if (url is null) return null;
            ValidateUrl(url);
            var updated = new Dictionary<string, string>(backgrounds) { [category] = url };
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            var temporaryPath = cachePath + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(updated));
            File.Move(temporaryPath, cachePath, overwrite: true);
            backgrounds = updated;
            return url;
        }
        finally { gate.Release(); }
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
