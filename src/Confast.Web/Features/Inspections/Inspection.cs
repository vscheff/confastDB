using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Parts;

namespace Confast.Web.Features.Inspections;

public sealed class Inspection
{
    public long Id { get; set; }

    public long PartId { get; set; }

    public Part Part { get; set; } = null!;

    public long InspectionCriteriaRevisionId { get; set; }

    public InspectionCriteriaRevision InspectionCriteriaRevision { get; set; } = null!;

    public string? LotNumber { get; set; }

    public string? ConformancePoNumber { get; set; }

    public string? ManufacturerLotNumber { get; set; }

    public DateOnly? DateReceived { get; set; }

    public int? QuantityReceived { get; set; }

    public int? QuantityInspected { get; set; }

    public string? Inspector { get; set; }

    public string? InspectorNotes { get; set; }

    public string? InHouseNotes { get; set; }

    public DateOnly InspectionDate { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; }

    public uint Version { get; set; }

    public ICollection<InspectionResult> Results { get; } = [];

    public ICollection<InspectionSecondaryProcess> SecondaryProcesses { get; } = [];

    public ICollection<InspectionCertificationRequirement> CertificationRequirements { get; } = [];

    public ICollection<InspectionCertification> Certifications { get; } = [];
}
