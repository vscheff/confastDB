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

    public async Task<IReadOnlyList<PlantOption>> GetPlantOptionsAsync(long customerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Plants.AsNoTracking().Where(x => x.CustomerId == customerId)
            .OrderBy(x => x.Name)
            .Select(x => new PlantOption(x.Id, x.Name, x.PlantCode))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SupplierOption>> GetSupplierOptionsAsync(
        long? includedSupplierId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Suppliers
            .AsNoTracking()
            .Where(x => x.IsActive || x.Id == includedSupplierId)
            .OrderBy(x => x.Name)
            .Select(x => new SupplierOption(x.Id, x.Name))
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
                SupplierId = x.SupplierId,
                PartNumber = x.PartNumber,
                Description = x.Description,
                SpecificationUsed = x.SpecificationUsed,
                Revision = x.Revision,
                IsActive = x.IsActive,
                Version = x.Version,
                PlantIds = x.PartPlants.Select(pp => pp.PlantId).ToList()
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

        if (!await IsSupplierAvailableAsync(db, model.SupplierId, cancellationToken))
        {
            return new SavePartResult(SavePartStatus.SupplierUnavailable);
        }

        var part = new Part
        {
            CustomerId = model.CustomerId,
            SupplierId = model.SupplierId,
            PartNumber = model.PartNumber.Trim(),
            Description = NormalizeOptionalText(model.Description),
            SpecificationUsed = NormalizeOptionalText(model.SpecificationUsed),
            Revision = NormalizeOptionalText(model.Revision),
            IsActive = model.IsActive
        };

        db.Parts.Add(part);

        var selectedPlantIds = model.PlantIds.Distinct().ToArray();
        var plants = await db.Plants.Where(x => selectedPlantIds.Contains(x.Id)).ToListAsync(cancellationToken);
        if (plants.Count != selectedPlantIds.Length || plants.Any(x => x.CustomerId != model.CustomerId))
            return new SavePartResult(SavePartStatus.ValidationFailed, Message: "Each selected plant must belong to the selected customer.");
        if (plants.Count == 0)
        {
            var customerPlants = await db.Plants.Where(x => x.CustomerId == model.CustomerId).Select(x => x.Id).ToListAsync(cancellationToken);
            if (customerPlants.Count == 1) selectedPlantIds = customerPlants.ToArray();
        }
        db.PartPlants.AddRange(selectedPlantIds.Select(plantId => new PartPlant { Part = part, PlantId = plantId }));

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
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var part = await db.Parts.SingleOrDefaultAsync(x => x.Id == model.Id, cancellationToken);

        if (part is null)
        {
            return new SavePartResult(SavePartStatus.NotFound);
        }

        if (!await db.Customers.AnyAsync(x => x.Id == model.CustomerId, cancellationToken))
        {
            return new SavePartResult(SavePartStatus.CustomerNotFound);
        }

        if (part.SupplierId != model.SupplierId
            && !await IsSupplierAvailableAsync(db, model.SupplierId, cancellationToken))
        {
            return new SavePartResult(SavePartStatus.SupplierUnavailable);
        }

        db.Entry(part).Property(x => x.Version).OriginalValue = model.Version;

        var selectedPlantIds = model.PlantIds.Distinct().ToArray();
        var plants = await db.Plants.Where(x => selectedPlantIds.Contains(x.Id)).ToListAsync(cancellationToken);
        if (plants.Count != selectedPlantIds.Length || plants.Any(x => x.CustomerId != model.CustomerId))
            return new SavePartResult(SavePartStatus.ValidationFailed, Message: "Each selected plant must belong to the selected customer.");
        var existingAssignments = await db.PartPlants.Where(x => x.PartId == part.Id).ToListAsync(cancellationToken);
        if (part.CustomerId != model.CustomerId)
        {
            db.PartPlants.RemoveRange(existingAssignments);
            await db.SaveChangesAsync(cancellationToken);
            existingAssignments = [];
        }
        else
        {
            db.PartPlants.RemoveRange(existingAssignments.Where(x => !selectedPlantIds.Contains(x.PlantId)));
        }

        part.CustomerId = model.CustomerId;
        part.SupplierId = model.SupplierId;
        part.PartNumber = model.PartNumber.Trim();
        part.Description = NormalizeOptionalText(model.Description);
        part.SpecificationUsed = NormalizeOptionalText(model.SpecificationUsed);
        part.Revision = NormalizeOptionalText(model.Revision);
        part.IsActive = model.IsActive;
        db.PartPlants.AddRange(selectedPlantIds.Except(existingAssignments.Select(x => x.PlantId))
            .Select(plantId => new PartPlant { PartId = part.Id, PlantId = plantId }));

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
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
            x.Description,
            x.Supplier == null ? null : x.Supplier.Name,
            x.IsActive,
            x.PartPlants
                .OrderBy(partPlant => partPlant.Plant.Name)
                .Select(partPlant => new PlantOption(
                    partPlant.PlantId,
                    partPlant.Plant.Name,
                    partPlant.Plant.PlantCode))
                .ToList()));

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

    private static Task<bool> IsSupplierAvailableAsync(
        AppDbContext db,
        long? supplierId,
        CancellationToken cancellationToken) =>
        supplierId is null
            ? Task.FromResult(true)
            : db.Suppliers.AnyAsync(x => x.Id == supplierId && x.IsActive, cancellationToken);

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
