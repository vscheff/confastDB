using System.Data;
using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Features.InspectionCriteria;

public sealed class InspectionCriteriaService(IDbContextFactory<AppDbContext> contextFactory)
{
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
                x.CreatedAtUtc,
                x.PublishedAtUtc,
                x.SupersededAtUtc,
                x.ChangeNote,
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
                x.Name,
                x.InspectionMethod,
                x.MinimumValue,
                x.MaximumValue,
                x.Unit,
                x.DisplayOrder,
                x.Notes,
                x.Version))
            .ToListAsync(cancellationToken);

        return new InspectionCriteriaRevisionDetails(
            revision.Id,
            revision.PartId,
            revision.PartNumber,
            revision.RevisionNumber,
            revision.CreatedAtUtc,
            revision.PublishedAtUtc,
            revision.SupersededAtUtc,
            revision.ChangeNote,
            revision.Version,
            criteria);
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
                Name = x.Name,
                InspectionMethod = x.InspectionMethod,
                MinimumValue = x.MinimumValue,
                MaximumValue = x.MaximumValue,
                Unit = x.Unit,
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
            CreatedAtUtc = DateTimeOffset.UtcNow,
            ChangeNote = NormalizeOptionalText(changeNote)
        };

        if (current is not null)
        {
            foreach (var source in current.Criteria)
            {
                draft.Criteria.Add(new InspectionCriterion
                {
                    Name = source.Name,
                    InspectionMethod = source.InspectionMethod,
                    MinimumValue = source.MinimumValue,
                    MaximumValue = source.MaximumValue,
                    Unit = source.Unit,
                    DisplayOrder = source.DisplayOrder,
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

        if (revision.PublishedAtUtc is not null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.PublishedRevision);
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
        Apply(model, criterion);
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

        if (revision.PublishedAtUtc is not null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.PublishedRevision);
        }

        var criterion = await db.InspectionCriteria.SingleOrDefaultAsync(
            x => x.Id == model.Id && x.InspectionCriteriaRevisionId == revisionId,
            cancellationToken);
        if (criterion is null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.NotFound);
        }

        db.Entry(criterion).Property(x => x.Version).OriginalValue = model.Version;
        Apply(model, criterion);

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

        if (revision.PublishedAtUtc is not null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.PublishedRevision);
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

        if (revision.PublishedAtUtc is not null)
        {
            return new CriteriaOperationResult(CriteriaOperationStatus.PublishedRevision);
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
                x.Criteria.Count));

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

    private static void Apply(InspectionCriterionEditModel model, InspectionCriterion criterion)
    {
        criterion.Name = model.Name.Trim();
        criterion.InspectionMethod = NormalizeOptionalText(model.InspectionMethod);
        criterion.MinimumValue = model.MinimumValue;
        criterion.MaximumValue = model.MaximumValue;
        criterion.Unit = NormalizeOptionalText(model.Unit);
        criterion.Notes = NormalizeOptionalText(model.Notes);
    }

    private static string? Validate(InspectionCriterionEditModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
        {
            return "Name is required.";
        }

        return model.MinimumValue is not null
            && model.MaximumValue is not null
            && model.MinimumValue > model.MaximumValue
                ? "Minimum value cannot be greater than maximum value."
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
