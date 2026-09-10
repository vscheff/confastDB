using Confast.Web.Features.ContainerTracking;
using Confast.Web.Features.Customers;
using Confast.Web.Features.Identity;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.Parts;
using Confast.Web.Features.Suppliers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class ReceivedPartsServiceTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly TestUser user = new();
    private readonly TestClock clock = new();
    private ReceivedPartsService receivedParts = null!;
    private InspectionService inspections = null!;
    private long lineId;
    private long containerId;

    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        inspections = new InspectionService(database);
        var access = new TrackingAccess(database, user);
        receivedParts = new ReceivedPartsService(database, access, inspections, user, clock);
        await using var db = database.CreateDbContext();
        db.Users.Add(new ApplicationUser { Id = user.Id, UserName = "receiver", DisplayName = "Receiver" });
        db.UserRoles.Add(new IdentityUserRole<string>
        {
            UserId = user.Id,
            RoleId = await db.Roles.Where(x => x.Name == AppRoles.Quality).Select(x => x.Id).SingleAsync()
        });
        var supplier = new Supplier { Name = "Supplier" };
        var part = new Part { PartNumber = "PART-1", Customer = new Customer { Name = "Customer" }, Supplier = supplier };
        var revision = new InspectionCriteriaRevision
        {
            Part = part, RevisionNumber = 1, CreatedAtUtc = clock.GetUtcNow(), PublishedAtUtc = clock.GetUtcNow()
        };
        revision.Criteria.Add(new InspectionCriterion { Name = "Length", Minimum = "1", MaximumOrTolerance = "2", DisplayOrder = 1 });
        var shipment = new Shipment();
        var container = new Container { Shipment = shipment, ContainerNumber = "CONTAINER-1" };
        var bill = new BillOfLading { Number = "BL-1", Supplier = supplier };
        var group = new ContainerGroup { Container = container, BillOfLading = bill };
        var line = new ContainerGroupPart { ContainerGroup = group, Part = part, PurchaseOrderNumber = "PO-1", Quantity = 100 };
        db.InspectionCriteriaRevisions.Add(revision);
        db.ContainerGroupParts.Add(line);
        await db.SaveChangesAsync();
        lineId = line.Id;
        containerId = container.Id;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReceiptIsIdempotentAndSplitAllocationsUseActualQuantity()
    {
        var version = await ContainerVersionAsync();
        Assert.True((await receivedParts.ReceiveContainerAsync(containerId, version, clock.Today)).Succeeded);
        var received = Assert.Single(await receivedParts.GetReceivedPartsAsync(null, true));
        var line = Assert.Single(received.Lines);
        Assert.Equal(100, line.ExpectedQuantity);
        Assert.Equal(100, line.ActualReceivedQuantity);

        // Retry with the current version does not create a second receipt event.
        Assert.True((await receivedParts.ReceiveContainerAsync(containerId, line.ContainerVersion, clock.Today)).Succeeded);
        await using (var db = database.CreateDbContext())
            Assert.Single(await db.ContainerReceiptHistory.ToListAsync());

        var first = await BeginAsync(line, "MFG-A", "LOT-A", 60);
        Assert.True(first.Succeeded, first.Message);
        line = Assert.Single(Assert.Single(await receivedParts.GetReceivedPartsAsync(null, true)).Lines);
        var second = await BeginAsync(line, "MFG-B", "LOT-B", 30);
        Assert.True(second.Succeeded, second.Message);
        line = Assert.Single(Assert.Single(await receivedParts.GetReceivedPartsAsync(null, true)).Lines);
        Assert.Equal(90, line.AllocatedQuantity);
        Assert.Equal(10, line.RemainingQuantity);
        Assert.Equal(ReceiptAllocationStatus.PartiallyAssigned, line.AllocationStatus);
        Assert.Equal(2, line.Allocations.Count(x => x.ReversedAtUtc is null));
    }

    [Fact]
    public async Task BeginInspectionExplainsWhenPartHasNoCurrentPublishedRevision()
    {
        await ReceiveAsync();
        await using (var db = database.CreateDbContext())
        {
            var revision = await db.InspectionCriteriaRevisions.SingleAsync();
            revision.SupersededAtUtc = clock.GetUtcNow();
            await db.SaveChangesAsync();
        }

        var result = await BeginAsync(await CurrentLineAsync(), "MFG-A", "LOT-A", 25);

        Assert.False(result.Succeeded);
        Assert.Equal("That Part has no current published inspection-criteria revision.", result.Message);
        await using var verificationDb = database.CreateDbContext();
        Assert.Empty(await verificationDb.Inspections.ToListAsync());
        Assert.Empty(await verificationDb.ContainerReceiptAllocations.ToListAsync());
    }

    [Fact]
    public async Task CandidateMatchingRequiresExactPartAndPoAndBumpRejectsManufacturerLotMismatchAtomically()
    {
        await ReceiveAsync();
        var source = CurrentLineAsync();
        var line = await source;
        var created = await BeginAsync(line, "MFG-A", "LOT-A", 40);
        Assert.True(created.Succeeded, created.Message);
        var candidate = Assert.Single(await receivedParts.GetCandidatesAsync(lineId));
        Assert.Equal(created.InspectionId, candidate.InspectionId);

        line = await CurrentLineAsync();
        var mismatch = await receivedParts.BumpUpAsync(new BumpUpReceiptModel
        {
            ContainerGroupPartId = lineId, ContainerVersion = line.ContainerVersion,
            InspectionId = candidate.InspectionId, InspectionVersion = candidate.Version,
            ManufacturerLotNumber = "MFG-OTHER", Quantity = 10
        });
        Assert.False(mismatch.Succeeded);
        await using var db = database.CreateDbContext();
        Assert.Equal(40, (await db.Inspections.SingleAsync()).QuantityReceived);
        Assert.Single(await db.ContainerReceiptAllocations.Where(x => x.ReversedAtUtc == null).ToListAsync());

        var destination = await db.Inspections.SingleAsync();
        destination.ManufacturerLotNumber = null;
        await db.SaveChangesAsync();
        var missingLotCandidate = (await receivedParts.GetCandidatesAsync(lineId)).Single(x => x.InspectionId == destination.Id);
        line = await CurrentLineAsync();
        var established = await receivedParts.BumpUpAsync(new BumpUpReceiptModel
        {
            ContainerGroupPartId = lineId, ContainerVersion = line.ContainerVersion,
            InspectionId = missingLotCandidate.InspectionId, InspectionVersion = missingLotCandidate.Version,
            ManufacturerLotNumber = "MFG-A", Quantity = 10, EstablishMissingManufacturerLot = true,
            EstablishmentReason = "Confirmed against packing list"
        });
        Assert.True(established.Succeeded, established.Message);
        db.ChangeTracker.Clear();
        var establishmentAudit = await db.ContainerReceiptAllocations.SingleAsync(x => x.EstablishedDestinationManufacturerLot);
        Assert.Equal("Confirmed against packing list", establishmentAudit.ManufacturerLotEstablishmentReason);

        db.Inspections.Add(new Inspection
        {
            PartId = (await db.ContainerGroupParts.FindAsync(lineId))!.PartId,
            InspectionCriteriaRevisionId = await db.InspectionCriteriaRevisions.Select(x => x.Id).SingleAsync(),
            LotNumber = "OTHER-PO", ConformancePoNumber = "PO-2", ManufacturerLotNumber = "MFG-A",
            QuantityReceived = 1, InspectionDate = clock.Today
        });
        await db.SaveChangesAsync();
        Assert.Single(await receivedParts.GetCandidatesAsync(lineId));
    }

    [Fact]
    public async Task ActualQuantityCannotFallBelowAllocatedAndZeroReceiptLeavesNoOutstandingWork()
    {
        await ReceiveAsync();
        var line = await CurrentLineAsync();
        Assert.True((await BeginAsync(line, "MFG-A", "LOT-A", 25)).Succeeded);
        line = await CurrentLineAsync();
        var rejected = await receivedParts.CorrectActualReceivedQuantityAsync(lineId, line.ContainerVersion, 24, "Count correction");
        Assert.False(rejected.Succeeded);
        Assert.True((await receivedParts.CorrectActualReceivedQuantityAsync(lineId, line.ContainerVersion, 25, "Count correction")).Succeeded);
        line = await CurrentLineAsync();
        Assert.Equal(ReceiptAllocationStatus.FullyAssigned, line.AllocationStatus);

        await using var db = database.CreateDbContext();
        var second = new ContainerGroupPart
        {
            ContainerGroupId = await db.ContainerGroupParts.Where(x => x.Id == lineId).Select(x => x.ContainerGroupId).SingleAsync(),
            PartId = await db.ContainerGroupParts.Where(x => x.Id == lineId).Select(x => x.PartId).SingleAsync(),
            PurchaseOrderNumber = "PO-ZERO", Quantity = 10, ActualReceivedQuantity = 10
        };
        db.ContainerGroupParts.Add(second);
        await db.SaveChangesAsync();
        var containerVersion = await ContainerVersionAsync();
        Assert.True((await receivedParts.CorrectActualReceivedQuantityAsync(second.Id, containerVersion, 0, "Nothing arrived")).Succeeded);
        var zero = Assert.Single((await receivedParts.GetReceivedPartsAsync("PO-ZERO", true)).Single().Lines);
        Assert.Equal(ReceiptAllocationStatus.FullyAssigned, zero.AllocationStatus);
        Assert.Equal(0, zero.RemainingQuantity);
    }

    [Fact]
    public async Task DuplicateOperationAndConcurrentAttemptsCannotDoubleAllocate()
    {
        await ReceiveAsync();
        var line = await CurrentLineAsync();
        var operationId = Guid.NewGuid();
        var model = new BeginReceiptInspectionModel
        {
            OperationId = operationId, ContainerGroupPartId = lineId, ContainerVersion = line.ContainerVersion,
            ManufacturerLotNumber = "MFG-A", InternalLotNumber = "LOT-A", Quantity = 70
        };
        var first = await receivedParts.BeginInspectionAsync(model);
        var retry = await receivedParts.BeginInspectionAsync(model);
        Assert.True(first.Succeeded);
        Assert.True(retry.Succeeded);
        Assert.Equal(first.InspectionId, retry.InspectionId);

        line = await CurrentLineAsync();
        var attempts = await Task.WhenAll(
            BeginAsync(line, "MFG-B", "LOT-B", 30),
            BeginAsync(line, "MFG-C", "LOT-C", 30));
        Assert.Single(attempts, x => x.Succeeded);
        await using var db = database.CreateDbContext();
        Assert.Equal(100, await db.ContainerReceiptAllocations.Where(x => x.ReversedAtUtc == null).SumAsync(x => x.Quantity));
    }

    [Fact]
    public async Task ReversalRestoresBumpAndRejectsNewInspectionWithDownstreamWork()
    {
        await ReceiveAsync();
        var line = await CurrentLineAsync();
        var created = await BeginAsync(line, "MFG-A", "LOT-A", 40);
        var candidate = Assert.Single(await receivedParts.GetCandidatesAsync(lineId));
        line = await CurrentLineAsync();
        var bumped = await receivedParts.BumpUpAsync(new BumpUpReceiptModel
        {
            ContainerGroupPartId = lineId, ContainerVersion = line.ContainerVersion,
            InspectionId = candidate.InspectionId, InspectionVersion = candidate.Version,
            ManufacturerLotNumber = "MFG-A", Quantity = 20
        });
        Assert.True(bumped.Succeeded, bumped.Message);
        var bumpedInspection = await inspections.GetInspectionAsync(candidate.InspectionId);
        var bumpHistory = Assert.Single(bumpedInspection!.History);
        Assert.Equal("Bump Up", bumpHistory.Operation);
        Assert.Equal("CONTAINER-1", bumpHistory.SourceContainerNumber);
        Assert.Equal(20, bumpHistory.QuantityMoved);
        Assert.Null(bumpHistory.LineageEntry);
        Assert.NotNull(bumpHistory.ReceiptAllocationId);
        Assert.True(bumpHistory.IsMostRecent);
        line = await CurrentLineAsync();
        var bumpAllocation = line.Allocations.Single(x => x.Action == ReceiptAllocationAction.BumpUp);
        Assert.True((await receivedParts.ReverseAllocationAsync(bumpAllocation.Id, "Wrong count")).Succeeded);
        line = await CurrentLineAsync();
        Assert.Equal("Wrong count", line.Allocations.Single(x => x.Id == bumpAllocation.Id).ReversalReason);
        await using (var db = database.CreateDbContext())
            Assert.Equal(40, (await db.Inspections.SingleAsync()).QuantityReceived);
        var reversedInspection = await inspections.GetInspectionAsync(candidate.InspectionId);
        Assert.Empty(reversedInspection!.History);

        var inspection = (await inspections.GetInspectionAsync(created.InspectionId!.Value))!;
        inspection.Results[0].ActualMin = "1.5";
        inspection.Results[0].ActualMax = "1.5";
        Assert.Equal(InspectionOperationStatus.Succeeded, (await inspections.SaveInspectionAsync(inspection)).Status);
        line = await CurrentLineAsync();
        var createAllocation = line.Allocations.Single(x => x.Action == ReceiptAllocationAction.BeginInspection);
        var unsafeReversal = await receivedParts.ReverseAllocationAsync(createAllocation.Id, "Undo receipt");
        Assert.False(unsafeReversal.Succeeded);
        Assert.Contains("inspection work", unsafeReversal.Message);
    }

    [Fact]
    public async Task BumpUpPreventsUndoingAnEarlierLineageOperation()
    {
        await ReceiveAsync();
        var line = await CurrentLineAsync();
        var created = await BeginAsync(line, "MFG-A", "LOT-A", 40);
        Assert.True(created.Succeeded, created.Message);
        var duplicate = await inspections.DuplicateInspectionAsync(created.InspectionId!.Value, 10, "LOT-B");
        Assert.Equal(InspectionOperationStatus.Succeeded, duplicate.Status);

        line = await CurrentLineAsync();
        var candidate = Assert.Single(
            await receivedParts.GetCandidatesAsync(lineId),
            x => x.InspectionId == created.InspectionId);
        var bumped = await receivedParts.BumpUpAsync(new BumpUpReceiptModel
        {
            ContainerGroupPartId = lineId,
            ContainerVersion = line.ContainerVersion,
            InspectionId = candidate.InspectionId,
            InspectionVersion = candidate.Version,
            ManufacturerLotNumber = "MFG-A",
            Quantity = 20
        });
        Assert.True(bumped.Succeeded, bumped.Message);
        await using (var db = database.CreateDbContext())
        {
            var allocation = await db.ContainerReceiptAllocations
                .SingleAsync(x => x.InspectionId == created.InspectionId
                    && x.Action == ReceiptAllocationAction.BumpUp);
            allocation.PerformedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1);
            await db.SaveChangesAsync();
        }

        var inspection = (await inspections.GetInspectionAsync(created.InspectionId.Value))!;
        Assert.Equal("Bump Up", inspection.History[0].Operation);
        var lineage = Assert.Single(inspection.LineageHistory);
        Assert.False(Assert.Single(inspection.History, x => x.LineageEntry == lineage).IsMostRecent);

        var undo = await inspections.UndoLineageOperationAsync(
            created.InspectionId.Value,
            lineage.Operation,
            lineage.Id,
            confirmDestinationDeletion: true);
        Assert.Equal(InspectionOperationStatus.ValidationFailed, undo.Status);
        Assert.Equal("A later receipt bump prevents this operation from being undone.", undo.Message);

        await using (var db = database.CreateDbContext())
        {
            var allocationId = await db.ContainerReceiptAllocations
                .Where(x => x.InspectionId == created.InspectionId
                    && x.Action == ReceiptAllocationAction.BumpUp)
                .Select(x => x.Id)
                .SingleAsync();
            Assert.True((await receivedParts.ReverseAllocationAsync(allocationId, "Undo bump")).Succeeded);
        }

        var afterBumpUndo = (await inspections.GetInspectionAsync(created.InspectionId.Value))!;
        Assert.Collection(afterBumpUndo.History, entry => Assert.Equal("Duplicate", entry.Operation));
        var lineageUndo = await inspections.UndoLineageOperationAsync(
            created.InspectionId.Value,
            lineage.Operation,
            lineage.Id,
            confirmDestinationDeletion: true);
        Assert.Equal(InspectionOperationStatus.Succeeded, lineageUndo.Status);
    }

    private async Task ReceiveAsync()
    {
        var result = await receivedParts.ReceiveContainerAsync(containerId, await ContainerVersionAsync(), clock.Today);
        Assert.True(result.Succeeded, result.Message);
    }

    private async Task<ReceivedPartLineItem> CurrentLineAsync() =>
        Assert.Single(Assert.Single(await receivedParts.GetReceivedPartsAsync(null, true)).Lines, x => x.Id == lineId);

    private Task<ReceiptOperationResult> BeginAsync(ReceivedPartLineItem line, string manufacturerLot, string internalLot, int quantity) =>
        receivedParts.BeginInspectionAsync(new BeginReceiptInspectionModel
        {
            ContainerGroupPartId = line.Id, ContainerVersion = line.ContainerVersion,
            ManufacturerLotNumber = manufacturerLot, InternalLotNumber = internalLot, Quantity = quantity
        });

    private async Task<uint> ContainerVersionAsync()
    {
        await using var db = database.CreateDbContext();
        return await db.Containers.Where(x => x.Id == containerId).Select(x => x.Version).SingleAsync();
    }

    private sealed class TestUser : ICurrentUser
    {
        public string Id { get; } = "received-parts-user";
        public ValueTask<string?> GetUserIdAsync() => ValueTask.FromResult<string?>(Id);
    }

    private sealed class TestClock : TimeProvider
    {
        private readonly DateTimeOffset now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        public DateOnly Today => DateOnly.FromDateTime(now.DateTime);
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
