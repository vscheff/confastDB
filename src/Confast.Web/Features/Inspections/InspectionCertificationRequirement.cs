using Confast.Web.Features.InspectionCriteria;

namespace Confast.Web.Features.Inspections;

public sealed class InspectionCertificationRequirement
{
    public long Id { get; set; }

    public long InspectionId { get; set; }

    public Inspection Inspection { get; set; } = null!;

    public long CertificationTypeId { get; set; }

    public CertificationType CertificationType { get; set; } = null!;

    public string CertificationTypeName { get; set; } = string.Empty;

    public CertificationRequirementLevel RequirementLevel { get; set; }

    public string? Notes { get; set; }
}
