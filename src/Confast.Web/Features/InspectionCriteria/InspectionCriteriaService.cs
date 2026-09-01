using System.Data;
using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Features.InspectionCriteria;

public sealed class InspectionCriteriaService(IDbContextFactory<AppDbContext> contextFactory)
{
    public const long MaximumMasterPrintBytes = 25 * 1024 * 1024;

    private static readonly IReadOnlyDictionary<string, CertificationRequirementLevel>
        InitialCertificationRequirements =
            new Dictionary<string, CertificationRequirementLevel>(StringComparer.Ordinal)
            {
                ["Supplier Inspection"] = CertificationRequirementLevel.Required,
                ["Material"] = CertificationRequirementLevel.Required,
                ["Notes/Misc"] = CertificationRequirementLevel.Optional
            };

    private static readonly string[] DefaultUnitChoices =
    [
        "N/A",
        "MM",
        "µM",
        "IN",
        "Degrees",
        "N",
        "N·m",
        "IN-LB",
        "Rz",
        "15Tw/ball",
        "15N",
        "30N",
        "HRB",
        "HRC",
        "HV"
    ];

    public async Task<IReadOnlyList<string>> GetUnitChoicesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var savedUnits = await db.InspectionCriteria
            .AsNoTracking()
            .Where(x => x.Unit != null && x.Unit != "")
            .Select(x => x.Unit!)
            .Distinct()
            .ToListAsync(cancellationToken);

        var choices = new List<string>(DefaultUnitChoices);
        var knownChoices = new HashSet<string>(choices, StringComparer.OrdinalIgnoreCase);
        foreach (var unit in savedUnits.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            if (knownChoices.Add(unit))
            {
                choices.Add(unit);
            }
        }

        return choices;
    }

    public async Task<IReadOnlyList<SecondaryProcessTypeChoice>> GetSecondaryProcessTypeChoicesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.SecondaryProcessTypes
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .Select(x => new SecondaryProcessTypeChoice(x.Id, x.Name))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CertificationTypeChoice>> GetCertificationTypeChoicesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.CertificationTypes
            .AsNoTracking()
            .Where(x => x.Name != "Inspection Sheet")
            .OrderBy(x => x.DisplayOrder)
            .Select(x => new CertificationTypeChoice(x.Id, x.Name, x.DisplayOrder))
            .ToListAsync(cancellationToken);
    }

    public async Task<PartInspectionCriteriaSummary?> GetPartSummaryAsync(
        long partId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var part = await db.Parts
            .AsNoTracking()
            .Where(x => x.Id == partId)
            .Select(x => new { x.Id, x.PartNumber })
            .SingleOrDefaultAsync(cancellationToken);

        if (part is null)
        {
            return null;
        }

        var revisions = await RevisionSummaries(
                db.InspectionCriteriaRevisions
                    .AsNoTracking()
                    .Where(x => x.PartId == partId
                        && (x.PublishedAtUtc == null || x.SupersededAtUtc == null)))
            .ToListAsync(cancellationToken);

        return new PartInspectionCriteriaSummary(
            part.Id,
            part.PartNumber,
            revisions.SingleOrDefault(x => x.IsCurrent),
            revisions.SingleOrDefault(x => x.IsDraft));
    }

    public async Task<IReadOnlyList<InspectionCriteriaRevisionSummary>> GetRevisionHistoryAsync(
        long partId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await RevisionSummaries(
                db.InspectionCriteriaRevisions
                    .AsNoTracking()
                    .Where(x => x.PartId == partId)
                    .OrderByDescending(x => x.RevisionNumber))
            .ToListAsync(cancellationToken);
    }

    public async Task<InspectionCriteriaRevisionDetails?> GetRevisionAsync(
        long partId,
        long revisionId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var revision = await db.InspectionCriteriaRevisions
            .AsNoTracking()
            .Where(x => x.Id == revisionId && x.PartId == partId)
            .Select(x => new
            {
                x.Id,
                x.PartId,
                x.Part.PartNumber,
                x.RevisionNumber,
                x.PrintRevisionNumber,
                x.PartDescription,
                x.Notes,
                HasMasterPrint = x.MasterPrintContent != null,
                x.MasterPrintFileName,
                x.MasterPrintUploadedAtUtc,
                x.CreatedAtUtc,
                x.PublishedAtUtc,
                x.SupersededAtUtc,
                x.ChangeNote,
                IsUsedByInspection = x.Inspections.Any(),
                x.Version
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (revision is null)
        {
            return null;
        }

        var criteria = await db.InspectionCriteria
            .AsNoTracking()
            .Where(x => x.InspectionCriteriaRevisionId == revisionId)
            .OrderBy(x => x.DisplayOrder)
            .Select(x => new InspectionCriterionListItem(
                x.Id,
                x.InspectionNumber,
                x.Name,
                x.GageTypeId,
                x.InspectionMethod,
                x.Minimum,
                x.MaximumOrTolerance,
                x.Unit,
                x.SecondaryProcessRequirementId,
                x.SecondaryProcessRequirement == null ? null : x.SecondaryProcessRequirement.SecondaryProcessType.Name,
                x.DisplayOrder,
                x.Notes,
                x.Version))
            .ToListAsync(cancellationToken);

        var secondaryProcessRequirements = await db.SecondaryProcessRequirements
            .AsNoTracking()
            .Where(x => x.InspectionCriteriaRevisionId == revisionId)
            .OrderBy(x => x.Id)
            .Select(x => new SecondaryProcessRequirementListItem(
                x.Id,
                x.SecondaryProcessTypeId,
                x.SecondaryProcessType.Name,
                x.Specification,
                x.Version))
            .ToListAsync(cancellationToken);

        var certificationRequirements = await db.RevisionCertificationRequirements
            .AsNoTracking()
            .Where(x => x.InspectionCriteriaRevisionId == revisionId)
            .OrderBy(x => x.CertificationType.DisplayOrder)
            .Select(x => new RevisionCertificationRequirementListItem(
                x.Id,
                x.CertificationTypeId,
                x.CertificationTypeName,
                x.RequirementLevel,
                x.Notes,
                x.Version))
            .ToListAsync(cancellationToken);

        return new InspectionCriteriaRevisionDetails(
            revision.Id,
            revision.PartId,
            revision.PartNumber,
            revision.RevisionNumber,
            revision.PrintRevisionNumber,
            revision.PartDescription,
            revision.Notes,
            revision.HasMasterPrint,
            revision.MasterPrintFileName,
            revision.MasterPrintUploadedAtUtc,
            revision.CreatedAtUtc,
            revision.PublishedAtUtc,
            revision.SupersededAtUtc,
            revision.ChangeNote,
            revision.IsUsedByInspection,
            revision.Version,
            criteria,
            secondaryProcessRequirements,
            certificationRequirements);
    }

    public async Task<InspectionCriterionEditModel?> GetCriterionAsync(
        long partId,
        long revisionId,
        long criterionId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await db.InspectionCriteria
            .AsNoTracking()
            .Where(x => x.Id == criterionId
                && x.InspectionCriteriaRevisionId == revisionId
                && x.Revision.PartId == partId)
            .Select(x => new InspectionCriterionEditModel
            {
                Id = x.Id,
                RevisionId = x.InspectionCriteriaRevisionId,
                InspectionNumber = x.InspectionNumber,
                Name = x.Name,
                GageTypeId = x.GageTypeId,
                Minimum = x.Minimum,
                MaximumOrTolerance = x.MaximumOrTolerance,
                Unit = x.Unit,
                SecondaryProcessRequirementId = x.SecondaryProcessRequirementId,
                Notes = x.Notes,
                Version = x.Version
            })
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<CriteriaOperationResult> CreateDraftRevisionAsync(
        long partId,
        string? changeNote,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        var part = await LockPartAsync(db, partId, cancellationToken);
        if (part is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        var draftId = await db.InspectionCriteriaRevisions
            .Where(x => x.PartId == partId && x.PublishedAtUtc == null)
            .Select(x => (long?)x.Id)
            .SingleOrDefaultAsync(cancellationToken);

        if (draftId is not null)
        {
            return new CriteriaOperationResult(
                CriteriaOperationStatus.DraftAlreadyExists,
                draftId);
        }

        var current = await db.InspectionCriteriaRevisions
            .AsNoTracking()
            .Include(x => x.Criteria.OrderBy(c => c.DisplayOrder))
            .Include(x => x.SecondaryProcessRequirements.OrderBy(r => r.Id))
            .Include(x => x.CertificationRequirements.OrderBy(r => r.CertificationType.DisplayOrder))
            .SingleOrDefaultAsync(
                x => x.PartId == partId
                    && x.PublishedAtUtc != null
                    && x.SupersededAtUtc == null,
                cancellationToken);

        var nextRevisionNumber = await db.InspectionCriteriaRevisions
            .Where(x => x.PartId == partId)
            .Select(x => (int?)x.RevisionNumber)
            .MaxAsync(cancellationToken) ?? 0;

        var draft = new InspectionCriteriaRevision
        {
            PartId = partId,
            RevisionNumber = nextRevisionNumber + 1,
            PrintRevisionNumber = current is null ? part.Revision : current.PrintRevisionNumber,
            PartDescription = current is null ? part.Description : current.PartDescription,
            Notes = current?.Notes,
            MasterPrintFileName = current?.MasterPrintFileName,
            MasterPrintContent = current?.MasterPrintContent?.ToArray(),
            MasterPrintUploadedAtUtc = current?.MasterPrintUploadedAtUtc,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ChangeNote = NormalizeOptionalText(changeNote)
        };

        if (current is null && nextRevisionNumber == 0)
        {
            var defaultCertificationTypes = await db.CertificationTypes
                .Where(x => InitialCertificationRequirements.Keys.Contains(x.Name))
                .ToListAsync(cancellationToken);
            if (defaultCertificationTypes.Count != InitialCertificationRequirements.Count)
            {
                return new CriteriaOperationResult(
                    CriteriaOperationStatus.Conflict,
                    Message: "The default certification types are unavailable.");
            }

            foreach (var certificationType in defaultCertificationTypes)
            {
                draft.CertificationRequirements.Add(new RevisionCertificationRequirement
                {
                    CertificationTypeId = certificationType.Id,
                    CertificationTypeName = certificationType.Name,
                    RequirementLevel = InitialCertificationRequirements[certificationType.Name]
                });
            }
        }
        else if (current is not null)
        {
            var copiedSecondaryProcesses = new Dictionary<long, SecondaryProcessRequirement>();
            foreach (var source in current.SecondaryProcessRequirements.OrderBy(x => x.Id))
            {
                var copiedProcess = new SecondaryProcessRequirement
                {
                    SecondaryProcessTypeId = source.SecondaryProcessTypeId,
                    Specification = source.Specification
                };
                draft.SecondaryProcessRequirements.Add(copiedProcess);
                copiedSecondaryProcesses.Add(source.Id, copiedProcess);
            }

            foreach (var source in current.Criteria.OrderBy(x => x.DisplayOrder))
            {
                draft.Criteria.Add(new InspectionCriterion
                {
                    InspectionNumber = source.InspectionNumber,
                    Name = source.Name,
                    GageTypeId = source.GageTypeId,
                    InspectionMethod = source.InspectionMethod,
                    Minimum = source.Minimum,
                    MaximumOrTolerance = source.MaximumOrTolerance,
                    Unit = source.Unit,
                    SecondaryProcessRequirement = source.SecondaryProcessRequirementId is long processRequirementId
                        ? copiedSecondaryProcesses[processRequirementId]
                        : null,
                    DisplayOrder = source.DisplayOrder,
                    Notes = source.Notes
                });
            }

            foreach (var source in current.CertificationRequirements)
            {
                draft.CertificationRequirements.Add(new RevisionCertificationRequirement
                {
                    CertificationTypeId = source.CertificationTypeId,
                    CertificationTypeName = source.CertificationTypeName,
                    RequirementLevel = source.RequirementLevel,
                    Notes = source.Notes
                });
            }
        }

        db.InspectionCriteriaRevisions.Add(draft);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CriteriaOperationResult(CriteriaOperationStatus.Succeeded, draft.Id);
        }
        catch (DbUpdateException exception) when (IsIntegrityConflict(exception))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
    }

    public async Task<CriteriaOperationResult> PublishRevisionAsync(
        long partId,
        long revisionId,
        uint version,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            cancellationToken);

        if (await LockPartAsync(db, partId, cancellationToken) is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        var draft = await db.InspectionCriteriaRevisions
            .SingleOrDefaultAsync(x => x.Id == revisionId && x.PartId == partId, cancellationToken);

        if (draft is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        if (draft.PublishedAtUtc is not null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.PublishedRevision);
        }

        if (!await db.InspectionCriteria.AnyAsync(
                x => x.InspectionCriteriaRevisionId == revisionId,
                cancellationToken))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.EmptyRevision);
        }

        if (await db.InspectionCriteria.AnyAsync(
                x => x.InspectionCriteriaRevisionId == revisionId
                    && x.GageTypeId == null,
                cancellationToken))
        {
            return new CriteriaOperationResult(
                CriteriaOperationStatus.ValidationFailed,
                Message: "Every criterion must have an inspection method before publishing.");
        }

        db.Entry(draft).Property(x => x.Version).OriginalValue = version;
        var now = DateTimeOffset.UtcNow;
        var current = await db.InspectionCriteriaRevisions.SingleOrDefaultAsync(
            x => x.PartId == partId
                && x.PublishedAtUtc != null
                && x.SupersededAtUtc == null,
            cancellationToken);

        try
        {
            if (current is not null)
            {
                current.SupersededAtUtc = now;
                await db.SaveChangesAsync(cancellationToken);
            }

            draft.PublishedAtUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CriteriaOperationResult(CriteriaOperationStatus.Succeeded, draft.Id);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
        catch (DbUpdateException exception) when (IsIntegrityConflict(exception))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
    }

    public async Task<CriteriaOperationResult> DeleteRevisionAsync(
        long partId,
        long revisionId,
        uint version,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var revision = await LockRevisionAsync(db, partId, revisionId, cancellationToken);
        if (revision is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        if (revision.Version != version)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }

        if (await IsRevisionProtectedAsync(db, revision, cancellationToken))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.RevisionInUse);
        }

        await db.RevisionCertificationRequirements
            .Where(x => x.InspectionCriteriaRevisionId == revisionId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.SecondaryProcessRequirements
            .Where(x => x.InspectionCriteriaRevisionId == revisionId)
            .ExecuteDeleteAsync(cancellationToken);
        await db.InspectionCriteria
            .Where(x => x.InspectionCriteriaRevisionId == revisionId)
            .ExecuteDeleteAsync(cancellationToken);

        db.Entry(revision).Property(x => x.Version).OriginalValue = version;
        db.InspectionCriteriaRevisions.Remove(revision);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CriteriaOperationResult(CriteriaOperationStatus.Succeeded, revisionId);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
        catch (DbUpdateException exception) when (IsIntegrityConflict(exception))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
    }

    public async Task<CriteriaOperationResult> SaveRevisionHeaderAsync(
        long partId,
        long revisionId,
        InspectionCriteriaRevisionHeaderEditModel model,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var revision = await LockRevisionAsync(db, partId, revisionId, cancellationToken);
        if (revision is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        if (await IsRevisionProtectedAsync(db, revision, cancellationToken))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.RevisionInUse);
        }

        db.Entry(revision).Property(x => x.Version).OriginalValue = model.Version;
        revision.PrintRevisionNumber = NormalizeOptionalText(model.PrintRevisionNumber);
        revision.PartDescription = NormalizeOptionalText(model.PartDescription);
        revision.Notes = NormalizeOptionalText(model.Notes);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CriteriaOperationResult(CriteriaOperationStatus.Succeeded, revisionId);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
    }

    public async Task<MasterPrintFile?> GetMasterPrintAsync(
        long partId,
        long revisionId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var file = await db.InspectionCriteriaRevisions
            .AsNoTracking()
            .Where(x => x.Id == revisionId
                && x.PartId == partId
                && x.MasterPrintContent != null
                && x.MasterPrintFileName != null)
            .Select(x => new { x.MasterPrintFileName, x.MasterPrintContent })
            .SingleOrDefaultAsync(cancellationToken);

        return file is null
            ? null
            : new MasterPrintFile(file.MasterPrintFileName!, file.MasterPrintContent!);
    }

    public async Task<CriteriaOperationResult> UploadMasterPrintAsync(
        long partId,
        long revisionId,
        string fileName,
        byte[] content,
        uint version,
        CancellationToken cancellationToken = default)
    {
        var validationError = ValidateMasterPrint(fileName, content);
        if (validationError is not null)
        {
            return new CriteriaOperationResult(
                CriteriaOperationStatus.ValidationFailed,
                Message: validationError);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var revision = await LockRevisionAsync(db, partId, revisionId, cancellationToken);
        if (revision is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        if (await IsRevisionProtectedAsync(db, revision, cancellationToken))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.RevisionInUse);
        }

        db.Entry(revision).Property(x => x.Version).OriginalValue = version;
        revision.MasterPrintFileName = Path.GetFileName(fileName);
        revision.MasterPrintContent = content;
        revision.MasterPrintUploadedAtUtc = DateTimeOffset.UtcNow;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CriteriaOperationResult(CriteriaOperationStatus.Succeeded, revisionId);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
    }

    public async Task<CriteriaOperationResult> DeleteMasterPrintAsync(
        long partId,
        long revisionId,
        uint version,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var revision = await LockRevisionAsync(db, partId, revisionId, cancellationToken);
        if (revision is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        if (await IsRevisionProtectedAsync(db, revision, cancellationToken))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.RevisionInUse);
        }

        db.Entry(revision).Property(x => x.Version).OriginalValue = version;
        revision.MasterPrintFileName = null;
        revision.MasterPrintContent = null;
        revision.MasterPrintUploadedAtUtc = null;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CriteriaOperationResult(CriteriaOperationStatus.Succeeded, revisionId);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
    }

    public async Task<CriteriaOperationResult> AddCriterionAsync(
        long partId,
        long revisionId,
        InspectionCriterionEditModel model,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(model);
        if (validationError is not null)
        {
            return new CriteriaOperationResult(
                CriteriaOperationStatus.ValidationFailed,
                Message: validationError);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var revision = await LockRevisionAsync(db, partId, revisionId, cancellationToken);
        if (revision is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        if (await IsRevisionProtectedAsync(db, revision, cancellationToken))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.RevisionInUse);
        }

        var lastOrder = await db.InspectionCriteria
            .Where(x => x.InspectionCriteriaRevisionId == revisionId)
            .Select(x => (int?)x.DisplayOrder)
            .MaxAsync(cancellationToken) ?? 0;

        var criterion = new InspectionCriterion
        {
            InspectionCriteriaRevisionId = revisionId,
            DisplayOrder = lastOrder + 1
        };
        var gageType = await db.GageTypes.SingleOrDefaultAsync(
            x => x.Id == model.GageTypeId && x.IsActive,
            cancellationToken);
        if (gageType is null)
        {
            return new CriteriaOperationResult(
                CriteriaOperationStatus.ValidationFailed,
                Message: "Select an active inspection method.");
        }

        if (!await IsValidSecondaryProcessRequirementAsync(
                db, revisionId, model.SecondaryProcessRequirementId, cancellationToken))
        {
            return new CriteriaOperationResult(
                CriteriaOperationStatus.ValidationFailed,
                Message: "Select a secondary process from this revision.");
        }

        Apply(model, criterion, gageType.Id, gageType.Name);
        db.InspectionCriteria.Add(criterion);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CriteriaOperationResult(
                CriteriaOperationStatus.Succeeded,
                revisionId,
                criterion.Id);
        }
        catch (DbUpdateException exception) when (IsIntegrityConflict(exception))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
    }

    public async Task<CriteriaOperationResult> SaveCriterionAsync(
        long partId,
        long revisionId,
        InspectionCriterionEditModel model,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(model);
        if (validationError is not null)
        {
            return new CriteriaOperationResult(
                CriteriaOperationStatus.ValidationFailed,
                Message: validationError);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var revision = await LockRevisionAsync(db, partId, revisionId, cancellationToken);
        if (revision is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        if (await IsRevisionProtectedAsync(db, revision, cancellationToken))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.RevisionInUse);
        }

        var criterion = await db.InspectionCriteria.SingleOrDefaultAsync(
            x => x.Id == model.Id && x.InspectionCriteriaRevisionId == revisionId,
            cancellationToken);
        if (criterion is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        var gageType = await db.GageTypes.SingleOrDefaultAsync(
            x => x.Id == model.GageTypeId
                && (x.IsActive || x.Id == criterion.GageTypeId),
            cancellationToken);
        if (gageType is null)
        {
            return new CriteriaOperationResult(
                CriteriaOperationStatus.ValidationFailed,
                Message: "Select an active inspection method.");
        }

        if (!await IsValidSecondaryProcessRequirementAsync(
                db, revisionId, model.SecondaryProcessRequirementId, cancellationToken))
        {
            return new CriteriaOperationResult(
                CriteriaOperationStatus.ValidationFailed,
                Message: "Select a secondary process from this revision.");
        }

        db.Entry(criterion).Property(x => x.Version).OriginalValue = model.Version;
        Apply(model, criterion, gageType.Id, gageType.Name);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CriteriaOperationResult(
                CriteriaOperationStatus.Succeeded,
                revisionId,
                criterion.Id);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
        catch (DbUpdateException exception) when (IsIntegrityConflict(exception))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
    }

    public async Task<CriteriaOperationResult> DeleteCriterionAsync(
        long partId,
        long revisionId,
        long criterionId,
        uint version,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var revision = await LockRevisionAsync(db, partId, revisionId, cancellationToken);
        if (revision is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        if (await IsRevisionProtectedAsync(db, revision, cancellationToken))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.RevisionInUse);
        }

        var criterion = await db.InspectionCriteria.SingleOrDefaultAsync(
            x => x.Id == criterionId && x.InspectionCriteriaRevisionId == revisionId,
            cancellationToken);
        if (criterion is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        db.Entry(criterion).Property(x => x.Version).OriginalValue = version;
        db.InspectionCriteria.Remove(criterion);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CriteriaOperationResult(CriteriaOperationStatus.Succeeded, revisionId);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
    }

    public async Task<CriteriaOperationResult> MoveCriterionAsync(
        long partId,
        long revisionId,
        long criterionId,
        bool moveUp,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var revision = await LockRevisionAsync(db, partId, revisionId, cancellationToken);
        if (revision is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        if (await IsRevisionProtectedAsync(db, revision, cancellationToken))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.RevisionInUse);
        }

        var ordered = await db.InspectionCriteria
            .Where(x => x.InspectionCriteriaRevisionId == revisionId)
            .OrderBy(x => x.DisplayOrder)
            .ToListAsync(cancellationToken);
        var index = ordered.FindIndex(x => x.Id == criterionId);
        var neighborIndex = moveUp ? index - 1 : index + 1;

        if (index < 0)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        if (neighborIndex < 0 || neighborIndex >= ordered.Count)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Succeeded, revisionId);
        }

        var criterion = ordered[index];
        var neighbor = ordered[neighborIndex];
        var criterionOrder = criterion.DisplayOrder;
        var neighborOrder = neighbor.DisplayOrder;
        var temporaryOrder = ordered.Max(x => x.DisplayOrder) + 1;

        try
        {
            criterion.DisplayOrder = temporaryOrder;
            await db.SaveChangesAsync(cancellationToken);
            neighbor.DisplayOrder = criterionOrder;
            await db.SaveChangesAsync(cancellationToken);
            criterion.DisplayOrder = neighborOrder;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CriteriaOperationResult(CriteriaOperationStatus.Succeeded, revisionId);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
    }

    public async Task<CriteriaOperationResult> AddSecondaryProcessRequirementAsync(
        long partId,
        long revisionId,
        SecondaryProcessRequirementEditModel model,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(model);
        if (validationError is not null)
        {
            return new CriteriaOperationResult(
                CriteriaOperationStatus.ValidationFailed,
                Message: validationError);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var revision = await LockRevisionAsync(db, partId, revisionId, cancellationToken);
        if (revision is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        if (await IsRevisionProtectedAsync(db, revision, cancellationToken))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.RevisionInUse);
        }

        if (!await db.SecondaryProcessTypes.AnyAsync(
                x => x.Id == model.SecondaryProcessTypeId,
                cancellationToken))
        {
            return new CriteriaOperationResult(
                CriteriaOperationStatus.ValidationFailed,
                Message: "Select a valid secondary process.");
        }

        var requirement = new SecondaryProcessRequirement
        {
            InspectionCriteriaRevisionId = revisionId,
            SecondaryProcessTypeId = model.SecondaryProcessTypeId!.Value,
            Specification = NormalizeOptionalText(model.Specification)
        };
        db.SecondaryProcessRequirements.Add(requirement);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CriteriaOperationResult(CriteriaOperationStatus.Succeeded, revisionId);
        }
        catch (DbUpdateException exception) when (IsIntegrityConflict(exception))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
    }

    public async Task<CriteriaOperationResult> SaveSecondaryProcessRequirementAsync(
        long partId,
        long revisionId,
        SecondaryProcessRequirementEditModel model,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(model);
        if (validationError is not null)
        {
            return new CriteriaOperationResult(
                CriteriaOperationStatus.ValidationFailed,
                Message: validationError);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var revision = await LockRevisionAsync(db, partId, revisionId, cancellationToken);
        if (revision is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        if (await IsRevisionProtectedAsync(db, revision, cancellationToken))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.RevisionInUse);
        }

        var requirement = await db.SecondaryProcessRequirements.SingleOrDefaultAsync(
            x => x.Id == model.Id && x.InspectionCriteriaRevisionId == revisionId,
            cancellationToken);
        if (requirement is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        if (!await db.SecondaryProcessTypes.AnyAsync(
                x => x.Id == model.SecondaryProcessTypeId,
                cancellationToken))
        {
            return new CriteriaOperationResult(
                CriteriaOperationStatus.ValidationFailed,
                Message: "Select a valid secondary process.");
        }

        db.Entry(requirement).Property(x => x.Version).OriginalValue = model.Version;
        requirement.SecondaryProcessTypeId = model.SecondaryProcessTypeId!.Value;
        requirement.Specification = NormalizeOptionalText(model.Specification);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CriteriaOperationResult(CriteriaOperationStatus.Succeeded, revisionId);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
        catch (DbUpdateException exception) when (IsIntegrityConflict(exception))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
    }

    public async Task<CriteriaOperationResult> DeleteSecondaryProcessRequirementAsync(
        long partId,
        long revisionId,
        long requirementId,
        uint version,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var revision = await LockRevisionAsync(db, partId, revisionId, cancellationToken);
        if (revision is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        if (await IsRevisionProtectedAsync(db, revision, cancellationToken))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.RevisionInUse);
        }

        var requirement = await db.SecondaryProcessRequirements.SingleOrDefaultAsync(
            x => x.Id == requirementId && x.InspectionCriteriaRevisionId == revisionId,
            cancellationToken);
        if (requirement is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        db.Entry(requirement).Property(x => x.Version).OriginalValue = version;
        db.SecondaryProcessRequirements.Remove(requirement);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CriteriaOperationResult(CriteriaOperationStatus.Succeeded, revisionId);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
        }
    }

    public async Task<CriteriaOperationResult> SaveCertificationRequirementsAsync(
        long partId,
        long revisionId,
        IReadOnlyCollection<RevisionCertificationRequirementEditModel> models,
        CancellationToken cancellationToken = default)
    {
        if (models.Any(x => x.CertificationTypeId <= 0)
            || models.Select(x => x.CertificationTypeId).Distinct().Count() != models.Count
            || models.Any(x => x.RequirementLevel is not null
                && !Enum.IsDefined(x.RequirementLevel.Value)))
        {
            return new CriteriaOperationResult(
                CriteriaOperationStatus.ValidationFailed,
                Message: "Certification requirements are invalid. Reload the revision.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var revision = await LockRevisionAsync(db, partId, revisionId, cancellationToken);
        if (revision is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        if (await IsRevisionProtectedAsync(db, revision, cancellationToken))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.RevisionInUse);
        }

        var certificationTypes = await db.CertificationTypes
            .Where(x => x.Name != "Inspection Sheet")
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        if (certificationTypes.Count != models.Count
            || models.Any(x => !certificationTypes.ContainsKey(x.CertificationTypeId)))
        {
            return new CriteriaOperationResult(
                CriteriaOperationStatus.Conflict,
                Message: "Certification types changed. Reload the revision.");
        }

        var existingRequirements = await db.RevisionCertificationRequirements
            .Where(x => x.InspectionCriteriaRevisionId == revisionId)
            .ToDictionaryAsync(x => x.CertificationTypeId, cancellationToken);

        foreach (var model in models)
        {
            existingRequirements.TryGetValue(model.CertificationTypeId, out var requirement);
            if (requirement is null)
            {
                if (model.Id != 0)
                {
                    return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
                }

                if (model.RequirementLevel is not null)
                {
                    var type = certificationTypes[model.CertificationTypeId];
                    db.RevisionCertificationRequirements.Add(new RevisionCertificationRequirement
                    {
                        InspectionCriteriaRevisionId = revisionId,
                        CertificationTypeId = type.Id,
                        CertificationTypeName = type.Name,
                        RequirementLevel = model.RequirementLevel.Value,
                        Notes = NormalizeOptionalText(model.Notes)
                    });
                }

                continue;
            }

            if (model.Id != requirement.Id)
            {
                return new CriteriaOperationResult(CriteriaOperationStatus.Conflict);
            }

            db.Entry(requirement).Property(x => x.Version).OriginalValue = model.Version;
            if (model.RequirementLevel is null)
            {
                db.RevisionCertificationRequirements.Remove(requirement);
            }
            else
            {
                requirement.RequirementLevel = model.RequirementLevel.Value;
                requirement.Notes = NormalizeOptionalText(model.Notes);
            }
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CriteriaOperationResult(CriteriaOperationStatus.Succeeded, revisionId);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict, revisionId);
        }
        catch (DbUpdateException exception) when (IsIntegrityConflict(exception))
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.Conflict, revisionId);
        }
    }

    private static IQueryable<InspectionCriteriaRevisionSummary> RevisionSummaries(
        IQueryable<InspectionCriteriaRevision> query) =>
        query
            .Select(x => new InspectionCriteriaRevisionSummary(
                x.Id,
                x.RevisionNumber,
                x.CreatedAtUtc,
                x.PublishedAtUtc,
                x.SupersededAtUtc,
                x.ChangeNote,
                x.Criteria.Count,
                x.Inspections.Any(),
                x.Version));

    private static async Task<bool> IsRevisionProtectedAsync(
        AppDbContext db,
        InspectionCriteriaRevision revision,
        CancellationToken cancellationToken) =>
        revision.PublishedAtUtc is not null
        && await db.Inspections.AnyAsync(
            x => x.InspectionCriteriaRevisionId == revision.Id,
            cancellationToken);

    private static Task<InspectionCriteriaRevision?> LockRevisionAsync(
        AppDbContext db,
        long partId,
        long revisionId,
        CancellationToken cancellationToken) =>
        db.InspectionCriteriaRevisions
            .FromSqlInterpolated($"SELECT r.*, r.xmin FROM inspection_criteria_revisions AS r WHERE id = {revisionId} AND part_id = {partId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private static Task<Confast.Web.Features.Parts.Part?> LockPartAsync(
        AppDbContext db,
        long partId,
        CancellationToken cancellationToken) =>
        db.Parts
            .FromSqlInterpolated($"SELECT p.*, p.xmin FROM parts AS p WHERE id = {partId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private static void Apply(
        InspectionCriterionEditModel model,
        InspectionCriterion criterion,
        long gageTypeId,
        string inspectionMethod)
    {
        criterion.Name = model.Name.Trim();
        criterion.InspectionNumber = model.InspectionNumber;
        criterion.GageTypeId = gageTypeId;
        criterion.InspectionMethod = inspectionMethod;
        criterion.Minimum = NormalizeOptionalText(model.Minimum);
        criterion.MaximumOrTolerance = NormalizeOptionalText(model.MaximumOrTolerance);
        criterion.Unit = NormalizeOptionalText(model.Unit);
        criterion.SecondaryProcessRequirementId = model.SecondaryProcessRequirementId;
        criterion.Notes = NormalizeOptionalText(model.Notes);
    }

    private static Task<bool> IsValidSecondaryProcessRequirementAsync(
        AppDbContext db,
        long revisionId,
        long? secondaryProcessRequirementId,
        CancellationToken cancellationToken) =>
        secondaryProcessRequirementId is null
            ? Task.FromResult(true)
            : db.SecondaryProcessRequirements.AnyAsync(
                x => x.Id == secondaryProcessRequirementId
                    && x.InspectionCriteriaRevisionId == revisionId,
                cancellationToken);

    private static string? Validate(InspectionCriterionEditModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
        {
            return "Name is required.";
        }

        if (model.InspectionNumber is <= 0)
        {
            return "Inspection number must be greater than zero.";
        }

        if (model.GageTypeId is null or <= 0)
        {
            return "Inspection method is required.";
        }

        if (InspectionCriterionRangeValidator.HasInvalidNumericOrder(
                model.Minimum,
                model.MaximumOrTolerance))
        {
            return "Minimum cannot be greater than maximum.";
        }

        return null;
    }

    private static string? Validate(SecondaryProcessRequirementEditModel model) =>
        model.SecondaryProcessTypeId is null or <= 0 ? "Process is required." : null;

    private static string? ValidateMasterPrint(string fileName, byte[] content)
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

        if (content.LongLength > MaximumMasterPrintBytes)
        {
            return "The PDF must be 25 MB or smaller.";
        }

        var headerLength = Math.Min(content.Length, 1024);
        return content.AsSpan(0, headerLength).IndexOf("%PDF-"u8) < 0
            ? "The selected file does not appear to be a PDF."
            : null;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }

    private static bool IsIntegrityConflict(DbUpdateException exception)
    {
        var postgresException = exception.GetBaseException() as PostgresException;
        return postgresException?.SqlState is PostgresErrorCodes.UniqueViolation
            or PostgresErrorCodes.ForeignKeyViolation
            or PostgresErrorCodes.CheckViolation
            or PostgresErrorCodes.SerializationFailure;
    }
}
