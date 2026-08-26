using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Gages;

namespace Confast.Web.Features.Inspections;

public sealed class InspectionResult
{
    public long Id { get; set; }

    public long InspectionId { get; set; }

    public Inspection Inspection { get; set; } = null!;

    // Included in both composite foreign keys so the database can guarantee that
    // this criterion belongs to the exact revision stored by the inspection.
    public long InspectionCriteriaRevisionId { get; set; }

    public long InspectionCriterionId { get; set; }

    public InspectionCriterion InspectionCriterion { get; set; } = null!;

    public long? GageId { get; set; }

    public Gage? Gage { get; set; }

    // Snapshot the number because a physical gage can be renamed after use.
    public string? GageNumber { get; set; }

    public string? ActualMin { get; set; }

    public string? ActualMax { get; set; }

    public uint Version { get; set; }
}
