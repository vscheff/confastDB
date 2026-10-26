using System.Security.Claims;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.ContainerTracking;
using Confast.Web.Features.Customers;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Parts;
using Confast.Web.Features.Suppliers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class LogisticsAuthorizationTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly AuthorizationTestSession session = new(database);
    private readonly TimeProvider clock = new FixtureClock();
    private EffectivePermissionService evaluator = null!;
    private ContainerTrackingService tracking = null!;
    private ReceivedPartsService receiving = null!;
    private string root = null!;
    private long shipmentId, containerId, billId, lineId, supplierId;
    private ShipmentEditModel shipment = null!;
    private ContainerDetail container = null!;
    private BillOfLadingEditModel bill = null!;

    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        root = await session.ProvisionRootAsync(); await session.SignInAsync(root);
        evaluator = session.Evaluator;
        var guard = new ApplicationAuthorization(evaluator, new HttpContextAccessor());
        tracking = new(database, guard, clock); receiving = new(database, session.Inspections(evaluator), clock, guard);
        await using var db = database.CreateDbContext();
        var supplier = new Supplier { Name = "Logistics Supplier" };
        var part = new Part { PartNumber = "AUTH-LOGISTICS", Customer = new Customer { Name = "Logistics Customer" }, Supplier = supplier };
        var source = new Shipment(); source.BillNumbers.Add(new() { Number = "AUTH-SHIPMENT" });
        var box = new Container { Shipment = source, ContainerNumber = "AUTH-CONTAINER", EstimatedDepartureDate = tracking.Today.AddDays(1) };
        var sharedBill = new BillOfLading { Supplier = supplier, Number = "AUTH-BILL" };
        var group = new ContainerGroup { Container = box, BillOfLading = sharedBill };
        var line = new ContainerGroupPart { ContainerGroup = group, Part = part, PurchaseOrderNumber = "AUTH-PO", Quantity = 100 };
        db.ContainerGroupParts.Add(line); await db.SaveChangesAsync();
        shipmentId = source.Id; containerId = box.Id; billId = sharedBill.Id; lineId = line.Id; supplierId = supplier.Id;
        await Reload();
    }
    public Task DisposeAsync() { session.Dispose(); return Task.CompletedTask; }
    private async Task Reload()
    {
        shipment = (await tracking.GetShipmentAsync(shipmentId))!;
        container = (await tracking.GetContainerAsync(containerId))!; bill = (await tracking.GetBillAsync(billId))!;
    }
    private async Task<string> Actor(params string[] keys)
    {
        await using var db = database.CreateDbContext();
        var role = new ApplicationRole { Name = Guid.NewGuid().ToString(), NormalizedName = Guid.NewGuid().ToString(), IsEnabled = true };
        db.Roles.Add(role);
        db.RolePermissions.AddRange(PermissionCatalog.ExpandRequirements(keys).Select(k => new RolePermission { RoleId = role.Id, PermissionKey = k }));
        var actor = new ApplicationUser { UserName = Guid.NewGuid().ToString(), DisplayName = "Logistics Actor", IsActive = true, SecurityStamp = Guid.NewGuid().ToString() };
        db.Users.Add(actor); db.UserRoles.Add(new IdentityUserRole<string> { UserId = actor.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        await db.UserRoles.Where(x => x.UserId == actor.Id && x.RoleId == AppRoles.ReadOnlyId).ExecuteDeleteAsync();
        await session.SignInAsync(actor.Id); return actor.Id;
    }
    private Dictionary<string, Func<Task>> Boundaries() => new()
    {
        ["Shipments.Read"] = () => tracking.SearchAsync(), ["Shipments.Get"] = () => tracking.GetShipmentAsync(shipmentId),
        ["Shipments.Create"] = () => tracking.SaveShipmentAsync(new() { BillNumbers = [new() { Number = "NEW" }] }),
        ["Shipments.Update"] = () => tracking.SaveShipmentAsync(shipment),
        ["Shipments.Delete"] = () => tracking.DeleteShipmentAsync(shipmentId, shipment.Version),
        ["Containers.Read"] = () => tracking.GetContainerAsync(containerId),
        ["Containers.Create"] = () => tracking.SaveContainerAsync(new() { ShipmentId = shipmentId, ContainerNumber = "NEW" }),
        ["Containers.Update"] = () => tracking.SaveContainerAsync(container.Metadata),
        ["Containers.Delete"] = () => tracking.DeleteContainerAsync(containerId, container.Metadata.Version),
        ["Contents.Update"] = () => tracking.SaveContentsAsync(container.Contents),
        ["Contents.Parts"] = () => tracking.GetPartsAsync(), ["Contents.References"] = () => tracking.GetContainerReferencesAsync(containerId),
        ["Creation.Shipment"] = () => tracking.GetContainerCreationShipmentAsync(shipmentId),
        ["Bills.Read"] = () => tracking.GetBillsAsync(), ["Bills.Get"] = () => tracking.GetBillAsync(billId),
        ["Bills.Create"] = () => tracking.SaveBillAsync(new() { Number = "NEW", SupplierId = supplierId }),
        ["Bills.Update"] = () => tracking.SaveBillAsync(bill),
        ["Bills.CreationSuppliers"] = () => tracking.GetBillSupplierOptionsAsync(0),
        ["Bills.UpdateSuppliers"] = () => tracking.GetBillSupplierOptionsAsync(billId),
        ["Receive"] = () => receiving.ReceiveContainerAsync(containerId, container.Metadata.Version, tracking.Today),
        ["Unreceive"] = () => receiving.UnreceiveContainerAsync(containerId, container.Metadata.Version),
        ["Quantities"] = () => receiving.CorrectActualReceivedQuantityAsync(lineId, container.Metadata.Version, 100, "Correction"),
        ["Receiving.Read"] = () => receiving.GetReceivedPartsAsync(null, true),
        ["Receiving.Candidates"] = () => receiving.GetCandidatesAsync(lineId),
        ["Receiving.Begin"] = () => receiving.BeginInspectionAsync(new() { ContainerGroupPartId = lineId }),
        ["Receiving.Bump"] = () => receiving.BumpUpAsync(new() { ContainerGroupPartId = lineId }),
        ["Receiving.Reverse"] = () => receiving.ReverseAllocationAsync(1, "Reverse")
    };

    [Theory]
    [InlineData("anonymous")]
    [InlineData("no grants")]
    [InlineData("inactive")]
    [InlineData("stale session")]
    [InlineData("revoked")]
    [InlineData("forged Administrator")]
    [InlineData("PendingRoot")]
    public async Task EveryBoundaryRejectsInvalidOrUnauthorizedActors(string kind)
    {
        var actor = await Actor(PermissionCatalog.All.Where(x => x.Authority == PermissionAuthority.Ordinary).Select(x => x.Key).ToArray());
        await using (var db = database.CreateDbContext())
        {
            if (kind is "no grants" or "revoked" or "forged Administrator")
                await db.UserRoles.Where(x => x.UserId == actor).ExecuteDeleteAsync();
            if (kind == "inactive") { var user = await db.Users.SingleAsync(x => x.Id == actor); user.IsActive = false; await db.SaveChangesAsync(); }
            if (kind == "stale session") { var user = await db.Users.SingleAsync(x => x.Id == actor); user.SecurityStamp = Guid.NewGuid().ToString(); await db.SaveChangesAsync(); }
        }
        if (kind == "anonymous") session.Principal = new(new ClaimsIdentity());
        if (kind == "forged Administrator") ((ClaimsIdentity)session.Principal.Identity!).AddClaim(new(ClaimTypes.Role, AppRoles.Administrator));
        if (kind == "PendingRoot") { await database.ResetAsync(); await Actor(Permissions.Shipments.Read); }
        foreach (var (name, call) in Boundaries())
        {
            var error = await Record.ExceptionAsync(call);
            Assert.True(error is AuthorizationDeniedException, $"{kind} / {name}: {error}");
        }
    }

    [Theory]
    [InlineData("Shipments.Read")]
    [InlineData("Containers.Read")]
    [InlineData("BillsOfLading.Read")]
    [InlineData("Receiving.Read")]
    public async Task ReadDoesNotGrantMutationsOrOtherResourceReads(string key)
    {
        await Actor(key);
        if (key == Permissions.Shipments.Read)
        {
            Assert.Empty(Assert.Single(await tracking.SearchAsync()).Containers);
            Assert.NotNull(await tracking.GetShipmentAsync(shipmentId));
        }
        if (key == Permissions.Containers.Read)
        {
            Assert.NotNull(await tracking.GetContainerAsync(containerId));
            Assert.Single((await tracking.GetContainerReferencesAsync(containerId)).Bills);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.GetBillsAsync());
        }
        if (key == Permissions.BillsOfLading.Read) Assert.NotNull(await tracking.GetBillAsync(billId));
        if (key == Permissions.Receiving.Read) Assert.Empty(await receiving.GetReceivedPartsAsync(null, true));
        foreach (var call in Boundaries().Where(x => x.Key.Contains("Create") || x.Key.Contains("Update") || x.Key.Contains("Delete")
            || x.Key is "Receive" or "Unreceive" or "Quantities" or "Receiving.Begin" or "Receiving.Bump" or "Receiving.Reverse"))
            await Assert.ThrowsAsync<AuthorizationDeniedException>(call.Value);
    }

    [Theory]
    [InlineData("Shipments")]
    [InlineData("Containers")]
    [InlineData("BillsOfLading")]
    public async Task CreateAndUpdateAreIndependentAndMissingIdsNeverBecomeCreates(string resource)
    {
        await Actor(resource + ".Create");
        if (resource == "Shipments")
        {
            Assert.True((await tracking.SaveShipmentAsync(new() { BillNumbers = [new() { Number = "CREATED" }] })).Succeeded);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.SaveShipmentAsync(shipment));
        }
        if (resource == "Containers")
        {
            Assert.True((await tracking.SaveContainerAsync(new() { ShipmentId = shipmentId, ContainerNumber = "CREATED" })).Succeeded);
            Assert.NotNull(await tracking.GetContainerCreationShipmentAsync(shipmentId));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.SaveContainerAsync(container.Metadata));
        }
        if (resource == "BillsOfLading")
        {
            Assert.True((await tracking.SaveBillAsync(new() { Number = "CREATED", SupplierId = supplierId })).Succeeded);
            Assert.NotEmpty(await tracking.GetBillSupplierOptionsAsync(0));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.SaveBillAsync(bill));
        }
        await Actor(resource + ".Update");
        if (resource == "Shipments")
        {
            shipment.FreightCost = 12; Assert.True((await tracking.SaveShipmentAsync(shipment)).Succeeded);
            shipment.Id = long.MaxValue; Assert.False((await tracking.SaveShipmentAsync(shipment)).Succeeded);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.SaveShipmentAsync(new()));
        }
        if (resource == "Containers")
        {
            container.Metadata.CbpNumber = "UPDATED"; Assert.True((await tracking.SaveContainerAsync(container.Metadata)).Succeeded);
            container.Metadata.Id = long.MaxValue; Assert.False((await tracking.SaveContainerAsync(container.Metadata)).Succeeded);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.SaveContainerAsync(new()));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.SaveContentsAsync(container.Contents));
        }
        if (resource == "BillsOfLading")
        {
            bill.Duty = 12; Assert.True((await tracking.SaveBillAsync(bill)).Succeeded);
            bill.Id = long.MaxValue; Assert.False((await tracking.SaveBillAsync(bill)).Succeeded);
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.SaveBillAsync(new()));
        }
    }

    [Fact]
    public async Task ContentsAndDeleteHaveIndependentAuthority()
    {
        await Actor(Permissions.ContainerContents.Update);
        container.Contents.Groups[0].Parts[0].Quantity = 90;
        Assert.True((await tracking.SaveContentsAsync(container.Contents)).Succeeded);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.SaveContainerAsync(container.Metadata));
        await Actor(Permissions.Containers.Delete);
        var fresh = (await tracking.GetContainerAsync(containerId))!;
        Assert.True((await tracking.DeleteContainerAsync(containerId, fresh.Metadata.Version)).Succeeded);
        await Actor(Permissions.Shipments.Delete);
        Assert.True((await tracking.DeleteShipmentAsync(shipmentId, shipment.Version)).Succeeded);
    }

    [Fact]
    public async Task DepartedExceptionsRequireNormalAuthorityAndDoNotUnlockContents()
    {
        await using (var db = database.CreateDbContext())
        { var box = await db.Containers.SingleAsync(); box.EstimatedDepartureDate = tracking.Today.AddDays(-1); await db.SaveChangesAsync(); }
        await Reload(); container.Metadata.ContainerNumber = "CORRECTED"; bill.Duty = 40;
        await Actor(Permissions.Containers.Update, Permissions.Containers.Delete, Permissions.BillsOfLading.Update);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.SaveContainerAsync(container.Metadata));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.DeleteContainerAsync(containerId, container.Metadata.Version));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => tracking.SaveBillAsync(bill));
        await Actor(Permissions.Containers.CorrectDepartedMetadata, Permissions.BillsOfLading.CorrectDeparted, Permissions.ContainerContents.Update);
        Assert.True((await tracking.SaveContainerAsync(container.Metadata)).Succeeded);
        Assert.True((await tracking.SaveBillAsync(bill)).Succeeded);
        var current = (await tracking.GetContainerAsync(containerId))!;
        Assert.False((await tracking.SaveContentsAsync(current.Contents)).Succeeded);
        await session.SignInAsync(root);
        Assert.False((await tracking.SaveContentsAsync(current.Contents)).Succeeded);
        await Actor(Permissions.Containers.DeleteDeparted);
        Assert.True((await tracking.DeleteContainerAsync(containerId, current.Metadata.Version)).Succeeded);
    }

    [Fact]
    public async Task InitialReceiptCannotOverwriteExistingOrHistoricalReceiptAndDenialLeavesHistoryUnchanged()
    {
        await Actor(Permissions.Containers.Receive);
        Assert.True((await receiving.ReceiveContainerAsync(containerId, container.Metadata.Version, tracking.Today)).Succeeded);
        container = (await tracking.GetContainerAsync(containerId))!;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => receiving.ReceiveContainerAsync(containerId, container.Metadata.Version, tracking.Today.AddDays(-1), "Attempt"));
        await using (var db = database.CreateDbContext())
        { Assert.Single(await db.ContainerReceiptHistory.ToListAsync()); Assert.Equal(tracking.Today, (await db.Containers.SingleAsync()).ReceivedDate); }
        await Actor(Permissions.Containers.CorrectReceipt);
        Assert.True((await receiving.ReceiveContainerAsync(containerId, container.Metadata.Version, tracking.Today.AddDays(-1), "Correction")).Succeeded);
        container = (await tracking.GetContainerAsync(containerId))!;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => receiving.UnreceiveContainerAsync(containerId, container.Metadata.Version));
        await Actor(Permissions.Containers.Unreceive);
        Assert.True((await receiving.UnreceiveContainerAsync(containerId, container.Metadata.Version)).Succeeded);
        container = (await tracking.GetContainerAsync(containerId))!;
        await Actor(Permissions.Containers.CorrectReceipt);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => receiving.ReceiveContainerAsync(containerId, container.Metadata.Version, tracking.Today));
        await using (var db = database.CreateDbContext())
        { var box = await db.Containers.SingleAsync(); box.ReceivedDate = tracking.Today; await db.SaveChangesAsync(); }
        await Actor(Permissions.Containers.Receive); container = (await tracking.GetContainerAsync(containerId))!;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => receiving.ReceiveContainerAsync(containerId, container.Metadata.Version, tracking.Today, "Reconcile"));
    }

    [Fact]
    public async Task ReceivingReadUsesOneSnapshotWithoutInspectionReadAndRevocationAffectsNextOperation()
    {
        Assert.True((await receiving.ReceiveContainerAsync(containerId, container.Metadata.Version, tracking.Today)).Succeeded);
        var actor = await Actor(Permissions.Receiving.Read);
        var before = evaluator.SnapshotCount;
        Assert.Single(await receiving.GetReceivedPartsAsync(null, true));
        Assert.Equal(before + 1, evaluator.SnapshotCount);
        Assert.Empty(await receiving.GetCandidatesAsync(lineId));
        await using var db = database.CreateDbContext();
        Assert.False(await db.UserRoles.AnyAsync(x => x.UserId == actor && x.RoleId == AppRoles.ReadOnlyId));
        await db.UserRoles.Where(x => x.UserId == actor).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => receiving.GetReceivedPartsAsync(null, true));
    }

    private sealed class FixtureClock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero); }
}
