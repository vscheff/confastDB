namespace Confast.Web.Features.InspectionCriteria;

using Confast.Web.Features.Gages;
using Confast.Web.Features.Inspections;

public sealed class InspectionCriterion
{
    public long Id { get; set; }

    public long InspectionCriteriaRevisionId { get; set; }

    public InspectionCriteriaRevision Revision { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    public int? InspectionNumber { get; set; }

    public long? GageTypeId { get; set; }

    public GageType? GageType { get; set; }

    // A requirement may be recorded only once this revision's process is complete.
    public long? SecondaryProcessRequirementId { get; set; }

    public SecondaryProcessRequirement? SecondaryProcessRequirement { get; set; }

    // A snapshot is required because a Gage Type can be renamed after this revision is published.
    public string? InspectionMethod { get; set; }

    public string? Minimum { get; set; }

    public string? MaximumOrTolerance { get; set; }

    public string? Unit { get; set; }

    public int DisplayOrder { get; set; }

    public string? Notes { get; set; }

    public uint Version { get; set; }

    public ICollection<InspectionResult> InspectionResults { get; } = [];
}
