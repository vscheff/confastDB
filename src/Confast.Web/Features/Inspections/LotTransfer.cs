namespace Confast.Web.Features.Inspections;

/// <summary>Immutable quantity movement between two inspections already linked by lineage.</summary>
public sealed class LotTransfer : IInspectionLotLineage
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
