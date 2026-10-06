using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Chat;

public sealed class ChatGifFavorite
{
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
    public string GiphyId { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; }
}

public sealed record ChatGif(string Id, string Title, string PreviewUrl, string Url, string SourceUrl, string Attribution);
public sealed record ChatGifResult(ChatGif[] Gifs, int Total, string? Error);

public sealed class ChatGifCacheOptions
{
    public int SearchCacheLifetimeHours { get; set; } = 168;
    public int HourlyRequestLimit { get; set; } = 100;
    public int RefreshRequestReserve { get; set; } = 20;
}

public sealed class ChatGifApiRequest
{
    public long Id { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public bool IsRefresh { get; set; }
}

public sealed class ChatGifMetadata
{
    public string GiphyId { get; set; } = string.Empty;
    public string Payload { get; set; } = string.Empty;
    public DateTime CachedAtUtc { get; set; }
}

public sealed class ChatGifSearchPage
{
    public string Query { get; set; } = string.Empty;
    public string Rating { get; set; } = "pg-13";
    public string Language { get; set; } = "en";
    public int Offset { get; set; }
    public int Limit { get; set; }
    public string GifIds { get; set; } = "[]";
    public int Total { get; set; }
    public DateTime CachedAtUtc { get; set; }
}

public static class ChatGifMapping
{
    public static void Configure(ModelBuilder model)
    {
        var request = model.Entity<ChatGifApiRequest>();
        request.ToTable("chat_gif_api_requests");
        request.HasKey(x => x.Id);
        request.Property(x => x.Id).HasColumnName("id");
        request.Property(x => x.RequestedAtUtc).HasColumnName("requested_at_utc");
        request.Property(x => x.IsRefresh).HasColumnName("is_refresh");
        request.HasIndex(x => x.RequestedAtUtc);
        var metadata = model.Entity<ChatGifMetadata>();
        metadata.ToTable("chat_gif_metadata");
        metadata.HasKey(x => x.GiphyId);
        metadata.Property(x => x.GiphyId).HasColumnName("giphy_id").HasMaxLength(100);
        metadata.Property(x => x.Payload).HasColumnName("payload").HasColumnType("jsonb");
        metadata.Property(x => x.CachedAtUtc).HasColumnName("cached_at_utc");
        var page = model.Entity<ChatGifSearchPage>();
        page.ToTable("chat_gif_search_pages");
        page.HasKey(x => new { x.Query, x.Rating, x.Language, x.Offset, x.Limit });
        page.Property(x => x.Query).HasColumnName("query").HasMaxLength(50);
        page.Property(x => x.Rating).HasColumnName("rating").HasMaxLength(10);
        page.Property(x => x.Language).HasColumnName("language").HasMaxLength(10);
        page.Property(x => x.Offset).HasColumnName("offset");
        page.Property(x => x.Limit).HasColumnName("limit");
        page.Property(x => x.GifIds).HasColumnName("gif_ids").HasColumnType("jsonb");
        page.Property(x => x.Total).HasColumnName("total");
        page.Property(x => x.CachedAtUtc).HasColumnName("cached_at_utc");
        var favorite = model.Entity<ChatGifFavorite>();
        favorite.ToTable("chat_gif_favorites", t => t.HasCheckConstraint("CK_chat_gif_favorites_id", "giphy_id ~ '^[A-Za-z0-9]{1,100}$'"));
        favorite.HasKey(x => new { x.UserId, x.GiphyId });
        favorite.Property(x => x.UserId).HasColumnName("user_id");
        favorite.Property(x => x.GiphyId).HasColumnName("giphy_id").HasMaxLength(100);
        favorite.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        favorite.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
