using Confast.Web.Data;
using Confast.Web.Features.Chat;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Identity;

public sealed class UserProfilePicture
{
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser User { get; set; } = null!;
    public byte[] Data { get; set; } = [];
    public string ContentType { get; set; } = string.Empty;
    public Guid Version { get; set; }
}

public sealed class UserProfilePictureService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICurrentUser currentUser,
    ChatNotifications notifications)
{
    public const int MaximumPictureBytes = 1_048_576;
    public const long MaximumSourcePictureBytes = 25L * 1024 * 1024;

    public async Task<IReadOnlyDictionary<string, Guid>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await RequireActiveUserAsync(db, cancellationToken);
        return await db.UserProfilePictures.AsNoTracking()
            .Where(x => x.User.IsActive)
            .ToDictionaryAsync(x => x.UserId, x => x.Version, cancellationToken);
    }

    public async Task<string> GetCurrentUserDisplayNameAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireActiveUserAsync(db, cancellationToken);
        return await db.Users.AsNoTracking().Where(x => x.Id == userId)
            .Select(x => x.DisplayName).SingleAsync(cancellationToken);
    }

    public async Task<(byte[] Data, string ContentType)?> GetAsync(string requesterUserId, string userId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Users.AnyAsync(x => x.Id == requesterUserId && x.IsActive, cancellationToken))
            throw new UnauthorizedAccessException("An active account is required to view profile pictures.");
        var picture = await db.UserProfilePictures.AsNoTracking()
            .Where(x => x.UserId == userId && x.User.IsActive)
            .Select(x => new { x.Data, x.ContentType })
            .SingleOrDefaultAsync(cancellationToken);
        return picture is null ? null : (picture.Data, picture.ContentType);
    }

    public async Task SaveAsync(byte[] data, CancellationToken cancellationToken = default)
    {
        if (data.Length is 0 or > MaximumPictureBytes)
            throw new InvalidOperationException("Choose a picture smaller than 1 MB.");
        var contentType = DetectContentType(data)
            ?? throw new InvalidOperationException("Choose a PNG, JPEG, GIF, or WebP picture.");

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireActiveUserAsync(db, cancellationToken);
        var version = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO identity_user_profile_pictures (user_id, data, content_type, version)
            VALUES ({userId}, {data}, {contentType}, {version})
            ON CONFLICT (user_id) DO UPDATE SET
                data = EXCLUDED.data,
                content_type = EXCLUDED.content_type,
                version = EXCLUDED.version
            """, cancellationToken);
        await NotifyUsersAsync(db, cancellationToken);
    }

    public async Task RemoveAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var userId = await RequireActiveUserAsync(db, cancellationToken);
        await db.UserProfilePictures.Where(x => x.UserId == userId).ExecuteDeleteAsync(cancellationToken);
        await NotifyUsersAsync(db, cancellationToken);
    }

    private async Task<string> RequireActiveUserAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var userId = await currentUser.GetUserIdAsync();
        if (userId is null || !await db.Users.AnyAsync(x => x.Id == userId && x.IsActive, cancellationToken))
            throw new UnauthorizedAccessException("An active account is required to view profile pictures.");
        return userId;
    }

    private async Task NotifyUsersAsync(AppDbContext db, CancellationToken cancellationToken) =>
        notifications.Publish(await db.Users.AsNoTracking().Where(x => x.IsActive)
            .Select(x => x.Id).ToListAsync(cancellationToken));

    internal static string? DetectContentType(ReadOnlySpan<byte> data)
    {
        if (data.StartsWith(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a })) return "image/png";
        if (data.StartsWith(new byte[] { 0xff, 0xd8, 0xff })) return "image/jpeg";
        if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) return "image/gif";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        return null;
    }
}
