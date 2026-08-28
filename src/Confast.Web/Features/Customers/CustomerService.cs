using Confast.Web.Data;
using System.ComponentModel.DataAnnotations;
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

    public async Task<CustomerCertificationDeliveryEditModel?> GetCertificationDeliveryAsync(
        long customerId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        if (!await db.Customers.AsNoTracking().AnyAsync(x => x.Id == customerId, cancellationToken))
        {
            return null;
        }

        var requiredTypeIds = await db.CustomerCertificationRequirements
            .AsNoTracking()
            .Where(x => x.CustomerId == customerId)
            .Select(x => x.CertificationTypeId)
            .ToHashSetAsync(cancellationToken);
        var settings = await db.CustomerCertificationSettings
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.CustomerId == customerId, cancellationToken);

        return new CustomerCertificationDeliveryEditModel
        {
            CustomerId = customerId,
            Recipients = await db.CustomerCertificationRecipients
                .AsNoTracking()
                .Where(x => x.CustomerId == customerId)
                .OrderBy(x => x.RecipientType)
                .ThenBy(x => x.Name)
                .ThenBy(x => x.EmailAddress)
                .Select(x => new CustomerCertificationRecipientEditModel
                {
                    Id = x.Id,
                    CustomerId = x.CustomerId,
                    Name = x.Name,
                    EmailAddress = x.EmailAddress,
                    RecipientType = x.RecipientType,
                    Version = x.Version
                })
                .ToListAsync(cancellationToken),
            CertificationTypes = await db.CertificationTypes
                .AsNoTracking()
                .OrderBy(x => x.DisplayOrder)
                .Select(x => new CustomerCertificationTypeChoice
                {
                    Id = x.Id,
                    Name = x.Name,
                    IsRequired = requiredTypeIds.Contains(x.Id)
                })
                .ToListAsync(cancellationToken),
            OriginalRequiredCertificationTypeIds = requiredTypeIds,
            FilenameTemplate = settings?.FilenameTemplate,
            MultiLotFilenameTemplate = settings?.MultiLotFilenameTemplate,
            SettingsVersion = settings?.Version
        };
    }

    public async Task<CustomerCertificationOperationResult> SaveCertificationRecipientAsync(
        CustomerCertificationRecipientEditModel model,
        CancellationToken cancellationToken = default)
    {
        var email = model.EmailAddress?.Trim() ?? string.Empty;
        if (email.Length > 320 || !new EmailAddressAttribute().IsValid(email))
        {
            return new CustomerCertificationOperationResult(
                CustomerCertificationOperationStatus.ValidationFailed,
                Message: "Enter a valid email address.");
        }

        if (model.Name?.Trim().Length > 200)
        {
            return new CustomerCertificationOperationResult(
                CustomerCertificationOperationStatus.ValidationFailed,
                Message: "Recipient name cannot exceed 200 characters.");
        }

        if (!Enum.IsDefined(model.RecipientType))
        {
            return new CustomerCertificationOperationResult(
                CustomerCertificationOperationStatus.ValidationFailed,
                Message: "Select To or Cc for the recipient.");
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Customers.AnyAsync(x => x.Id == model.CustomerId, cancellationToken))
        {
            return new CustomerCertificationOperationResult(CustomerCertificationOperationStatus.NotFound);
        }

        CustomerCertificationRecipient recipient;
        if (model.Id == 0)
        {
            recipient = new CustomerCertificationRecipient { CustomerId = model.CustomerId };
            db.CustomerCertificationRecipients.Add(recipient);
        }
        else
        {
            var existingRecipient = await db.CustomerCertificationRecipients.SingleOrDefaultAsync(
                x => x.Id == model.Id && x.CustomerId == model.CustomerId,
                cancellationToken);
            if (existingRecipient is null)
            {
                return new CustomerCertificationOperationResult(CustomerCertificationOperationStatus.NotFound);
            }

            recipient = existingRecipient;
            db.Entry(recipient).Property(x => x.Version).OriginalValue = model.Version;
        }

        recipient.Name = NormalizeOptionalText(model.Name);
        recipient.EmailAddress = email;
        recipient.RecipientType = model.RecipientType;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new CustomerCertificationOperationResult(
                CustomerCertificationOperationStatus.Succeeded,
                recipient.Id,
                recipient.Version);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new CustomerCertificationOperationResult(CustomerCertificationOperationStatus.Conflict);
        }
    }

    public async Task<CustomerCertificationOperationResult> DeleteCertificationRecipientAsync(
        long customerId,
        long recipientId,
        uint version,
        CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var recipient = await db.CustomerCertificationRecipients.SingleOrDefaultAsync(
            x => x.Id == recipientId && x.CustomerId == customerId,
            cancellationToken);
        if (recipient is null)
        {
            return new CustomerCertificationOperationResult(CustomerCertificationOperationStatus.NotFound);
        }

        db.Entry(recipient).Property(x => x.Version).OriginalValue = version;
        db.CustomerCertificationRecipients.Remove(recipient);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new CustomerCertificationOperationResult(CustomerCertificationOperationStatus.Succeeded);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new CustomerCertificationOperationResult(CustomerCertificationOperationStatus.Conflict);
        }
    }

    public async Task<CustomerCertificationOperationResult> SaveCertificationConfigurationAsync(
        CustomerCertificationDeliveryEditModel model,
        CertificationPackageFilenameFormatter filenameFormatter,
        CancellationToken cancellationToken = default)
    {
        var template = NormalizeOptionalText(model.FilenameTemplate);
        var multiLotTemplate = NormalizeOptionalText(model.MultiLotFilenameTemplate);
        if (template?.Length > 500)
        {
            return new CustomerCertificationOperationResult(
                CustomerCertificationOperationStatus.ValidationFailed,
                Message: "Filename template cannot exceed 500 characters.");
        }

        if (multiLotTemplate?.Length > 500)
        {
            return new CustomerCertificationOperationResult(
                CustomerCertificationOperationStatus.ValidationFailed,
                Message: "Multiple-lot filename template cannot exceed 500 characters.");
        }

        try
        {
            filenameFormatter.Format(
                template,
                new CertificationPackageFilenameValues(
                    "Example Customer",
                    "ABC123",
                    "856342",
                    "PO-1001",
                    new DateOnly(2026, 8, 28),
                    new DateOnly(2026, 9, 1)));
            filenameFormatter.FormatMultiLot(
                multiLotTemplate,
                new CertificationMultiLotPackageFilenameValues(
                    "Example Customer",
                    new DateOnly(2026, 9, 1)));
        }
        catch (CertificationFilenameTemplateException exception)
        {
            return new CustomerCertificationOperationResult(
                CustomerCertificationOperationStatus.ValidationFailed,
                Message: exception.Message);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        if (!await db.Customers.AnyAsync(x => x.Id == model.CustomerId, cancellationToken))
        {
            return new CustomerCertificationOperationResult(CustomerCertificationOperationStatus.NotFound);
        }

        var selectedTypeIds = model.CertificationTypes
            .Where(x => x.IsRequired)
            .Select(x => x.Id)
            .ToHashSet();
        var validTypeIds = await db.CertificationTypes
            .Where(x => selectedTypeIds.Contains(x.Id))
            .Select(x => x.Id)
            .ToHashSetAsync(cancellationToken);
        if (!selectedTypeIds.SetEquals(validTypeIds))
        {
            return new CustomerCertificationOperationResult(
                CustomerCertificationOperationStatus.ValidationFailed,
                Message: "One or more certification types are no longer available.");
        }

        var existingRequirements = await db.CustomerCertificationRequirements
            .Where(x => x.CustomerId == model.CustomerId)
            .ToListAsync(cancellationToken);
        if (!model.OriginalRequiredCertificationTypeIds.SetEquals(
                existingRequirements.Select(x => x.CertificationTypeId)))
        {
            return new CustomerCertificationOperationResult(CustomerCertificationOperationStatus.Conflict);
        }

        db.CustomerCertificationRequirements.RemoveRange(
            existingRequirements.Where(x => !selectedTypeIds.Contains(x.CertificationTypeId)));
        var existingTypeIds = existingRequirements.Select(x => x.CertificationTypeId).ToHashSet();
        db.CustomerCertificationRequirements.AddRange(
            selectedTypeIds.Except(existingTypeIds).Select(typeId =>
                new CustomerCertificationRequirement
                {
                    CustomerId = model.CustomerId,
                    CertificationTypeId = typeId
                }));

        var settings = await db.CustomerCertificationSettings.SingleOrDefaultAsync(
            x => x.CustomerId == model.CustomerId,
            cancellationToken);
        if (settings is not null)
        {
            if (model.SettingsVersion is null)
            {
                return new CustomerCertificationOperationResult(CustomerCertificationOperationStatus.Conflict);
            }

            db.Entry(settings).Property(x => x.Version).OriginalValue = model.SettingsVersion.Value;
            if (template is null && multiLotTemplate is null)
            {
                db.CustomerCertificationSettings.Remove(settings);
            }
            else
            {
                settings.FilenameTemplate = template;
                settings.MultiLotFilenameTemplate = multiLotTemplate;
            }
        }
        else if (model.SettingsVersion is not null)
        {
            return new CustomerCertificationOperationResult(CustomerCertificationOperationStatus.Conflict);
        }
        else if (template is not null || multiLotTemplate is not null)
        {
            settings = new CustomerCertificationSettings
            {
                CustomerId = model.CustomerId,
                FilenameTemplate = template,
                MultiLotFilenameTemplate = multiLotTemplate
            };
            db.CustomerCertificationSettings.Add(settings);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new CustomerCertificationOperationResult(
                CustomerCertificationOperationStatus.Succeeded,
                Version: settings is not null && (template is not null || multiLotTemplate is not null)
                    ? settings.Version
                    : null);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new CustomerCertificationOperationResult(CustomerCertificationOperationStatus.Conflict);
        }
    }

    private static string? NormalizeOptionalText(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrEmpty(normalized) ? null : normalized;
    }
}
