using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Authorization;

public static class AuthorizationMapping
{
    public static void Configure(ModelBuilder model)
    {
        var role = model.Entity<ApplicationRole>();
        role.Property(x => x.Description).HasColumnName("description").HasMaxLength(2000);
        role.Property(x => x.SystemKind).HasColumnName("system_kind");
        role.Property(x => x.SystemKey).HasColumnName("system_key").HasMaxLength(64);
        role.Property(x => x.IsEnabled).HasColumnName("is_enabled").HasDefaultValue(true);
        role.HasIndex(x => x.SystemKey).IsUnique();
        role.ToTable("identity_roles", t => t.HasCheckConstraint("CK_role_system_kind",
            "(system_kind = 0 AND system_key IS NULL) OR (system_kind IN (1,2) AND system_key IS NOT NULL)"));

        var manifest = model.Entity<PermissionManifest>();
        manifest.ToTable("permissions");
        manifest.HasKey(x => x.Key);
        manifest.HasData(PermissionCatalog.All.Select(x => new PermissionManifest
        {
            Key = x.Key, DisplayName = x.DisplayName, Category = x.Category, Kind = x.Kind,
            Authority = x.Authority, AllowedInBaseline = x.AllowedInBaseline,
            CatalogVersion = PermissionCatalog.Version
        }));

        var grant = model.Entity<RolePermission>();
        grant.ToTable("role_permissions");
        grant.HasKey(x => new { x.RoleId, x.PermissionKey });
        grant.HasOne<ApplicationRole>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Restrict);
        grant.HasOne<PermissionManifest>().WithMany().HasForeignKey(x => x.PermissionKey).OnDelete(DeleteBehavior.Restrict);
        grant.HasData(AuthorizationSeeds.Grants);

        var edge = model.Entity<RoleInheritance>();
        edge.ToTable("role_inheritance", t => t.HasCheckConstraint("CK_role_inheritance_not_self", "child_role_id <> parent_role_id"));
        edge.HasKey(x => new { x.ChildRoleId, x.ParentRoleId });
        edge.HasOne<ApplicationRole>().WithMany().HasForeignKey(x => x.ChildRoleId).OnDelete(DeleteBehavior.Restrict);
        edge.HasOne<ApplicationRole>().WithMany().HasForeignKey(x => x.ParentRoleId).OnDelete(DeleteBehavior.Restrict);
        edge.HasData(AuthorizationSeeds.Edges);

        var state = model.Entity<AuthorizationState>();
        state.ToTable("authorization_state", t =>
        {
            t.HasCheckConstraint("CK_authorization_singleton", "id = 1 AND global_epoch >= 0");
            t.HasCheckConstraint("CK_authorization_readiness", "(readiness = 0 AND root_user_id IS NULL) OR (readiness = 1 AND root_user_id IS NOT NULL)");
        });
        state.HasKey(x => x.Id);
        state.Property(x => x.Id).ValueGeneratedNever();
        state.Property(x => x.GlobalEpoch).IsConcurrencyToken();
        state.Property(x => x.InstallationGeneration).HasDefaultValueSql("gen_random_uuid()");
        state.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.RootUserId).OnDelete(DeleteBehavior.Restrict);
        state.HasOne<ApplicationRole>().WithMany().HasForeignKey(x => x.BaselineRoleId).OnDelete(DeleteBehavior.Restrict);
        state.HasOne<ApplicationRole>().WithMany().HasForeignKey(x => x.RootRoleId).OnDelete(DeleteBehavior.Restrict);

        var history = model.Entity<AuthorizationChangeHistory>();
        history.ToTable("authorization_change_history", t => t.HasCheckConstraint("CK_authorization_history_actor",
            "(actor_kind = 0 AND actor_user_id IS NOT NULL) OR (actor_kind = 1 AND actor_user_id IS NULL)"));
        history.HasKey(x => x.Id);
        history.Property(x => x.Purpose).HasMaxLength(200);
        history.Property(x => x.Reason).HasMaxLength(2000);
        history.HasIndex(x => new { x.InstallationGeneration, x.ResultingEpoch });
        history.HasMany(x => x.Details).WithOne().HasForeignKey(x => x.HistoryId).OnDelete(DeleteBehavior.Restrict);
        var detail = model.Entity<AuthorizationChangeDetail>();
        detail.ToTable("authorization_change_details");
        detail.HasKey(x => x.Id);
        detail.Property(x => x.PreviousName).HasMaxLength(256);
        detail.Property(x => x.ResultingName).HasMaxLength(256);
        detail.Property(x => x.FieldName).HasMaxLength(64);
        detail.Property(x => x.PreviousValue).HasMaxLength(2000);
        detail.Property(x => x.ResultingValue).HasMaxLength(2000);

        var reset = model.Entity<PasswordResetDelegation>();
        reset.ToTable("password_reset_delegations", t => t.HasCheckConstraint("CK_reset_delegation_expiry",
            "expires_at_utc > issued_at_utc AND (consumed_at_utc IS NULL OR consumed_at_utc >= issued_at_utc)"));
        reset.HasKey(x => x.Id);
        reset.Property(x => x.TokenDigest).HasMaxLength(64);
        reset.HasIndex(x => x.TokenDigest).IsUnique();
        reset.HasIndex(x => new { x.TargetUserId, x.ExpiresAtUtc });
        // Delegations are historical ID snapshots. Deleted issuers cannot authorize redemption;
        // target IDs are never reused, and deleting an ordinary account must remain possible.

        // Explicit snake-case mapping consistent with the existing Identity tables.
        foreach (var type in new[] { typeof(PermissionManifest), typeof(RolePermission), typeof(RoleInheritance),
                     typeof(AuthorizationState), typeof(AuthorizationChangeHistory), typeof(AuthorizationChangeDetail), typeof(PasswordResetDelegation) })
        {
            foreach (var property in model.Entity(type).Metadata.GetProperties())
                property.SetColumnName(string.Concat(property.Name.Select((c, i) =>
                    i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString())));
        }
    }
}
