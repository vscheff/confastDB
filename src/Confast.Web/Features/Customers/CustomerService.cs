using Confast.Web.Data;
using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Customers;

public sealed class CustomerService(IDbContextFactory<AppDbContext> contextFactory)
{
    public async Task<long> CreateCustomerAsync(CustomerEditModel model, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Name);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var customer = new Customer { Name = model.Name.Trim(), IsActive = model.IsActive };
        var inspectionSheetTypeId = await GetInspectionSheetTypeIdAsync(db, cancellationToken);
        var mainPlant = new Plant { Name = "Main" };
        mainPlant.CertificationRequirements.Add(new PlantCertificationRequirement { CertificationTypeId = inspectionSheetTypeId });
        customer.Plants.Add(mainPlant);
        db.Customers.Add(customer);
        await db.SaveChangesAsync(cancellationToken);
        return customer.Id;
    }

    public async Task<IReadOnlyList<CustomerListItem>> GetCustomersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Customers.AsNoTracking().OrderBy(x => x.Name)
            .Select(x => new CustomerListItem(x.Id, x.Name, x.IsActive)).ToListAsync(cancellationToken);
    }

    public async Task<CustomerEditModel?> GetCustomerAsync(long id, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Customers.AsNoTracking().Where(x => x.Id == id).Select(x => new CustomerEditModel
        { Id = x.Id, Name = x.Name, IsActive = x.IsActive, Version = x.Version }).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<SaveCustomerResult> SaveCustomerAsync(CustomerEditModel model, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model.Name);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var customer = await db.Customers.SingleOrDefaultAsync(x => x.Id == model.Id, cancellationToken);
        if (customer is null) return new SaveCustomerResult(SaveCustomerStatus.NotFound);
        db.Entry(customer).Property(x => x.Version).OriginalValue = model.Version;
        customer.Name = model.Name.Trim();
        customer.IsActive = model.IsActive;
        try { await db.SaveChangesAsync(cancellationToken); return new SaveCustomerResult(SaveCustomerStatus.Saved, customer.Version); }
        catch (DbUpdateConcurrencyException) { return new SaveCustomerResult(SaveCustomerStatus.Conflict); }
    }

    public async Task<IReadOnlyList<PlantEditModel>> GetPlantsAsync(long customerId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Plants.AsNoTracking().Where(x => x.CustomerId == customerId).OrderBy(x => x.Name)
            .Select(x => new PlantEditModel
            {
                Id = x.Id, CustomerId = x.CustomerId, Name = x.Name, PlantCode = x.PlantCode,
                AddressLine1 = x.AddressLine1, AddressLine2 = x.AddressLine2, City = x.City,
                State = x.State, PostalCode = x.PostalCode, Version = x.Version
            }).ToListAsync(cancellationToken);
    }

    public async Task<PlantOperationResult> SavePlantAsync(PlantEditModel model, CancellationToken cancellationToken = default)
    {
        var name = model.Name?.Trim() ?? string.Empty;
        if (name.Length == 0 || name.Length > 200)
            return new PlantOperationResult(PlantOperationStatus.ValidationFailed, Message: "Plant name is required and cannot exceed 200 characters.");
        if (model.PlantCode?.Trim().Length > 100)
            return new PlantOperationResult(PlantOperationStatus.ValidationFailed, Message: "Plant code cannot exceed 100 characters.");
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Customers.AnyAsync(x => x.Id == model.CustomerId, cancellationToken)) return new PlantOperationResult(PlantOperationStatus.NotFound);
        if (await db.Plants.AnyAsync(x => x.CustomerId == model.CustomerId && x.Name == name && x.Id != model.Id, cancellationToken))
            return new PlantOperationResult(PlantOperationStatus.DuplicateName);
        Plant plant;
        if (model.Id == 0)
        {
            plant = new Plant { CustomerId = model.CustomerId };
            plant.CertificationRequirements.Add(new PlantCertificationRequirement
            {
                CertificationTypeId = await GetInspectionSheetTypeIdAsync(db, cancellationToken)
            });
            db.Plants.Add(plant);
        }
        else
        {
            plant = await db.Plants.SingleOrDefaultAsync(x => x.Id == model.Id && x.CustomerId == model.CustomerId, cancellationToken)
                ?? throw new InvalidOperationException("Plant no longer exists.");
            db.Entry(plant).Property(x => x.Version).OriginalValue = model.Version;
        }
        ApplyPlantDetails(plant, model, name);
        try { await db.SaveChangesAsync(cancellationToken); return new PlantOperationResult(PlantOperationStatus.Succeeded, plant.Id, plant.Version); }
        catch (DbUpdateConcurrencyException) { return new PlantOperationResult(PlantOperationStatus.Conflict); }
    }

    public async Task<PlantOperationResult> DeletePlantAsync(long customerId, long plantId, uint version, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var plant = await db.Plants.SingleOrDefaultAsync(x => x.Id == plantId && x.CustomerId == customerId, cancellationToken);
        if (plant is null) return new PlantOperationResult(PlantOperationStatus.NotFound);
        var partAssignments = await db.PartPlants.Where(x => x.PlantId == plantId).ToListAsync(cancellationToken);
        var hasSolePlantAssignment = await db.PartPlants
            .Where(x => x.PlantId == plantId && x.Part.IsActive)
            .AnyAsync(x => !db.PartPlants.Any(other => other.PartId == x.PartId && other.PlantId != plantId), cancellationToken);
        if (hasSolePlantAssignment)
            return new PlantOperationResult(PlantOperationStatus.Blocked, Message: "Assign each part to another plant before deleting this plant.");
        db.Entry(plant).Property(x => x.Version).OriginalValue = version;
        db.PartPlants.RemoveRange(partAssignments);
        db.Plants.Remove(plant);
        try { await db.SaveChangesAsync(cancellationToken); return new PlantOperationResult(PlantOperationStatus.Succeeded); }
        catch (DbUpdateConcurrencyException) { return new PlantOperationResult(PlantOperationStatus.Conflict); }
    }

    public async Task<PlantCertificationDeliveryEditModel?> GetPlantCertificationDeliveryAsync(long plantId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Plants.AsNoTracking().AnyAsync(x => x.Id == plantId, cancellationToken)) return null;
        var requiredIds = await db.PlantCertificationRequirements.AsNoTracking().Where(x => x.PlantId == plantId)
            .Select(x => x.CertificationTypeId).ToHashSetAsync(cancellationToken);
        var settings = await db.PlantCertificationSettings.AsNoTracking().SingleOrDefaultAsync(x => x.PlantId == plantId, cancellationToken);
        return new PlantCertificationDeliveryEditModel
        {
            PlantId = plantId,
            Recipients = await db.PlantCertificationRecipients.AsNoTracking().Where(x => x.PlantId == plantId)
                .OrderBy(x => x.RecipientType).ThenBy(x => x.Name).ThenBy(x => x.EmailAddress)
                .Select(x => new PlantCertificationRecipientEditModel { Id = x.Id, PlantId = x.PlantId, Name = x.Name, EmailAddress = x.EmailAddress, RecipientType = x.RecipientType, Version = x.Version }).ToListAsync(cancellationToken),
            CertificationTypes = await db.CertificationTypes.AsNoTracking().OrderBy(x => x.DisplayOrder)
                .Select(x => new PlantCertificationTypeChoice { Id = x.Id, Name = x.Name, IsRequired = requiredIds.Contains(x.Id) }).ToListAsync(cancellationToken),
            OriginalRequiredCertificationTypeIds = requiredIds,
            FilenameTemplate = settings?.FilenameTemplate,
            SinglePartMultiLotFilenameTemplate = settings?.SinglePartMultiLotFilenameTemplate,
            MultiPartFilenameTemplate = settings?.MultiPartFilenameTemplate,
            SettingsVersion = settings?.Version
        };
    }

    public async Task<PlantCertificationOperationResult> SavePlantCertificationRecipientAsync(PlantCertificationRecipientEditModel model, CancellationToken cancellationToken = default)
    {
        var email = model.EmailAddress?.Trim() ?? string.Empty;
        if (email.Length > 320 || !new EmailAddressAttribute().IsValid(email)) return Failure("Enter a valid email address.");
        if (model.Name?.Trim().Length > 200) return Failure("Recipient name cannot exceed 200 characters.");
        if (!Enum.IsDefined(model.RecipientType)) return Failure("Select To or Cc for the recipient.");
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.Plants.AnyAsync(x => x.Id == model.PlantId, cancellationToken)) return new PlantCertificationOperationResult(PlantCertificationOperationStatus.NotFound);
        PlantCertificationRecipient recipient;
        if (model.Id == 0) { recipient = new PlantCertificationRecipient { PlantId = model.PlantId }; db.PlantCertificationRecipients.Add(recipient); }
        else
        {
            recipient = await db.PlantCertificationRecipients.SingleOrDefaultAsync(x => x.Id == model.Id && x.PlantId == model.PlantId, cancellationToken)
                ?? throw new InvalidOperationException("Recipient no longer exists.");
            db.Entry(recipient).Property(x => x.Version).OriginalValue = model.Version;
        }
        recipient.Name = NormalizeOptionalText(model.Name); recipient.EmailAddress = email; recipient.RecipientType = model.RecipientType;
        try { await db.SaveChangesAsync(cancellationToken); return new PlantCertificationOperationResult(PlantCertificationOperationStatus.Succeeded, recipient.Id, recipient.Version); }
        catch (DbUpdateConcurrencyException) { return new PlantCertificationOperationResult(PlantCertificationOperationStatus.Conflict); }
    }

    public async Task<PlantCertificationOperationResult> DeletePlantCertificationRecipientAsync(long plantId, long recipientId, uint version, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var recipient = await db.PlantCertificationRecipients.SingleOrDefaultAsync(x => x.Id == recipientId && x.PlantId == plantId, cancellationToken);
        if (recipient is null) return new PlantCertificationOperationResult(PlantCertificationOperationStatus.NotFound);
        db.Entry(recipient).Property(x => x.Version).OriginalValue = version; db.PlantCertificationRecipients.Remove(recipient);
        try { await db.SaveChangesAsync(cancellationToken); return new PlantCertificationOperationResult(PlantCertificationOperationStatus.Succeeded); }
        catch (DbUpdateConcurrencyException) { return new PlantCertificationOperationResult(PlantCertificationOperationStatus.Conflict); }
    }

    public async Task<PlantCertificationOperationResult> SavePlantCertificationConfigurationAsync(PlantCertificationDeliveryEditModel model, CertificationPackageFilenameFormatter formatter, CancellationToken cancellationToken = default)
    {
        var template = NormalizeOptionalText(model.FilenameTemplate);
        var singlePartMultiLot = NormalizeOptionalText(model.SinglePartMultiLotFilenameTemplate);
        var multiPart = NormalizeOptionalText(model.MultiPartFilenameTemplate);
        if (template?.Length > 500 || singlePartMultiLot?.Length > 500 || multiPart?.Length > 500) return Failure("Filename templates cannot exceed 500 characters.");
        try
        {
            formatter.Format(template, new CertificationPackageFilenameValues("Example Customer", "ABC123", "856342", "PO-1001", new DateOnly(2026, 8, 28)));
            formatter.FormatSinglePartMultiLot(singlePartMultiLot, new CertificationSinglePartMultiLotPackageFilenameValues("Example Customer", "ABC123", new DateOnly(2026, 9, 1)));
            formatter.FormatMultiPart(multiPart, new CertificationMultiPartPackageFilenameValues("Example Customer", new DateOnly(2026, 9, 1)));
        }
        catch (CertificationFilenameTemplateException exception) { return Failure(exception.Message); }
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await db.Plants.AnyAsync(x => x.Id == model.PlantId, cancellationToken)) return new PlantCertificationOperationResult(PlantCertificationOperationStatus.NotFound);
        var selectedIds = model.CertificationTypes.Where(x => x.IsRequired).Select(x => x.Id).ToHashSet();
        var validIds = await db.CertificationTypes.Where(x => selectedIds.Contains(x.Id)).Select(x => x.Id).ToHashSetAsync(cancellationToken);
        if (!selectedIds.SetEquals(validIds)) return Failure("One or more certification types are no longer available.");
        var existing = await db.PlantCertificationRequirements.Where(x => x.PlantId == model.PlantId).ToListAsync(cancellationToken);
        if (!model.OriginalRequiredCertificationTypeIds.SetEquals(existing.Select(x => x.CertificationTypeId))) return new PlantCertificationOperationResult(PlantCertificationOperationStatus.Conflict);
        db.PlantCertificationRequirements.RemoveRange(existing.Where(x => !selectedIds.Contains(x.CertificationTypeId)));
        db.PlantCertificationRequirements.AddRange(selectedIds.Except(existing.Select(x => x.CertificationTypeId)).Select(id => new PlantCertificationRequirement { PlantId = model.PlantId, CertificationTypeId = id }));
        var settings = await db.PlantCertificationSettings.SingleOrDefaultAsync(x => x.PlantId == model.PlantId, cancellationToken);
        if (settings is not null)
        {
            if (model.SettingsVersion is null) return new PlantCertificationOperationResult(PlantCertificationOperationStatus.Conflict);
            db.Entry(settings).Property(x => x.Version).OriginalValue = model.SettingsVersion.Value;
            if (template is null && singlePartMultiLot is null && multiPart is null) db.PlantCertificationSettings.Remove(settings);
            else { settings.FilenameTemplate = template; settings.SinglePartMultiLotFilenameTemplate = singlePartMultiLot; settings.MultiPartFilenameTemplate = multiPart; }
        }
        else if (model.SettingsVersion is not null) return new PlantCertificationOperationResult(PlantCertificationOperationStatus.Conflict);
        else if (template is not null || singlePartMultiLot is not null || multiPart is not null) { settings = new PlantCertificationSettings { PlantId = model.PlantId, FilenameTemplate = template, SinglePartMultiLotFilenameTemplate = singlePartMultiLot, MultiPartFilenameTemplate = multiPart }; db.PlantCertificationSettings.Add(settings); }
        try
        {
            await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
            return new PlantCertificationOperationResult(PlantCertificationOperationStatus.Succeeded, Version: settings is not null && (template is not null || singlePartMultiLot is not null || multiPart is not null) ? settings.Version : null);
        }
        catch (DbUpdateConcurrencyException) { return new PlantCertificationOperationResult(PlantCertificationOperationStatus.Conflict); }
    }

    private static void ApplyPlantDetails(Plant plant, PlantEditModel model, string name)
    {
        plant.Name = name; plant.PlantCode = NormalizeOptionalText(model.PlantCode);
        plant.AddressLine1 = NormalizeOptionalText(model.AddressLine1); plant.AddressLine2 = NormalizeOptionalText(model.AddressLine2);
        plant.City = NormalizeOptionalText(model.City); plant.State = NormalizeOptionalText(model.State); plant.PostalCode = NormalizeOptionalText(model.PostalCode);
    }

    private static PlantCertificationOperationResult Failure(string message) => new(PlantCertificationOperationStatus.ValidationFailed, Message: message);
    private static string? NormalizeOptionalText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<long> GetInspectionSheetTypeIdAsync(AppDbContext db, CancellationToken cancellationToken) =>
        await db.CertificationTypes
            .Where(x => x.Name == "Inspection Sheet")
            .Select(x => x.Id)
            .SingleAsync(cancellationToken);
}
