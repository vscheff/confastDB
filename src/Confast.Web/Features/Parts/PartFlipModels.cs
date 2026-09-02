namespace Confast.Web.Features.Parts;

public sealed record PartFlipCriterionOption(
    long Id,
    string Name,
    string? Unit,
    string? Minimum,
    string? MaximumOrTolerance);
public sealed record PartFlipMappingInput(long SourceCriterionId, long TargetCriterionId);
public sealed record PartFlipDefinitionItem(long Id, long TargetPartId, string TargetPartNumber, string CustomerName, bool IsCompatible, string? ValidationMessage, IReadOnlyList<PartFlipMappingInput> Mappings);
public sealed record PartFlipConfiguration(long SourcePartId, IReadOnlyList<PartFlipCriterionOption> SourceCriteria, IReadOnlyList<PartFlipDefinitionItem> Definitions, IReadOnlyList<PartFlipTargetOption> AvailableTargets);
public sealed record PartFlipTargetOption(long Id, string PartNumber);
public enum SavePartFlipStatus { Saved, NotFound, Duplicate, Invalid }
public sealed record SavePartFlipResult(SavePartFlipStatus Status, long? DefinitionId = null, string? Message = null);
