namespace Confast.Web.Features.MorningProductionReview;

public sealed record MorningProductionReview(
    DateOnly ProductionDate,
    DateOnly? LatestPriorProductionDate,
    DateOnly? PreviousProductionDate,
    DateOnly? NextProductionDate,
    ProductionSummary Summary,
    IReadOnlyList<MachineProductionSummary> MachineResults,
    IReadOnlyList<DowntimeSummary> Downtime,
    IReadOnlyList<MachineCurrentStatus> CurrentMachines,
    IReadOnlyList<PlannedChangeover> Changeovers);

public sealed record ProductionSummary(
    long GoodQuantity,
    long FailedQuantity,
    long FeedQuantity,
    decimal ActualPph,
    decimal TargetPph,
    decimal Efficiency,
    TimeSpan ProductionTime,
    TimeSpan Downtime);

public sealed record MachineProductionSummary(
    long MachineId,
    string MachineName,
    int RunCount,
    long GoodQuantity,
    long FailedQuantity,
    decimal ActualPph,
    decimal TargetPph,
    decimal Efficiency,
    decimal? ScheduledQuantity,
    decimal? ScheduleEfficiency,
    TimeSpan ProductionTime,
    TimeSpan Downtime,
    IReadOnlyList<ProductionRunSummary> Runs);

public sealed record ProductionRunSummary(
    long SortLogId,
    string PartNumber,
    string? MoNumber,
    string? PoNumber,
    string LotNumber,
    long GoodQuantity,
    long FailedQuantity,
    decimal ActualPph,
    decimal TargetPph,
    decimal Efficiency,
    TimeSpan Runtime);

public sealed record DowntimeSummary(
    long MachineId,
    string MachineName,
    DateTimeOffset StartTimeUtc,
    DateTimeOffset EndTimeUtc,
    TimeSpan Duration,
    string Reason,
    long SortLogId,
    string PartNumber,
    string LotNumber);

public sealed record ScheduledJobSummary(
    long JobId,
    long SegmentId,
    string PartNumber,
    string? MoNumber,
    string? PoNumber,
    DateOnly? ExpectedStart);

public sealed record MachineCurrentStatus(
    long MachineId,
    string MachineName,
    bool IsIdle,
    long? SortLogId,
    string? PartNumber,
    string? MoNumber,
    string? PoNumber,
    string? LotNumber,
    DateTimeOffset? RunStartTimeUtc,
    long GoodQuantity,
    decimal? ScheduledQuantity,
    decimal? ProgressPercent,
    ScheduledJobSummary? NextJob);

public sealed record PlannedChangeover(
    DateOnly PlannedDate,
    long MachineId,
    string MachineName,
    long? PreviousJobId,
    string PreviousPartNumber,
    string? PreviousMoNumber,
    string? PreviousPoNumber,
    long NextJobId,
    string NextPartNumber,
    string? NextMoNumber,
    string? NextPoNumber,
    int ScheduleOrder);

public sealed class MorningProductionReviewException(string message) : InvalidOperationException(message);
