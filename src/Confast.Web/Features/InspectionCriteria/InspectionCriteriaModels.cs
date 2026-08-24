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
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? PublishedAtUtc,
    DateTimeOffset? SupersededAtUtc,
    string? ChangeNote,
    uint Version,
    IReadOnlyList<InspectionCriterionListItem> Criteria)
{
    public bool IsDraft => PublishedAtUtc is null;
    public bool IsCurrent => PublishedAtUtc is not null && SupersededAtUtc is null;
    public string Status => IsDraft ? "Draft" : IsCurrent ? "Current" : "Historical";
}

public sealed record InspectionCriterionListItem(
    long Id,
    string Name,
    string? InspectionMethod,
    decimal? MinimumValue,
    decimal? MaximumValue,
    string? Unit,
    int DisplayOrder,
    string? Notes,
    uint Version);

public sealed class CreateInspectionCriteriaRevisionModel
{
    [Display(Name = "Change note")]
    public string? ChangeNote { get; set; }
}

public sealed class InspectionCriterionEditModel : IValidatableObject
{
    public long Id { get; set; }

    public long RevisionId { get; set; }

    [Required(ErrorMessage = "Name is required.")]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Inspection method")]
    public string? InspectionMethod { get; set; }

    [Display(Name = "Minimum value")]
    public decimal? MinimumValue { get; set; }

    [Display(Name = "Maximum value")]
    public decimal? MaximumValue { get; set; }

    public string? Unit { get; set; }

    public string? Notes { get; set; }

    public uint Version { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MinimumValue is not null && MaximumValue is not null && MinimumValue > MaximumValue)
        {
            yield return new ValidationResult(
                "Minimum value cannot be greater than maximum value.",
                [nameof(MinimumValue), nameof(MaximumValue)]);
        }
    }
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
