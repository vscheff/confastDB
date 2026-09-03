using System.ComponentModel.DataAnnotations;
using Confast.Web.Data;
using Confast.Web.Features.ContainerTracking;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Suppliers;

public sealed class SupplierEditModel
{
    public long Id { get; set; }
    [Required, StringLength(200)]
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public uint Version { get; set; }
}

public sealed class SupplierService(IDbContextFactory<AppDbContext> contextFactory, TrackingAccess access)
{
    public async Task<List<SupplierEditModel>> SearchAsync(string? search = null, CancellationToken cancellationToken = default)
    {
        await access.GetAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Suppliers.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(x => x.Name.ToUpper().Contains(term));
        }
        return await query.OrderBy(x => x.Name).Select(x => new SupplierEditModel
        { Id = x.Id, Name = x.Name, IsActive = x.IsActive, Version = x.Version }).ToListAsync(cancellationToken);
    }

    public async Task<TrackingSaveResult> SaveAsync(SupplierEditModel model, CancellationToken cancellationToken = default)
    {
        await access.RequireEditAsync(cancellationToken);
        model.Name = model.Name?.Trim() ?? string.Empty;
        if (TrackingValidation.Validate(model) is { } error) return TrackingSaveResult.Invalid(error);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var supplier = model.Id == 0 ? new Supplier() : await db.Suppliers.FindAsync([model.Id], cancellationToken);
        if (supplier is null) return TrackingSaveResult.Invalid("Supplier no longer exists.");
        if (model.Id == 0) db.Suppliers.Add(supplier);
        else if (supplier.Version != model.Version) return TrackingSaveResult.Conflict();
        supplier.Name = model.Name;
        supplier.IsActive = model.IsActive;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new(true, Id: supplier.Id, Version: supplier.Version);
        }
        catch (DbUpdateConcurrencyException) { return TrackingSaveResult.Conflict(); }
    }
}
