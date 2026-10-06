using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public sealed partial class ChatService
{
    // Bounded lock storage, shared by circuits. Identical search misses wait for
    // the first browser request and then read its committed page from PostgreSQL.
    private static readonly SemaphoreSlim[] GifSearchLocks = Enumerable.Range(0, 32).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private ChatGifCacheOptions GifCacheOptions => gifCacheOptions?.Value ?? new();

    // The browser invokes this once per actual ID lookup batch, rather than once
    // per message component. Reservations are committed before network I/O.
    [Microsoft.JSInterop.JSInvokable]
    public async Task RecordGifRequestAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        await RequireUserAsync(db, default);
        await ReserveGifRequestAsync(db, refresh: false);
    }

    private async Task<bool> ReserveGifRequestAsync(Confast.Web.Data.AppDbContext db, bool refresh)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Coordinates admission across users and application instances. No network
        // request runs while this short database transaction holds the lock.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(714903861)");
        var now = clock.GetUtcNow().UtcDateTime;
        var cutoff = now.AddHours(-1);
        var used = await db.Set<ChatGifApiRequest>().CountAsync(x => x.RequestedAtUtc > cutoff);
        var allowance = GifCacheOptions.HourlyRequestLimit - (refresh ? GifCacheOptions.RefreshRequestReserve : 0);
        if (refresh && used >= allowance) return false;
        db.Add(new ChatGifApiRequest { RequestedAtUtc = now, IsRefresh = refresh });
        await db.SaveChangesAsync();
        // Retain seven days of hourly usage history, bounded independently of cache size.
        await db.Set<ChatGifApiRequest>().Where(x => x.RequestedAtUtc < now.AddDays(-7)).ExecuteDeleteAsync();
        await transaction.CommitAsync();
        return true;
    }

    public async Task<ChatGifResult> ResolveGifSearchAsync(string query, int offset, int limit,
        Func<Task<ChatGifResult>> fetch)
    {
        query = query.Trim().ToLowerInvariant();
        if (query.Length is < 1 or > 50 || offset is < 0 or > 4999 || limit is < 1 or > 50)
            throw new InvalidOperationException("Choose a valid GIF search page.");
        await using var db = await dbFactory.CreateDbContextAsync();
        await RequireUserAsync(db, default);
        var gate = GifSearchLocks[(uint)HashCode.Combine(query, offset, limit) % GifSearchLocks.Length];
        await gate.WaitAsync();
        try
        {
            var page = await db.Set<ChatGifSearchPage>().AsNoTracking().SingleOrDefaultAsync(x =>
                x.Query == query && x.Offset == offset && x.Limit == limit && x.Rating == "pg-13" && x.Language == "en");
            ChatGifResult? cached = null;
            if (page is not null)
            {
                var ids = JsonSerializer.Deserialize<string[]>(page.GifIds)!;
                var saved = await db.Set<ChatGifMetadata>().AsNoTracking().Where(x => ids.Contains(x.GiphyId)).ToArrayAsync();
                var byId = saved.ToDictionary(x => x.GiphyId, x => JsonSerializer.Deserialize<ChatGif>(x.Payload)!);
                cached = new(ids.Select(id => byId[id]).ToArray(), page.Total, null);
                if (page.CachedAtUtc > clock.GetUtcNow().UtcDateTime.AddHours(-GifCacheOptions.SearchCacheLifetimeHours))
                    return cached;
            }
            if (!await ReserveGifRequestAsync(db, refresh: cached is not null))
                return cached ?? new([], 0, "GIF request budget is exhausted. Try again later.");
            ChatGifResult result;
            try { result = await fetch(); }
            catch (Exception ex) when (cached is not null && (ex is Microsoft.JSInterop.JSException or OperationCanceledException))
            {
                logger?.LogWarning(ex, "GIF refresh failed; serving the cached search page.");
                return cached;
            }
            if (result.Error is not null) return cached ?? result;
            ValidateGifResult(result, limit);
            await using var transaction = await db.Database.BeginTransactionAsync();
            await SaveGifMetadataAsync(db, result.Gifs);
            var idsJson = JsonSerializer.Serialize(result.Gifs.Select(x => x.Id));
            var now = clock.GetUtcNow().UtcDateTime;
            // Also safe across application instances, where process locks aren't shared.
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO chat_gif_search_pages (query, rating, language, "offset", "limit", gif_ids, total, cached_at_utc)
                VALUES ({query}, 'pg-13', 'en', {offset}, {limit}, CAST({idsJson} AS jsonb), {result.Total}, {now})
                ON CONFLICT (query, rating, language, "offset", "limit") DO UPDATE
                SET gif_ids = EXCLUDED.gif_ids, total = EXCLUDED.total, cached_at_utc = EXCLUDED.cached_at_utc
                WHERE chat_gif_search_pages.cached_at_utc <= EXCLUDED.cached_at_utc
                """);
            await transaction.CommitAsync();
            return result;
        }
        finally { gate.Release(); }
    }

    public async Task<ChatGifResult> ResolveGifsAsync(IReadOnlyList<string> ids,
        Func<string[], Task<ChatGifResult>> fetch)
    {
        if (ids.Count > 50) throw new InvalidOperationException("Too many GIFs requested.");
        foreach (var id in ids) ValidateGiphyId(id);
        await using var db = await dbFactory.CreateDbContextAsync();
        await RequireUserAsync(db, default);
        var saved = await db.Set<ChatGifMetadata>().AsNoTracking().Where(x => ids.Contains(x.GiphyId)).ToArrayAsync();
        var byId = saved.ToDictionary(x => x.GiphyId, x => JsonSerializer.Deserialize<ChatGif>(x.Payload)!);
        var missing = ids.Distinct().Where(id => !byId.ContainsKey(id)).ToArray();
        string? error = null;
        if (missing.Length > 0)
        {
            var result = await fetch(missing);
            error = result.Error;
            if (error is null)
            {
                ValidateGifResult(result, missing.Length);
                if (result.Gifs.Any(x => !missing.Contains(x.Id))) throw new InvalidOperationException("Unexpected GIF lookup response.");
                await SaveGifMetadataAsync(db, result.Gifs);
                foreach (var gif in result.Gifs) byId[gif.Id] = gif;
            }
        }
        // Cached favorites remain usable even if resolving older, uncached IDs fails.
        var gifs = ids.Distinct().Where(byId.ContainsKey).Select(id => byId[id]).ToArray();
        return new(gifs, gifs.Length, error);
    }

    private async Task SaveGifMetadataAsync(Confast.Web.Data.AppDbContext db, IEnumerable<ChatGif> gifs)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var gif in gifs)
        {
            var payload = JsonSerializer.Serialize(gif);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO chat_gif_metadata (giphy_id, payload, cached_at_utc)
                VALUES ({gif.Id}, CAST({payload} AS jsonb), {now}) ON CONFLICT (giphy_id) DO UPDATE
                SET payload = EXCLUDED.payload, cached_at_utc = EXCLUDED.cached_at_utc
                WHERE chat_gif_metadata.cached_at_utc <= EXCLUDED.cached_at_utc
                """);
        }
    }

    private static void ValidateGifResult(ChatGifResult result, int limit)
    {
        if (result.Total < 0 || result.Gifs.Length > limit || result.Gifs.Select(x => x.Id).Distinct().Count() != result.Gifs.Length)
            throw new InvalidOperationException("Invalid GIF response.");
        foreach (var gif in result.Gifs)
        {
            ValidateGiphyId(gif.Id);
            foreach (var url in new[] { gif.Url, gif.PreviewUrl, gif.SourceUrl })
                if (url.Length > 4096 || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
                    || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
                    || !(uri.Host.Equals("giphy.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".giphy.com", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("GIF URLs must use GIPHY HTTPS addresses.");
            if (gif.Title.Length > 1000 || gif.Attribution.Length > 1000)
                throw new InvalidOperationException("Invalid GIF metadata.");
        }
    }
}
