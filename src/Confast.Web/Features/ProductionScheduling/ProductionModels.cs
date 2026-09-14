using Confast.Web.Features.Parts;

namespace Confast.Web.Features.ProductionScheduling;

public sealed class ProductionSettings
{
    public int Id { get; set; } = 1;
    public decimal EfficiencyPercent { get; set; } = 91m;
    public List<DefaultWorkingDay> DefaultWorkingDays { get; set; } = [];
    public long Revision { get; set; }
    public uint Version { get; set; }
}

public sealed class SortingMachine
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public bool UsesDefaultWorkingDays { get; set; }
    public List<MachineWorkingDay> WorkingDays { get; set; } = [];
    public List<PartMachine> Parts { get; set; } = [];
}

public sealed class DefaultWorkingDay
{
    public int SettingsId { get; set; } = 1;
    public DayOfWeek Day { get; set; }
    public decimal Hours { get; set; }
}

public sealed class MachineWorkingDay
{
    public long MachineId { get; set; }
    public DayOfWeek Day { get; set; }
    public decimal Hours { get; set; }
}

public sealed class PartMachine
{
    public long MachineId { get; set; }
    public long PartId { get; set; }
    public Part Part { get; set; } = null!;
    public decimal TargetPph { get; set; }
}

public sealed class ProductionHoliday
{
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
}

public sealed class DowntimeReason
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed class MachineDowntime
{
    public long Id { get; set; }
    public long MachineId { get; set; }
    public long ReasonId { get; set; }
    public DowntimeReason Reason { get; set; } = null!;
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public string? Notes { get; set; }
}

public sealed class ProductionJob
{
    public long Id { get; set; }
    public long PartId { get; set; }
    public Part Part { get; set; } = null!;
    public string? PoNumber { get; set; }
    public string? MoNumber { get; set; }
    public decimal Quantity { get; set; }
    public string? Notes { get; set; }
    public List<ProductionSegment> Segments { get; set; } = [];
}

public sealed class ProductionRequirement
{
    public long Id { get; set; }
    public long PartId { get; set; }
    public Part Part { get; set; } = null!;
    public DateOnly Date { get; set; }
    public decimal CumulativeTarget { get; set; }
}

public enum ProductionState { Pending, Running, Completed }

// Completing an allocation with a different actual quantity requires an explicit
// choice; it must never silently change the next allocation or purchase total.
public enum CompletionQuantityAdjustment { Exact, RebalanceNextSegment, UpdateJobQuantity }

public sealed class ProductionSegment
{
    public long Id { get; set; }
    public long JobId { get; set; }
    public long MachineId { get; set; }
    public int Sequence { get; set; }
    public decimal Quantity { get; set; }
    public decimal CompletedQuantity { get; set; }
    public ProductionState State { get; set; }
    public DateOnly? NotBefore { get; set; }
    public bool IsPinned { get; set; }
    public long? PredecessorId { get; set; }
    public DateOnly? ActualStart { get; set; }
    public DateOnly? ActualCompletion { get; set; }
    public decimal? ActualElapsedWorkingDays { get; set; }
    public DateOnly? ProgressAsOf { get; set; }
    public decimal OriginalHours { get; set; }
    public decimal OriginalTargetPph { get; set; }
    public decimal OriginalEfficiencyPercent { get; set; }
    public List<ProductionProgress> Progress { get; set; } = [];
}

// Append-only ledger entries retain both the prior and resulting cumulative totals.
// Normal checkpoints add output; explicit corrections replace the current total.
public sealed class ProductionProgress
{
    public long Id { get; set; }
    public long SegmentId { get; set; }
    public long MachineId { get; set; }
    public DateOnly AsOf { get; set; }
    public decimal PreviousQuantity { get; set; }
    public decimal CompletedQuantity { get; set; }
    public DateTime RecordedAt { get; set; }
    public string RecordedBy { get; set; } = "";
    public string? Notes { get; set; }
}

public sealed class ProductionAudit
{
    public long Id { get; set; }
    public long Revision { get; set; }
    public DateTime RecordedAt { get; set; }
    public string RecordedBy { get; set; } = "";
    public string Description { get; set; } = "";
}

public sealed class SchedulingException(string message) : InvalidOperationException(message);

public sealed record ProductionSnapshot(ProductionSettings Settings, List<SortingMachine> Machines,
    List<ProductionHoliday> Holidays, List<DowntimeReason> Reasons, List<MachineDowntime> Downtime,
    List<ProductionJob> Jobs, List<ProductionRequirement> Requirements, List<Part> Parts,
    List<ProductionStartReadiness> StartReadiness, DateOnly Horizon, bool CanEdit, bool IsAdministrator)
{
    public IEnumerable<ProductionSegment> Segments => Jobs.SelectMany(x => x.Segments);
}

public enum ProductionStartBlocker
{
    None,
    MissingPoNumber,
    NoMatchingInspection,
    InspectionNotAccepted,
    SecondaryProcessesIncomplete
}

public sealed record ProductionStartReadiness(long JobId, ProductionStartBlocker Blocker, string? AwaitingProcessName = null)
{
    public bool IsReady => Blocker == ProductionStartBlocker.None;

    public string Message => Blocker switch
    {
        ProductionStartBlocker.None => "Inspection prerequisites met.",
        ProductionStartBlocker.MissingPoNumber => "Blocked: add a PO number and complete its inspection prerequisites.",
        ProductionStartBlocker.NoMatchingInspection => "Awaiting Inspection Creation",
        ProductionStartBlocker.InspectionNotAccepted => "Awaiting Inspection Acceptance",
        ProductionStartBlocker.SecondaryProcessesIncomplete => $"Awaiting {AwaitingProcessName ?? "secondary process completion"}",
        _ => throw new ArgumentOutOfRangeException()
    };
}

public sealed record CapacitySlice(DateOnly Date, decimal Hours, decimal Quantity);
public sealed record SegmentForecast(long SegmentId, DateOnly? Start, DateOnly? Finish,
    decimal TargetPph, decimal EffectivePph, decimal RequiredHours, decimal RemainingHours,
    decimal DayHours, decimal ElapsedDays, bool StaleProgress, string? Error, List<CapacitySlice> Capacity);
public sealed record RequirementForecast(long RequirementId, DateOnly? ExpectedDate, bool Met, string Message);
public sealed record OptimizationPreview(long Revision, long MachineId, DateOnly Horizon,
    List<long> Order, List<SegmentForecast> Forecasts, List<RequirementForecast> Requirements, string Explanation);
