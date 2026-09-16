using Confast.Web.Features.Identity;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.Parts;
using Confast.Web.Features.ProductionScheduling;

namespace Confast.Web.Features.ProductionTracking;

public sealed class SortLog
{
    public long Id { get; set; }
    public DateOnly ProductionDate { get; set; }
    public long MachineId { get; set; }
    public SortingMachine Machine { get; set; } = null!;
    public long PartId { get; set; }
    public Part Part { get; set; } = null!;
    public long InspectionId { get; set; }
    public Inspection Inspection { get; set; } = null!;
    public long? ProductionSegmentId { get; set; }
    public ProductionSegment? ProductionSegment { get; set; }
    public decimal TargetPphSnapshot { get; set; }
    public string? Comments { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string CreatedByUserId { get; set; } = "";
    public ApplicationUser CreatedByUser { get; set; } = null!;
    public List<SortLogLine> Lines { get; set; } = [];
    public uint Version { get; set; }
}

public sealed class SortLogLine
{
    public long Id { get; set; }
    public long SortLogId { get; set; }
    public SortLog SortLog { get; set; } = null!;
    public int Sequence { get; set; }
    public DateTimeOffset StartTimeUtc { get; set; }
    public DateTimeOffset? StopTimeUtc { get; set; }
    public long? DowntimeCauseId { get; set; }
    public SortLogDowntimeCause? DowntimeCause { get; set; }
    public long PassQuantity { get; set; }
    public long FailQuantity { get; set; }
    public long BoundarySamplesRanQuantity { get; set; }
    public long BoundarySamplesPassedQuantity { get; set; }
    public long? StartBoxCount { get; set; }
    public long? EndBoxCount { get; set; }
    public string? ProductionUserId { get; set; }
    public ApplicationUser? ProductionUser { get; set; }
    public string? QualityUserId { get; set; }
    public ApplicationUser? QualityUser { get; set; }
    public string? ProductionInitials { get; set; }
    public string? QualityInitials { get; set; }
    public string? Notes { get; set; }
    public uint Version { get; set; }
}

public sealed class SortLogDowntimeCause
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public sealed record TrackingMachineOption(long Id, string Name);
public sealed record TrackingPartOption(long Id, string PartNumber, string CustomerName);
public sealed record TrackingLotOption(long InspectionId, long PartId, string LotNumber);
public sealed record TrackingScheduledJobOption(long SegmentId, long PartId, string PartNumber,
    long InspectionId, string LotNumber, string CustomerName, ProductionState State);
public sealed record TodaySortLogItem(long Id, long MachineId, string PartNumber, string LotNumber,
    string CustomerName, long TotalPassed, bool IsRunning);

public sealed record SortLogLineItem(long Id, int Sequence, DateTimeOffset StartTimeUtc,
    DateTimeOffset? StopTimeUtc, long? DowntimeCauseId, string? DowntimeCause,
    long PassQuantity, long FailQuantity, long BoundarySamplesRanQuantity,
    long BoundarySamplesPassedQuantity, long? StartBoxCount, long? EndBoxCount,
    string? ProductionInitials, string? QualityInitials, string? Notes);

public sealed record SortLogDetail(long Id, DateOnly ProductionDate, long MachineId, string MachineName,
    long PartId, string PartNumber, long InspectionId, string LotNumber, string CustomerName,
    long? ProductionSegmentId, decimal TargetPphSnapshot, string? Comments,
    IReadOnlyList<SortLogLineItem> Lines, SortLogMetrics Metrics)
{
    public SortLogLineItem? ActiveLine => Lines.SingleOrDefault(x => x.StopTimeUtc == null);
}

public sealed record ProductionTrackingDashboard(DateOnly ProductionDate,
    IReadOnlyList<TrackingMachineOption> Machines,
    IReadOnlyList<TrackingScheduledJobOption> ScheduledJobs,
    IReadOnlyList<TrackingPartOption> EligibleParts,
    IReadOnlyList<TrackingLotOption> Lots,
    IReadOnlyList<TodaySortLogItem> TodayLogs,
    IReadOnlyList<SortLogDowntimeCause> DowntimeCauses,
    SortLogDetail? CurrentLog);

public sealed record StartSortLogLineInput(long BoundarySamplesRanQuantity,
    long BoundarySamplesPassedQuantity, long? StartBoxCount, string? ProductionInitials,
    string? QualityInitials, string? Notes);

public sealed record UpdateSortLogLineInput(DateTimeOffset StartTimeUtc, DateTimeOffset StopTimeUtc,
    long DowntimeCauseId, long PassQuantity, long FailQuantity,
    long BoundarySamplesRanQuantity, long BoundarySamplesPassedQuantity,
    long? StartBoxCount, long? EndBoxCount, string? ProductionInitials,
    string? QualityInitials, string? Notes);

public sealed class ProductionTrackingException(string message) : InvalidOperationException(message);
