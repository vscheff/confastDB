using Confast.Web.Features.ContainerTracking;
using Confast.Web.Features.Customers;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Parts;
using Confast.Web.Features.Suppliers;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Tests;

public sealed class ContainerEditPolicyTests
{
    [Fact]
    public void DepartureIsStrictlyAfterEtdAndMissingEtdRemainsEditable()
    {
        var today = new DateOnly(2026, 9, 3);
        Assert.False(ContainerEditPolicy.HasDeparted(null, today));
        Assert.False(ContainerEditPolicy.HasDeparted(today, today));
        Assert.False(ContainerEditPolicy.HasDeparted(today.AddDays(1), today));
        Assert.True(ContainerEditPolicy.HasDeparted(today.AddDays(-1), today));
        Assert.True(ContainerEditPolicy.CanEditMetadata(today.AddDays(-1), today, true));
        Assert.False(ContainerEditPolicy.CanEditContents(today.AddDays(-1), today));
    }
}

[Collection(PostgresCollection.Name)]
public sealed class ContainerTrackingTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly TestClock clock = new();
    private readonly TestUser user = new();
    private ContainerTrackingService tracking = null!;
    private SupplierService suppliers = null!;

    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        var access = new TrackingAccess(database, user);
        tracking = new(database, access, clock);
        suppliers = new(database, access);
        await using var db = database.CreateDbContext();
        db.Users.Add(new ApplicationUser { Id = "tracking-user", UserName = "tracking", DisplayName = "Tracking tester" });
        db.UserRoles.Add(new IdentityUserRole<string>
        { UserId = "tracking-user", RoleId = await db.Roles.Where(x => x.Name == AppRoles.Production).Select(x => x.Id).SingleAsync() });
        await db.SaveChangesAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SuppliersValidateNormalizeDeactivateAndRejectStaleEdits()
    {
        Assert.False((await suppliers.SaveAsync(new() { Name = "  " })).Succeeded);
        Assert.False((await suppliers.SaveAsync(new() { Name = new string('x', 201) })).Succeeded);
        var saved = await suppliers.SaveAsync(new() { Name = "  Supplier ABC  " });
        Assert.True(saved.Succeeded);
        var edit = Assert.Single(await suppliers.SearchAsync("abc"));
        Assert.Equal("Supplier ABC", edit.Name);
        edit.IsActive = false;
        Assert.True((await suppliers.SaveAsync(edit)).Succeeded);
        Assert.False((await suppliers.SaveAsync(edit)).Succeeded);
        Assert.False((await tracking.SaveBillAsync(new() { Number = "BL-1", SupplierId = saved.Id!.Value })).Succeeded);
    }

    [Fact]
    public async Task MultipleShipmentBillsSearchIndividuallyAndEditsPreserveRowIdentity()
    {
        var created = await tracking.SaveShipmentAsync(new()
        {
            FreightCost = 1234.56m,
            BillNumbers = [new() { Number = "270649" }, new() { Number = "270649/A" }, new() { Number = "8GX-8020038-9" }]
        });
        Assert.True(created.Succeeded);
        Assert.Equal(1234.56m, Assert.Single(await tracking.SearchAsync("270649")).FreightCost);
        Assert.Single(await tracking.SearchAsync("8020038"));
        var edit = (await tracking.GetShipmentAsync(created.Id!.Value))!;
        var retainedId = edit.BillNumbers[0].Id;
        Assert.Equal(1234.56m, edit.FreightCost);
        edit.FreightCost = 987.65m;
        edit.BillNumbers.RemoveAt(1);
        edit.BillNumbers.Add(new() { Number = "ADDED" });
        Assert.True((await tracking.SaveShipmentAsync(edit)).Succeeded);
        Assert.False((await tracking.SaveShipmentAsync(edit)).Succeeded);
        var loaded = (await tracking.GetShipmentAsync(edit.Id))!;
        Assert.Contains(loaded.BillNumbers, x => x.Id == retainedId);
        Assert.Equal(987.65m, loaded.FreightCost);
        Assert.Equal(3, loaded.BillNumbers.Count);
        loaded.BillNumbers.Clear();
        Assert.False((await tracking.SaveShipmentAsync(loaded)).Succeeded);
    }

    [Fact]
    public async Task DeletingAnEmptyShipmentCascadesItsBillsButRejectsStaleAndNonemptyShipments()
    {
        var shipment = await tracking.SaveShipmentAsync(new()
        { BillNumbers = [new() { Number = "DELETE-1" }, new() { Number = "DELETE-2" }] });
        Assert.True(shipment.Succeeded);
        var stale = (await tracking.GetShipmentAsync(shipment.Id!.Value))!;
        stale.BillNumbers[0].Number = "DELETE-UPDATED";
        Assert.True((await tracking.SaveShipmentAsync(stale)).Succeeded);
        Assert.False((await tracking.DeleteShipmentAsync(stale.Id, stale.Version)).Succeeded);
        var current = (await tracking.GetShipmentAsync(stale.Id))!;
        Assert.True((await tracking.DeleteShipmentAsync(current.Id, current.Version)).Succeeded);
        await using (var db = database.CreateDbContext())
        {
            Assert.Empty(await db.Shipments.ToListAsync());
            Assert.Empty(await db.ShipmentBillNumbers.ToListAsync());
        }

        var nonempty = await tracking.SaveShipmentAsync(new() { BillNumbers = [new() { Number = "HAS-CONTAINER" }] });
        var container = await tracking.SaveContainerAsync(new() { ShipmentId = nonempty.Id!.Value, ContainerNumber = "KEEP-SHIPMENT" });
        Assert.True(container.Succeeded);
        var nonemptyCurrent = (await tracking.GetShipmentAsync(nonempty.Id.Value))!;
        Assert.False((await tracking.DeleteShipmentAsync(nonemptyCurrent.Id, nonemptyCurrent.Version)).Succeeded);
    }

    [Fact]
    public async Task SharedBillAndRepeatedPartPoRowsRemainNormalizedAndSearchable()
    {
        var (container, bill, part) = await CreateMaterialAsync();
        var other = await tracking.SaveContainerAsync(new() { ShipmentId = container.Metadata.ShipmentId, ContainerNumber = "SECOND" });
        var otherContents = (await tracking.GetContainerAsync(other.Id!.Value))!.Contents;
        otherContents.Groups.Add(new() { BillOfLadingId = bill, Parts = [new() { PartId = part, PurchaseOrderNumber = "PO-3", Quantity = 8 }] });
        Assert.True((await tracking.SaveContentsAsync(otherContents)).Succeeded);
        foreach (var term in new[] { "270649", "TCLU", "RWRD", "Supplier ABC", "P-12345" })
            Assert.Single(await tracking.SearchAsync(term));
        await using var db = database.CreateDbContext();
        Assert.Equal(1, await db.BillsOfLading.CountAsync());
        Assert.Equal(2, await db.ContainerGroups.CountAsync());
        Assert.Equal(3, await db.ContainerGroupParts.CountAsync());
        Assert.Equal(24492.09m, (await db.BillsOfLading.SingleAsync()).Duty);
        Assert.Equal(2, container.Contents.Groups[0].Parts.Select(x => x.Id).Distinct().Count());
        Assert.Empty(await db.Inspections.ToListAsync());
    }

    [Fact]
    public async Task BillUniquenessIsNormalizedAndEnforcedByDatabase()
    {
        var (_, billId, _) = await CreateMaterialAsync();
        var bill = (await tracking.GetBillAsync(billId))!;
        var duplicate = await tracking.SaveBillAsync(new() { Number = "  rwrd002500039301 ", SupplierId = bill.SupplierId });
        Assert.False(duplicate.Succeeded);
        Assert.Equal(billId, duplicate.ExistingBillOfLadingId);
        await using var db = database.CreateDbContext();
        db.BillsOfLading.Add(new() { Number = bill.Number, SupplierId = bill.SupplierId });
        var exception = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)exception.InnerException!).SqlState);
    }

    [Fact]
    public async Task ConcurrentDuplicateBillCreationReturnsExistingIdentity()
    {
        var supplier = await suppliers.SaveAsync(new() { Name = "Supplier" });
        var input1 = new BillOfLadingEditModel { Number = "CONCURRENT", SupplierId = supplier.Id!.Value };
        var input2 = new BillOfLadingEditModel { Number = " concurrent ", SupplierId = supplier.Id.Value };
        var results = await Task.WhenAll(tracking.SaveBillAsync(input1), tracking.SaveBillAsync(input2));
        var success = Assert.Single(results, x => x.Succeeded);
        var rejected = Assert.Single(results, x => !x.Succeeded);
        Assert.Equal(success.Id, rejected.ExistingBillOfLadingId);
    }

    [Fact]
    public async Task DepartureProtectsContentsMetadataAndSharedBillButAllowsReceiptAndAdminCorrection()
    {
        var (container, billId, _) = await CreateMaterialAsync();
        clock.AdvanceDay();
        container.Contents.Groups[0].Parts[0].Quantity = 999;
        Assert.False((await tracking.SaveContentsAsync(container.Contents)).Succeeded);
        container.Metadata.EstimatedDepartureDate = null;
        Assert.False((await tracking.SaveContainerAsync(container.Metadata)).Succeeded);
        container.Metadata.EstimatedDepartureDate = new DateOnly(2026, 9, 3);
        container.Metadata.ReceivedDate = tracking.Today;
        container.Metadata.AddedToProductionSchedule = true;
        Assert.True((await tracking.SaveContainerAsync(container.Metadata)).Succeeded);
        var bill = (await tracking.GetBillAsync(billId))!;
        bill.Duty = 50;
        Assert.False((await tracking.SaveBillAsync(bill)).Succeeded);
        await SetRoleAsync(AppRoles.Administrator);
        var fresh = (await tracking.GetContainerAsync(container.Metadata.Id))!;
        Assert.False((await tracking.SaveContentsAsync(fresh.Contents)).Succeeded);
        Assert.True((await tracking.SaveBillAsync(bill)).Succeeded);
        fresh.Metadata.EstimatedDepartureDate = tracking.Today;
        Assert.True((await tracking.SaveContainerAsync(fresh.Metadata)).Succeeded);
        Assert.True((await tracking.SaveContentsAsync((await tracking.GetContainerAsync(fresh.Metadata.Id))!.Contents)).Succeeded);
    }

    [Fact]
    public async Task ContentsUsePersistedEtdAndContainerVersionRatherThanClientValues()
    {
        var (container, _, _) = await CreateMaterialAsync();
        var secondEditor = (await tracking.GetContainerAsync(container.Metadata.Id))!;
        container.Contents.Groups[0].Parts[0].Quantity = 1;
        Assert.True((await tracking.SaveContentsAsync(container.Contents)).Succeeded);
        secondEditor.Metadata.ContainerNumber = "OVERWRITE";
        Assert.False((await tracking.SaveContainerAsync(secondEditor.Metadata)).Succeeded);
        secondEditor.Contents.Groups.Clear();
        Assert.False((await tracking.SaveContentsAsync(secondEditor.Contents)).Succeeded);
        var saved = (await tracking.GetContainerAsync(container.Metadata.Id))!;
        Assert.Equal(1, saved.Contents.Groups[0].Parts[0].Quantity);
        Assert.NotEqual("OVERWRITE", saved.Metadata.ContainerNumber);
    }

    [Fact]
    public async Task DeletingAContainerCascadesOwnedContentsPreservesBillsAndRespectsConcurrencyAndDepartureRules()
    {
        var (container, billId, _) = await CreateMaterialAsync();
        var stale = (await tracking.GetContainerAsync(container.Metadata.Id))!;
        container.Metadata.ReceivedDate = tracking.Today;
        Assert.True((await tracking.SaveContainerAsync(container.Metadata)).Succeeded);
        Assert.False((await tracking.DeleteContainerAsync(stale.Metadata.Id, stale.Metadata.Version)).Succeeded);

        var current = (await tracking.GetContainerAsync(container.Metadata.Id))!;
        Assert.True((await tracking.DeleteContainerAsync(current.Metadata.Id, current.Metadata.Version)).Succeeded);
        await using (var db = database.CreateDbContext())
        {
            Assert.Empty(await db.Containers.ToListAsync());
            Assert.Empty(await db.ContainerGroups.ToListAsync());
            Assert.Empty(await db.ContainerGroupParts.ToListAsync());
            Assert.Equal(billId, (await db.BillsOfLading.SingleAsync()).Id);
        }

        var (departed, _, _) = await CreateMaterialAsync("-SECOND");
        clock.AdvanceDay();
        Assert.False((await tracking.DeleteContainerAsync(departed.Metadata.Id, departed.Metadata.Version)).Succeeded);
        await SetRoleAsync(AppRoles.Administrator);
        var fresh = (await tracking.GetContainerAsync(departed.Metadata.Id))!;
        Assert.True((await tracking.DeleteContainerAsync(fresh.Metadata.Id, fresh.Metadata.Version)).Succeeded);
    }

    [Fact]
    public async Task ForgedParentIdsAndInvalidReferencesAreRejected()
    {
        var (container, bill, part) = await CreateMaterialAsync();
        var other = await tracking.SaveContainerAsync(new() { ShipmentId = container.Metadata.ShipmentId, ContainerNumber = "OTHER" });
        var contents = (await tracking.GetContainerAsync(other.Id!.Value))!.Contents;
        contents.Groups.Add(container.Contents.Groups[0]);
        Assert.False((await tracking.SaveContentsAsync(contents)).Succeeded);
        contents.Groups = [new() { BillOfLadingId = bill, Parts = [container.Contents.Groups[0].Parts[0]] }];
        Assert.False((await tracking.SaveContentsAsync(contents)).Succeeded);
        contents.Groups = [new() { BillOfLadingId = long.MaxValue }];
        Assert.False((await tracking.SaveContentsAsync(contents)).Succeeded);
        contents.Groups = [new() { BillOfLadingId = bill, Parts = [new() { PartId = long.MaxValue, PurchaseOrderNumber = "PO", Quantity = 1 }] }];
        Assert.False((await tracking.SaveContentsAsync(contents)).Succeeded);
        contents.Groups[0].Parts[0].PartId = part;
        contents.Groups[0].Parts[0].Quantity = -1;
        Assert.False((await tracking.SaveContentsAsync(contents)).Succeeded);
        Assert.False((await tracking.SaveBillAsync(new() { Number = "BAD", SupplierId = long.MaxValue })).Succeeded);
        var shipmentId = container.Metadata.ShipmentId;
        container.Metadata.ShipmentId = long.MaxValue;
        Assert.False((await tracking.SaveContainerAsync(container.Metadata)).Succeeded);
        var shipment = (await tracking.GetShipmentAsync(shipmentId))!;
        var forgedShipment = new ShipmentEditModel { BillNumbers = [new() { Id = shipment.BillNumbers[0].Id, Number = "FORGED" }] };
        Assert.False((await tracking.SaveShipmentAsync(forgedShipment)).Succeeded);
    }

    [Fact]
    public async Task PartRowsCanBeEditedAndRemovedWithoutReplacingOtherIdentities()
    {
        var (container, bill, part) = await CreateMaterialAsync();
        var retained = container.Contents.Groups[0].Parts[0].Id;
        container.Contents.Groups[0].Parts.RemoveAt(1);
        container.Contents.Groups[0].Parts[0].Quantity = 7;
        container.Contents.Groups.Add(new() { BillOfLadingId = bill, Parts = [new() { PartId = part, PurchaseOrderNumber = "PO-NEW", Quantity = 3 }] });
        Assert.True((await tracking.SaveContentsAsync(container.Contents)).Succeeded);
        var updated = (await tracking.GetContainerAsync(container.Metadata.Id))!;
        Assert.Equal(retained, updated.Contents.Groups[0].Parts[0].Id);
        Assert.Equal(7, updated.Contents.Groups[0].Parts[0].Quantity);
        updated.Contents.Groups.RemoveAt(0);
        Assert.True((await tracking.SaveContentsAsync(updated.Contents)).Succeeded);
        await using var db = database.CreateDbContext();
        Assert.Single(await db.ContainerGroupParts.ToListAsync());
        Assert.Single(await db.BillsOfLading.ToListAsync());
    }

    [Fact]
    public async Task ReadOnlyAnonymousAndDeactivatedUsersCannotWrite()
    {
        await SetRoleAsync(AppRoles.ReadOnly);
        Assert.Empty(await tracking.SearchAsync());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => suppliers.SaveAsync(new() { Name = "Forbidden" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tracking.SaveShipmentAsync(new()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tracking.SaveContainerAsync(new()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tracking.SaveContentsAsync(new()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tracking.SaveBillAsync(new()));
        user.Id = null;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => tracking.SearchAsync());
        user.Id = "tracking-user";
        await SetRoleAsync(AppRoles.Administrator);
        await using var db = database.CreateDbContext();
        (await db.Users.SingleAsync()).IsActive = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => suppliers.SaveAsync(new() { Name = "Forbidden" }));
    }

    [Fact]
    public async Task InvalidMoneyAndWeightAreRejectedWithoutChangingSavedValues()
    {
        var (container, billId, _) = await CreateMaterialAsync();
        container.Metadata.QuotedRate = -1;
        Assert.False((await tracking.SaveContainerAsync(container.Metadata)).Succeeded);
        container.Metadata.QuotedRate = 1.001m;
        Assert.False((await tracking.SaveContainerAsync(container.Metadata)).Succeeded);
        container.Contents.Groups[0].TotalWeight = 1.0001m;
        Assert.False((await tracking.SaveContentsAsync(container.Contents)).Succeeded);
        var bill = (await tracking.GetBillAsync(billId))!;
        bill.Duty = -1;
        Assert.False((await tracking.SaveBillAsync(bill)).Succeeded);
        bill.Duty = 1.001m;
        Assert.False((await tracking.SaveBillAsync(bill)).Succeeded);
        var saved = (await tracking.GetContainerAsync(container.Metadata.Id))!;
        Assert.Equal(4200m, saved.Metadata.QuotedRate);
        Assert.Equal(14400m, saved.Contents.Groups[0].TotalWeight);
        Assert.Equal(24492.09m, (await tracking.GetBillAsync(billId))!.Duty);
    }

    [Fact]
    public async Task GroupInvoiceNumberIsNormalizedPersistedAndSearchable()
    {
        var (container, _, _) = await CreateMaterialAsync();
        var group = container.Contents.Groups[0];
        group.InvoiceNumber = "  INV-2026-001  ";

        Assert.True((await tracking.SaveContentsAsync(container.Contents)).Succeeded);

        var saved = (await tracking.GetContainerAsync(container.Metadata.Id))!;
        Assert.Equal("INV-2026-001", saved.Contents.Groups[0].InvoiceNumber);
        Assert.Single(await tracking.SearchAsync("2026-001"));
    }

    [Fact]
    public async Task SearchIncludesEachContainerGroupSummary()
    {
        var (container, _, _) = await CreateMaterialAsync();

        var summary = Assert.Single(Assert.Single(await tracking.SearchAsync()).Containers);
        var group = Assert.Single(summary.Groups);

        Assert.Equal(container.Metadata.Id, summary.Id);
        Assert.Equal("Supplier ABC", group.SupplierName);
        Assert.Equal("RWRD002500039301", group.BillNumber);
        Assert.Equal(24492.09m, group.Duty);
        Assert.Equal(14400m, group.Weight);
        Assert.Equal(8, group.Pallets);
        Assert.False(group.CertificationsReceived);
        Assert.Collection(group.Parts,
            part =>
            {
                Assert.Equal("P-12345", part.PartNumber);
                Assert.Equal("Customer", part.CustomerName);
                Assert.Equal("PO-9981", part.PurchaseOrderNumber);
                Assert.Equal(50000, part.Quantity);
            },
            part =>
            {
                Assert.Equal("PO-9987", part.PurchaseOrderNumber);
                Assert.Equal(80000, part.Quantity);
            });
    }

    [Fact]
    public async Task SupplierCanOwnManyBillsAndIdenticalPartPoLinesAreStillIndependent()
    {
        var (container, billId, partId) = await CreateMaterialAsync();
        var bill = (await tracking.GetBillAsync(billId))!;
        Assert.True((await tracking.SaveBillAsync(new() { Number = "SECOND-BL", SupplierId = bill.SupplierId })).Succeeded);
        container.Contents.Groups[0].Parts.Add(new() { PartId = partId, PurchaseOrderNumber = "PO-9981", Quantity = 50 });
        Assert.True((await tracking.SaveContentsAsync(container.Contents)).Succeeded);
        var rows = (await tracking.GetContainerAsync(container.Metadata.Id))!.Contents.Groups[0].Parts;
        Assert.Equal(3, rows.Select(x => x.Id).Distinct().Count());
        await using var db = database.CreateDbContext();
        Assert.Equal(2, await db.BillsOfLading.CountAsync(x => x.SupplierId == bill.SupplierId));
    }

    [Fact]
    public async Task PartMustBelongToTheGroupBillSupplier()
    {
        var (container, _, _) = await CreateMaterialAsync();
        var otherSupplier = await suppliers.SaveAsync(new() { Name = "Other supplier" });
        await using (var db = database.CreateDbContext())
        {
            db.Parts.Add(new Part
            {
                Customer = new Customer { Name = "Other customer" },
                SupplierId = otherSupplier.Id!.Value,
                PartNumber = "OTHER-PART"
            });
            await db.SaveChangesAsync();
        }

        var edited = (await tracking.GetContainerAsync(container.Metadata.Id))!;
        var mismatchedPartId = (await tracking.GetPartsAsync()).Single(x => x.Label.StartsWith("OTHER-PART")).Id;
        edited.Contents.Groups[0].Parts[0].PartId = mismatchedPartId;

        var result = await tracking.SaveContentsAsync(edited.Contents);

        Assert.False(result.Succeeded);
        Assert.Equal("Each part must have the same supplier as its group's B/L.", result.Message);
    }

    [Fact]
    public async Task DatabaseRejectsBadReferencesAndNegativeValues()
    {
        var (container, bill, part) = await CreateMaterialAsync();
        await using var db = database.CreateDbContext();
        var group = new ContainerGroup { ContainerId = container.Metadata.Id, BillOfLadingId = bill, TotalWeight = -1 };
        db.ContainerGroups.Add(group);
        var negative = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, ((PostgresException)negative.InnerException!).SqlState);
        db.ChangeTracker.Clear();
        db.ContainerGroupParts.Add(new() { ContainerGroupId = container.Contents.Groups[0].Id, PartId = long.MaxValue, PurchaseOrderNumber = "PO", Quantity = 1 });
        var reference = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ((PostgresException)reference.InnerException!).SqlState);
        db.ChangeTracker.Clear();
        db.Parts.Remove((await db.Parts.FindAsync(part))!);
        var deletion = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Contains(((PostgresException)deletion.InnerException!).SqlState,
            new[] { PostgresErrorCodes.ForeignKeyViolation, PostgresErrorCodes.RestrictViolation });
    }

    private async Task<(ContainerDetail Container, long Bill, long Part)> CreateMaterialAsync(string billSuffix = "")
    {
        var supplier = await suppliers.SaveAsync(new() { Name = "Supplier ABC" });
        var bill = await tracking.SaveBillAsync(new() { Number = "rwrd002500039301" + billSuffix, SupplierId = supplier.Id!.Value, Duty = 24492.09m });
        Assert.True(bill.Succeeded, bill.Message);
        var shipment = await tracking.SaveShipmentAsync(new() { BillNumbers = [new() { Number = "270649" }] });
        var container = await tracking.SaveContainerAsync(new()
        {
            ShipmentId = shipment.Id!.Value,
            ContainerNumber = "TCLU1234567",
            CbpNumber = "  CBP-12345  ",
            EstimatedDepartureDate = tracking.Today,
            QuotedRate = 4200m
        });
        Assert.True(container.Succeeded, container.Message);
        await using var db = database.CreateDbContext();
        var customer = new Customer { Name = "Customer" };
        var part = new Part { Customer = customer, SupplierId = supplier.Id!.Value, PartNumber = "P-12345" };
        db.Parts.Add(part);
        await db.SaveChangesAsync();
        var detail = (await tracking.GetContainerAsync(container.Id!.Value))!;
        Assert.Equal("CBP-12345", detail.Metadata.CbpNumber);
        Assert.Single(await tracking.SearchAsync("cbp-12345"));
        detail.Contents.Groups.Add(new()
        {
            BillOfLadingId = bill.Id!.Value, PalletCount = 8, TotalWeight = 14400,
            Parts = [new() { PartId = part.Id, PurchaseOrderNumber = "PO-9981", Quantity = 50000 }, new() { PartId = part.Id, PurchaseOrderNumber = "PO-9987", Quantity = 80000 }]
        });
        var save = await tracking.SaveContentsAsync(detail.Contents);
        Assert.True(save.Succeeded, save.Message);
        return ((await tracking.GetContainerAsync(container.Id.Value))!, bill.Id.Value, part.Id);
    }

    private async Task SetRoleAsync(string role)
    {
        await using var db = database.CreateDbContext();
        db.UserRoles.RemoveRange(await db.UserRoles.ToListAsync());
        await db.SaveChangesAsync();
        db.UserRoles.Add(new() { UserId = "tracking-user", RoleId = await db.Roles.Where(x => x.Name == role).Select(x => x.Id).SingleAsync() });
        await db.SaveChangesAsync();
    }
    private sealed class TestUser : ICurrentUser
    {
        public string? Id { get; set; } = "tracking-user";
        public ValueTask<string?> GetUserIdAsync() => ValueTask.FromResult(Id);
    }
    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
        public void AdvanceDay() => now = now.AddDays(1);
    }
}
