using Confast.Web.Features.InspectionCriteria;

namespace Confast.Web.Features.Inspections;

public sealed class InspectionCertification
{
    public long Id { get; set; }

    public long InspectionId { get; set; }

    public Inspection Inspection { get; set; } = null!;

    public long CertificationTypeId { get; set; }

    public CertificationType CertificationType { get; set; } = null!;

    public string CertificationTypeName { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public uint Version { get; set; }

    public ICollection<CertificationDocument> Documents { get; } = [];
}
