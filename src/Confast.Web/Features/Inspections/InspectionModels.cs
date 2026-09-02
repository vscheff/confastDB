using System.ComponentModel.DataAnnotations;

namespace Confast.Web.Features.Inspections;

using Confast.Web.Features.InspectionCriteria;

public sealed record InspectionPartOption(long Id, string PartNumber, string CustomerName);

public sealed record CertificationPackageLotOption(
    long InspectionId,
    string? LotNumber,
    string PartNumber,
    DateOnly InspectionDate,
    bool IsCompleted);

public sealed record CertificationPackagePlantOption(long Id, string Name);

public sealed record InspectionListItem(
    long Id,
    string PartNumber,
    string CustomerName,
    int RevisionNumber,
    string? LotNumber,
    DateOnly InspectionDate,
    DateTimeOffset CreatedAtUtc,
    uint Version,
    int? QuantityReceived,
    bool Accepted,
    bool Completed);

public sealed record InspectionDeleteModel(
    long Id,
    string PartNumber,
    string CustomerName,
    string? LotNumber,
    DateOnly InspectionDate,
    uint Version);

public sealed record InspectionGageChoice(long Id, long GageTypeId, string GageNumber);

public sealed class CreateInspectionModel : IValidatableObject
{
    [Range(1, long.MaxValue, ErrorMessage = "Part is required.")]
    public long PartId { get; set; }

    [Display(Name = "Lot number")]
    public string? LotNumber { get; set; }

    [Display(Name = "Conformance PO#")]
    public string? ConformancePoNumber { get; set; } = "PO-";

    [Display(Name = "Manufacturer's Lot#")]
    public string? ManufacturerLotNumber { get; set; }

    [Display(Name = "Date received")]
    public DateOnly? DateReceived { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Quantity received must be greater than zero.")]
    [Display(Name = "Quantity received")]
    public int? QuantityReceived { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Quantity inspected must be greater than zero.")]
    [Display(Name = "Quantity inspected")]
    public int? QuantityInspected { get; set; }

    public string? Inspector { get; set; }

    [Required(ErrorMessage = "Inspection date is required.")]
    [Display(Name = "Inspection date")]
    public DateOnly? InspectionDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        InspectionDateValidator.Validate(DateReceived, InspectionDate);
}

internal static class InspectionDateValidator
{
    public static IEnumerable<ValidationResult> Validate(
        DateOnly? dateReceived,
        DateOnly? inspectionDate)
    {
        var today = DateOnly.FromDateTime(DateTime.Today);

        if (dateReceived > today)
        {
            yield return new ValidationResult(
                "Date received cannot be in the future.",
                [nameof(CreateInspectionModel.DateReceived)]);
        }

        if (inspectionDate > today)
        {
            yield return new ValidationResult(
                "Date inspected cannot be in the future.",
                [nameof(CreateInspectionModel.InspectionDate)]);
        }

        if (dateReceived is not null
            && inspectionDate is not null
            && inspectionDate < dateReceived)
        {
            yield return new ValidationResult(
                "Date inspected cannot be before Date Received.",
                [nameof(CreateInspectionModel.InspectionDate)]);
        }
    }

    public static string? GetError(DateOnly? dateReceived, DateOnly? inspectionDate) =>
        Validate(dateReceived, inspectionDate).FirstOrDefault()?.ErrorMessage;
}

public sealed class InspectionEditModel : IValidatableObject
{
    public long Id { get; set; }

    public long PartId { get; set; }

    public long CustomerId { get; set; }

    public string PartNumber { get; set; } = string.Empty;

    public string CustomerName { get; set; } = string.Empty;

    public long InspectionCriteriaRevisionId { get; set; }

    public int RevisionNumber { get; set; }

    public string? PrintRevisionNumber { get; set; }

    public string? PartDescription { get; set; }

    public string? SpecificationUsed { get; set; }

    public string? CriteriaNotes { get; set; }

    public bool HasMasterPrint { get; set; }

    public string? MasterPrintFileName { get; set; }

    public DateTimeOffset? MasterPrintUploadedAtUtc { get; set; }

    [Display(Name = "Lot number")]
    public string? LotNumber { get; set; }

    [Display(Name = "Conformance PO#")]
    public string? ConformancePoNumber { get; set; }

    [Display(Name = "Manufacturer's Lot#")]
    public string? ManufacturerLotNumber { get; set; }

    [Display(Name = "Date received")]
    public DateOnly? DateReceived { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Quantity received must be greater than zero.")]
    [Display(Name = "Quantity received")]
    public int? QuantityReceived { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Quantity inspected must be greater than zero.")]
    [Display(Name = "Quantity inspected")]
    public int? QuantityInspected { get; set; }

    public string? Inspector { get; set; }

    [Display(Name = "Inspector's Notes")]
    public string? InspectorNotes { get; set; }

    [Display(Name = "In House Notes")]
    public string? InHouseNotes { get; set; }

    [Required(ErrorMessage = "Inspection date is required.")]
    [Display(Name = "Inspection date")]
    public DateOnly? InspectionDate { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        InspectionDateValidator.Validate(DateReceived, InspectionDate);

    public DateTimeOffset CreatedAtUtc { get; set; }

    public uint Version { get; set; }

    public List<InspectionResultEditModel> Results { get; set; } = [];

    public List<InspectionSecondaryProcessEditModel> SecondaryProcesses { get; set; } = [];

    public List<InspectionCertificationListItem> Certifications { get; set; } = [];

    public List<InspectionFlipLineageItem> FlippedTo { get; set; } = [];

    public InspectionFlipLineageItem? FlippedFrom { get; set; }

    public bool IsMissingRequiredCertifications => Certifications.Any(x => x.IsMissingRequired);

    // Used only by the protected, short-lived package print route. It is never
    // saved and lets a user explicitly produce a temporarily completed sheet.
    public bool IsTemporarilyCompletedForCertificationPackage { get; set; }

    public bool InspectionAccepted => InspectionStatusEvaluator.IsAccepted(Results, SecondaryProcesses);

    public bool InspectionCompleted => IsTemporarilyCompletedForCertificationPackage
        || InspectionStatusEvaluator.IsCompleted(Results, SecondaryProcesses, Certifications);

    public void ApplyGageSelection(InspectionResultEditModel source)
    {
        if (source.GageTypeId is null)
        {
            return;
        }

        foreach (var result in Results.Where(x => x.GageTypeId == source.GageTypeId))
        {
            // A sole gage is an automatic choice. Keep that behavior for every
            // matching result, even if a saved inspection somehow lacks it.
            if (source.GageChoices.Count == 1 || result.GageId is null)
            {
                result.GageId = source.GageId;
            }
        }
    }
}

public sealed record InspectionFlipLineageItem(long InspectionId, string? LotNumber, string PartNumber);

public sealed class InspectionCertificationListItem
{
    public long CertificationTypeId { get; set; }

    public string CertificationTypeName { get; set; } = string.Empty;

    public CertificationRequirementLevel? RequirementLevel { get; set; }

    public string? RequirementNotes { get; set; }

    public long? InspectionCertificationId { get; set; }

    public string? Description { get; set; }

    public string? Notes { get; set; }

    public List<CertificationDocumentListItem> Documents { get; set; } = [];

    public bool IsMissingRequired => InspectionCertificationStatus.IsMissingRequired(
        RequirementLevel,
        Documents.Count);

    public string StatusText => RequirementLevel switch
    {
        CertificationRequirementLevel.Required when Documents.Count > 0 => "Required · uploaded",
        CertificationRequirementLevel.Required => "Required · missing",
        CertificationRequirementLevel.Optional when Documents.Count > 0 => "Optional · uploaded",
        CertificationRequirementLevel.Optional => "Optional · no document",
        _ when Documents.Count > 0 => "Uploaded",
        _ => "Not applicable"
    };
}

public sealed record CertificationDocumentListItem(
    long Id,
    string OriginalFileName,
    string ContentType,
    DateTimeOffset UploadedAtUtc,
    uint Version);

public sealed record InspectionCertificationDocumentFile(
    string OriginalFileName,
    string ContentType,
    byte[] Content);

public static class InspectionCertificationStatus
{
    public static bool IsMissingRequired(
        CertificationRequirementLevel? requirementLevel,
        int documentCount) =>
        requirementLevel == CertificationRequirementLevel.Required && documentCount == 0;
}

public static class InspectionStatusEvaluator
{
    public static bool IsAccepted(IEnumerable<InspectionResultEditModel> results) =>
        IsAccepted(results, []);

    public static bool IsAccepted(
        IEnumerable<InspectionResultEditModel> results,
        IEnumerable<InspectionSecondaryProcessEditModel> secondaryProcesses)
    {
        var processesByRequirementId = secondaryProcesses.ToDictionary(
            x => x.SecondaryProcessRequirementId,
            x => x.IsComplete);

        return results.Any()
            && results.All(x => IsNotYetAvailable(x, processesByRequirementId)
                || x.GageId is not null
                    && x.Evaluation == InspectionResultEvaluation.Pass);
    }

    private static bool IsNotYetAvailable(
        InspectionResultEditModel result,
        IReadOnlyDictionary<long, bool> processesByRequirementId) =>
        result.SecondaryProcessRequirementId is long processRequirementId
        && processesByRequirementId.TryGetValue(processRequirementId, out var isComplete)
        && !isComplete;

    public static bool IsCompleted(
        IEnumerable<InspectionResultEditModel> results,
        IEnumerable<InspectionSecondaryProcessEditModel> secondaryProcesses,
        IEnumerable<InspectionCertificationListItem> certifications) =>
        IsAccepted(results, secondaryProcesses)
        && secondaryProcesses.All(x => x.IsComplete)
        && certifications.All(x => !x.IsMissingRequired);
}

public sealed class InspectionSecondaryProcessEditModel
{
    public long Id { get; set; }

    public long SecondaryProcessRequirementId { get; set; }

    public string ProcessName { get; set; } = string.Empty;

    public string? Specification { get; set; }

    public string? PurchaseOrderNumber { get; set; }

    public bool IsComplete { get; set; }

    public uint Version { get; set; }
}

public sealed class InspectionResultEditModel : IValidatableObject
{
    public long Id { get; set; }

    public long InspectionCriterionId { get; set; }

    public int? InspectionNumber { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? InspectionMethod { get; set; }

    public long? GageTypeId { get; set; }

    public long? GageId { get; set; }

    public string? GageNumber { get; set; }

    public IReadOnlyList<InspectionGageChoice> GageChoices { get; set; } = [];

    public string? SpecifiedMinimum { get; set; }

    public string? SpecifiedMaximum { get; set; }

    public string? Unit { get; set; }

    public long? SecondaryProcessRequirementId { get; set; }

    public string? RequiredSecondaryProcessName { get; set; }

    public string? Notes { get; set; }

    public string? ActualMin { get; set; }

    public string? ActualMax { get; set; }

    public bool DeviationApproved { get; set; }

    public decimal NominalToleranceFloor { get; set; } =
        InspectionResultEvaluator.DefaultNominalToleranceFloor;

    public decimal NominalToleranceDivisor { get; set; } =
        InspectionResultEvaluator.DefaultNominalToleranceDivisor;

    public uint Version { get; set; }

    public InspectionResultEvaluation Evaluation => InspectionResultEvaluator.Evaluate(
        SpecifiedMinimum,
        SpecifiedMaximum,
        ActualMin,
        ActualMax,
        DeviationApproved,
        NominalToleranceFloor,
        NominalToleranceDivisor);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (InspectionResultEvaluator.IsPassingEntry(ActualMin)
            || InspectionResultEvaluator.IsPassingEntry(ActualMax))
        {
            yield break;
        }

        if (!InspectionResultEvaluator.IsValidMeasurementEntry(ActualMin))
        {
            yield return new ValidationResult(
                "Recorded minimum must be a number, Pass, or OK.",
                [nameof(ActualMin)]);
        }

        if (!InspectionResultEvaluator.IsValidMeasurementEntry(ActualMax))
        {
            yield return new ValidationResult(
                "Recorded maximum must be a number, Pass, or OK.",
                [nameof(ActualMax)]);
        }

        if (InspectionResultEvaluator.HasInvalidRecordedOrder(ActualMin, ActualMax))
        {
            yield return new ValidationResult(
                "Recorded minimum cannot exceed recorded maximum.",
                [nameof(ActualMin), nameof(ActualMax)]);
        }
    }
}

public enum InspectionOperationStatus
{
    Succeeded,
    NotFound,
    Conflict,
    NoCurrentRevision,
    ValidationFailed
}

public sealed record InspectionOperationResult(
    InspectionOperationStatus Status,
    long? InspectionId = null,
    string? Message = null,
    long? RelatedInspectionId = null);
