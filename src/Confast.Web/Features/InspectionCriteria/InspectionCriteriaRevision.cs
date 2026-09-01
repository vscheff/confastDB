using Confast.Web.Features.Parts;
using Confast.Web.Features.Inspections;

namespace Confast.Web.Features.InspectionCriteria;

public sealed class InspectionCriteriaRevision
{
    public long Id { get; set; }

    public long PartId { get; set; }

    public Part Part { get; set; } = null!;

    public int RevisionNumber { get; set; }

    public string? PrintRevisionNumber { get; set; }

    public string? PartDescription { get; set; }

    public string? Notes { get; set; }

    public string? MasterPrintFileName { get; set; }

    public byte[]? MasterPrintContent { get; set; }

    public DateTimeOffset? MasterPrintUploadedAtUtc { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset? PublishedAtUtc { get; set; }

    public DateTimeOffset? SupersededAtUtc { get; set; }

    public string? ChangeNote { get; set; }

    public uint Version { get; set; }

    public ICollection<InspectionCriterion> Criteria { get; } = [];

    public ICollection<SecondaryProcessRequirement> SecondaryProcessRequirements { get; } = [];

    public ICollection<RevisionCertificationRequirement> CertificationRequirements { get; } = [];

    public ICollection<Inspection> Inspections { get; } = [];
}
