using Confast.Web.Features.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.ProductionTracking;

public static class ProductionTrackingMapping
{
    public static void Configure(ModelBuilder model)
    {
        var log = model.Entity<SortLog>();
        log.ToTable("sort_logs", table =>
        {
            table.HasCheckConstraint("CK_sort_logs_target_pph", "target_pph_snapshot > 0");
            table.HasCheckConstraint("CK_sort_logs_comments", "comments IS NULL OR char_length(comments) <= 4000");
        });
        log.HasKey(x => x.Id).HasName("PK_sort_logs");
        log.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        log.Property(x => x.ProductionDate).HasColumnName("production_date");
        log.Property(x => x.MachineId).HasColumnName("machine_id");
        log.Property(x => x.PartId).HasColumnName("part_id");
        log.Property(x => x.InspectionId).HasColumnName("inspection_id");
        log.Property(x => x.ProductionSegmentId).HasColumnName("production_segment_id");
        log.Property(x => x.TargetPphSnapshot).HasColumnName("target_pph_snapshot").HasPrecision(24, 0);
        log.Property(x => x.Comments).HasColumnName("comments").HasMaxLength(4000);
        log.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
        log.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id");
        log.Property(x => x.Version).HasColumnName("xmin").IsRowVersion();
        log.HasOne(x => x.Machine).WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        log.HasOne(x => x.Part).WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        log.HasOne(x => x.Inspection).WithMany().HasForeignKey(x => x.InspectionId).OnDelete(DeleteBehavior.Restrict);
        log.HasOne(x => x.ProductionSegment).WithMany().HasForeignKey(x => x.ProductionSegmentId).OnDelete(DeleteBehavior.SetNull);
        log.HasOne(x => x.CreatedByUser).WithMany().HasForeignKey(x => x.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
        log.HasMany(x => x.Lines).WithOne(x => x.SortLog).HasForeignKey(x => x.SortLogId).OnDelete(DeleteBehavior.Restrict);
        log.HasIndex(x => new { x.ProductionDate, x.MachineId, x.PartId, x.InspectionId })
            .IsUnique().HasDatabaseName("UX_sort_logs_daily_job");
        log.HasIndex(x => x.MachineId).HasDatabaseName("IX_sort_logs_machine_id");
        log.HasIndex(x => x.PartId).HasDatabaseName("IX_sort_logs_part_id");
        log.HasIndex(x => x.InspectionId).HasDatabaseName("IX_sort_logs_inspection_id");
        log.HasIndex(x => x.ProductionSegmentId).HasDatabaseName("IX_sort_logs_production_segment_id");
        log.HasIndex(x => x.CreatedByUserId).HasDatabaseName("IX_sort_logs_created_by_user_id");

        var line = model.Entity<SortLogLine>();
        line.ToTable("sort_log_lines", table =>
        {
            table.HasCheckConstraint("CK_sort_log_lines_time", "stop_time_utc IS NULL OR stop_time_utc >= start_time_utc");
            table.HasCheckConstraint("CK_sort_log_lines_quantities", "pass_quantity >= 0 AND fail_quantity >= 0 AND boundary_samples_ran_quantity >= 0 AND boundary_samples_passed_quantity >= 0 AND boundary_samples_passed_quantity <= boundary_samples_ran_quantity");
            table.HasCheckConstraint("CK_sort_log_lines_notes", "notes IS NULL OR char_length(notes) <= 4000");
            table.HasCheckConstraint("CK_sort_log_lines_initials", "(production_initials IS NOT NULL OR quality_initials IS NOT NULL) AND (production_initials IS NULL OR char_length(production_initials) = 2) AND (quality_initials IS NULL OR char_length(quality_initials) = 2)");
        });
        line.HasKey(x => x.Id).HasName("PK_sort_log_lines");
        line.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        line.Property(x => x.SortLogId).HasColumnName("sort_log_id");
        line.Property(x => x.Sequence).HasColumnName("sequence");
        line.Property(x => x.StartTimeUtc).HasColumnName("start_time_utc");
        line.Property(x => x.StopTimeUtc).HasColumnName("stop_time_utc");
        line.Property(x => x.DowntimeCauseId).HasColumnName("downtime_cause_id");
        line.Property(x => x.PassQuantity).HasColumnName("pass_quantity");
        line.Property(x => x.FailQuantity).HasColumnName("fail_quantity");
        line.Property(x => x.BoundarySamplesRanQuantity).HasColumnName("boundary_samples_ran_quantity");
        line.Property(x => x.BoundarySamplesPassedQuantity).HasColumnName("boundary_samples_passed_quantity");
        line.Property(x => x.StartBoxCount).HasColumnName("start_box_count");
        line.Property(x => x.EndBoxCount).HasColumnName("end_box_count");
        line.Property(x => x.ProductionUserId).HasColumnName("production_user_id");
        line.Property(x => x.QualityUserId).HasColumnName("quality_user_id");
        line.Property(x => x.ProductionInitials).HasColumnName("production_initials").HasMaxLength(2);
        line.Property(x => x.QualityInitials).HasColumnName("quality_initials").HasMaxLength(2);
        line.Property(x => x.Notes).HasColumnName("notes").HasMaxLength(4000);
        line.Property(x => x.Version).HasColumnName("xmin").IsRowVersion();
        line.HasOne(x => x.DowntimeCause).WithMany().HasForeignKey(x => x.DowntimeCauseId).OnDelete(DeleteBehavior.Restrict);
        line.HasOne(x => x.ProductionUser).WithMany().HasForeignKey(x => x.ProductionUserId).OnDelete(DeleteBehavior.SetNull);
        line.HasOne(x => x.QualityUser).WithMany().HasForeignKey(x => x.QualityUserId).OnDelete(DeleteBehavior.SetNull);
        line.HasIndex(x => new { x.SortLogId, x.Sequence }).IsUnique().HasDatabaseName("UX_sort_log_lines_sequence");
        line.HasIndex(x => x.SortLogId).IsUnique().HasFilter("stop_time_utc IS NULL").HasDatabaseName("UX_sort_log_lines_active");
        line.HasIndex(x => x.DowntimeCauseId).HasDatabaseName("IX_sort_log_lines_downtime_cause_id");
        line.HasIndex(x => x.ProductionUserId).HasDatabaseName("IX_sort_log_lines_production_user_id");
        line.HasIndex(x => x.QualityUserId).HasDatabaseName("IX_sort_log_lines_quality_user_id");

        var cause = model.Entity<SortLogDowntimeCause>();
        cause.ToTable("sort_log_downtime_causes");
        cause.HasKey(x => x.Id).HasName("PK_sort_log_downtime_causes");
        cause.Property(x => x.Id).HasColumnName("id").UseIdentityByDefaultColumn();
        cause.Property(x => x.Name).HasColumnName("name").HasMaxLength(150);
        cause.Property(x => x.NormalizedName).HasColumnName("normalized_name").HasMaxLength(150);
        cause.Property(x => x.IsActive).HasColumnName("is_active");
        cause.HasIndex(x => x.NormalizedName).IsUnique().HasDatabaseName("UX_sort_log_downtime_causes_normalized_name");
        cause.HasData(new SortLogDowntimeCause
        {
            Id = -1,
            Name = "End of Day",
            NormalizedName = "END OF DAY",
            IsActive = true
        });
    }
}
