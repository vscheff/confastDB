using System.Text.Json;
using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Backups;

internal enum BackupTrigger { Manual, Scheduled }
internal enum BackupOutcome { Pending, Running, Completed, Failed, Interrupted }
internal enum BackupAvailability { None, Available, Missing, Unavailable, Corrupted, Expired, Deleted }
internal enum BackupVerification { None, ArchiveListed, Verified, Failed }
internal enum BackupStage { Queued, CheckingDestination, DumpingDatabase, CopyingApplicationData, ValidatingArchive, Publishing, ApplyingRetention, Finished }
internal enum BackupHistorySort { Newest, Oldest, Largest, Longest }

[Flags]
internal enum BackupNotice
{
    None = 0, Failure = 1, Integrity = 2, Storage = 4, Capacity = 8, Overdue = 16, Retention = 32,
    Catalog = 64, Success = 128, Default = Failure | Integrity | Storage | Capacity | Overdue | Retention | Catalog,
    All = Default | Success
}

internal sealed class BackupSettings
{
    public int Id { get; set; } = 1;
    public Guid Revision { get; set; }
    public Guid? DeploymentId { get; set; }
    public string? Destination { get; set; }
    public Guid? DestinationId { get; set; }
    public bool Enabled { get; set; }
    public bool CatchUp { get; set; } = true;
    public bool FullVerification { get; set; } = true;
    public int Retain { get; set; } = 14;
    public long ReserveBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public List<BackupRecipient> Recipients { get; } = [];
    public string? ServerZoneId { get; set; }
    public BackupFrequency Frequency { get; set; } = BackupFrequency.Daily;
    public TimeOnly LocalTime { get; set; } = new(2, 0);
    public int Hours { get; set; } = 1;
    public int Weekdays { get; set; } = 62;
    public int MonthDay { get; set; } = 1;
    public int MonthShift { get; set; }
    public DateOnly HourAnchor { get; set; } = new(2000, 1, 1);
    public DateTimeOffset? NextDueAtUtc { get; set; }
    public DateTimeOffset? ScheduleStartedAtUtc { get; set; }
    public DateTimeOffset? LastReconciledAtUtc { get; set; }
    public string? CatalogIssue { get; set; }
    public string? RetentionIssue { get; set; }
    public string? ReconcileRequestedBy { get; set; }
    public Guid? ReconcileGeneration { get; set; }

    internal BackupSchedule Schedule
    {
        get => new(Frequency, LocalTime, Hours, Weekdays, MonthDay, MonthShift, HourAnchor);
        set => (Frequency, LocalTime, Hours, Weekdays, MonthDay, MonthShift, HourAnchor) =
            (value.Frequency, value.Time, value.Hours, value.Weekdays, value.MonthDay, value.MonthShift,
                value.HourAnchor == default ? new DateOnly(2000, 1, 1) : value.HourAnchor);
    }
}

internal sealed class BackupExecution
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public string Destination { get; set; } = "";
    public Guid DestinationId { get; set; }
    public string? DirectoryName { get; set; }
    public BackupTrigger Trigger { get; set; }
    public DateTimeOffset? ScheduledForUtc { get; set; }
    public int Attempt { get; set; }
    public Guid SettingsRevision { get; set; }
    public Guid? RequestedGeneration { get; set; }
    public string? RequestedBy { get; set; }
    public DateTimeOffset RequestedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public long? DurationMilliseconds { get; set; }
    public BackupOutcome Outcome { get; set; }
    public BackupStage Stage { get; set; }
    public BackupAvailability Availability { get; set; }
    public string? Diagnostic { get; set; }
    public string? RetentionDiagnostic { get; set; }
    public long? TotalBytes { get; set; }
    public bool Pinned { get; set; }
    public string? Label { get; set; }
    public string? Notes { get; set; }
    public Guid MetadataVersion { get; set; }
    public BackupVerification Verification { get; set; }
    public DateTimeOffset? VerifiedAtUtc { get; set; }
    public string? VerificationDiagnostic { get; set; }
    public string? VerifyRequestedBy { get; set; }
    public Guid? VerifyGeneration { get; set; }
    public DateTimeOffset? VerifyStartedAtUtc { get; set; }
    public DateTimeOffset? RemovedAtUtc { get; set; }

    internal bool IsRetired => Availability is BackupAvailability.Expired or BackupAvailability.Deleted;
}

internal sealed class BackupAuditEntry
{
    public long Id { get; set; }
    public Guid DeploymentId { get; set; }
    public Guid? ExecutionId { get; set; }
    public string? ActorUserId { get; set; }
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string Action { get; set; } = "";
    public string? Before { get; set; }
    public string? After { get; set; }
}

internal sealed class BackupRecipient
{
    public string Email { get; set; } = "";
    public BackupNotice Notices { get; set; }
}

internal sealed class BackupAlert
{
    public Guid DeploymentId { get; set; }
    public string RecipientEmail { get; set; } = "";
    public BackupNotice Kind { get; set; }
    public string? Message { get; set; }
    public string? LastNotifiedMessage { get; set; }
    public DateTimeOffset? NextDeliveryAtUtc { get; set; }
    public Guid? DeliveryClaim { get; set; }
}

internal sealed class BackupException(string code, string message) : InvalidOperationException(message)
{
    internal string Code { get; } = code;
}

internal static class BackupMapping
{
    internal static void Configure(ModelBuilder model)
    {
        var settings = model.Entity<BackupSettings>();
        settings.ToTable("backup_settings", t => t.HasCheckConstraint("ck_backup_settings",
            "id = 1 AND retain BETWEEN 1 AND 1000 AND reserve_bytes >= 0 AND hours BETWEEN 1 AND 168 " +
            "AND weekdays BETWEEN 1 AND 127 AND month_day BETWEEN 1 AND 31 AND month_shift BETWEEN -4 AND 4"));
        settings.Property(x => x.Id).ValueGeneratedNever();
        settings.Property(x => x.Revision).IsConcurrencyToken();
        settings.Property(x => x.Destination).HasMaxLength(64);
        settings.Property(x => x.ServerZoneId).HasMaxLength(128);
        settings.Ignore(x => x.Schedule);
        settings.HasMany(x => x.Recipients).WithOne().HasForeignKey("SettingsId").IsRequired();
        settings.HasData(new BackupSettings());

        var recipient = model.Entity<BackupRecipient>();
        recipient.ToTable("backup_notification_recipients", t =>
            t.HasCheckConstraint("ck_backup_recipient_notices", "notices BETWEEN 0 AND 255"));
        recipient.HasKey(x => x.Email);
        recipient.Property(x => x.Email).HasMaxLength(254);

        var executions = model.Entity<BackupExecution>();
        executions.ToTable("backup_executions", t => t.HasCheckConstraint("ck_backup_execution_trigger",
            "(trigger = 0 AND scheduled_for_utc IS NULL AND attempt = 0) OR " +
            "(trigger = 1 AND scheduled_for_utc IS NOT NULL AND attempt BETWEEN 1 AND 2)"));
        executions.Property(x => x.Id).ValueGeneratedNever();
        executions.Property(x => x.Destination).HasMaxLength(64);
        executions.Property(x => x.DirectoryName).HasMaxLength(72);
        executions.Property(x => x.Label).HasMaxLength(160);
        executions.Property(x => x.Notes).HasMaxLength(2000);
        executions.HasIndex(x => new { x.DeploymentId, x.ScheduledForUtc, x.Attempt }).IsUnique();
        executions.HasIndex(x => new { x.DeploymentId, x.RequestedAtUtc });
        executions.HasIndex(x => x.DeploymentId).IsUnique().HasFilter("outcome = 1")
            .HasDatabaseName("ux_backup_executions_running");

        var audit = model.Entity<BackupAuditEntry>();
        audit.ToTable("backup_audit");
        audit.Property(x => x.Action).HasMaxLength(80);
        audit.Property(x => x.Before).HasColumnType("jsonb");
        audit.Property(x => x.After).HasColumnType("jsonb");
        audit.HasIndex(x => new { x.DeploymentId, x.ExecutionId, x.OccurredAtUtc });

        var alert = model.Entity<BackupAlert>();
        alert.ToTable("backup_alerts");
        alert.HasKey(x => new { x.DeploymentId, x.RecipientEmail, x.Kind });
        alert.HasOne<BackupRecipient>().WithMany().HasForeignKey(x => x.RecipientEmail).OnDelete(DeleteBehavior.Cascade);

        foreach (var type in new[] { typeof(BackupSettings), typeof(BackupExecution), typeof(BackupAuditEntry), typeof(BackupRecipient), typeof(BackupAlert) })
        {
            foreach (var property in model.Entity(type).Metadata.GetProperties())
                property.SetColumnName(string.Concat(property.Name.Select((c, i) =>
                    i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString())));
        }
    }

    internal static void Audit(AppDbContext db, Guid deployment, DateTimeOffset now, string action,
        string? actor = null, Guid? id = null, object? before = null, object? after = null) =>
        db.BackupAudit.Add(new()
        {
            DeploymentId = deployment, ExecutionId = id, OccurredAtUtc = now, Action = action, ActorUserId = actor,
            Before = before is null ? null : JsonSerializer.Serialize(before),
            After = after is null ? null : JsonSerializer.Serialize(after)
        });
}
