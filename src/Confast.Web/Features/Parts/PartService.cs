using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Features.Parts;

public sealed class PartService(IDbContextFactory<AppDbContext> contextFactory)
{
    private const string UniquePartNumberConstraint = "UX_parts_customer_id_part_number";

    public async Task<IReadOnlyList<PartListItem>> GetPartsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await ProjectPartList(db.Parts
                .AsNoTracking()
                .OrderBy(x => x.Customer.Name)
                .ThenBy(x => x.PartNumber))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PartListItem>> GetPartsForCustomerAsync(
        long customerId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await ProjectPartList(db.Parts
                .AsNoTracking()
                .Where(x => x.CustomerId == customerId)
                .OrderBy(x => x.PartNumber))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<CustomerOption>> GetCustomerOptionsAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Customers
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new CustomerOption(x.Id, x.Name, x.IsActive))
            .ToListAsync(cancellationToken);
    }

    public async Task<PartEditModel?> GetPartAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Parts
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new PartEditModel
            {
                Id = x.Id,
                CustomerId = x.CustomerId,
                PartNumber = x.PartNumber,
                Description = x.Description,
                Revision = x.Revision,
                Version = x.Version
            })
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<PartDeleteModel?> GetPartForDeleteAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Parts
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new PartDeleteModel(
                x.Id,
                x.PartNumber,
                x.Customer.Name,
                x.Version))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<SavePartResult> CreatePartAsync(
        PartEditModel model,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(model);
        if (validationError is not null)
        {
            return new SavePartResult(SavePartStatus.ValidationFailed, Message: validationError);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (!await db.Customers.AnyAsync(x => x.Id == model.CustomerId, cancellationToken))
        {
            return new SavePartResult(SavePartStatus.CustomerNotFound);
        }

        var part = new Part
        {
            CustomerId = model.CustomerId,
            PartNumber = model.PartNumber.Trim(),
            Description = NormalizeOptionalText(model.Description),
            Revision = NormalizeOptionalText(model.Revision)
        };

        db.Parts.Add(part);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new SavePartResult(SavePartStatus.Saved, part.Id, part.Version);
        }
        catch (DbUpdateException exception) when (HasPostgresError(
                   exception,
                   PostgresErrorCodes.UniqueViolation,
                   UniquePartNumberConstraint))
        {
            return new SavePartResult(SavePartStatus.DuplicatePartNumber);
        }
        catch (DbUpdateException exception) when (HasPostgresError(
                   exception,
                   PostgresErrorCodes.ForeignKeyViolation))
        {
            return new SavePartResult(SavePartStatus.CustomerNotFound);
        }
    }

    public async Task<SavePartResult> SavePartAsync(
        PartEditModel model,
        CancellationToken cancellationToken = default)
    {
        var validationError = Validate(model);
        if (validationError is not null)
        {
            return new SavePartResult(SavePartStatus.ValidationFailed, Message: validationError);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var part = await db.Parts.SingleOrDefaultAsync(x => x.Id == model.Id, cancellationToken);

        if (part is null)
        {
            return new SavePartResult(SavePartStatus.NotFound);
        }

        if (!await db.Customers.AnyAsync(x => x.Id == model.CustomerId, cancellationToken))
        {
            return new SavePartResult(SavePartStatus.CustomerNotFound);
        }

        db.Entry(part).Property(x => x.Version).OriginalValue = model.Version;

        part.CustomerId = model.CustomerId;
        part.PartNumber = model.PartNumber.Trim();
        part.Description = NormalizeOptionalText(model.Description);
        part.Revision = NormalizeOptionalText(model.Revision);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new SavePartResult(SavePartStatus.Saved, part.Id, part.Version);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new SavePartResult(SavePartStatus.Conflict);
        }
        catch (DbUpdateException exception) when (HasPostgresError(
                   exception,
                   PostgresErrorCodes.UniqueViolation,
                   UniquePartNumberConstraint))
        {
            return new SavePartResult(SavePartStatus.DuplicatePartNumber);
        }
        catch (DbUpdateException exception) when (HasPostgresError(
                   exception,
                   PostgresErrorCodes.ForeignKeyViolation))
        {
            return new SavePartResult(SavePartStatus.CustomerNotFound);
        }
    }

    public async Task<DeletePartStatus> DeletePartAsync(
        long id,
        uint version,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var part = await db.Parts.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (part is null)
        {
            return DeletePartStatus.NotFound;
        }

        db.Entry(part).Property(x => x.Version).OriginalValue = version;
        db.Parts.Remove(part);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return DeletePartStatus.Deleted;
        }
        catch (DbUpdateConcurrencyException)
        {
            return DeletePartStatus.Conflict;
        }
        catch (DbUpdateException exception) when (HasPostgresError(
                   exception,
                   PostgresErrorCodes.ForeignKeyViolation)
               || HasPostgresError(exception, PostgresErrorCodes.RestrictViolation))
        {
            return DeletePartStatus.Blocked;
        }
    }

    private static IQueryable<PartListItem> ProjectPartList(IQueryable<Part> query) =>
        query.Select(x => new PartListItem(
            x.Id,
            x.CustomerId,
            x.Customer.Name,
            x.PartNumber,
            x.Revision,
            x.Description));

    private static string? Validate(PartEditModel model)
    {
        if (model.CustomerId <= 0)
        {
            return "Customer is required.";
        }

        return string.IsNullOrWhiteSpace(model.PartNumber)
            ? "Part number is required."
            : null;
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
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
