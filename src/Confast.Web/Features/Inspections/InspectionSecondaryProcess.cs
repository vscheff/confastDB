using Confast.Web.Features.InspectionCriteria;

namespace Confast.Web.Features.Inspections;

public sealed class InspectionSecondaryProcess
{
    public long Id { get; set; }

    public long InspectionId { get; set; }

    public Inspection Inspection { get; set; } = null!;

    public long InspectionCriteriaRevisionId { get; set; }

    public long SecondaryProcessRequirementId { get; set; }

    public SecondaryProcessRequirement SecondaryProcessRequirement { get; set; } = null!;

    public string ProcessName { get; set; } = string.Empty;

    public string? Specification { get; set; }

    public string? PurchaseOrderNumber { get; set; }

    public bool IsComplete { get; set; }

    public uint Version { get; set; }
}
