namespace Confast.Web.Features.InspectionCriteria;

public enum CertificationRequirementLevel
{
    Required = 1,
    Optional = 2
}

public sealed class RevisionCertificationRequirement
{
    public long Id { get; set; }

    public long InspectionCriteriaRevisionId { get; set; }

    public InspectionCriteriaRevision InspectionCriteriaRevision { get; set; } = null!;

    public long CertificationTypeId { get; set; }

    public CertificationType CertificationType { get; set; } = null!;

    public string CertificationTypeName { get; set; } = string.Empty;

    public CertificationRequirementLevel RequirementLevel { get; set; }

    public string? Notes { get; set; }

    public uint Version { get; set; }
}
