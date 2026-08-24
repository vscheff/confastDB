namespace Confast.Web.Features.InspectionCriteria;

public sealed class InspectionCriterion
{
    public long Id { get; set; }

    public long InspectionCriteriaRevisionId { get; set; }

    public InspectionCriteriaRevision Revision { get; set; } = null!;

    public string Name { get; set; } = string.Empty;

    public string? InspectionMethod { get; set; }

    public decimal? MinimumValue { get; set; }

    public decimal? MaximumValue { get; set; }

    public string? Unit { get; set; }

    public int DisplayOrder { get; set; }

    public string? Notes { get; set; }

    public uint Version { get; set; }
}
