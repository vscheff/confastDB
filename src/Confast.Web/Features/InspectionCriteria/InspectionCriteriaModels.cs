using System.ComponentModel.DataAnnotations;

namespace Confast.Web.Features.InspectionCriteria;

public sealed record PartInspectionCriteriaSummary(
    long PartId,
    string PartNumber,
    InspectionCriteriaRevisionSummary? CurrentRevision,
    InspectionCriteriaRevisionSummary? DraftRevision);

public sealed record InspectionCriteriaRevisionSummary(
    long Id,
    int RevisionNumber,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset? SupersededAtUtc,
    string? ChangeNote,
    int CriterionCount)
{
    public bool IsDraft => PublishedAtUtc is null;
    public bool IsCurrent => PublishedAtUtc is not null && SupersededAtUtc is null;
    public string Status => IsDraft ? "Draft" : IsCurrent ? "Current" : "Historical";
}

public sealed record InspectionCriteriaRevisionDetails(
    long Id,
    long PartId,
    string PartNumber,
    int RevisionNumber,
    string? PrintRevisionNumber,
    string? PartDescription,
    string? SpecificationUsed,
    string? Notes,
    bool HasMasterPrint,
    string? MasterPrintFileName,
    DateTimeOffset? MasterPrintUploadedAtUtc,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset? SupersededAtUtc,
    string? ChangeNote,
    uint Version,
    IReadOnlyList<InspectionCriterionListItem> Criteria,
    IReadOnlyList<SecondaryProcessRequirementListItem> SecondaryProcessRequirements,
    IReadOnlyList<RevisionCertificationRequirementListItem> CertificationRequirements)
{
    public bool IsDraft => PublishedAtUtc is null;
    public bool IsCurrent => PublishedAtUtc is not null && SupersededAtUtc is null;
    public string Status => IsDraft ? "Draft" : IsCurrent ? "Current" : "Historical";
}

public sealed record InspectionCriterionListItem(
    long Id,
    int InspectionNumber,
    string Name,
    long? GageTypeId,
    string? InspectionMethod,
    string? Minimum,
    string? MaximumOrTolerance,
    string? Unit,
    int DisplayOrder,
    string? Notes,
    uint Version);

public sealed record SecondaryProcessTypeChoice(long Id, string Name);

public sealed record CertificationTypeChoice(long Id, string Name, int DisplayOrder);

public sealed record MasterPrintFile(string FileName, byte[] Content);

public sealed record SecondaryProcessRequirementListItem(
    long Id,
    long SecondaryProcessTypeId,
    string ProcessName,
    string? Specification,
    uint Version);

public sealed record RevisionCertificationRequirementListItem(
    long Id,
    long CertificationTypeId,
    string CertificationTypeName,
    CertificationRequirementLevel RequirementLevel,
    string? Notes,
    uint Version);

public sealed class RevisionCertificationRequirementEditModel
{
    public long Id { get; set; }

    public long CertificationTypeId { get; set; }

    public string CertificationTypeName { get; set; } = string.Empty;

    public CertificationRequirementLevel? RequirementLevel { get; set; }

    public string? Notes { get; set; }

    public uint Version { get; set; }
}

public sealed class SecondaryProcessRequirementEditModel
{
    public long Id { get; set; }

    public long RevisionId { get; set; }

    [Range(1, long.MaxValue, ErrorMessage = "Process is required.")]
    [Display(Name = "Process")]
    public long? SecondaryProcessTypeId { get; set; }

    public string? Specification { get; set; }

    public uint Version { get; set; }
}

public sealed class CreateInspectionCriteriaRevisionModel
{
    [Display(Name = "Change note")]
    public string? ChangeNote { get; set; }
}

public sealed class InspectionCriteriaRevisionHeaderEditModel
{
    [Display(Name = "Print Revision Number")]
    public string? PrintRevisionNumber { get; set; }

    [Display(Name = "Part Description")]
    public string? PartDescription { get; set; }

    [Display(Name = "Spec Used")]
    public string? SpecificationUsed { get; set; }

    public string? Notes { get; set; }

    public uint Version { get; set; }
}

public sealed class InspectionCriterionEditModel
{
    public long Id { get; set; }

    public long RevisionId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Inspection number must be greater than zero.")]
    [Display(Name = "Inspection number")]
    public int InspectionNumber { get; set; }

    [Required(ErrorMessage = "Name is required.")]
    public string Name { get; set; } = string.Empty;

    [Range(1, long.MaxValue, ErrorMessage = "Inspection method is required.")]
    [Display(Name = "Inspection method")]
    public long? GageTypeId { get; set; }

    public string? Minimum { get; set; }

    [Display(Name = "Maximum / tolerance")]
    public string? MaximumOrTolerance { get; set; }

    public string? Unit { get; set; }

    public string? Notes { get; set; }

    public uint Version { get; set; }
}

public enum CriteriaOperationStatus
{
    Succeeded,
    NotFound,
    Conflict,
    DraftAlreadyExists,
    PublishedRevision,
    EmptyRevision,
    ValidationFailed
}

public sealed record CriteriaOperationResult(
    CriteriaOperationStatus Status,
    long? RevisionId = null,
    long? CriterionId = null,
    string? Message = null);
