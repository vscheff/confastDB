namespace Confast.Web.Features.InspectionCriteria;

public sealed class SecondaryProcessRequirement
{
    public long Id { get; set; }

    public long InspectionCriteriaRevisionId { get; set; }

    public InspectionCriteriaRevision InspectionCriteriaRevision { get; set; } = null!;

    public long SecondaryProcessTypeId { get; set; }

    public SecondaryProcessType SecondaryProcessType { get; set; } = null!;

    public string? Specification { get; set; }

    public uint Version { get; set; }
}
