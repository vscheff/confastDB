namespace Confast.Web.Features.Inspections;

/// <summary>Immutable lineage for an inspection split created by duplication.</summary>
public sealed class LotDuplication : IInspectionLotLineage
{
    public long Id { get; set; }
    public long SourceInspectionId { get; set; }
    public Inspection SourceInspection { get; set; } = null!;
    public long DestinationInspectionId { get; set; }
    public Inspection DestinationInspection { get; set; } = null!;
    public int QuantityMoved { get; set; }
    int? IInspectionLotLineage.QuantityMoved => QuantityMoved;
    public DateTimeOffset PerformedAtUtc { get; set; }
}
