using System.Data;
using Confast.Web.Data;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Inspections;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Confast.Web.Features.ContainerTracking;

public sealed class ReceivedPartsService(
    IDbContextFactory<AppDbContext> contextFactory,
    TrackingAccess trackingAccess,
    InspectionService inspectionService,
    ICurrentUser currentUser,
    TimeProvider clock)
{
    public DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    public async Task<ReceiptOperationResult> ReceiveContainerAsync(
        long containerId,
        uint version,
        DateOnly receivedDate,
        string? correctionReason = null,
        CancellationToken cancellationToken = default)
    {
        await trackingAccess.RequireEditAsync(cancellationToken);
        var userId = await RequireUserIdAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var container = await db.Containers
            .FromSqlInterpolated($"SELECT c.*, c.xmin FROM containers AS c WHERE id = {containerId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (container is null) return ReceiptOperationResult.Invalid("Container no longer exists.");
        if (container.Version != version) return ReceiptOperationResult.Conflict();
        if (receivedDate > Today) return ReceiptOperationResult.Invalid("Received date cannot be in the future.");
        if (container.ReceivedDate == receivedDate && container.ReceivedAtUtc is not null) return new(true);
        if (container.ReceivedDate == receivedDate && string.IsNullOrWhiteSpace(correctionReason))
            return ReceiptOperationResult.Invalid("Explain why this historical receipt is being reconciled.");
        if (container.ReceivedDate is not null && string.IsNullOrWhiteSpace(correctionReason))
            return ReceiptOperationResult.Invalid("Explain why the received date is being corrected.");

        var previousDate = container.ReceivedDate;
        container.ReceivedDate = receivedDate;
        if (previousDate is null || container.ReceivedAtUtc is null)
        {
            container.ReceivedAtUtc = clock.GetUtcNow();
            container.ReceivedByUserId = userId;
            await db.ContainerGroupParts
                .Where(x => x.ContainerGroup.ContainerId == containerId && x.ActualReceivedQuantity == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ActualReceivedQuantity, x => x.Quantity), cancellationToken);
        }

        db.ContainerReceiptHistory.Add(new ContainerReceiptHistory
        {
            ContainerId = containerId,
            PreviousReceivedDate = previousDate,
            ReceivedDate = receivedDate,
            Reason = NormalizeOptional(correctionReason),
            ActingUserId = userId,
            PerformedAtUtc = clock.GetUtcNow()
        });
        db.Entry(container).Property(x => x.ContainerNumber).IsModified = true;
        return await SaveAsync(db, transaction, cancellationToken);
    }

    public async Task<ReceiptOperationResult> CorrectActualReceivedQuantityAsync(
        long lineId,
        uint containerVersion,
        int actualQuantity,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await trackingAccess.RequireEditAsync(cancellationToken);
        var userId = await RequireUserIdAsync(cancellationToken);
        if (actualQuantity < 0) return ReceiptOperationResult.Invalid("Actual received quantity cannot be negative.");
        if (string.IsNullOrWhiteSpace(reason)) return ReceiptOperationResult.Invalid("A correction reason is required.");
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var line = await LockLineAsync(db, lineId, cancellationToken);
        if (line is null) return ReceiptOperationResult.Invalid("Received part line no longer exists.");
        var container = await GetContainerAsync(db, line.ContainerGroupId, cancellationToken);
        if (container is null || container.ReceivedDate is null) return ReceiptOperationResult.Invalid("Only a received container can be corrected.");
        if (container.Version != containerVersion) return ReceiptOperationResult.Conflict();
        var allocated = await ActiveAllocatedQuantityAsync(db, lineId, cancellationToken);
        if (actualQuantity < allocated)
            return ReceiptOperationResult.Invalid($"Actual received quantity cannot be less than the {allocated:N0} already allocated.");
        var previous = line.ActualReceivedQuantity ?? line.Quantity;
        if (previous == actualQuantity) return new(true);
        line.ActualReceivedQuantity = actualQuantity;
        db.ContainerReceivedQuantityHistory.Add(new ContainerReceivedQuantityHistory
        {
            ContainerGroupPartId = lineId,
            PreviousQuantity = previous,
            NewQuantity = actualQuantity,
            Reason = reason.Trim(),
            ActingUserId = userId,
            PerformedAtUtc = clock.GetUtcNow()
        });
        db.Entry(container).Property(x => x.ContainerNumber).IsModified = true;
        return await SaveAsync(db, transaction, cancellationToken);
    }

    public async Task<IReadOnlyList<ReceivedContainerItem>> GetReceivedPartsAsync(
        string? search,
        bool includeFullyAssigned,
        CancellationToken cancellationToken = default)
    {
        await RequireInspectorAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var term = NormalizeOptional(search)?.ToUpperInvariant();
        var lines = await db.ContainerGroupParts.AsNoTracking()
            .Where(x => x.ContainerGroup.Container.ReceivedDate != null && x.ActualReceivedQuantity != null)
            .Where(x => term == null
                || x.ContainerGroup.Container.ContainerNumber.ToUpper().Contains(term)
                || x.Part.PartNumber.ToUpper().Contains(term)
                || x.PurchaseOrderNumber.ToUpper().Contains(term))
            .OrderByDescending(x => x.ContainerGroup.Container.ReceivedDate)
            .ThenBy(x => x.ContainerGroup.Container.ContainerNumber)
            .ThenBy(x => x.Id)
            .Select(x => new
            {
                x.Id,
                x.PartId,
                x.Part.PartNumber,
                CustomerName = x.Part.Customer.Name,
                x.PurchaseOrderNumber,
                SupplierName = x.ContainerGroup.BillOfLading.Supplier.Name,
                BillNumber = x.ContainerGroup.BillOfLading.Number,
                ExpectedQuantity = x.Quantity,
                ActualQuantity = x.ActualReceivedQuantity!.Value,
                ContainerId = x.ContainerGroup.Container.Id,
                x.ContainerGroup.Container.ContainerNumber,
                ReceivedDate = x.ContainerGroup.Container.ReceivedDate!.Value,
                ContainerVersion = x.ContainerGroup.Container.Version,
                HasCandidates = db.Inspections.Any(i => i.PartId == x.PartId
                    && i.ConformancePoNumber != null
                    && i.ConformancePoNumber.Trim().ToUpper() == x.PurchaseOrderNumber.Trim().ToUpper()),
                Allocations = x.ReceiptAllocations.OrderByDescending(a => a.PerformedAtUtc).Select(a => new
                {
                    a.Id, a.InspectionId, a.ManufacturerLotNumber, a.InternalLotNumber, a.Quantity,
                    a.Action, a.PerformedAtUtc, a.ActingUserId, a.ReversedAtUtc, a.ReversalReason
                }).ToList()
            })
            .ToListAsync(cancellationToken);

        var inspectionIds = lines.SelectMany(x => x.Allocations).Where(x => x.InspectionId != null)
            .Select(x => x.InspectionId!.Value).Distinct().ToArray();
        var statuses = (await inspectionService.GetInspectionsAsync(inspectionIds, cancellationToken))
            .ToDictionary(x => x.Id);
        var mapped = lines.Select(x =>
        {
            var activeAllocated = x.Allocations.Where(a => a.ReversedAtUtc is null).Sum(a => a.Quantity);
            var remaining = x.ActualQuantity - activeAllocated;
            var status = remaining == 0 ? ReceiptAllocationStatus.FullyAssigned
                : activeAllocated == 0 ? ReceiptAllocationStatus.Unassigned
                : ReceiptAllocationStatus.PartiallyAssigned;
            return new
            {
                x.ContainerId, x.ContainerNumber, x.ReceivedDate,
                Line = new ReceivedPartLineItem(x.Id, x.PartId, x.PartNumber, x.CustomerName,
                    x.PurchaseOrderNumber, x.SupplierName, x.BillNumber, x.ExpectedQuantity,
                    x.ActualQuantity, activeAllocated, remaining, status, x.HasCandidates, x.ContainerVersion,
                    x.Allocations.Select(a =>
                    {
                        var inspectionStatus = a.InspectionId is long id ? statuses.GetValueOrDefault(id) : null;
                        return new ReceivedPartAllocationItem(a.Id, a.InspectionId, a.ManufacturerLotNumber,
                            a.InternalLotNumber, a.Quantity, a.Action, a.PerformedAtUtc, a.ActingUserId,
                            a.ReversedAtUtc, a.ReversalReason, inspectionStatus?.Accepted ?? false,
                            inspectionStatus?.Completed ?? false);
                    }).ToList())
            };
        }).Where(x => includeFullyAssigned || x.Line.AllocationStatus != ReceiptAllocationStatus.FullyAssigned)
          .GroupBy(x => new { x.ContainerId, x.ContainerNumber, x.ReceivedDate })
          .Select(g => new ReceivedContainerItem(g.Key.ContainerId, g.Key.ContainerNumber, g.Key.ReceivedDate,
              g.Select(x => x.Line).ToList()))
          .ToList();
        return mapped;
    }

    public async Task<IReadOnlyList<ReceiptInspectionCandidate>> GetCandidatesAsync(
        long lineId,
        CancellationToken cancellationToken = default)
    {
        await RequireInspectorAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var source = await db.ContainerGroupParts.AsNoTracking().Where(x => x.Id == lineId)
            .Select(x => new { x.PartId, Po = x.PurchaseOrderNumber.Trim().ToUpper() })
            .SingleOrDefaultAsync(cancellationToken);
        if (source is null) return [];
        var inspections = await db.Inspections.AsNoTracking()
            .Where(x => x.PartId == source.PartId && x.ConformancePoNumber != null
                && x.ConformancePoNumber.Trim().ToUpper() == source.Po)
            .Select(x => new { x.Id, x.LotNumber, x.ManufacturerLotNumber, x.Part.PartNumber,
                x.ConformancePoNumber, Quantity = x.QuantityReceived ?? 0, x.Version })
            .OrderByDescending(x => x.Id).ToListAsync(cancellationToken);
        var statuses = (await inspectionService.GetInspectionsAsync(inspections.Select(x => x.Id).ToArray(), cancellationToken))
            .ToDictionary(x => x.Id);
        return inspections.Select(x => new ReceiptInspectionCandidate(x.Id, x.LotNumber, x.ManufacturerLotNumber,
            x.PartNumber, x.ConformancePoNumber, x.Quantity, statuses.GetValueOrDefault(x.Id)?.Accepted ?? false,
            statuses.GetValueOrDefault(x.Id)?.Completed ?? false, x.Version)).ToList();
    }

    public async Task<ReceiptOperationResult> BeginInspectionAsync(
        BeginReceiptInspectionModel model,
        CancellationToken cancellationToken = default)
    {
        await RequireInspectorAsync(cancellationToken);
        var userId = await RequireUserIdAsync(cancellationToken);
        var manufacturerLot = NormalizeOptional(model.ManufacturerLotNumber);
        var internalLot = NormalizeOptional(model.InternalLotNumber);
        if (manufacturerLot is null) return ReceiptOperationResult.Invalid("Manufacturer's lot is required.");
        if (internalLot is null) return ReceiptOperationResult.Invalid("Internal lot number is required.");
        if (model.Quantity <= 0) return ReceiptOperationResult.Invalid("Allocation quantity must be greater than zero.");

        await using (var lookup = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var existing = await lookup.ContainerReceiptAllocations.AsNoTracking()
                .Where(x => x.OperationId == model.OperationId)
                .Select(x => new { x.InspectionId }).SingleOrDefaultAsync(cancellationToken);
            if (existing is not null) return new(true, InspectionId: existing.InspectionId);
        }

        await using var sourceDb = await contextFactory.CreateDbContextAsync(cancellationToken);
        var source = await sourceDb.ContainerGroupParts.AsNoTracking().Where(x => x.Id == model.ContainerGroupPartId)
            .Select(x => new { x.PartId, x.PurchaseOrderNumber, x.ContainerGroup.Container.ReceivedDate })
            .SingleOrDefaultAsync(cancellationToken);
        if (source is null) return ReceiptOperationResult.Invalid("Received part line no longer exists.");
        if (source.ReceivedDate is null) return ReceiptOperationResult.Invalid("Only received containers can be allocated.");
        var create = new CreateInspectionModel
        {
            PartId = source.PartId,
            LotNumber = internalLot,
            ConformancePoNumber = source.PurchaseOrderNumber,
            ManufacturerLotNumber = manufacturerLot,
            DateReceived = source.ReceivedDate,
            QuantityReceived = model.Quantity,
            QuantityInspected = InspectionSamplingPlan.GetQuantityInspected(model.Quantity),
            Inspector = model.Inspector,
            InspectionDate = Today
        };
        var result = await inspectionService.CreateInspectionWithCallbackAsync(create, async (db, inspection, ct) =>
        {
            var line = await LockLineAsync(db, model.ContainerGroupPartId, ct);
            if (line is null) return new InspectionOperationResult(InspectionOperationStatus.NotFound, Message: "Received part line no longer exists.");
            var container = await GetContainerAsync(db, line.ContainerGroupId, ct);
            if (container?.ReceivedDate is null) return new InspectionOperationResult(InspectionOperationStatus.ValidationFailed, Message: "Only received containers can be allocated.");
            if (container.Version != model.ContainerVersion) return new InspectionOperationResult(InspectionOperationStatus.Conflict);
            if (inspection.PartId != line.PartId
                || NormalizeIdentifier(inspection.ConformancePoNumber) != NormalizeIdentifier(line.PurchaseOrderNumber))
                return new InspectionOperationResult(InspectionOperationStatus.ValidationFailed, Message: "The source Part or PO changed. Reload before creating the inspection.");
            var remaining = (line.ActualReceivedQuantity ?? -1) - await ActiveAllocatedQuantityAsync(db, line.Id, ct);
            if (model.Quantity > remaining) return new InspectionOperationResult(InspectionOperationStatus.ValidationFailed, Message: $"Only {Math.Max(0, remaining):N0} remain to allocate.");
            db.ContainerReceiptAllocations.Add(new ContainerReceiptAllocation
            {
                OperationId = model.OperationId, ContainerGroupPartId = line.Id, Inspection = inspection,
                Quantity = model.Quantity, ManufacturerLotNumber = manufacturerLot, InternalLotNumber = internalLot,
                Action = ReceiptAllocationAction.BeginInspection, ActingUserId = userId, PerformedAtUtc = clock.GetUtcNow()
            });
            db.Entry(container).Property(x => x.ContainerNumber).IsModified = true;
            return null;
        }, cancellationToken);
        return result.Status == InspectionOperationStatus.Succeeded
            ? new(true, InspectionId: result.InspectionId)
            : ReceiptOperationResult.Invalid(result.Message ?? "The inspection allocation could not be saved. Reload and try again.");
    }

    public async Task<ReceiptOperationResult> BumpUpAsync(BumpUpReceiptModel model, CancellationToken cancellationToken = default)
    {
        await RequireInspectorAsync(cancellationToken);
        var userId = await RequireUserIdAsync(cancellationToken);
        var manufacturerLot = NormalizeOptional(model.ManufacturerLotNumber);
        if (manufacturerLot is null) return ReceiptOperationResult.Invalid("Manufacturer's lot is required.");
        if (model.Quantity <= 0) return ReceiptOperationResult.Invalid("Incoming quantity must be greater than zero.");
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var existing = await db.ContainerReceiptAllocations.AsNoTracking().Where(x => x.OperationId == model.OperationId)
            .Select(x => new { x.InspectionId }).SingleOrDefaultAsync(cancellationToken);
        if (existing is not null) return new(true, InspectionId: existing.InspectionId);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var line = await LockLineAsync(db, model.ContainerGroupPartId, cancellationToken);
        if (line is null) return ReceiptOperationResult.Invalid("Received part line no longer exists.");
        var container = await GetContainerAsync(db, line.ContainerGroupId, cancellationToken);
        if (container?.ReceivedDate is null) return ReceiptOperationResult.Invalid("Only received containers can be allocated.");
        if (container.Version != model.ContainerVersion) return ReceiptOperationResult.Conflict();
        var inspection = await db.Inspections
            .FromSqlInterpolated($"SELECT i.*, i.xmin FROM inspections AS i WHERE id = {model.InspectionId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (inspection is null) return ReceiptOperationResult.Invalid("The selected inspection no longer exists.");
        if (inspection.Version != model.InspectionVersion) return ReceiptOperationResult.Conflict();
        if (inspection.PartId != line.PartId || NormalizeIdentifier(inspection.ConformancePoNumber) != NormalizeIdentifier(line.PurchaseOrderNumber))
            return ReceiptOperationResult.Invalid("The selected inspection does not match this Part and PO.");
        var established = false;
        var destinationManufacturerLot = NormalizeOptional(inspection.ManufacturerLotNumber);
        if (destinationManufacturerLot is null)
        {
            if (!model.EstablishMissingManufacturerLot || string.IsNullOrWhiteSpace(model.EstablishmentReason))
                return ReceiptOperationResult.Invalid("This inspection has no manufacturer's lot. Explicitly establish it and provide a reason before bumping.");
            inspection.ManufacturerLotNumber = manufacturerLot;
            established = true;
        }
        else if (!string.Equals(destinationManufacturerLot, manufacturerLot, StringComparison.OrdinalIgnoreCase))
        {
            return ReceiptOperationResult.Invalid("Manufacturer's lot does not match the selected inspection.");
        }
        var remaining = (line.ActualReceivedQuantity ?? -1) - await ActiveAllocatedQuantityAsync(db, line.Id, cancellationToken);
        if (model.Quantity > remaining) return ReceiptOperationResult.Invalid($"Only {Math.Max(0, remaining):N0} remain to allocate.");
        if ((long)(inspection.QuantityReceived ?? 0) + model.Quantity > int.MaxValue)
            return ReceiptOperationResult.Invalid("The resulting inspection quantity is too large.");
        inspection.QuantityReceived = (inspection.QuantityReceived ?? 0) + model.Quantity;
        inspection.QuantityInspected = InspectionSamplingPlan.GetQuantityInspected(inspection.QuantityReceived);
        if (established)
        {
            try
            {
                // The allocation trigger verifies the destination lot. Flush the newly
                // established lot first, but keep both saves inside this transaction.
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                return ReceiptOperationResult.Conflict();
            }
            catch (DbUpdateException)
            {
                return ReceiptOperationResult.Conflict();
            }
        }
        db.ContainerReceiptAllocations.Add(new ContainerReceiptAllocation
        {
            OperationId = model.OperationId, ContainerGroupPartId = line.Id, InspectionId = inspection.Id,
            Quantity = model.Quantity, ManufacturerLotNumber = manufacturerLot, InternalLotNumber = inspection.LotNumber,
            Action = ReceiptAllocationAction.BumpUp, EstablishedDestinationManufacturerLot = established,
            ManufacturerLotEstablishmentReason = established ? model.EstablishmentReason!.Trim() : null,
            ActingUserId = userId, PerformedAtUtc = clock.GetUtcNow(),
            ReversalReason = null
        });
        db.Entry(container).Property(x => x.ContainerNumber).IsModified = true;
        var saved = await SaveAsync(db, transaction, cancellationToken);
        return saved.Succeeded ? saved with { InspectionId = inspection.Id } : saved;
    }

    public async Task<ReceiptOperationResult> ReverseAllocationAsync(long allocationId, string reason, CancellationToken cancellationToken = default)
    {
        await RequireInspectorAsync(cancellationToken);
        var userId = await RequireUserIdAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(reason)) return ReceiptOperationResult.Invalid("A reversal reason is required.");
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var allocation = await db.ContainerReceiptAllocations
            .FromSqlInterpolated($"SELECT a.* FROM container_receipt_allocations AS a WHERE id = {allocationId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (allocation is null) return ReceiptOperationResult.Invalid("Allocation no longer exists.");
        if (allocation.ReversedAtUtc is not null) return new(true, InspectionId: allocation.InspectionId);
        var line = await LockLineAsync(db, allocation.ContainerGroupPartId, cancellationToken);
        if (line is null) return ReceiptOperationResult.Invalid("Source receipt line no longer exists.");
        var container = await GetContainerAsync(db, line.ContainerGroupId, cancellationToken);
        Inspection? inspection = null;
        if (allocation.InspectionId is long inspectionId)
        {
            inspection = await db.Inspections.FromSqlInterpolated($"SELECT i.*, i.xmin FROM inspections AS i WHERE id = {inspectionId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken);
        }
        if (inspection is null) return ReceiptOperationResult.Invalid("The destination inspection no longer exists.");
        if (allocation.Action == ReceiptAllocationAction.BumpUp)
        {
            if ((inspection.QuantityReceived ?? 0) < allocation.Quantity)
                return ReceiptOperationResult.Invalid("The inspection quantity is already below this adjustment; reversal would corrupt its quantity.");
            inspection.QuantityReceived -= allocation.Quantity;
            inspection.QuantityInspected = InspectionSamplingPlan.GetQuantityInspected(inspection.QuantityReceived);
            if (allocation.EstablishedDestinationManufacturerLot)
            {
                var otherActive = await db.ContainerReceiptAllocations.AnyAsync(x => x.Id != allocation.Id
                    && x.InspectionId == inspection.Id && x.ReversedAtUtc == null, cancellationToken);
                if (!otherActive) inspection.ManufacturerLotNumber = null;
            }
        }
        else
        {
            var hasDownstreamWork = await db.InspectionResults.AnyAsync(x => x.InspectionId == inspection.Id
                    && (x.ActualMin != null || x.ActualMax != null || x.DeviationApproved), cancellationToken)
                || await db.InspectionSecondaryProcesses.AnyAsync(x => x.InspectionId == inspection.Id && x.IsComplete, cancellationToken)
                || await db.InspectionCertifications.AnyAsync(x => x.InspectionId == inspection.Id, cancellationToken)
                || NormalizeOptional(inspection.InspectorNotes) is not null || NormalizeOptional(inspection.InHouseNotes) is not null;
            if (hasDownstreamWork)
                return ReceiptOperationResult.Invalid("This allocation created an inspection that now contains inspection work. Remove or deliberately reconcile that work before reversing the receipt allocation.");
            var certificationIds = db.InspectionCertifications.Where(x => x.InspectionId == inspection.Id).Select(x => x.Id);
            await db.CertificationDocuments.Where(x => certificationIds.Contains(x.InspectionCertificationId)).ExecuteDeleteAsync(cancellationToken);
            await db.InspectionCertifications.Where(x => x.InspectionId == inspection.Id).ExecuteDeleteAsync(cancellationToken);
            await db.InspectionCertificationRequirements.Where(x => x.InspectionId == inspection.Id).ExecuteDeleteAsync(cancellationToken);
            await db.InspectionSecondaryProcesses.Where(x => x.InspectionId == inspection.Id).ExecuteDeleteAsync(cancellationToken);
            await db.InspectionResults.Where(x => x.InspectionId == inspection.Id).ExecuteDeleteAsync(cancellationToken);
            allocation.InspectionId = null;
            db.Inspections.Remove(inspection);
        }
        allocation.ReversalReason = reason.Trim();
        allocation.ReversedByUserId = userId;
        allocation.ReversedAtUtc = clock.GetUtcNow();
        if (container is not null) db.Entry(container).Property(x => x.ContainerNumber).IsModified = true;
        return await SaveAsync(db, transaction, cancellationToken);
    }

    private async Task RequireInspectorAsync(CancellationToken cancellationToken)
    {
        var userId = await RequireUserIdAsync(cancellationToken);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var allowed = await (from assignment in db.UserRoles
            join role in db.Roles on assignment.RoleId equals role.Id
            where assignment.UserId == userId && (role.Name == AppRoles.Quality || role.Name == AppRoles.Administrator)
            select assignment.UserId).AnyAsync(cancellationToken);
        if (!allowed || !await db.Users.AnyAsync(x => x.Id == userId && x.IsActive, cancellationToken))
            throw new UnauthorizedAccessException("Quality or Administrator access is required for Received Parts.");
    }

    private async Task<string> RequireUserIdAsync(CancellationToken cancellationToken) =>
        await currentUser.GetUserIdAsync() ?? throw new UnauthorizedAccessException("Sign in to perform this operation.");

    private static Task<ContainerGroupPart?> LockLineAsync(AppDbContext db, long lineId, CancellationToken cancellationToken) =>
        db.ContainerGroupParts.FromSqlInterpolated($"SELECT p.* FROM container_group_parts AS p WHERE id = {lineId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);

    private static async Task<Container?> GetContainerAsync(AppDbContext db, long groupId, CancellationToken cancellationToken)
    {
        var containerId = await db.ContainerGroups.Where(x => x.Id == groupId).Select(x => x.ContainerId).SingleOrDefaultAsync(cancellationToken);
        return containerId == 0 ? null : await db.Containers.SingleOrDefaultAsync(x => x.Id == containerId, cancellationToken);
    }

    private static async Task<int> ActiveAllocatedQuantityAsync(AppDbContext db, long lineId, CancellationToken cancellationToken) =>
        await db.ContainerReceiptAllocations.Where(x => x.ContainerGroupPartId == lineId && x.ReversedAtUtc == null)
            .SumAsync(x => (int?)x.Quantity, cancellationToken) ?? 0;

    private static string? NormalizeOptional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? NormalizeIdentifier(string? value) => NormalizeOptional(value)?.ToUpperInvariant();

    private static async Task<ReceiptOperationResult> SaveAsync(AppDbContext db, IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(true);
        }
        catch (DbUpdateConcurrencyException)
        {
            return ReceiptOperationResult.Conflict();
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException postgres
            && postgres.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation)
        {
            return ReceiptOperationResult.Conflict();
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            return ReceiptOperationResult.Conflict();
        }
    }
}
