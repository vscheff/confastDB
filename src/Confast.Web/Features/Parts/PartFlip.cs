using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;

namespace Confast.Web.Features.Parts;

/// <summary>Administrator-configured, directional permission to create an inspection for another part.</summary>
public sealed class PartFlipDefinition
{
    public long Id { get; set; }
    public long SourcePartId { get; set; }
    public Part SourcePart { get; set; } = null!;
    public long TargetPartId { get; set; }
    public Part TargetPart { get; set; } = null!;
    public ICollection<PartFlipCriterionMapping> CriterionMappings { get; } = [];
    public ICollection<LotFlip> LotFlips { get; } = [];
}

/// <summary>Maps criteria from the configured source and target revisions; it is deliberately revision-specific.</summary>
public sealed class PartFlipCriterionMapping
{
    public long Id { get; set; }
    public long PartFlipDefinitionId { get; set; }
    public PartFlipDefinition PartFlipDefinition { get; set; } = null!;
    public long SourceCriterionId { get; set; }
    public InspectionCriterion SourceCriterion { get; set; } = null!;
    public long TargetCriterionId { get; set; }
    public InspectionCriterion TargetCriterion { get; set; } = null!;
}

/// <summary>Immutable lineage: a flip never replaces or modifies its source lot.</summary>
public sealed class LotFlip : IInspectionLotLineage
{
    public long Id { get; set; }
    public long SourceInspectionId { get; set; }
    public Inspection SourceInspection { get; set; } = null!;
    public long DestinationInspectionId { get; set; }
    public Inspection DestinationInspection { get; set; } = null!;
    public long PartFlipDefinitionId { get; set; }
    public PartFlipDefinition PartFlipDefinition { get; set; } = null!;
    public string? PerformedByUserId { get; set; }
    public DateTimeOffset PerformedAtUtc { get; set; }
    // Flips recorded before lineage history did not capture the moved quantity.
    public int? QuantityMoved { get; set; }
}
