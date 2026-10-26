using Confast.Web.Features.Authorization;
using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Features.Gages;

public sealed class GageService(IDbContextFactory<AppDbContext> contextFactory, ApplicationAuthorization authorization)
{
    public async Task<IReadOnlyList<GageTypeChoice>> GetCriteriaMethodChoicesAsync(bool activeOnly = false,
        long? includeGageTypeId = null, CancellationToken cancellationToken = default)
    {
        using var operation = await authorization.BeginAsync("InspectionCriteria.MethodChoices", new("CriteriaMethods"),
            [Permissions.InspectionCriteria.Read], cancellationToken);
        return await GetMethodChoicesCoreAsync(activeOnly, includeGageTypeId, cancellationToken);
    }

    public async Task<IReadOnlyList<GageTypeChoice>> GetGageMethodChoicesAsync(CancellationToken cancellationToken = default)
    {
        using var operation = await authorization.BeginAsync("Gages.MethodChoices", new("GageMethods"),
            [Permissions.Gages.Read], cancellationToken);
        return await GetMethodChoicesCoreAsync(false, null, cancellationToken);
    }

    private async Task<IReadOnlyList<GageTypeChoice>> GetMethodChoicesCoreAsync(bool activeOnly, long? includeGageTypeId, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.GageTypes.AsNoTracking().Where(x => !activeOnly || x.IsActive || x.Id == includeGageTypeId)
            .OrderBy(x => x.Name).Select(x => new GageTypeChoice(x.Id, x.Name, x.IsActive)).ToListAsync(cancellationToken);
    }
    public async Task<IReadOnlyList<GageTypeListItem>> GetGageTypesAsync(
        CancellationToken cancellationToken = default)
    {
        using var operation = await authorization.BeginAsync("GageTypes.Read", new("GageTypes"), [Permissions.GageTypes.Read], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.GageTypes
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new GageTypeListItem(x.Id, x.Name, x.IsActive, x.Version))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GageTypeChoice>> GetGageTypeChoicesAsync(
        bool activeOnly = false,
        long? includeGageTypeId = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = await authorization.BeginAsync("GageTypes.Read", new("GageTypes"), [Permissions.GageTypes.Read], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.GageTypes
            .AsNoTracking()
            .Where(x => !activeOnly || x.IsActive || x.Id == includeGageTypeId)
            .OrderBy(x => x.Name)
            .Select(x => new GageTypeChoice(x.Id, x.Name, x.IsActive))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GageListItem>> GetGagesAsync(
        CancellationToken cancellationToken = default)
    {
        using var operation = await authorization.BeginAsync("Gages.Read", new("Gages"), [Permissions.Gages.Read], cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Gages
            .AsNoTracking()
            .OrderBy(x => x.GageNumber)
            .Select(x => new GageListItem(
                x.Id,
                x.GageTypeId,
                x.GageType.Name,
                x.GageNumber,
                x.IsActive,
                x.Version))
            .ToListAsync(cancellationToken);
    }

    public async Task<SaveGageResult> SaveGageTypeAsync(
        GageTypeEditModel model,
        CancellationToken cancellationToken = default)
    {
        using var operation = await authorization.BeginAsync("GageTypes.Save", new("GageTypes", model.Id.ToString()),
            [model.Id == 0 ? Permissions.GageTypes.Create : Permissions.GageTypes.Update], cancellationToken);
        var name = model.Name.Trim();
        if (name.Length == 0)
        {
            return new SaveGageResult(SaveGageStatus.ValidationFailed, "Name is required.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        GageType entity;
        if (model.Id == 0)
        {
            entity = new GageType();
            db.GageTypes.Add(entity);
        }
        else
        {
            entity = await db.GageTypes.SingleOrDefaultAsync(x => x.Id == model.Id, cancellationToken)
                ?? null!;
            if (entity is null)
            {
                return new SaveGageResult(SaveGageStatus.NotFound);
            }

            db.Entry(entity).Property(x => x.Version).OriginalValue = model.Version;
        }

        entity.Name = name;
        entity.IsActive = model.IsActive;

        return await SaveAsync(db, "A gage type with that name already exists.", cancellationToken);
    }

    public async Task<SaveGageResult> SaveGageAsync(
        GageEditModel model,
        CancellationToken cancellationToken = default)
    {
        using var operation = await authorization.BeginAsync("Gages.Save", new("Gages", model.Id.ToString()),
            [model.Id == 0 ? Permissions.Gages.Create : Permissions.Gages.Update], cancellationToken);
        var gageNumber = model.GageNumber.Trim();
        if (gageNumber.Length == 0)
        {
            return new SaveGageResult(SaveGageStatus.ValidationFailed, "Gage number is required.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.GageTypes.AnyAsync(x => x.Id == model.GageTypeId, cancellationToken))
        {
            return new SaveGageResult(SaveGageStatus.ValidationFailed, "Select a valid gage type.");
        }

        Gage entity;
        if (model.Id == 0)
        {
            entity = new Gage();
            db.Gages.Add(entity);
        }
        else
        {
            entity = await db.Gages.SingleOrDefaultAsync(x => x.Id == model.Id, cancellationToken)
                ?? null!;
            if (entity is null)
            {
                return new SaveGageResult(SaveGageStatus.NotFound);
            }

            db.Entry(entity).Property(x => x.Version).OriginalValue = model.Version;
        }

        entity.GageTypeId = model.GageTypeId;
        entity.GageNumber = gageNumber;
        entity.IsActive = model.IsActive;

        return await SaveAsync(db, "A gage with that number already exists.", cancellationToken);
    }

    private static async Task<SaveGageResult> SaveAsync(
        AppDbContext db,
        string duplicateMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new SaveGageResult(SaveGageStatus.Saved);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new SaveGageResult(SaveGageStatus.Conflict);
        }
        catch (DbUpdateException exception)
            when (exception.GetBaseException() is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return new SaveGageResult(SaveGageStatus.Duplicate, duplicateMessage);
        }
        catch (DbUpdateException exception)
            when (exception.GetBaseException() is PostgresException
                { SqlState: PostgresErrorCodes.RestrictViolation })
        {
            return new SaveGageResult(
                SaveGageStatus.ValidationFailed,
                "The gage type cannot be changed because the gage has been used on an inspection.");
        }
    }
}
