using System.Data;
using Confast.Web.Data;
using Confast.Web.Features.InspectionCriteria;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Features.Inspections;

public sealed class InspectionService
{
    private readonly IDbContextFactory<AppDbContext> contextFactory;
    private readonly CertificationPreviewRenderer? certificationPreviewRenderer;

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

    public const long MaximumCertificationDocumentBytes = 25 * 1024 * 1024;

    public async Task<IReadOnlyList<InspectionListItem>> GetInspectionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var inspections = await db.Inspections
            .AsNoTracking()
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
                false,
                false))
            .ToListAsync(cancellationToken);

        if (inspections.Count == 0)
        {
            return inspections;
        }

        var inspectionIds = inspections.Select(x => x.Id).ToArray();
        var resultRows = await db.InspectionResults
            .AsNoTracking()
            .Where(x => inspectionIds.Contains(x.InspectionId))
            .Select(x => new InspectionListResultRow(
                x.InspectionId,
                x.GageId,
                x.ActualMin,
                x.ActualMax,
                x.InspectionCriterion.Minimum,
                x.InspectionCriterion.MaximumOrTolerance))
            .ToListAsync(cancellationToken);
        var secondaryProcessRows = await db.InspectionSecondaryProcesses
            .AsNoTracking()
            .Where(x => inspectionIds.Contains(x.InspectionId))
            .Select(x => new { x.InspectionId, x.IsComplete })
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

        var resultStatusByInspection = resultRows
            .GroupBy(x => x.InspectionId)
            .ToDictionary(
                group => group.Key,
                group => group.Any()
                    && group.All(x => x.GageId is not null
                        && InspectionResultEvaluator.Evaluate(
                            x.Minimum,
                            x.MaximumOrTolerance,
                            x.ActualMin,
                            x.ActualMax) == InspectionResultEvaluation.Pass));
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
        string? Minimum,
        string? MaximumOrTolerance);

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
        long customerId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Inspections
            .AsNoTracking()
            .Where(x => x.Part.CustomerId == customerId)
            .OrderByDescending(x => x.InspectionDate)
            .ThenByDescending(x => x.Id)
            .Select(x => new CertificationPackageLotOption(
                x.Id,
                x.LotNumber,
                x.Part.PartNumber,
                x.InspectionDate,
                x.Certifications.Any(c => c.Documents.Any()) ? "Documents uploaded" : "No documents uploaded"))
            .ToListAsync(cancellationToken);
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
            .Select(x => new { x.Id, x.GageTypeId, x.GageNumber })
            .ToListAsync(cancellationToken);
        var soleActiveGagesByType = activeGages
            .GroupBy(x => x.GageTypeId)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single());

        var inspection = new Inspection
        {
            PartId = model.PartId,
            InspectionCriteriaRevisionId = revision.Id,
            LotNumber = NormalizeOptionalText(model.LotNumber),
            ConformancePoNumber = NormalizeOptionalText(model.ConformancePoNumber),
            ManufacturerLotNumber = NormalizeOptionalText(model.ManufacturerLotNumber),
            DateReceived = model.DateReceived,
            QuantityReceived = model.QuantityReceived,
            QuantityInspected = InspectionSamplingPlan.GetQuantityInspected(model.QuantityReceived)
                ?? model.QuantityInspected,
            Inspector = NormalizeOptionalText(model.Inspector),
            InspectionDate = model.InspectionDate!.Value
        };

        foreach (var criterion in revision.Criteria)
        {
            soleActiveGagesByType.TryGetValue(
                criterion.GageTypeId ?? 0,
                out var soleActiveGage);
            inspection.Results.Add(new InspectionResult
            {
                InspectionCriteriaRevisionId = revision.Id,
                InspectionCriterionId = criterion.Id,
                GageId = soleActiveGage?.Id,
                GageNumber = soleActiveGage?.GageNumber
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
                SpecificationUsed = x.InspectionCriteriaRevision.SpecificationUsed,
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
                ActualMin = x.ActualMin,
                ActualMax = x.ActualMax,
                Version = x.Version
            })
            .ToListAsync(cancellationToken);

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

        var inconsistentGageSelection = inspection.Results
            .Where(x => x.InspectionCriterion.GageTypeId is not null)
            .GroupBy(x => x.InspectionCriterion.GageTypeId)
            .Any(group => group
                .Select(x => submittedResults[x.Id].GageId)
                .Distinct()
                .Skip(1)
                .Any());
        if (inconsistentGageSelection)
        {
            return new InspectionOperationResult(
                InspectionOperationStatus.ValidationFailed,
                model.Id,
                "Criteria with the same inspection method must use the same gage number.");
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
        inspection.LotNumber = NormalizeOptionalText(model.LotNumber);
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
}
