using Confast.Web.Features.Customers;
using Confast.Web.Features.Parts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PlantTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly CustomerService customerService = new(database);
    private readonly PartService partService = new(database);

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreatingCustomerCreatesMainPlantAndNewPartUsesIt()
    {
        var customerId = await customerService.CreateCustomerAsync(new CustomerEditModel { Name = "Acme" });
        var plant = Assert.Single(await customerService.GetPlantsAsync(customerId));
        Assert.Equal("Main", plant.Name);

        var result = await partService.CreatePartAsync(new PartEditModel { CustomerId = customerId, PartNumber = "P-1" });
        Assert.Equal(SavePartStatus.Saved, result.Status);
        await using var db = database.CreateDbContext();
        Assert.Equal(plant.Id, await db.PartPlants.Where(x => x.PartId == result.Id).Select(x => x.PlantId).SingleAsync());
    }

    [Fact]
    public async Task PartCanBeAssignedToMultiplePlants()
    {
        var customerId = await CreateCustomerAsync("Acme");
        var first = await SavePlantAsync(customerId, "North");
        var second = await SavePlantAsync(customerId, "South");

        var result = await partService.CreatePartAsync(new PartEditModel
        {
            CustomerId = customerId, PartNumber = "P-2", PlantIds = [first, second]
        });

        Assert.Equal(SavePartStatus.Saved, result.Status);
        await using var db = database.CreateDbContext();
        Assert.Equal(2, await db.PartPlants.CountAsync(x => x.PartId == result.Id));
    }

    [Fact]
    public async Task CrossCustomerPlantAssignmentIsRejectedByServiceAndDatabase()
    {
        var firstCustomer = await CreateCustomerAsync("First");
        var secondCustomer = await CreateCustomerAsync("Second");
        var plantId = await SavePlantAsync(secondCustomer, "Other");
        var result = await partService.CreatePartAsync(new PartEditModel
        {
            CustomerId = firstCustomer, PartNumber = "P-3", PlantIds = [plantId]
        });
        Assert.Equal(SavePartStatus.ValidationFailed, result.Status);

        await using var db = database.CreateDbContext();
        var part = new Part { CustomerId = firstCustomer, PartNumber = "P-4" };
        db.Parts.Add(part);
        await db.SaveChangesAsync();
        db.PartPlants.Add(new PartPlant { PartId = part.Id, PlantId = plantId });
        var exception = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.RaiseException, Assert.IsType<PostgresException>(exception!.GetBaseException()).SqlState);
    }

    [Fact]
    public async Task DuplicatePartPlantAssignmentIsRejectedByCompositeKey()
    {
        var customerId = await CreateCustomerAsync("Acme");
        var plantId = await SavePlantAsync(customerId, "Main");
        await using var db = database.CreateDbContext();
        var part = new Part { CustomerId = customerId, PartNumber = "P-5" };
        db.Parts.Add(part);
        await db.SaveChangesAsync();
        db.PartPlants.Add(new PartPlant { PartId = part.Id, PlantId = plantId });
        await db.SaveChangesAsync();
        await using var duplicate = database.CreateDbContext();
        duplicate.PartPlants.Add(new PartPlant { PartId = part.Id, PlantId = plantId });
        var exception = await Record.ExceptionAsync(() => duplicate.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(exception!.GetBaseException()).SqlState);
    }

    [Fact]
    public async Task PlantWithSolePartAssignmentsCannotBeDeleted()
    {
        var customerId = await CreateCustomerAsync("Acme");
        var plantId = await SavePlantAsync(customerId, "Main");
        var part = await partService.CreatePartAsync(new PartEditModel
        {
            CustomerId = customerId, PartNumber = "P-6", PlantIds = [plantId]
        });
        Assert.Equal(SavePartStatus.Saved, part.Status);
        var plant = Assert.Single(await customerService.GetPlantsAsync(customerId));

        var result = await customerService.DeletePlantAsync(customerId, plant.Id, plant.Version);

        Assert.Equal(PlantOperationStatus.Blocked, result.Status);
    }

    [Fact]
    public async Task PlantWithOnlySharedPartAssignmentsCanBeDeleted()
    {
        var customerId = await CreateCustomerAsync("Acme");
        var northId = await SavePlantAsync(customerId, "North");
        var southId = await SavePlantAsync(customerId, "South");
        var part = await partService.CreatePartAsync(new PartEditModel
        {
            CustomerId = customerId, PartNumber = "P-7", PlantIds = [northId, southId]
        });
        Assert.Equal(SavePartStatus.Saved, part.Status);
        var north = (await customerService.GetPlantsAsync(customerId)).Single(x => x.Id == northId);

        var result = await customerService.DeletePlantAsync(customerId, northId, north.Version);

        Assert.Equal(PlantOperationStatus.Succeeded, result.Status);
        await using var db = database.CreateDbContext();
        Assert.Equal(southId, await db.PartPlants.Where(x => x.PartId == part.Id).Select(x => x.PlantId).SingleAsync());
    }

    [Fact]
    public async Task PlantWithOnlyInactiveSolePartAssignmentsCanBeDeleted()
    {
        var customerId = await CreateCustomerAsync("Acme");
        var plantId = await SavePlantAsync(customerId, "Main");
        var part = await partService.CreatePartAsync(new PartEditModel
        {
            CustomerId = customerId, PartNumber = "P-8", PlantIds = [plantId], IsActive = false
        });
        Assert.Equal(SavePartStatus.Saved, part.Status);
        var plant = Assert.Single(await customerService.GetPlantsAsync(customerId));

        var result = await customerService.DeletePlantAsync(customerId, plantId, plant.Version);

        Assert.Equal(PlantOperationStatus.Succeeded, result.Status);
        await using var db = database.CreateDbContext();
        Assert.False(await db.Plants.AnyAsync(x => x.Id == plantId));
    }

    [Fact]
    public async Task PlantAddressesAreStoredIndependently()
    {
        var customerId = await CreateCustomerAsync("Acme");
        var north = await customerService.SavePlantAsync(new PlantEditModel
        {
            CustomerId = customerId, Name = "North", AddressLine1 = "100 North Road",
            City = "Cleveland", State = "OH", PostalCode = "44101"
        });
        var south = await customerService.SavePlantAsync(new PlantEditModel
        {
            CustomerId = customerId, Name = "South", AddressLine1 = "200 South Road",
            City = "Columbus", State = "OH", PostalCode = "43215"
        });

        var plants = await customerService.GetPlantsAsync(customerId);

        Assert.Equal("100 North Road", plants.Single(x => x.Id == north.Id).AddressLine1);
        Assert.Equal("200 South Road", plants.Single(x => x.Id == south.Id).AddressLine1);
    }

    private async Task<long> CreateCustomerAsync(string name)
    {
        await using var db = database.CreateDbContext();
        var customer = new Customer { Name = $"{name} {Guid.NewGuid():N}" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }

    private async Task<long> SavePlantAsync(long customerId, string name)
    {
        var result = await customerService.SavePlantAsync(new PlantEditModel { CustomerId = customerId, Name = name });
        Assert.Equal(PlantOperationStatus.Succeeded, result.Status);
        return result.Id!.Value;
    }
}
