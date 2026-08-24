using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Customers;

public sealed class CustomerService(IDbContextFactory<AppDbContext> contextFactory)
{
    public async Task<IReadOnlyList<CustomerListItem>> GetCustomersAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Customers
            .AsNoTracking()
            .OrderBy(x => x.Name)
            .Select(x => new CustomerListItem(x.Id, x.Name, x.IsActive))
            .ToListAsync(cancellationToken);
    }

    public async Task<CustomerEditModel?> GetCustomerAsync(
        long id,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await db.Customers
            .AsNoTracking()
            .Where(x => x.Id == id)
            .Select(x => new CustomerEditModel
            {
                Id = x.Id,
                Name = x.Name,
                AddressLine1 = x.AddressLine1,
                AddressLine2 = x.AddressLine2,
                City = x.City,
                State = x.State,
                PostalCode = x.PostalCode,
                IsActive = x.IsActive,
                Version = x.Version
            })
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<SaveCustomerResult> SaveCustomerAsync(
        CustomerEditModel model,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Name);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var customer = await db.Customers
            .SingleOrDefaultAsync(x => x.Id == model.Id, cancellationToken);

        if (customer is null)
        {
            return new SaveCustomerResult(SaveCustomerStatus.NotFound);
        }

        db.Entry(customer).Property(x => x.Version).OriginalValue = model.Version;

        customer.Name = model.Name.Trim();
        customer.AddressLine1 = NormalizeOptionalText(model.AddressLine1);
        customer.AddressLine2 = NormalizeOptionalText(model.AddressLine2);
        customer.City = NormalizeOptionalText(model.City);
        customer.State = NormalizeOptionalText(model.State);
        customer.PostalCode = NormalizeOptionalText(model.PostalCode);
        customer.IsActive = model.IsActive;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new SaveCustomerResult(SaveCustomerStatus.Saved, customer.Version);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new SaveCustomerResult(SaveCustomerStatus.Conflict);
        }
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
