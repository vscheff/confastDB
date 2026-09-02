namespace Confast.Web.Features.Inspections;

public sealed record InspectionFlipDestination(long DefinitionId, long TargetPartId, string TargetPartNumber, bool IsCompatible, string? ValidationMessage, IReadOnlyList<InspectionFlipMappingPreview> Mappings);
public sealed record InspectionFlipMappingPreview(
    string SourceCriterion,
    string TargetCriterion,
    string? RecordedMaximum,
    string? RecordedMinimum);
public sealed record InspectionFlipPreview(long SourceInspectionId, string? SourceLotNumber, string SourcePartNumber, IReadOnlyList<InspectionFlipDestination> Destinations);
