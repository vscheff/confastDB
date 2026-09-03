using System.Data;
using Confast.Web.Data;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Parts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Features.Inspections;

public sealed class InspectionService
{
    private const string UniqueLotNumberConstraint = "UX_inspections_lot_number";

    private readonly IDbContextFactory<AppDbContext> contextFactory;
    private readonly CertificationPreviewRenderer? certificationPreviewRenderer;
    private readonly ICurrentUser? currentUser;

    public InspectionService(IDbContextFactory<AppDbContext> contextFactory)
    {
        this.contextFactory = contextFactory;
    }

    public InspectionService(
        IDbContextFactory<AppDbContext> contextFactory,
        CertificationPreviewRenderer certificationPreviewRenderer)
        : this(contextFactory)
    {
        this.certificationPreviewRenderer = certificationPreviewRenderer;
    }

    public InspectionService(
        IDbContextFactory<AppDbContext> contextFactory,
        CertificationPreviewRenderer certificationPreviewRenderer,
        ICurrentUser currentUser)
        : this(contextFactory, certificationPreviewRenderer)
    {
        this.currentUser = currentUser;
    }

    public const long MaximumCertificationDocumentBytes = 25 * 1024 * 1024;

    public async Task<IReadOnlyList<InspectionListItem>> GetInspectionsAsync(
        CancellationToken cancellationToken = default)
        => await GetInspectionsAsyncCore(null, cancellationToken);

    public async Task<IReadOnlyList<InspectionListItem>> GetInspectionsAsync(
        IReadOnlyCollection<long> inspectionIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inspectionIds);
        return await GetInspectionsAsyncCore(inspectionIds, cancellationToken);
    }

    public async Task<IReadOnlyList<InspectionListItem>> FindInspectionsAsync(
        InspectionFindModel model,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Inspections.AsNoTracking().AsQueryable();
        var partNumberPattern = LikeContains(model.PartNumber);
        var lotNumberPattern = LikeContains(model.LotNumber);
        var conformancePoNumberPattern = LikeContains(model.ConformancePoNumber);
        var manufacturerLotNumberPattern = LikeContains(model.ManufacturerLotNumber);
        var inspectorPattern = LikeContains(model.Inspector);

        if (!string.IsNullOrWhiteSpace(model.PartNumber))
        {
            query = query.Where(x => EF.Functions.ILike(x.Part.PartNumber, partNumberPattern));
        }

        if (!string.IsNullOrWhiteSpace(model.LotNumber))
        {
            query = query.Where(x => x.LotNumber != null && EF.Functions.ILike(x.LotNumber, lotNumberPattern));
        }

        if (!string.IsNullOrWhiteSpace(model.ConformancePoNumber))
        {
            query = query.Where(x => x.ConformancePoNumber != null && EF.Functions.ILike(x.ConformancePoNumber, conformancePoNumberPattern));
        }

        if (!string.IsNullOrWhiteSpace(model.ManufacturerLotNumber))
        {
            query = query.Where(x => x.ManufacturerLotNumber != null && EF.Functions.ILike(x.ManufacturerLotNumber, manufacturerLotNumberPattern));
        }

        if (!string.IsNullOrWhiteSpace(model.Inspector))
        {
            query = query.Where(x => x.Inspector != null && EF.Functions.ILike(x.Inspector, inspectorPattern));
        }

        if (model.DateReceived is DateOnly dateReceived)
        {
            query = query.Where(x => x.DateReceived == dateReceived);
        }

        if (model.InspectionDate is DateOnly inspectionDate)
        {
            query = query.Where(x => x.InspectionDate == inspectionDate);
        }

        if (model.QuantityReceived is int quantityReceived)
        {
            query = query.Where(x => x.QuantityReceived == quantityReceived);
        }

        if (model.QuantityInspected is int quantityInspected)
        {
            query = query.Where(x => x.QuantityInspected == quantityInspected);
        }

        var inspectionIds = await query.Select(x => x.Id).ToArrayAsync(cancellationToken);
        return await GetInspectionsAsync(inspectionIds, cancellationToken);
    }

    private static string LikeContains(string? value) =>
        $"%{(value ?? string.Empty).Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_")}%";

    private async Task<IReadOnlyList<InspectionListItem>> GetInspectionsAsyncCore(
        IReadOnlyCollection<long>? inspectionIdFilter,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        IQueryable<Inspection> inspectionQuery = db.Inspections.AsNoTracking();
        if (inspectionIdFilter is not null)
        {
            inspectionQuery = inspectionQuery.Where(x => inspectionIdFilter.Contains(x.Id));
        }

        var inspections = await inspectionQuery
            .OrderByDescending(x => x.InspectionDate)
            .ThenByDescending(x => x.Id)
            .Select(x => new InspectionListItem(
                x.Id,
                x.Part.PartNumber,
                x.Part.Customer.Name,
                x.InspectionCriteriaRevision.RevisionNumber,
                x.LotNumber,
                x.InspectionDate,
                x.CreatedAtUtc,
                x.Version,
                x.QuantityReceived,
                false,
                false))
            .ToListAsync(cancellationToken);

        if (inspections.Count == 0)
        {
            return inspections;
        }

        var nominalToleranceSettings = await GetNominalToleranceSettingsAsync(db, cancellationToken);
        var inspectionIds = inspections.Select(x => x.Id).ToArray();
        var resultRows = await db.InspectionResults
            .AsNoTracking()
            .Where(x => inspectionIds.Contains(x.InspectionId))
            .Select(x => new InspectionListResultRow(
                x.InspectionId,
                x.GageId,
                x.ActualMin,
                x.ActualMax,
                x.DeviationApproved,
                x.InspectionCriterion.SecondaryProcessRequirementId,
                x.InspectionCriterion.Minimum,
                x.InspectionCriterion.MaximumOrTolerance))
            .ToListAsync(cancellationToken);
        var secondaryProcessRows = await db.InspectionSecondaryProcesses
            .AsNoTracking()
            .Where(x => inspectionIds.Contains(x.InspectionId))
            .Select(x => new
            {
                x.InspectionId,
                x.SecondaryProcessRequirementId,
                x.IsComplete
            })
            .ToListAsync(cancellationToken);
        var missingRequiredCertificationRows = await db.InspectionCertificationRequirements
            .AsNoTracking()
            .Where(x => inspectionIds.Contains(x.InspectionId)
                && x.RequirementLevel == CertificationRequirementLevel.Required)
            .Select(x => new
            {
                x.InspectionId,
                HasDocument = db.InspectionCertifications
                    .Where(c => c.InspectionId == x.InspectionId
                        && c.CertificationTypeId == x.CertificationTypeId)
                    .SelectMany(c => c.Documents)
                    .Any()
            })
            .ToListAsync(cancellationToken);

        var processCompletionByInspection = secondaryProcessRows
            .GroupBy(x => x.InspectionId)
            .ToDictionary(
                group => group.Key,
                group => group.ToDictionary(x => x.SecondaryProcessRequirementId, x => x.IsComplete));
        var resultStatusByInspection = resultRows
            .GroupBy(x => x.InspectionId)
            .ToDictionary(
                group => group.Key,
                group => group.Any()
                    && group.All(x => x.SecondaryProcessRequirementId is long requirementId
                        && processCompletionByInspection.TryGetValue(group.Key, out var processes)
                        && processes.TryGetValue(requirementId, out var isComplete)
                        && !isComplete
                        || x.GageId is not null
                            && InspectionResultEvaluator.Evaluate(
                                x.Minimum,
                                x.MaximumOrTolerance,
                                x.ActualMin,
                                x.ActualMax,
                                x.DeviationApproved,
                                nominalToleranceSettings.ToleranceFloor,
                                nominalToleranceSettings.LargeDimensionDivisor) == InspectionResultEvaluation.Pass));
        var secondaryProcessStatusByInspection = secondaryProcessRows
            .GroupBy(x => x.InspectionId)
            .ToDictionary(group => group.Key, group => group.All(x => x.IsComplete));
        var hasMissingRequiredCertificationByInspection = missingRequiredCertificationRows
            .GroupBy(x => x.InspectionId)
            .ToDictionary(group => group.Key, group => group.Any(x => !x.HasDocument));

        return inspections
            .Select(inspection =>
            {
                resultStatusByInspection.TryGetValue(inspection.Id, out var accepted);
                var completed = accepted
                    && (!secondaryProcessStatusByInspection.TryGetValue(
                        inspection.Id,
                        out var processesComplete) || processesComplete)
                    && (!hasMissingRequiredCertificationByInspection.TryGetValue(
                        inspection.Id,
                        out var missingCertification) || !missingCertification);
                return inspection with { Accepted = accepted, Completed = completed };
            })
            .ToList();
    }

    private sealed record InspectionListResultRow(
        long InspectionId,
        long? GageId,
        string? ActualMin,
        string? ActualMax,
        bool DeviationApproved,
        long? SecondaryProcessRequirementId,
        string? Minimum,
        string? MaximumOrTolerance);

    private static async Task<NominalToleranceSettings> GetNominalToleranceSettingsAsync(
        AppDbContext db,
        CancellationToken cancellationToken) =>
        await db.NominalToleranceSettings.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == 1, cancellationToken)
        ?? new NominalToleranceSettings();

    public async Task<IReadOnlyList<InspectionPartOption>> GetPartOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Parts
            .AsNoTracking()
            .Where(x => x.InspectionCriteriaRevisions.Any(r =>
                r.PublishedAtUtc != null && r.SupersededAtUtc == null))
            .OrderBy(x => x.Customer.Name)
            .ThenBy(x => x.PartNumber)
            .Select(x => new InspectionPartOption(x.Id, x.PartNumber, x.Customer.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CertificationPackageLotOption>> GetCertificationPackageLotOptionsAsync(
        long activeInspectionId,
        long? destinationPlantId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var activeInspection = await db.Inspections
            .AsNoTracking()
            .Where(x => x.Id == activeInspectionId)
            .Select(x => new { x.Part.CustomerId, x.PartId })
            .SingleOrDefaultAsync(cancellationToken);

        if (activeInspection is null)
        {
            return [];
        }

        var activePartPlantIds = await db.PartPlants
            .AsNoTracking()
            .Where(x => x.PartId == activeInspection.PartId)
            .Select(x => x.PlantId)
            .ToArrayAsync(cancellationToken);

        if (destinationPlantId is not null && !activePartPlantIds.Contains(destinationPlantId.Value))
        {
            return [];
        }

        var eligiblePlantIds = destinationPlantId is null
            ? activePartPlantIds
            : [destinationPlantId.Value];

        var lots = await db.Inspections
            .AsNoTracking()
            .Where(x => x.Part.CustomerId == activeInspection.CustomerId
                && x.Part.PartPlants.Any(partPlant => eligiblePlantIds.Contains(partPlant.PlantId)))
            .OrderByDescending(x => x.InspectionDate)
            .ThenByDescending(x => x.Id)
            .Select(x => new
            {
                x.Id,
                x.LotNumber,
                PartNumber = x.Part.PartNumber,
                x.InspectionDate
            })
            .ToListAsync(cancellationToken);

        var statusByInspectionId = (await GetInspectionsAsyncCore(
                lots.Select(x => x.Id).ToArray(),
                cancellationToken))
            .ToDictionary(x => x.Id);

        return lots.Where(x => statusByInspectionId.TryGetValue(x.Id, out var status) && status.Accepted)
            .Select(x => new CertificationPackageLotOption(
                x.Id,
                x.LotNumber,
                x.PartNumber,
                x.InspectionDate,
                statusByInspectionId[x.Id].Completed))
            .ToList();
    }

    public async Task<IReadOnlyList<CertificationPackagePlantOption>> GetCertificationPackagePlantOptionsAsync(
        long inspectionId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.PartPlants
            .AsNoTracking()
            .Where(x => x.Part.Inspections.Any(inspection => inspection.Id == inspectionId))
            .OrderBy(x => x.Plant.Name)
            .ThenBy(x => x.PlantId)
            .Select(x => new CertificationPackagePlantOption(x.PlantId, x.Plant.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<InspectionDeleteModel?> GetInspectionForDeleteAsync(
        long inspectionId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Inspections
            .AsNoTracking()
            .Where(x => x.Id == inspectionId)
            .Select(x => new InspectionDeleteModel(
                x.Id,
                x.Part.PartNumber,
                x.Part.Customer.Name,
                x.LotNumber,
                x.InspectionDate,
                x.Version))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<InspectionOperationResult> CreateInspectionAsync(
        CreateInspectionModel model,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(model);
        if (validationError is not null)
        {
            return new InspectionOperationResult(
                InspectionOperationStatus.ValidationFailed,
                Message: validationError);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var lotNumber = NormalizeOptionalText(model.LotNumber);
        var existingLotInspectionId = lotNumber is null
            ? null
            : await db.Inspections
                .Where(x => x.LotNumber == lotNumber)
                .Select(x => (long?)x.Id)
                .SingleOrDefaultAsync(cancellationToken);
        if (existingLotInspectionId is not null)
        {
            return DuplicateLotNumberResult(relatedInspectionId: existingLotInspectionId);
        }

        var revision = await db.InspectionCriteriaRevisions
            .FromSqlInterpolated($$"""
                SELECT r.*, r.xmin
                FROM inspection_criteria_revisions AS r
                WHERE r.part_id = {{model.PartId}}
                    AND r.published_at_utc IS NOT NULL
                    AND r.superseded_at_utc IS NULL
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);

        if (revision is null)
        {
            return new InspectionOperationResult(InspectionOperationStatus.NoCurrentRevision);
        }

        await db.Entry(revision).Collection(x => x.Criteria).LoadAsync(cancellationToken);
        await db.Entry(revision)
            .Collection(x => x.SecondaryProcessRequirements)
            .Query()
            .Include(x => x.SecondaryProcessType)
            .LoadAsync(cancellationToken);
        await db.Entry(revision)
            .Collection(x => x.CertificationRequirements)
            .LoadAsync(cancellationToken);

        var criterionGageTypeIds = revision.Criteria
            .Where(x => x.GageTypeId is not null)
            .Select(x => x.GageTypeId!.Value)
            .Distinct()
            .ToArray();
        var activeGages = await db.Gages
            .AsNoTracking()
            .Where(x => x.IsActive && criterionGageTypeIds.Contains(x.GageTypeId))
        .Select(x => new InspectorCaliper(x.Id, x.GageTypeId, x.GageNumber))
            .ToListAsync(cancellationToken);
        var soleActiveGagesByType = activeGages
            .GroupBy(x => x.GageTypeId)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());
        var digitalCaliperTypeIds = await db.GageTypes
            .AsNoTracking()
            .Where(x => criterionGageTypeIds.Contains(x.Id)
                && EF.Functions.ILike(x.Name, "Digital Caliper%"))
            .Select(x => x.Id)
            .ToHashSetAsync(cancellationToken);
        var inspectorName = NormalizeOptionalText(model.Inspector);
        var inspectorCaliper = await GetInspectorCaliperAsync(
            db,
            inspectorName,
            cancellationToken);

        var inspection = new Inspection
        {
            PartId = model.PartId,
            InspectionCriteriaRevisionId = revision.Id,
            LotNumber = lotNumber,
            ConformancePoNumber = NormalizeOptionalText(model.ConformancePoNumber),
            ManufacturerLotNumber = NormalizeOptionalText(model.ManufacturerLotNumber),
            DateReceived = model.DateReceived,
            QuantityReceived = model.QuantityReceived,
            QuantityInspected = InspectionSamplingPlan.GetQuantityInspected(model.QuantityReceived)
                ?? model.QuantityInspected,
            Inspector = inspectorName,
            InspectionDate = model.InspectionDate!.Value
        };

        foreach (var criterion in revision.Criteria)
        {
            var isDigitalCaliperRequirement = criterion.GageTypeId is long gageTypeId
                && digitalCaliperTypeIds.Contains(gageTypeId);
            var selectedGage = isDigitalCaliperRequirement
                ? inspectorCaliper is not null
                    && criterion.GageTypeId == inspectorCaliper.GageTypeId
                        ? inspectorCaliper
                        : null
                : soleActiveGagesByType.GetValueOrDefault(criterion.GageTypeId ?? 0);

            inspection.Results.Add(new InspectionResult
            {
                InspectionCriteriaRevisionId = revision.Id,
                InspectionCriterionId = criterion.Id,
                GageId = selectedGage?.Id,
                GageNumber = selectedGage?.GageNumber
            });
        }

        foreach (var requirement in revision.SecondaryProcessRequirements.OrderBy(x => x.Id))
        {
            inspection.SecondaryProcesses.Add(new InspectionSecondaryProcess
            {
                InspectionCriteriaRevisionId = revision.Id,
                SecondaryProcessRequirementId = requirement.Id,
                ProcessName = requirement.SecondaryProcessType.Name,
                Specification = requirement.Specification,
                IsComplete = false
            });
        }

        foreach (var requirement in revision.CertificationRequirements)
        {
            inspection.CertificationRequirements.Add(new InspectionCertificationRequirement
            {
                CertificationTypeId = requirement.CertificationTypeId,
                CertificationTypeName = requirement.CertificationTypeName,
                RequirementLevel = requirement.RequirementLevel,
                Notes = requirement.Notes
            });
        }

        db.Inspections.Add(inspection);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new InspectionOperationResult(
                InspectionOperationStatus.Succeeded,
                inspection.Id);
        }
        catch (DbUpdateException exception) when (HasPostgresError(
            exception,
            PostgresErrorCodes.UniqueViolation,
            UniqueLotNumberConstraint))
        {
            return DuplicateLotNumberResult(
                relatedInspectionId: await FindInspectionIdByLotNumberAsync(lotNumber, cancellationToken));
        }
        catch (DbUpdateException exception) when (IsIntegrityOrSerializationConflict(exception))
        {
            return new InspectionOperationResult(InspectionOperationStatus.Conflict);
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            return new InspectionOperationResult(InspectionOperationStatus.Conflict);
        }
    }

    public async Task<InspectionOperationResult> DuplicateInspectionAsync(
        long inspectionId,
        int quantityToMove,
        string? newLotNumber,
        CancellationToken cancellationToken = default)
    {
        var lotNumber = NormalizeOptionalText(newLotNumber);
        if (lotNumber is null)
        {
            return new InspectionOperationResult(
                InspectionOperationStatus.ValidationFailed,
                Message: "Lot number is required for the duplicated inspection.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var source = await db.Inspections
            .AsSplitQuery()
            .Include(x => x.Results)
            .Include(x => x.SecondaryProcesses)
            .Include(x => x.CertificationRequirements)
            .Include(x => x.Certifications)
                .ThenInclude(x => x.Documents)
            .SingleOrDefaultAsync(x => x.Id == inspectionId, cancellationToken);
        if (source is null)
        {
            return new InspectionOperationResult(InspectionOperationStatus.NotFound);
        }

        if (source.QuantityReceived is not int quantityReceived)
        {
            return new InspectionOperationResult(
                InspectionOperationStatus.ValidationFailed,
                Message: "Quantity received is required before an inspection can be split.");
        }

        if (quantityToMove <= 0 || quantityToMove >= quantityReceived)
        {
            return new InspectionOperationResult(
                InspectionOperationStatus.ValidationFailed,
                Message: $"Quantity to move must be greater than zero and less than the {quantityReceived:N0} received for this lot.");
        }

        // A duplicate deliberately stays pinned to the source inspection's criteria
        // revision. Choosing today's revision would make this a new inspection, not a copy.
        var duplicate = new Inspection
        {
            PartId = source.PartId,
            InspectionCriteriaRevisionId = source.InspectionCriteriaRevisionId,
            LotNumber = lotNumber,
            ConformancePoNumber = source.ConformancePoNumber,
            ManufacturerLotNumber = source.ManufacturerLotNumber,
            DateReceived = source.DateReceived,
            QuantityReceived = quantityToMove,
            QuantityInspected = source.QuantityInspected,
            Inspector = source.Inspector,
            InspectorNotes = source.InspectorNotes,
            InHouseNotes = source.InHouseNotes,
            InspectionDate = source.InspectionDate
        };

        foreach (var result in source.Results)
        {
            duplicate.Results.Add(new InspectionResult
            {
                InspectionCriteriaRevisionId = result.InspectionCriteriaRevisionId,
                InspectionCriterionId = result.InspectionCriterionId,
                GageId = result.GageId,
                GageNumber = result.GageNumber,
                ActualMin = result.ActualMin,
                ActualMax = result.ActualMax,
                DeviationApproved = result.DeviationApproved
            });
        }

        foreach (var process in source.SecondaryProcesses)
        {
            duplicate.SecondaryProcesses.Add(new InspectionSecondaryProcess
            {
                InspectionCriteriaRevisionId = process.InspectionCriteriaRevisionId,
                SecondaryProcessRequirementId = process.SecondaryProcessRequirementId,
                ProcessName = process.ProcessName,
                Specification = process.Specification,
                PurchaseOrderNumber = process.PurchaseOrderNumber,
                IsComplete = process.IsComplete
            });
        }

        foreach (var requirement in source.CertificationRequirements)
        {
            duplicate.CertificationRequirements.Add(new InspectionCertificationRequirement
            {
                CertificationTypeId = requirement.CertificationTypeId,
                CertificationTypeName = requirement.CertificationTypeName,
                RequirementLevel = requirement.RequirementLevel,
                Notes = requirement.Notes
            });
        }

        foreach (var certification in source.Certifications)
        {
            var copiedCertification = new InspectionCertification
            {
                CertificationTypeId = certification.CertificationTypeId,
                CertificationTypeName = certification.CertificationTypeName,
                Description = certification.Description,
                Notes = certification.Notes
            };
            foreach (var document in certification.Documents)
            {
                copiedCertification.Documents.Add(new CertificationDocument
                {
                    OriginalFileName = document.OriginalFileName,
                    ContentType = document.ContentType,
                    Content = document.Content.ToArray(),
                    PreviewContent = document.PreviewContent?.ToArray()
                });
            }

            duplicate.Certifications.Add(copiedCertification);
        }

        source.QuantityReceived = quantityReceived - quantityToMove;
        db.Inspections.Add(duplicate);
        db.LotDuplications.Add(new LotDuplication
        {
            SourceInspection = source,
            DestinationInspection = duplicate,
            QuantityMoved = quantityToMove,
            PerformedAtUtc = DateTimeOffset.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new InspectionOperationResult(
                InspectionOperationStatus.Succeeded,
                duplicate.Id);
        }
        catch (DbUpdateException exception) when (HasPostgresError(
            exception,
            PostgresErrorCodes.UniqueViolation,
            UniqueLotNumberConstraint))
        {
            return DuplicateLotNumberResult(
                relatedInspectionId: await FindInspectionIdByLotNumberAsync(lotNumber, cancellationToken));
        }
        catch (DbUpdateException exception) when (IsIntegrityOrSerializationConflict(exception))
        {
            return new InspectionOperationResult(InspectionOperationStatus.Conflict);
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            return new InspectionOperationResult(InspectionOperationStatus.Conflict);
        }
    }

    public async Task<InspectionFlipPreview?> GetFlipPreviewAsync(long inspectionId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var source = await db.Inspections.AsNoTracking().Include(x => x.Part).Include(x => x.Results).ThenInclude(x => x.InspectionCriterion).SingleOrDefaultAsync(x => x.Id == inspectionId, cancellationToken);
        if (source is null) return null;
        var definitions = await db.PartFlipDefinitions.AsNoTracking().Where(x => x.SourcePartId == source.PartId && x.TargetPart.IsActive)
            .Include(x => x.TargetPart).Include(x => x.CriterionMappings).ThenInclude(x => x.SourceCriterion)
            .Include(x => x.CriterionMappings).ThenInclude(x => x.TargetCriterion).ToListAsync(cancellationToken);
        var destinationRows = new List<InspectionFlipDestination>();
        var sourceCriteria = source.Results.Select(x => x.InspectionCriterion).ToList();
        foreach (var definition in definitions)
        {
            var targetCriteria = await PartFlipService.CurrentCriteriaAsync(db, definition.TargetPartId, cancellationToken);
            var mappings = definition.CriterionMappings.Select(x => new PartFlipMappingInput(x.SourceCriterionId, x.TargetCriterionId)).ToList();
            var compatible = PartFlipService.ValidateMappings(sourceCriteria, targetCriteria, mappings);
            var previewMappings = definition.CriterionMappings
                .Select(x =>
                {
                    var recorded = source.Results.SingleOrDefault(
                        r => r.InspectionCriterionId == x.SourceCriterionId);
                    return new InspectionFlipMappingPreview(
                        x.SourceCriterion.Name,
                        x.TargetCriterion.Name,
                        recorded?.ActualMax,
                        recorded?.ActualMin);
                })
                .ToList();
            destinationRows.Add(new InspectionFlipDestination(
                definition.Id, definition.TargetPartId, definition.TargetPart.PartNumber,
                compatible, compatible ? null : "The configured mapping does not match this lot and the target's current criteria.", previewMappings));
        }
        return new InspectionFlipPreview(source.Id, source.LotNumber, source.Part.PartNumber, destinationRows);
    }

    public async Task<InspectionOperationResult> FlipInspectionAsync(long inspectionId, long definitionId, int quantityToMove, string? newLotNumber, CancellationToken cancellationToken = default)
    {
        var lotNumber = NormalizeOptionalText(newLotNumber);
        if (lotNumber is null) return new(InspectionOperationStatus.ValidationFailed, Message: "Lot number is required for the flipped inspection.");
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var source = await db.Inspections.AsSplitQuery()
            .Include(x => x.Part)
            .Include(x => x.Results).ThenInclude(x => x.InspectionCriterion)
            .Include(x => x.SecondaryProcesses).ThenInclude(x => x.SecondaryProcessRequirement).ThenInclude(x => x.SecondaryProcessType)
            .Include(x => x.Certifications).ThenInclude(x => x.Documents)
            .SingleOrDefaultAsync(x => x.Id == inspectionId, cancellationToken);
        if (source is null) return new(InspectionOperationStatus.NotFound);
        if (source.QuantityReceived is not int quantityReceived)
        {
            return new(InspectionOperationStatus.ValidationFailed, Message: "Quantity received is required before an inspection can be flipped.");
        }
        if (quantityToMove <= 0 || quantityToMove >= quantityReceived)
        {
            return new(InspectionOperationStatus.ValidationFailed, Message: $"Quantity to move must be greater than zero and less than the {quantityReceived:N0} received for this lot.");
        }
        var definition = await db.PartFlipDefinitions.Include(x => x.TargetPart).Include(x => x.CriterionMappings).SingleOrDefaultAsync(x => x.Id == definitionId && x.SourcePartId == source.PartId, cancellationToken);
        if (definition is null || !definition.TargetPart.IsActive) return new(InspectionOperationStatus.ValidationFailed, Message: "This flip destination is no longer available.");
        if (await db.Inspections.AnyAsync(x => x.LotNumber == lotNumber, cancellationToken)) return DuplicateLotNumberResult();
        var targetRevision = await db.InspectionCriteriaRevisions.Include(x => x.Criteria).Include(x => x.SecondaryProcessRequirements).ThenInclude(x => x.SecondaryProcessType).Include(x => x.CertificationRequirements)
            .SingleOrDefaultAsync(x => x.PartId == definition.TargetPartId && x.PublishedAtUtc != null && x.SupersededAtUtc == null, cancellationToken);
        if (targetRevision is null) return new(InspectionOperationStatus.NoCurrentRevision);
        var mappings = definition.CriterionMappings.Select(x => new PartFlipMappingInput(x.SourceCriterionId, x.TargetCriterionId)).ToList();
        if (!PartFlipService.ValidateMappings(source.Results.Select(x => x.InspectionCriterion).ToList(), targetRevision.Criteria.ToList(), mappings)) return new(InspectionOperationStatus.ValidationFailed, Message: "The flip mapping is no longer compatible with this lot and the target's current criteria.");
        var target = new Inspection { PartId = definition.TargetPartId, InspectionCriteriaRevisionId = targetRevision.Id, LotNumber = lotNumber, ConformancePoNumber = source.ConformancePoNumber, ManufacturerLotNumber = source.ManufacturerLotNumber, DateReceived = source.DateReceived, QuantityReceived = quantityToMove, QuantityInspected = source.QuantityInspected, Inspector = source.Inspector, InspectorNotes = source.InspectorNotes, InHouseNotes = source.InHouseNotes, InspectionDate = source.InspectionDate };
        var recordedByCriterion = source.Results.ToDictionary(x => x.InspectionCriterionId);
        var sourceByTarget = mappings.ToDictionary(x => x.TargetCriterionId, x => x.SourceCriterionId);
        foreach (var criterion in targetRevision.Criteria)
        {
            recordedByCriterion.TryGetValue(sourceByTarget[criterion.Id], out var recorded);
            target.Results.Add(new InspectionResult
            {
                InspectionCriteriaRevisionId = targetRevision.Id,
                InspectionCriterionId = criterion.Id,
                GageId = recorded?.GageId,
                GageNumber = recorded?.GageNumber,
                ActualMin = recorded?.ActualMin,
                ActualMax = recorded?.ActualMax,
                DeviationApproved = recorded?.DeviationApproved ?? false
            });
        }
        foreach (var requirement in targetRevision.SecondaryProcessRequirements)
        {
            var sourceProcess = source.SecondaryProcesses
                .SingleOrDefault(x => x.SecondaryProcessRequirement.SecondaryProcessTypeId == requirement.SecondaryProcessTypeId);
            target.SecondaryProcesses.Add(new InspectionSecondaryProcess
            {
                InspectionCriteriaRevisionId = targetRevision.Id,
                SecondaryProcessRequirementId = requirement.Id,
                ProcessName = requirement.SecondaryProcessType.Name,
                Specification = requirement.Specification,
                PurchaseOrderNumber = sourceProcess?.PurchaseOrderNumber,
                IsComplete = sourceProcess?.IsComplete ?? false
            });
        }
        foreach (var requirement in targetRevision.CertificationRequirements) target.CertificationRequirements.Add(new InspectionCertificationRequirement { CertificationTypeId = requirement.CertificationTypeId, CertificationTypeName = requirement.CertificationTypeName, RequirementLevel = requirement.RequirementLevel, Notes = requirement.Notes });
        foreach (var certification in source.Certifications)
        {
            var copiedCertification = new InspectionCertification
            {
                CertificationTypeId = certification.CertificationTypeId,
                CertificationTypeName = certification.CertificationTypeName,
                Description = certification.Description,
                Notes = certification.Notes
            };
            foreach (var document in certification.Documents)
            {
                copiedCertification.Documents.Add(new CertificationDocument
                {
                    OriginalFileName = document.OriginalFileName,
                    ContentType = document.ContentType,
                    Content = document.Content.ToArray(),
                    PreviewContent = document.PreviewContent?.ToArray()
                });
            }

            target.Certifications.Add(copiedCertification);
        }
        db.Inspections.Add(target);
        source.QuantityReceived = quantityReceived - quantityToMove;
        db.LotFlips.Add(new LotFlip { SourceInspection = source, DestinationInspection = target, PartFlipDefinitionId = definition.Id, PerformedByUserId = currentUser is null ? null : await currentUser.GetUserIdAsync(), PerformedAtUtc = DateTimeOffset.UtcNow, QuantityMoved = quantityToMove });
        try { await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken); return new(InspectionOperationStatus.Succeeded, target.Id); }
        catch (DbUpdateException exception) when (HasPostgresError(exception, PostgresErrorCodes.UniqueViolation, UniqueLotNumberConstraint)) { return DuplicateLotNumberResult(await FindInspectionIdByLotNumberAsync(lotNumber, cancellationToken)); }
        catch (DbUpdateException exception) when (IsIntegrityOrSerializationConflict(exception)) { return new(InspectionOperationStatus.Conflict); }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure) { return new(InspectionOperationStatus.Conflict); }
    }

    private static async Task<InspectorCaliper?> GetInspectorCaliperAsync(
        AppDbContext db,
        string? inspectorName,
        CancellationToken cancellationToken)
    {
        if (inspectorName is null)
        {
            return null;
        }

        return await db.Users
            .AsNoTracking()
            .Where(x => x.DisplayName == inspectorName
                && x.Caliper != null
                && x.Caliper.IsActive
                && EF.Functions.ILike(x.Caliper.GageType.Name, "Digital Caliper%"))
            .OrderBy(x => x.UserName)
            .Select(x => new InspectorCaliper(
                x.Caliper!.Id,
                x.Caliper.GageTypeId,
                x.Caliper.GageNumber))
            .FirstOrDefaultAsync(cancellationToken);
    }

    private sealed record InspectorCaliper(long Id, long GageTypeId, string GageNumber);

    public async Task<InspectionOperationResult> UndoLineageOperationAsync(
        long inspectionId,
        InspectionLineageOperation operation,
        long lineageId,
        bool confirmDestinationDeletion,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        IInspectionLotLineage? lineage = operation switch
        {
            InspectionLineageOperation.Flip => await db.LotFlips
                .Include(x => x.SourceInspection)
                .Include(x => x.DestinationInspection)
                .SingleOrDefaultAsync(x => x.Id == lineageId, cancellationToken),
            InspectionLineageOperation.Duplicate => await db.LotDuplications
                .Include(x => x.SourceInspection)
                .Include(x => x.DestinationInspection)
                .SingleOrDefaultAsync(x => x.Id == lineageId, cancellationToken),
            InspectionLineageOperation.Transfer => await db.LotTransfers
                .Include(x => x.SourceInspection)
                .Include(x => x.DestinationInspection)
                .SingleOrDefaultAsync(x => x.Id == lineageId, cancellationToken),
            _ => null
        };

        if (lineage is null || (lineage.SourceInspectionId != inspectionId && lineage.DestinationInspectionId != inspectionId))
        {
            return new(InspectionOperationStatus.NotFound);
        }

        var quantityMoved = lineage.QuantityMoved;
        if (quantityMoved is null)
        {
            return new(InspectionOperationStatus.ValidationFailed, Message: "This older flip cannot be undone because its moved quantity was not recorded.");
        }

        var latestFlip = await db.LotFlips
            .Where(x => (x.SourceInspectionId == lineage.SourceInspectionId || x.DestinationInspectionId == lineage.DestinationInspectionId)
                && (x.PerformedAtUtc > lineage.PerformedAtUtc || (x.PerformedAtUtc == lineage.PerformedAtUtc && x.Id > lineage.Id)))
            .AnyAsync(cancellationToken);
        var latestDuplication = await db.LotDuplications
            .Where(x => (x.SourceInspectionId == lineage.SourceInspectionId || x.DestinationInspectionId == lineage.DestinationInspectionId)
                && (x.PerformedAtUtc > lineage.PerformedAtUtc || (x.PerformedAtUtc == lineage.PerformedAtUtc && x.Id > lineage.Id)))
            .AnyAsync(cancellationToken);
        var latestTransfer = await db.LotTransfers
            .Where(x => (x.SourceInspectionId == lineage.SourceInspectionId || x.DestinationInspectionId == lineage.DestinationInspectionId)
                && (x.PerformedAtUtc > lineage.PerformedAtUtc || (x.PerformedAtUtc == lineage.PerformedAtUtc && x.Id > lineage.Id)))
            .AnyAsync(cancellationToken);
        if (latestFlip || latestDuplication || latestTransfer)
        {
            return new(InspectionOperationStatus.ValidationFailed, Message: "Only the most recently performed operation can be undone.");
        }

        if (lineage.SourceInspection.QuantityReceived is not int sourceQuantity)
        {
            return new(InspectionOperationStatus.ValidationFailed, Message: "The from lot no longer has a valid received quantity.");
        }

        var remainingDestinationQuantity = (lineage.DestinationInspection.QuantityReceived ?? 0) - quantityMoved.Value;
        if (remainingDestinationQuantity <= 0 && !confirmDestinationDeletion)
        {
            return new(InspectionOperationStatus.ConfirmationRequired, Message: "Undoing this operation will delete the to-lot inspection because no pieces will remain.");
        }

        lineage.SourceInspection.QuantityReceived = checked(sourceQuantity + quantityMoved.Value);
        if (remainingDestinationQuantity > 0)
        {
            lineage.DestinationInspection.QuantityReceived = remainingDestinationQuantity;
        }
        else
        {
            var destinationId = lineage.DestinationInspectionId;
            var certificationIds = db.InspectionCertifications.Where(x => x.InspectionId == destinationId).Select(x => x.Id);
            await db.CertificationDocuments.Where(x => certificationIds.Contains(x.InspectionCertificationId)).ExecuteDeleteAsync(cancellationToken);
            await db.InspectionCertifications.Where(x => x.InspectionId == destinationId).ExecuteDeleteAsync(cancellationToken);
            await db.InspectionCertificationRequirements.Where(x => x.InspectionId == destinationId).ExecuteDeleteAsync(cancellationToken);
            await db.InspectionSecondaryProcesses.Where(x => x.InspectionId == destinationId).ExecuteDeleteAsync(cancellationToken);
            await db.InspectionResults.Where(x => x.InspectionId == destinationId).ExecuteDeleteAsync(cancellationToken);
        }

        if (operation == InspectionLineageOperation.Flip)
        {
            db.LotFlips.Remove((LotFlip)lineage);
        }
        else if (operation == InspectionLineageOperation.Duplicate)
        {
            db.LotDuplications.Remove((LotDuplication)lineage);
        }
        else
        {
            db.LotTransfers.Remove((LotTransfer)lineage);
        }

        if (remainingDestinationQuantity <= 0)
        {
            db.Inspections.Remove(lineage.DestinationInspection);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(InspectionOperationStatus.Succeeded, lineage.SourceInspectionId, RelatedInspectionId: lineage.DestinationInspectionId);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(InspectionOperationStatus.Conflict);
        }
        catch (DbUpdateException exception) when (IsIntegrityOrSerializationConflict(exception))
        {
            return new(InspectionOperationStatus.Conflict);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            return new(InspectionOperationStatus.Conflict);
        }
    }

    public async Task<InspectionOperationResult> MoveAdditionalLineageQuantityAsync(
        long inspectionId,
        long fromInspectionId,
        long toInspectionId,
        int quantityToMove,
        CancellationToken cancellationToken = default)
    {
        if (quantityToMove <= 0 || fromInspectionId == toInspectionId)
        {
            return new(InspectionOperationStatus.ValidationFailed, Message: "Enter a positive quantity to move between different lots.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var related = await db.LotFlips.AnyAsync(x => (x.SourceInspectionId == fromInspectionId && x.DestinationInspectionId == toInspectionId) || (x.SourceInspectionId == toInspectionId && x.DestinationInspectionId == fromInspectionId), cancellationToken)
            || await db.LotDuplications.AnyAsync(x => (x.SourceInspectionId == fromInspectionId && x.DestinationInspectionId == toInspectionId) || (x.SourceInspectionId == toInspectionId && x.DestinationInspectionId == fromInspectionId), cancellationToken)
            || await db.LotTransfers.AnyAsync(x => (x.SourceInspectionId == fromInspectionId && x.DestinationInspectionId == toInspectionId) || (x.SourceInspectionId == toInspectionId && x.DestinationInspectionId == fromInspectionId), cancellationToken);
        if (!related || (inspectionId != fromInspectionId && inspectionId != toInspectionId))
        {
            return new(InspectionOperationStatus.NotFound);
        }

        var from = await db.Inspections.FromSqlInterpolated($"SELECT i.*, i.xmin FROM inspections AS i WHERE id = {fromInspectionId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        var to = await db.Inspections.FromSqlInterpolated($"SELECT i.*, i.xmin FROM inspections AS i WHERE id = {toInspectionId} FOR UPDATE").SingleOrDefaultAsync(cancellationToken);
        if (from is null || to is null)
        {
            return new(InspectionOperationStatus.NotFound);
        }

        if (from.QuantityReceived is not int fromQuantity || quantityToMove >= fromQuantity)
        {
            return new(InspectionOperationStatus.ValidationFailed, Message: "Quantity to move must leave at least one piece in the from lot.");
        }

        from.QuantityReceived = fromQuantity - quantityToMove;
        to.QuantityReceived = checked((to.QuantityReceived ?? 0) + quantityToMove);
        db.LotTransfers.Add(new LotTransfer
        {
            SourceInspection = from,
            DestinationInspection = to,
            QuantityMoved = quantityToMove,
            PerformedAtUtc = DateTimeOffset.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(InspectionOperationStatus.Succeeded, toInspectionId);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new(InspectionOperationStatus.Conflict);
        }
        catch (DbUpdateException exception) when (IsIntegrityOrSerializationConflict(exception))
        {
            return new(InspectionOperationStatus.Conflict);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            return new(InspectionOperationStatus.Conflict);
        }
    }

    public async Task<InspectionEditModel?> GetInspectionAsync(
        long inspectionId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var model = await db.Inspections
            .AsNoTracking()
            .Where(x => x.Id == inspectionId)
            .Select(x => new InspectionEditModel
            {
                Id = x.Id,
                PartId = x.PartId,
                CustomerId = x.Part.CustomerId,
                PartNumber = x.Part.PartNumber,
                CustomerName = x.Part.Customer.Name,
                InspectionCriteriaRevisionId = x.InspectionCriteriaRevisionId,
                RevisionNumber = x.InspectionCriteriaRevision.RevisionNumber,
                PrintRevisionNumber = x.InspectionCriteriaRevision.PrintRevisionNumber,
                PartDescription = x.InspectionCriteriaRevision.PartDescription,
                SpecificationUsed = x.Part.SpecificationUsed,
                CriteriaNotes = x.InspectionCriteriaRevision.Notes,
                HasMasterPrint = x.InspectionCriteriaRevision.MasterPrintContent != null,
                MasterPrintFileName = x.InspectionCriteriaRevision.MasterPrintFileName,
                MasterPrintUploadedAtUtc = x.InspectionCriteriaRevision.MasterPrintUploadedAtUtc,
                LotNumber = x.LotNumber,
                ConformancePoNumber = x.ConformancePoNumber,
                ManufacturerLotNumber = x.ManufacturerLotNumber,
                DateReceived = x.DateReceived,
                QuantityReceived = x.QuantityReceived,
                QuantityInspected = x.QuantityInspected,
                Inspector = x.Inspector,
                InspectorNotes = x.InspectorNotes,
                InHouseNotes = x.InHouseNotes,
                InspectionDate = x.InspectionDate,
                CreatedAtUtc = x.CreatedAtUtc,
                Version = x.Version
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (model is null)
        {
            return null;
        }

        var nominalToleranceSettings = await GetNominalToleranceSettingsAsync(db, cancellationToken);
        model.Results = await db.InspectionResults
            .AsNoTracking()
            .Where(x => x.InspectionId == inspectionId)
            .OrderBy(x => x.InspectionCriterion.DisplayOrder)
            .Select(x => new InspectionResultEditModel
            {
                Id = x.Id,
                InspectionCriterionId = x.InspectionCriterionId,
                InspectionNumber = x.InspectionCriterion.InspectionNumber,
                Name = x.InspectionCriterion.Name,
                InspectionMethod = x.InspectionCriterion.InspectionMethod,
                GageTypeId = x.InspectionCriterion.GageTypeId,
                GageId = x.GageId,
                GageNumber = x.GageNumber,
                SpecifiedMinimum = x.InspectionCriterion.Minimum,
                SpecifiedMaximum = x.InspectionCriterion.MaximumOrTolerance,
                Unit = x.InspectionCriterion.Unit,
                SecondaryProcessRequirementId = x.InspectionCriterion.SecondaryProcessRequirementId,
                RequiredSecondaryProcessName = x.InspectionCriterion.SecondaryProcessRequirement == null
                    ? null
                    : x.InspectionCriterion.SecondaryProcessRequirement.SecondaryProcessType.Name,
                Notes = x.InspectionCriterion.Notes,
                ActualMin = x.ActualMin,
                ActualMax = x.ActualMax,
                DeviationApproved = x.DeviationApproved,
                Version = x.Version
            })
            .ToListAsync(cancellationToken);

        foreach (var result in model.Results)
        {
            result.NominalToleranceFloor = nominalToleranceSettings.ToleranceFloor;
            result.NominalToleranceDivisor = nominalToleranceSettings.LargeDimensionDivisor;
        }

        model.SecondaryProcesses = await db.InspectionSecondaryProcesses
            .AsNoTracking()
            .Where(x => x.InspectionId == inspectionId)
            .OrderBy(x => x.SecondaryProcessRequirementId)
            .Select(x => new InspectionSecondaryProcessEditModel
            {
                Id = x.Id,
                SecondaryProcessRequirementId = x.SecondaryProcessRequirementId,
                ProcessName = x.ProcessName,
                Specification = x.Specification,
                PurchaseOrderNumber = x.PurchaseOrderNumber,
                IsComplete = x.IsComplete,
                Version = x.Version
            })
            .ToListAsync(cancellationToken);

        var certificationTypes = await db.CertificationTypes
            .AsNoTracking()
            .Where(x => x.Name != "Inspection Sheet")
            .OrderBy(x => x.DisplayOrder)
            .Select(x => new CertificationTypeChoice(x.Id, x.Name, x.DisplayOrder))
            .ToListAsync(cancellationToken);
        var certificationRequirements = await db.InspectionCertificationRequirements
            .AsNoTracking()
            .Where(x => x.InspectionId == inspectionId)
            .ToDictionaryAsync(x => x.CertificationTypeId, cancellationToken);
        var certifications = await db.InspectionCertifications
            .AsNoTracking()
            .Where(x => x.InspectionId == inspectionId)
            .Select(x => new
            {
                x.Id,
                x.CertificationTypeId,
                x.CertificationTypeName,
                x.Description,
                x.Notes,
                Documents = x.Documents
                    .OrderBy(d => d.UploadedAtUtc)
                    .ThenBy(d => d.Id)
                    .Select(d => new CertificationDocumentListItem(
                        d.Id,
                        d.OriginalFileName,
                        d.ContentType,
                        d.UploadedAtUtc,
                        d.Version))
                    .ToList()
            })
            .ToDictionaryAsync(x => x.CertificationTypeId, cancellationToken);

        model.Certifications = certificationTypes
            .Select(type =>
            {
                certificationRequirements.TryGetValue(type.Id, out var requirement);
                certifications.TryGetValue(type.Id, out var certification);
                return new InspectionCertificationListItem
                {
                    CertificationTypeId = type.Id,
                    CertificationTypeName = requirement?.CertificationTypeName
                        ?? certification?.CertificationTypeName
                        ?? type.Name,
                    RequirementLevel = requirement?.RequirementLevel,
                    RequirementNotes = requirement?.Notes,
                    InspectionCertificationId = certification?.Id,
                    Description = certification?.Description,
                    Notes = certification?.Notes,
                    Documents = certification?.Documents ?? []
                };
            })
            .ToList();

        model.FlippedTo = await db.LotFlips.AsNoTracking()
            .Where(x => x.SourceInspectionId == inspectionId)
            .OrderBy(x => x.PerformedAtUtc)
            .Select(x => new InspectionFlipLineageItem(x.DestinationInspectionId, x.DestinationInspection.LotNumber, x.DestinationInspection.Part.PartNumber))
            .ToListAsync(cancellationToken);
        model.FlippedFrom = await db.LotFlips.AsNoTracking()
            .Where(x => x.DestinationInspectionId == inspectionId)
            .Select(x => new InspectionFlipLineageItem(x.SourceInspectionId, x.SourceInspection.LotNumber, x.SourceInspection.Part.PartNumber))
            .SingleOrDefaultAsync(cancellationToken);

        var flips = await db.LotFlips.AsNoTracking()
            .Where(x => x.SourceInspectionId == inspectionId || x.DestinationInspectionId == inspectionId)
            .Select(x => new InspectionLineageHistoryItem(
                x.Id,
                InspectionLineageOperation.Flip,
                x.PerformedAtUtc,
                x.SourceInspectionId,
                x.SourceInspection.LotNumber,
                x.DestinationInspectionId,
                x.DestinationInspection.LotNumber,
                x.QuantityMoved))
            .ToListAsync(cancellationToken);
        var duplications = await db.LotDuplications.AsNoTracking()
            .Where(x => x.SourceInspectionId == inspectionId || x.DestinationInspectionId == inspectionId)
            .Select(x => new InspectionLineageHistoryItem(
                x.Id,
                InspectionLineageOperation.Duplicate,
                x.PerformedAtUtc,
                x.SourceInspectionId,
                x.SourceInspection.LotNumber,
                x.DestinationInspectionId,
                x.DestinationInspection.LotNumber,
                x.QuantityMoved))
            .ToListAsync(cancellationToken);
        var transfers = await db.LotTransfers.AsNoTracking()
            .Where(x => x.SourceInspectionId == inspectionId || x.DestinationInspectionId == inspectionId)
            .Select(x => new InspectionLineageHistoryItem(
                x.Id,
                InspectionLineageOperation.Transfer,
                x.PerformedAtUtc,
                x.SourceInspectionId,
                x.SourceInspection.LotNumber,
                x.DestinationInspectionId,
                x.DestinationInspection.LotNumber,
                x.QuantityMoved))
            .ToListAsync(cancellationToken);
        model.LineageHistory = flips.Concat(duplications).Concat(transfers)
            .OrderByDescending(x => x.PerformedAtUtc)
            .ThenByDescending(x => x.Id)
            .Select((entry, index) => entry with { IsMostRecent = index == 0 })
            .ToList();

        var gageTypeIds = model.Results
            .Where(x => x.GageTypeId is not null)
            .Select(x => x.GageTypeId!.Value)
            .Distinct()
            .ToArray();
        var selectedGageIds = model.Results
            .Where(x => x.GageId is not null)
            .Select(x => x.GageId!.Value)
            .Distinct()
            .ToArray();
        var availableGages = await db.Gages
            .AsNoTracking()
            .Where(x => (x.IsActive && gageTypeIds.Contains(x.GageTypeId))
                || selectedGageIds.Contains(x.Id))
            .Select(x => new InspectionGageChoice(x.Id, x.GageTypeId, x.GageNumber))
            .ToListAsync(cancellationToken);

        foreach (var result in model.Results)
        {
            result.GageChoices = availableGages
                .Where(x => x.GageTypeId == result.GageTypeId)
                .Select(x => x.Id == result.GageId && result.GageNumber is not null
                    ? x with { GageNumber = result.GageNumber }
                    : x)
                .OrderBy(x => x.GageNumber, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        return model;
    }

    public async Task<InspectionOperationResult> UploadCertificationDocumentAsync(
        long inspectionId,
        long certificationTypeId,
        string fileName,
        byte[] content,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateCertificationDocument(fileName, content);
        if (validationError is not null)
        {
            return new InspectionOperationResult(
                InspectionOperationStatus.ValidationFailed,
                inspectionId,
                validationError);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.Inspections.AnyAsync(x => x.Id == inspectionId, cancellationToken))
        {
            return new InspectionOperationResult(InspectionOperationStatus.NotFound);
        }

        var certificationType = await db.CertificationTypes
            .SingleOrDefaultAsync(x => x.Id == certificationTypeId, cancellationToken);
        if (certificationType is null)
        {
            return new InspectionOperationResult(
                InspectionOperationStatus.ValidationFailed,
                inspectionId,
                "Select a valid certification type.");
        }

        var certification = await db.InspectionCertifications.SingleOrDefaultAsync(
            x => x.InspectionId == inspectionId
                && x.CertificationTypeId == certificationTypeId,
            cancellationToken);
        if (certification is null)
        {
            certification = new InspectionCertification
            {
                InspectionId = inspectionId,
                CertificationTypeId = certificationTypeId,
                CertificationTypeName = certificationType.Name,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };
            db.InspectionCertifications.Add(certification);
        }

        certification.Documents.Add(new CertificationDocument
        {
            OriginalFileName = Path.GetFileName(fileName),
            ContentType = "application/pdf",
            Content = content,
            UploadedAtUtc = DateTimeOffset.UtcNow
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new InspectionOperationResult(InspectionOperationStatus.Succeeded, inspectionId);
        }
        catch (DbUpdateException exception) when (IsIntegrityOrSerializationConflict(exception))
        {
            return new InspectionOperationResult(InspectionOperationStatus.Conflict, inspectionId);
        }
        catch (PostgresException exception) when (exception.SqlState is
            PostgresErrorCodes.ForeignKeyViolation
            or PostgresErrorCodes.RestrictViolation
            or PostgresErrorCodes.SerializationFailure)
        {
            return new InspectionOperationResult(InspectionOperationStatus.Conflict, inspectionId);
        }
    }

    public async Task<InspectionCertificationDocumentFile?> GetCertificationDocumentAsync(
        long inspectionId,
        long documentId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.CertificationDocuments
            .AsNoTracking()
            .Where(x => x.Id == documentId
                && x.InspectionCertification.InspectionId == inspectionId)
            .Select(x => new InspectionCertificationDocumentFile(
                x.OriginalFileName,
                x.ContentType,
                x.Content))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InspectionCertificationDocumentFile>> GetCertificationDocumentsForPdfAsync(
        long inspectionId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.CertificationDocuments
            .AsNoTracking()
            .Where(x => x.InspectionCertification.InspectionId == inspectionId)
            .OrderBy(x => x.InspectionCertification.CertificationType.DisplayOrder)
            .ThenBy(x => x.UploadedAtUtc)
            .ThenBy(x => x.Id)
            .Select(x => new InspectionCertificationDocumentFile(
                x.OriginalFileName,
                x.ContentType,
                x.Content))
            .ToListAsync(cancellationToken);
    }

    public async Task<InspectionCertificationDocumentFile?> GetCertificationDocumentPreviewAsync(
        long inspectionId,
        long documentId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await db.CertificationDocuments
            .SingleOrDefaultAsync(x => x.Id == documentId
                && x.InspectionCertification.InspectionId == inspectionId,
                cancellationToken);
        if (document is null)
        {
            return null;
        }

        if (document.PreviewContent is { Length: > 0 })
        {
            return new InspectionCertificationDocumentFile(
                document.OriginalFileName,
                "application/pdf",
                document.PreviewContent);
        }

        if (certificationPreviewRenderer is null)
        {
            return new InspectionCertificationDocumentFile(
                document.OriginalFileName,
                document.ContentType,
                document.Content);
        }

        var previewContent = await certificationPreviewRenderer.RenderAsync(
            document.Content,
            cancellationToken);
        if (previewContent is null)
        {
            return new InspectionCertificationDocumentFile(
                document.OriginalFileName,
                document.ContentType,
                document.Content);
        }

        document.PreviewContent = previewContent;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another request may have generated the preview first. The bytes
            // generated for this request are still safe to return.
        }
        catch (DbUpdateException exception) when (IsIntegrityOrSerializationConflict(exception))
        {
            // A concurrent request may have generated the preview first. The bytes
            // generated for this request are still safe to return.
        }

        return new InspectionCertificationDocumentFile(
            document.OriginalFileName,
            "application/pdf",
            previewContent);
    }

    public async Task<InspectionOperationResult> DeleteCertificationDocumentAsync(
        long inspectionId,
        long documentId,
        uint version,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var document = await db.CertificationDocuments.SingleOrDefaultAsync(
            x => x.Id == documentId
                && x.InspectionCertification.InspectionId == inspectionId,
            cancellationToken);
        if (document is null)
        {
            return new InspectionOperationResult(InspectionOperationStatus.NotFound, inspectionId);
        }

        db.Entry(document).Property(x => x.Version).OriginalValue = version;
        db.CertificationDocuments.Remove(document);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new InspectionOperationResult(InspectionOperationStatus.Succeeded, inspectionId);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new InspectionOperationResult(InspectionOperationStatus.Conflict, inspectionId);
        }
    }

    public async Task<InspectionOperationResult> SaveInspectionAsync(
        InspectionEditModel model,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(model);
        if (validationError is not null)
        {
            return new InspectionOperationResult(
                InspectionOperationStatus.ValidationFailed,
                model.Id,
                validationError);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var inspection = await db.Inspections
            .Include(x => x.Results)
                .ThenInclude(x => x.InspectionCriterion)
            .Include(x => x.SecondaryProcesses)
            .SingleOrDefaultAsync(x => x.Id == model.Id, cancellationToken);

        if (inspection is null)
        {
            return new InspectionOperationResult(InspectionOperationStatus.NotFound);
        }

        var lotNumber = NormalizeOptionalText(model.LotNumber);
        var existingLotInspectionId = lotNumber is null
            ? null
            : await db.Inspections
                .Where(x => x.Id != inspection.Id && x.LotNumber == lotNumber)
                .Select(x => (long?)x.Id)
                .SingleOrDefaultAsync(cancellationToken);
        if (existingLotInspectionId is not null)
        {
            return DuplicateLotNumberResult(model.Id, existingLotInspectionId);
        }

        if (model.Results.Select(x => x.Id).Distinct().Count() != model.Results.Count)
        {
            return new InspectionOperationResult(
                InspectionOperationStatus.ValidationFailed,
                model.Id,
                "The inspection contains duplicate result rows. Reload the inspection.");
        }

        var submittedResults = model.Results.ToDictionary(x => x.Id);
        if (submittedResults.Count != inspection.Results.Count
            || inspection.Results.Any(x => !submittedResults.ContainsKey(x.Id)))
        {
            return new InspectionOperationResult(
                InspectionOperationStatus.ValidationFailed,
                model.Id,
                "The inspection criteria changed unexpectedly. Reload the inspection.");
        }

        if (model.SecondaryProcesses.Select(x => x.Id).Distinct().Count()
            != model.SecondaryProcesses.Count)
        {
            return new InspectionOperationResult(
                InspectionOperationStatus.ValidationFailed,
                model.Id,
                "The inspection contains duplicate secondary-process rows. Reload the inspection.");
        }

        var submittedSecondaryProcesses = model.SecondaryProcesses.ToDictionary(x => x.Id);
        if (submittedSecondaryProcesses.Count != inspection.SecondaryProcesses.Count
            || inspection.SecondaryProcesses.Any(x =>
                !submittedSecondaryProcesses.ContainsKey(x.Id)))
        {
            return new InspectionOperationResult(
                InspectionOperationStatus.ValidationFailed,
                model.Id,
                "The secondary-process requirements changed unexpectedly. Reload the inspection.");
        }

        var submittedProcessesByRequirementId = inspection.SecondaryProcesses.ToDictionary(
            x => x.SecondaryProcessRequirementId,
            x => submittedSecondaryProcesses[x.Id]);
        var gatedResultChangedBeforeCompletion = inspection.Results.Any(result =>
            result.InspectionCriterion.SecondaryProcessRequirementId is long processRequirementId
            && submittedProcessesByRequirementId.TryGetValue(processRequirementId, out var process)
            && !process.IsComplete
            && HasRecordedResultChanged(submittedResults[result.Id], result));
        if (gatedResultChangedBeforeCompletion)
        {
            return new InspectionOperationResult(
                InspectionOperationStatus.ValidationFailed,
                model.Id,
                "Complete the secondary process before recording this requirement.");
        }

        var submittedGageIds = model.Results
            .Where(x => x.GageId is not null)
            .Select(x => x.GageId!.Value)
            .Distinct()
            .ToArray();
        var submittedGages = await db.Gages
            .Where(x => submittedGageIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        db.Entry(inspection).Property(x => x.Version).OriginalValue = model.Version;
        inspection.LotNumber = lotNumber;
        inspection.ConformancePoNumber = NormalizeOptionalText(model.ConformancePoNumber);
        inspection.ManufacturerLotNumber = NormalizeOptionalText(model.ManufacturerLotNumber);
        inspection.DateReceived = model.DateReceived;
        inspection.QuantityReceived = model.QuantityReceived;
        inspection.QuantityInspected = InspectionSamplingPlan.GetQuantityInspected(
                model.QuantityReceived)
            ?? model.QuantityInspected;
        inspection.Inspector = NormalizeOptionalText(model.Inspector);
        inspection.InspectorNotes = NormalizeOptionalText(model.InspectorNotes);
        inspection.InHouseNotes = NormalizeOptionalText(model.InHouseNotes);
        inspection.InspectionDate = model.InspectionDate!.Value;

        foreach (var result in inspection.Results)
        {
            var submitted = submittedResults[result.Id];
            if (submitted.InspectionCriterionId != result.InspectionCriterionId)
            {
                return new InspectionOperationResult(
                    InspectionOperationStatus.ValidationFailed,
                    model.Id,
                    "An inspection result does not belong to the submitted criterion.");
            }

            db.Entry(result).Property(x => x.Version).OriginalValue = submitted.Version;
            if (submitted.GageId is long submittedGageId)
            {
                if (!submittedGages.TryGetValue(submittedGageId, out var gage)
                    || gage.GageTypeId != result.InspectionCriterion.GageTypeId)
                {
                    return new InspectionOperationResult(
                        InspectionOperationStatus.ValidationFailed,
                        model.Id,
                        "The selected gage does not match the inspection method.");
                }

                if (!gage.IsActive && result.GageId != gage.Id)
                {
                    return new InspectionOperationResult(
                        InspectionOperationStatus.ValidationFailed,
                        model.Id,
                        "Select an active gage.");
                }

                if (result.GageId != gage.Id || result.GageNumber is null)
                {
                    result.GageNumber = gage.GageNumber;
                }

                result.GageId = gage.Id;
            }
            else
            {
                result.GageId = null;
                result.GageNumber = null;
            }

            var recordedMinimum = NormalizeOptionalText(submitted.ActualMin);
            var recordedMaximum = NormalizeOptionalText(submitted.ActualMax);
            if (InspectionResultEvaluator.TryNormalizePassingEntry(
                    recordedMaximum,
                    out var normalizedPassingEntry)
                || InspectionResultEvaluator.TryNormalizePassingEntry(
                    recordedMinimum,
                    out normalizedPassingEntry))
            {
                recordedMinimum = normalizedPassingEntry;
                recordedMaximum = normalizedPassingEntry;
            }

            result.ActualMin = recordedMinimum;
            result.ActualMax = recordedMaximum;
            result.DeviationApproved = submitted.DeviationApproved;
        }

        foreach (var secondaryProcess in inspection.SecondaryProcesses)
        {
            var submitted = submittedSecondaryProcesses[secondaryProcess.Id];
            if (submitted.SecondaryProcessRequirementId
                != secondaryProcess.SecondaryProcessRequirementId)
            {
                return new InspectionOperationResult(
                    InspectionOperationStatus.ValidationFailed,
                    model.Id,
                    "A secondary process does not belong to the submitted requirement.");
            }

            db.Entry(secondaryProcess).Property(x => x.Version).OriginalValue = submitted.Version;
            secondaryProcess.PurchaseOrderNumber = NormalizeOptionalText(
                submitted.PurchaseOrderNumber);
            secondaryProcess.IsComplete = submitted.IsComplete;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // Keep the caller's edit model usable for the next autosave. Npgsql
            // refreshes xmin after SaveChangesAsync, but the DbContext is about
            // to be disposed and the UI must not reload the whole form just to
            // obtain the new concurrency tokens.
            model.Version = inspection.Version;
            foreach (var result in inspection.Results)
            {
                submittedResults[result.Id].Version = result.Version;
            }

            foreach (var secondaryProcess in inspection.SecondaryProcesses)
            {
                submittedSecondaryProcesses[secondaryProcess.Id].Version = secondaryProcess.Version;
            }

            return new InspectionOperationResult(InspectionOperationStatus.Succeeded, inspection.Id);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new InspectionOperationResult(InspectionOperationStatus.Conflict, inspection.Id);
        }
        catch (DbUpdateException exception) when (HasPostgresError(
            exception,
            PostgresErrorCodes.UniqueViolation,
            UniqueLotNumberConstraint))
        {
            return DuplicateLotNumberResult(
                inspection.Id,
                await FindInspectionIdByLotNumberAsync(lotNumber, cancellationToken));
        }
        catch (DbUpdateException exception) when (IsIntegrityOrSerializationConflict(exception))
        {
            return new InspectionOperationResult(InspectionOperationStatus.Conflict, inspection.Id);
        }
    }

    public async Task<InspectionOperationResult> DeleteInspectionAsync(
        long inspectionId,
        uint version,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var inspection = await db.Inspections
            .FromSqlInterpolated($"SELECT i.*, i.xmin FROM inspections AS i WHERE id = {inspectionId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (inspection is null)
        {
            return new InspectionOperationResult(InspectionOperationStatus.NotFound);
        }

        if (inspection.Version != version)
        {
            return new InspectionOperationResult(InspectionOperationStatus.Conflict, inspectionId);
        }

        var certificationIds = db.InspectionCertifications
            .Where(x => x.InspectionId == inspectionId)
            .Select(x => x.Id);

        try
        {
            await db.LotFlips
                .Where(x => x.SourceInspectionId == inspectionId || x.DestinationInspectionId == inspectionId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.LotDuplications
                .Where(x => x.SourceInspectionId == inspectionId || x.DestinationInspectionId == inspectionId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.CertificationDocuments
                .Where(x => certificationIds.Contains(x.InspectionCertificationId))
                .ExecuteDeleteAsync(cancellationToken);
            await db.InspectionCertifications
                .Where(x => x.InspectionId == inspectionId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.InspectionCertificationRequirements
                .Where(x => x.InspectionId == inspectionId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.InspectionSecondaryProcesses
                .Where(x => x.InspectionId == inspectionId)
                .ExecuteDeleteAsync(cancellationToken);
            await db.InspectionResults
                .Where(x => x.InspectionId == inspectionId)
                .ExecuteDeleteAsync(cancellationToken);

            db.Entry(inspection).Property(x => x.Version).OriginalValue = version;
            db.Inspections.Remove(inspection);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new InspectionOperationResult(InspectionOperationStatus.Succeeded, inspectionId);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new InspectionOperationResult(InspectionOperationStatus.Conflict, inspectionId);
        }
        catch (DbUpdateException exception) when (IsIntegrityOrSerializationConflict(exception))
        {
            return new InspectionOperationResult(InspectionOperationStatus.Conflict, inspectionId);
        }
        catch (PostgresException exception) when (exception.SqlState is
            PostgresErrorCodes.ForeignKeyViolation
            or PostgresErrorCodes.RestrictViolation
            or PostgresErrorCodes.SerializationFailure)
        {
            return new InspectionOperationResult(InspectionOperationStatus.Conflict, inspectionId);
        }
    }

    private static string? Validate(CreateInspectionModel model)
    {
        if (model.PartId <= 0)
        {
            return "Part is required.";
        }

        if (model.InspectionDate is null)
        {
            return "Inspection date is required.";
        }

        var dateError = InspectionDateValidator.GetError(
            model.DateReceived,
            model.InspectionDate);
        if (dateError is not null)
        {
            return dateError;
        }

        if (model.QuantityReceived is <= 0)
        {
            return "Quantity received must be greater than zero.";
        }

        return model.QuantityInspected is <= 0
            ? "Quantity inspected must be greater than zero."
            : null;
    }

    private static string? Validate(InspectionEditModel model)
    {
        if (model.InspectionDate is null)
        {
            return "Inspection date is required.";
        }

        var dateError = InspectionDateValidator.GetError(
            model.DateReceived,
            model.InspectionDate);
        if (dateError is not null)
        {
            return dateError;
        }

        if (model.QuantityInspected is <= 0)
        {
            return "Quantity inspected must be greater than zero.";
        }

        if (model.QuantityReceived is <= 0)
        {
            return "Quantity received must be greater than zero.";
        }

        foreach (var result in model.Results)
        {
            if (InspectionResultEvaluator.IsPassingEntry(result.ActualMin)
                || InspectionResultEvaluator.IsPassingEntry(result.ActualMax))
            {
                continue;
            }

            if (!InspectionResultEvaluator.IsValidMeasurementEntry(result.ActualMin))
            {
                return "Recorded minimum must be a number, Pass, or OK.";
            }

            if (!InspectionResultEvaluator.IsValidMeasurementEntry(result.ActualMax))
            {
                return "Recorded maximum must be a number, Pass, or OK.";
            }

            if (InspectionResultEvaluator.HasInvalidRecordedOrder(
                    result.ActualMin,
                    result.ActualMax))
            {
                return "Recorded minimum cannot exceed recorded maximum.";
            }
        }

        return null;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private async Task<long?> FindInspectionIdByLotNumberAsync(
        string? lotNumber,
        CancellationToken cancellationToken)
    {
        if (lotNumber is null)
        {
            return null;
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Inspections
            .AsNoTracking()
            .Where(x => x.LotNumber == lotNumber)
            .Select(x => (long?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static InspectionOperationResult DuplicateLotNumberResult(
        long? inspectionId = null,
        long? relatedInspectionId = null) =>
        new(
            InspectionOperationStatus.ValidationFailed,
            inspectionId,
            "Lot number must be unique. An inspection already uses that lot number.",
            relatedInspectionId);

    private static bool HasRecordedResultChanged(
        InspectionResultEditModel submitted,
        InspectionResult stored) =>
        submitted.GageId != stored.GageId
        || !string.Equals(
            NormalizeOptionalText(submitted.ActualMin),
            stored.ActualMin,
            StringComparison.Ordinal)
        || !string.Equals(
            NormalizeOptionalText(submitted.ActualMax),
            stored.ActualMax,
            StringComparison.Ordinal)
        || submitted.DeviationApproved != stored.DeviationApproved;

    private static string? ValidateCertificationDocument(string fileName, byte[] content)
    {
        var safeFileName = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safeFileName)
            || !string.Equals(Path.GetExtension(safeFileName), ".pdf", StringComparison.OrdinalIgnoreCase))
        {
            return "Select a PDF file.";
        }

        if (safeFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return "The PDF file name contains invalid characters.";
        }

        if (safeFileName.Length > 255)
        {
            return "The PDF file name must be 255 characters or fewer.";
        }

        if (content.Length == 0)
        {
            return "The PDF file is empty.";
        }

        if (content.LongLength > MaximumCertificationDocumentBytes)
        {
            return "The PDF must be 25 MB or smaller.";
        }

        var headerLength = Math.Min(content.Length, 1024);
        return content.AsSpan(0, headerLength).IndexOf("%PDF-"u8) < 0
            ? "The selected file does not appear to be a PDF."
            : null;
    }

    private static bool IsIntegrityOrSerializationConflict(DbUpdateException exception)
    {
        var postgresException = exception.GetBaseException() as PostgresException;
        return postgresException?.SqlState is PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.ForeignKeyViolation
            or PostgresErrorCodes.RestrictViolation
            or PostgresErrorCodes.CheckViolation
            or PostgresErrorCodes.SerializationFailure;
    }

    private static bool HasPostgresError(
        DbUpdateException exception,
        string sqlState,
        string? constraintName = null)
    {
        var postgresException = exception.GetBaseException() as PostgresException;
        return postgresException?.SqlState == sqlState
            && (constraintName is null || postgresException.ConstraintName == constraintName);
    }
}
