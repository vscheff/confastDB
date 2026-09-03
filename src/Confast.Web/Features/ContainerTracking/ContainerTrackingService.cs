using System.Data;
using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Features.ContainerTracking;

public sealed class ContainerTrackingService(
    IDbContextFactory<AppDbContext> contextFactory, TrackingAccess access, TimeProvider clock)
{
    public DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    public async Task<List<ShipmentSummary>> SearchAsync(string? search = null, CancellationToken cancellationToken = default)
    {
        await access.GetAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Shipments.AsNoTracking().AsSplitQuery();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(s => s.BillNumbers.Any(b => b.Number.ToUpper().Contains(term)) ||
                s.Containers.Any(c => c.ContainerNumber.ToUpper().Contains(term) ||
                    (c.CbpNumber != null && c.CbpNumber.ToUpper().Contains(term)) || c.Groups.Any(g =>
                    g.BillOfLading.Number.Contains(term) || g.BillOfLading.Supplier.Name.ToUpper().Contains(term) ||
                    (g.InvoiceNumber != null && g.InvoiceNumber.ToUpper().Contains(term)) ||
                    g.Parts.Any(p => p.Part.PartNumber.ToUpper().Contains(term)))));
        }
        return await query.OrderByDescending(x => x.Id).Select(s => new ShipmentSummary(s.Id, s.Version, s.FreightCost,
            s.BillNumbers.OrderBy(b => b.Id).Select(b => b.Number).ToList(),
            s.Containers.OrderBy(c => c.Id).Select(c => new ContainerSummary(c.Id, c.ContainerNumber, c.CbpNumber,
                c.EstimatedDepartureDate, c.EstimatedArrivalDate, c.ReceivedDate, c.AddedToProductionSchedule,
                c.Groups.Count, c.Groups.Sum(g => g.PalletCount ?? 0), c.Groups.Sum(g => g.TotalWeight ?? 0))).ToList()))
            .ToListAsync(cancellationToken);
    }

    public async Task<ShipmentEditModel?> GetShipmentAsync(long id, CancellationToken cancellationToken = default)
    {
        await access.GetAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Shipments.AsNoTracking().Where(x => x.Id == id).Select(x => new ShipmentEditModel
        {
            Id = x.Id, Version = x.Version, FreightCost = x.FreightCost,
            BillNumbers = x.BillNumbers.OrderBy(b => b.Id).Select(b => new ShipmentBillEditModel
            {
                Id = b.Id,
                Number = b.Number
            }).ToList()
        }).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<TrackingSaveResult> SaveShipmentAsync(ShipmentEditModel model, CancellationToken cancellationToken = default)
    {
        await access.RequireEditAsync(cancellationToken);
        if (model.BillNumbers.Count == 0) return TrackingSaveResult.Invalid("A shipment needs at least one bill number.");
        foreach (var number in model.BillNumbers)
        {
            number.Number = number.Number?.Trim() ?? string.Empty;
            if (TrackingValidation.Validate(number) is { } error) return TrackingSaveResult.Invalid(error);
        }
        if (!TrackingValidation.ValidMoney(model.FreightCost))
            return TrackingSaveResult.Invalid("Freight cost must be a nonnegative amount with at most two decimal places.");
        if (HasRepeatedIds(model.BillNumbers.Select(x => x.Id))) return TrackingSaveResult.Invalid("A shipment bill row was included more than once.");
        return await MutateAsync(async db =>
        {
            var shipment = model.Id == 0 ? new Shipment() : await db.Shipments.Include(x => x.BillNumbers)
                .SingleOrDefaultAsync(x => x.Id == model.Id, cancellationToken);
            if (shipment is null) return TrackingSaveResult.Invalid("Shipment no longer exists.");
            if (model.Id != 0 && shipment.Version != model.Version) return TrackingSaveResult.Conflict();
            if (model.BillNumbers.Any(x => x.Id != 0 && !shipment.BillNumbers.Any(b => b.Id == x.Id)))
                return TrackingSaveResult.Invalid("A bill number does not belong to this shipment.");
            if (model.Id == 0) db.Shipments.Add(shipment);
            foreach (var removed in shipment.BillNumbers.Where(b => !model.BillNumbers.Any(x => x.Id == b.Id)).ToList())
                db.ShipmentBillNumbers.Remove(removed);
            foreach (var input in model.BillNumbers)
            {
                var bill = input.Id == 0 ? new ShipmentBillNumber() : shipment.BillNumbers.Single(x => x.Id == input.Id);
                if (input.Id == 0) shipment.BillNumbers.Add(bill);
                bill.Number = input.Number;
            }
            shipment.FreightCost = model.FreightCost;
            shipment.UpdatedAtUtc = clock.GetUtcNow();
            if (model.Id != 0) db.Entry(shipment).Property(x => x.UpdatedAtUtc).IsModified = true;
            await db.SaveChangesAsync(cancellationToken);
            return new(true, Id: shipment.Id, Version: shipment.Version);
        }, cancellationToken);
    }

    public async Task<TrackingSaveResult> DeleteShipmentAsync(long id, uint version, CancellationToken cancellationToken = default)
    {
        await access.RequireEditAsync(cancellationToken);
        return await MutateAsync(async db =>
        {
            var shipment = await db.Shipments.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (shipment is null) return TrackingSaveResult.Invalid("Shipment no longer exists.");
            if (shipment.Version != version) return TrackingSaveResult.Conflict();
            if (await db.Containers.AnyAsync(x => x.ShipmentId == id, cancellationToken))
                return TrackingSaveResult.Invalid("Delete this shipment's containers before deleting the shipment.");

            // Shipment bill numbers are owned rows and cascade with the empty shipment.
            db.Shipments.Remove(shipment);
            await db.SaveChangesAsync(cancellationToken);
            return new(true);
        }, cancellationToken);
    }

    public async Task<ContainerDetail?> GetContainerAsync(long id, CancellationToken cancellationToken = default)
    {
        await access.GetAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // A single statement keeps this bounded aggregate and its concurrency token consistent.
        return await db.Containers.AsNoTracking().AsSingleQuery().Where(c => c.Id == id).Select(c => new ContainerDetail(
            new ContainerEditModel
            {
                Id = c.Id, ShipmentId = c.ShipmentId, Version = c.Version, ContainerNumber = c.ContainerNumber, CbpNumber = c.CbpNumber,
                ReceivedDate = c.ReceivedDate, QuotedRate = c.QuotedRate, DrayageCharge = c.DrayageCharge,
                EstimatedDepartureDate = c.EstimatedDepartureDate, EstimatedArrivalDate = c.EstimatedArrivalDate,
                AddedToProductionSchedule = c.AddedToProductionSchedule
            }, new ContainerContentsEditModel
            {
                ContainerId = c.Id, Version = c.Version,
                Groups = c.Groups.OrderBy(g => g.Id).Select(g => new ContainerGroupEditModel
                {
                    Id = g.Id, BillOfLadingId = g.BillOfLadingId, TotalWeight = g.TotalWeight,
                    PalletCount = g.PalletCount, InvoiceNumber = g.InvoiceNumber, CertificationsReceived = g.CertificationsReceived,
                    Parts = g.Parts.OrderBy(p => p.Id).Select(p => new ContainerPartEditModel
                    {
                        Id = p.Id, PartId = p.PartId, PurchaseOrderNumber = p.PurchaseOrderNumber, Quantity = p.Quantity
                    }).ToList()
                }).ToList()
            }, c.Shipment.BillNumbers.OrderBy(b => b.Id).Select(b => b.Number).ToList()))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<TrackingSaveResult> SaveContainerAsync(ContainerEditModel model, CancellationToken cancellationToken = default)
    {
        var permissions = await access.RequireEditAsync(cancellationToken);
        model.ContainerNumber = model.ContainerNumber?.Trim() ?? string.Empty;
        model.CbpNumber = string.IsNullOrWhiteSpace(model.CbpNumber) ? null : model.CbpNumber.Trim();
        if (TrackingValidation.Validate(model) is { } error) return TrackingSaveResult.Invalid(error);
        if (!TrackingValidation.ValidMoney(model.QuotedRate) || !TrackingValidation.ValidMoney(model.DrayageCharge))
            return TrackingSaveResult.Invalid("Charges must be nonnegative amounts with at most two decimal places.");
        return await MutateAsync(async db =>
        {
            if (!await db.Shipments.AnyAsync(x => x.Id == model.ShipmentId, cancellationToken))
                return TrackingSaveResult.Invalid("Select an existing shipment.");
            var container = model.Id == 0 ? new Container { ShipmentId = model.ShipmentId } :
                await db.Containers.SingleOrDefaultAsync(x => x.Id == model.Id && x.ShipmentId == model.ShipmentId, cancellationToken);
            if (container is null) return TrackingSaveResult.Invalid("Container does not belong to this shipment.");
            if (model.Id != 0 && container.Version != model.Version) return TrackingSaveResult.Conflict();
            // Read the shared bills in this serializable transaction so concurrent B/L corrections
            // cannot race a departure change or a new assignment.
            if (model.Id != 0)
                await db.ContainerGroups.Where(x => x.ContainerId == container.Id).Select(x => x.BillOfLading.Version).ToListAsync(cancellationToken);
            if (!ContainerEditPolicy.CanEditMetadata(container.EstimatedDepartureDate, Today, permissions.IsAdministrator) &&
                (container.ContainerNumber != model.ContainerNumber || container.EstimatedDepartureDate != model.EstimatedDepartureDate ||
                 container.EstimatedArrivalDate != model.EstimatedArrivalDate || container.QuotedRate != model.QuotedRate ||
                 container.DrayageCharge != model.DrayageCharge || container.CbpNumber != model.CbpNumber))
                return TrackingSaveResult.Invalid("Departed / Locked. Only an administrator can correct container metadata; receipt and schedule status can still be recorded.");
            if (model.Id == 0) db.Containers.Add(container);
            container.ContainerNumber = model.ContainerNumber;
            container.CbpNumber = model.CbpNumber;
            container.EstimatedDepartureDate = model.EstimatedDepartureDate;
            container.EstimatedArrivalDate = model.EstimatedArrivalDate;
            container.QuotedRate = model.QuotedRate;
            container.DrayageCharge = model.DrayageCharge;
            container.ReceivedDate = model.ReceivedDate;
            container.AddedToProductionSchedule = model.AddedToProductionSchedule;
            // The root token protects metadata and all content rows as one aggregate.
            if (model.Id != 0) db.Entry(container).Property(x => x.ContainerNumber).IsModified = true;
            await db.SaveChangesAsync(cancellationToken);
            return new(true, Id: container.Id, Version: container.Version);
        }, cancellationToken);
    }

    public async Task<TrackingSaveResult> DeleteContainerAsync(long id, uint version, CancellationToken cancellationToken = default)
    {
        var permissions = await access.RequireEditAsync(cancellationToken);
        return await MutateAsync(async db =>
        {
            var container = await db.Containers.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (container is null) return TrackingSaveResult.Invalid("Container no longer exists.");
            if (container.Version != version) return TrackingSaveResult.Conflict();
            if (!ContainerEditPolicy.CanEditMetadata(container.EstimatedDepartureDate, Today, permissions.IsAdministrator))
                return TrackingSaveResult.Invalid("Departed / Locked. Only an administrator can delete a departed container.");

            // Database cascades remove the container's owned groups and part lines. B/Ls are shared records and remain.
            db.Containers.Remove(container);
            await db.SaveChangesAsync(cancellationToken);
            return new(true);
        }, cancellationToken);
    }

    public async Task<TrackingSaveResult> SaveContentsAsync(ContainerContentsEditModel model, CancellationToken cancellationToken = default)
    {
        await access.RequireEditAsync(cancellationToken);
        if (HasRepeatedIds(model.Groups.Select(x => x.Id)) || HasRepeatedIds(model.Groups.SelectMany(x => x.Parts).Select(x => x.Id)))
            return TrackingSaveResult.Invalid("A group or part row was included more than once.");
        foreach (var group in model.Groups)
        {
            group.InvoiceNumber = string.IsNullOrWhiteSpace(group.InvoiceNumber) ? null : group.InvoiceNumber.Trim();
            if (TrackingValidation.Validate(group) is { } error) return TrackingSaveResult.Invalid(error);
            if (group.TotalWeight is { } weight && decimal.Round(weight, 3) != weight)
                return TrackingSaveResult.Invalid("Weight supports at most three decimal places.");
            foreach (var part in group.Parts)
            {
                part.PurchaseOrderNumber = part.PurchaseOrderNumber?.Trim() ?? string.Empty;
                if (TrackingValidation.Validate(part) is { } lineError) return TrackingSaveResult.Invalid(lineError);
            }
        }
        return await MutateAsync(async db =>
        {
            var container = await db.Containers.AsSingleQuery().Include(x => x.Groups).ThenInclude(x => x.Parts)
                .SingleOrDefaultAsync(x => x.Id == model.ContainerId, cancellationToken);
            if (container is null) return TrackingSaveResult.Invalid("Container no longer exists.");
            if (container.Version != model.Version) return TrackingSaveResult.Conflict();
            if (!ContainerEditPolicy.CanEditContents(container.EstimatedDepartureDate, Today))
                return TrackingSaveResult.Invalid("Departed / Locked. Container groups and part lines cannot be changed.");
            foreach (var input in model.Groups)
            {
                var existing = container.Groups.SingleOrDefault(x => x.Id == input.Id);
                if (input.Id != 0 && existing is null) return TrackingSaveResult.Invalid("A group does not belong to this container.");
                if (input.Parts.Any(x => x.Id != 0 && (existing is null || !existing.Parts.Any(p => p.Id == x.Id))))
                    return TrackingSaveResult.Invalid("A part line does not belong to this group.");
            }
            var billIds = model.Groups.Select(x => x.BillOfLadingId).Distinct().ToArray();
            var billSuppliers = await db.BillsOfLading.Where(x => billIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.SupplierId, cancellationToken);
            if (billSuppliers.Count != billIds.Length)
                return TrackingSaveResult.Invalid("Select an existing B/L for every group.");
            var partIds = model.Groups.SelectMany(x => x.Parts).Select(x => x.PartId).Distinct().ToArray();
            var partSuppliers = await db.Parts.Where(x => partIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, x => x.SupplierId, cancellationToken);
            if (partSuppliers.Count != partIds.Length)
                return TrackingSaveResult.Invalid("Select an existing Part for every line.");
            if (model.Groups.Any(group => group.Parts.Any(part =>
                    partSuppliers[part.PartId] != billSuppliers[group.BillOfLadingId])))
                return TrackingSaveResult.Invalid("Each part must have the same supplier as its group's B/L.");
            foreach (var removed in container.Groups.Where(g => !model.Groups.Any(x => x.Id == g.Id)).ToList())
                db.ContainerGroups.Remove(removed);
            foreach (var input in model.Groups)
            {
                var group = input.Id == 0 ? new ContainerGroup() : container.Groups.Single(x => x.Id == input.Id);
                if (input.Id == 0) container.Groups.Add(group);
                group.BillOfLadingId = input.BillOfLadingId;
                group.TotalWeight = input.TotalWeight;
                group.PalletCount = input.PalletCount;
                group.InvoiceNumber = input.InvoiceNumber;
                group.CertificationsReceived = input.CertificationsReceived;
                foreach (var removed in group.Parts.Where(p => !input.Parts.Any(x => x.Id == p.Id)).ToList())
                    db.ContainerGroupParts.Remove(removed);
                foreach (var row in input.Parts)
                {
                    var part = row.Id == 0 ? new ContainerGroupPart() : group.Parts.Single(x => x.Id == row.Id);
                    if (row.Id == 0) group.Parts.Add(part);
                    part.PartId = row.PartId;
                    part.PurchaseOrderNumber = row.PurchaseOrderNumber;
                    part.Quantity = row.Quantity!.Value;
                }
            }
            db.Entry(container).Property(x => x.ContainerNumber).IsModified = true;
            await db.SaveChangesAsync(cancellationToken);
            return new(true, Id: container.Id, Version: container.Version);
        }, cancellationToken);
    }

    public async Task<List<BillOfLadingChoice>> GetBillsAsync(CancellationToken cancellationToken = default)
    {
        await access.GetAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.BillsOfLading.AsNoTracking().OrderBy(x => x.Number)
            .Select(x => new BillOfLadingChoice(x.Id, x.SupplierId, x.Number, x.Supplier.Name, x.Duty)).ToListAsync(cancellationToken);
    }

    public async Task<List<TrackingChoice>> GetPartsAsync(CancellationToken cancellationToken = default)
    {
        await access.GetAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.Parts.AsNoTracking().OrderBy(x => x.PartNumber).ThenBy(x => x.Customer.Name)
            .Select(x => new TrackingChoice(x.Id, x.SupplierId,
                x.PartNumber + " — " + x.Customer.Name + (x.IsActive ? "" : " (inactive)")))
            .ToListAsync(cancellationToken);
    }

    public async Task<BillOfLadingEditModel?> GetBillAsync(long id, CancellationToken cancellationToken = default)
    {
        await access.GetAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await db.BillsOfLading.AsNoTracking().Where(x => x.Id == id).Select(x => new BillOfLadingEditModel
        { Id = x.Id, Version = x.Version, Number = x.Number, SupplierId = x.SupplierId, Duty = x.Duty }).SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<TrackingSaveResult> SaveBillAsync(BillOfLadingEditModel model, CancellationToken cancellationToken = default)
    {
        var permissions = await access.RequireEditAsync(cancellationToken);
        model.Number = model.Number?.Trim().ToUpperInvariant() ?? string.Empty;
        if (TrackingValidation.Validate(model) is { } error) return TrackingSaveResult.Invalid(error);
        if (!TrackingValidation.ValidMoney(model.Duty)) return TrackingSaveResult.Invalid("Duty must be a nonnegative amount with at most two decimal places.");
        var result = await MutateAsync(async db =>
        {
            var duplicate = await db.BillsOfLading.Where(x => x.Number == model.Number && x.Id != model.Id)
                .Select(x => (long?)x.Id).SingleOrDefaultAsync(cancellationToken);
            if (duplicate is not null) return new(false, "This B/L number already exists. Select the existing B/L.", ExistingBillOfLadingId: duplicate);
            var bill = model.Id == 0 ? new BillOfLading() : await db.BillsOfLading.FindAsync([model.Id], cancellationToken);
            if (bill is null) return TrackingSaveResult.Invalid("B/L no longer exists.");
            if (model.Id != 0 && bill.Version != model.Version) return TrackingSaveResult.Conflict();
            var supplier = await db.Suppliers.FindAsync([model.SupplierId], cancellationToken);
            if (supplier is null || (!supplier.IsActive && bill.SupplierId != supplier.Id))
                return TrackingSaveResult.Invalid("Select an active supplier. An existing inactive supplier may be retained.");
            if (model.Id != 0 && !permissions.IsAdministrator)
            {
                var departures = await db.ContainerGroups.Where(x => x.BillOfLadingId == bill.Id)
                    .Select(x => x.Container.EstimatedDepartureDate).ToListAsync(cancellationToken);
                if (departures.Any(x => ContainerEditPolicy.HasDeparted(x, Today)))
                    return TrackingSaveResult.Invalid("This B/L is shared with a departed container. Only an administrator can correct it.");
            }
            if (model.Id == 0) db.BillsOfLading.Add(bill);
            bill.Number = model.Number;
            bill.SupplierId = model.SupplierId;
            bill.Duty = model.Duty;
            await db.SaveChangesAsync(cancellationToken);
            return new(true, Id: bill.Id, Version: bill.Version);
        }, cancellationToken);
        // A concurrent insert can win after the friendly precheck. Resolve its identity after rollback.
        if (!result.Succeeded && result.ExistingBillOfLadingId is null)
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var existingId = await db.BillsOfLading.Where(x => x.Number == model.Number && x.Id != model.Id)
                .Select(x => (long?)x.Id).SingleOrDefaultAsync(cancellationToken);
            if (existingId is not null) return new(false, "This B/L number already exists. Select the existing B/L.", ExistingBillOfLadingId: existingId);
        }
        return result;
    }

    private async Task<TrackingSaveResult> MutateAsync(Func<AppDbContext, Task<TrackingSaveResult>> action, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        // Covers aggregate edits and the shared B/L/departure check without a check-then-write race.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        try
        {
            var result = await action(db);
            if (result.Succeeded) await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateConcurrencyException) { return TrackingSaveResult.Conflict(); }
        catch (Exception exception) when (exception.GetBaseException() is PostgresException
            { SqlState: PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected })
        { return TrackingSaveResult.Conflict(); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UX_bills_of_lading_number" })
        { return TrackingSaveResult.Invalid("This B/L number already exists."); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        { return TrackingSaveResult.Invalid("A related record was removed or changed. Reload and select an existing record."); }
    }

    private static bool HasRepeatedIds(IEnumerable<long> ids)
    {
        var persistedIds = ids.Where(x => x != 0).ToList();
        return persistedIds.Count != persistedIds.Distinct().Count();
    }
}
