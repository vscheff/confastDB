using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Navigation;

internal sealed class NavigationPreference
{
    public string UserId { get; set; } = null!;
    public int Capacity { get; set; } = QuickAccessService.DefaultCapacity;
    public DateOnly? RecommendationDay { get; set; }
    public string[] RecommendationOrder { get; set; } = [];
    public string? AppearanceMode { get; set; }
    public string? AccentColor { get; set; }
    public List<NavigationShortcutPreference> Shortcuts { get; } = [];
}

internal sealed class NavigationShortcutPreference
{
    public string UserId { get; set; } = null!;
    public string DestinationId { get; set; } = null!;
    // A row without a position represents a dismissal.
    public int? PinOrder { get; set; }
}

internal sealed class NavigationUsage
{
    public string UserId { get; set; } = null!;
    public string DestinationId { get; set; } = null!;
    public long VisitCount { get; set; }
    public DateTimeOffset LastVisitedUtc { get; set; }
    public double VisitScore { get; set; }
}

internal static class NavigationMapping
{
    public static void Configure(ModelBuilder model)
    {
        var preferences = model.Entity<NavigationPreference>();
        preferences.ToTable("navigation_preferences", table => table.HasCheckConstraint(
            "CK_navigation_preferences_capacity", "capacity IN (6, 8, 12)"));
        preferences.HasKey(x => x.UserId);
        preferences.Property(x => x.UserId).HasColumnName("user_id");
        preferences.Property(x => x.Capacity).HasColumnName("capacity").HasDefaultValue(QuickAccessService.DefaultCapacity);
        preferences.Property(x => x.RecommendationDay).HasColumnName("recommendation_day");
        preferences.Property(x => x.RecommendationOrder).HasColumnName("recommendation_order").HasDefaultValueSql("ARRAY[]::text[]");
        preferences.Property(x => x.AppearanceMode).HasColumnName("appearance_mode").HasMaxLength(6);
        preferences.Property(x => x.AccentColor).HasColumnName("accent_color").HasMaxLength(7);
        preferences.HasOne<ApplicationUser>().WithOne().HasForeignKey<NavigationPreference>(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        var shortcuts = model.Entity<NavigationShortcutPreference>();
        shortcuts.ToTable("navigation_shortcut_preferences", table => table.HasCheckConstraint(
            "CK_navigation_shortcut_preferences_pin_order", "pin_order >= 0"));
        shortcuts.HasKey(x => new { x.UserId, x.DestinationId });
        shortcuts.Property(x => x.UserId).HasColumnName("user_id");
        shortcuts.Property(x => x.DestinationId).HasColumnName("destination_id").HasMaxLength(80);
        shortcuts.Property(x => x.PinOrder).HasColumnName("pin_order");
        shortcuts.HasOne<NavigationPreference>().WithMany(x => x.Shortcuts).HasForeignKey(x => x.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        var usage = model.Entity<NavigationUsage>();
        usage.ToTable("navigation_usage", table =>
        {
            table.HasCheckConstraint("CK_navigation_usage_visit_count", "visit_count > 0");
            table.HasCheckConstraint("CK_navigation_usage_visit_score", "visit_score > 0 AND visit_score < 'Infinity'::double precision");
        });
        usage.HasKey(x => new { x.UserId, x.DestinationId });
        usage.Property(x => x.UserId).HasColumnName("user_id");
        usage.Property(x => x.DestinationId).HasColumnName("destination_id").HasMaxLength(80);
        usage.Property(x => x.VisitCount).HasColumnName("visit_count");
        usage.Property(x => x.LastVisitedUtc).HasColumnName("last_visited_utc");
        usage.Property(x => x.VisitScore).HasColumnName("visit_score");
        usage.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
