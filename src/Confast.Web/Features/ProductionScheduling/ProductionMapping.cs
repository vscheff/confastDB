using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.ProductionScheduling;

public static class ProductionMapping
{
    public static void Configure(ModelBuilder model)
    {
        var settings = model.Entity<ProductionSettings>();
        settings.ToTable("production_settings", t => t.HasCheckConstraint("ck_production_efficiency", "id = 1 AND efficiency_percent > 0 AND efficiency_percent <= 100"));
        settings.Property(x => x.Version).IsRowVersion();
        settings.HasData(new ProductionSettings { Id = 1, EfficiencyPercent = 91m });
        var machine = model.Entity<SortingMachine>();
        machine.ToTable("sorting_machines");
        machine.Property(x => x.Name).HasMaxLength(150);
        machine.HasIndex(x => x.Name).IsUnique();
        machine.HasMany(x => x.WorkingDays).WithOne().HasForeignKey(x => x.MachineId);
        machine.HasMany(x => x.Parts).WithOne().HasForeignKey(x => x.MachineId);
        var day = model.Entity<MachineWorkingDay>();
        day.ToTable("machine_working_days", t => t.HasCheckConstraint("ck_machine_hours", "day BETWEEN 0 AND 6 AND hours >= 0 AND hours <= 24"));
        day.HasKey(x => new { x.MachineId, x.Day });
        var rate = model.Entity<PartMachine>();
        rate.ToTable("part_machines", t => t.HasCheckConstraint("ck_machine_pph", "target_pph > 0"));
        rate.HasKey(x => new { x.MachineId, x.PartId });
        rate.HasOne(x => x.Part).WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ProductionHoliday>().ToTable("production_holidays").HasKey(x => x.Date);
        model.Entity<DowntimeReason>().ToTable("downtime_reasons");
        var down = model.Entity<MachineDowntime>();
        down.ToTable("machine_downtime", t => t.HasCheckConstraint("ck_downtime_dates", "\"end\" >= start"));
        down.HasOne<SortingMachine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        down.HasOne(x => x.Reason).WithMany().HasForeignKey(x => x.ReasonId).OnDelete(DeleteBehavior.Restrict);
        var job = model.Entity<ProductionJob>();
        job.ToTable("production_jobs", t => t.HasCheckConstraint("ck_job_quantity", "quantity > 0"));
        job.HasOne(x => x.Part).WithMany().HasForeignKey(x => x.PartId).OnDelete(DeleteBehavior.Restrict);
        job.HasMany(x => x.Segments).WithOne().HasForeignKey(x => x.JobId).OnDelete(DeleteBehavior.Restrict);
        job.HasMany(x => x.Requirements).WithOne().HasForeignKey(x => x.JobId);
        var req = model.Entity<ProductionRequirement>();
        req.ToTable("production_requirements", t => t.HasCheckConstraint("ck_requirement_quantity", "cumulative_target > 0"));
        req.HasIndex(x => new { x.JobId, x.Date }).IsUnique();
        var segment = model.Entity<ProductionSegment>();
        segment.ToTable("production_segments", t => t.HasCheckConstraint("ck_segment_quantity", "quantity >= 0 AND completed_quantity >= 0 AND completed_quantity <= quantity AND state BETWEEN 0 AND 2 AND (state <> 2 OR completed_quantity = quantity)"));
        segment.HasOne<SortingMachine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        segment.HasOne<ProductionSegment>().WithMany().HasForeignKey(x => x.PredecessorId).OnDelete(DeleteBehavior.Restrict);
        segment.HasIndex(x => x.MachineId).IsUnique().HasFilter("state = 1");
        segment.HasMany(x => x.Progress).WithOne().HasForeignKey(x => x.SegmentId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ProductionProgress>().ToTable("production_progress");
        model.Entity<ProductionProgress>().HasOne<SortingMachine>().WithMany().HasForeignKey(x => x.MachineId).OnDelete(DeleteBehavior.Restrict);
        model.Entity<ProductionAudit>().ToTable("production_audit");

        // Keep these mappings together and use the repository's snake_case convention.
        Type[] types = [typeof(ProductionSettings), typeof(SortingMachine), typeof(MachineWorkingDay),
            typeof(PartMachine), typeof(ProductionHoliday), typeof(DowntimeReason), typeof(MachineDowntime),
            typeof(ProductionJob), typeof(ProductionRequirement), typeof(ProductionSegment),
            typeof(ProductionProgress), typeof(ProductionAudit)];
        foreach (var type in types)
        foreach (var property in model.Entity(type).Metadata.GetProperties())
        {
            if (property.Name == "Version") continue;
            property.SetColumnName(System.Text.RegularExpressions.Regex.Replace(property.Name, "(?<!^)([A-Z])", "_$1").ToLowerInvariant());
            if (property.ClrType == typeof(decimal)) property.SetPrecision(24);
            if (property.ClrType == typeof(decimal)) property.SetScale(6);
        }
    }
}
