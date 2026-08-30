using Confast.Web.Features.Customers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class PlantCertificationDeliveryTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly CustomerService customerService = new(database);
    private readonly CertificationPackageFilenameFormatter formatter = new();

    public Task InitializeAsync() => database.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RecipientRequirementsAndFilenameAreIndependentPerPlant()
    {
        var customerId = await customerService.CreateCustomerAsync(new CustomerEditModel { Name = "Acme" });
        var first = Assert.Single(await customerService.GetPlantsAsync(customerId));
        var second = await customerService.SavePlantAsync(new PlantEditModel { CustomerId = customerId, Name = "South" });
        var recipient = await customerService.SavePlantCertificationRecipientAsync(new PlantCertificationRecipientEditModel
        {
            PlantId = first.Id, EmailAddress = "quality@example.com"
        });
        Assert.Equal(PlantCertificationOperationStatus.Succeeded, recipient.Status);

        var configuration = (await customerService.GetPlantCertificationDeliveryAsync(first.Id))!;
        Assert.True(configuration.CertificationTypes.Single(x => x.Name == "Inspection Sheet").IsRequired);
        configuration.CertificationTypes.Single(x => x.Name == "Material").IsRequired = true;
        configuration.MultiPartFilenameTemplate = "{CustomerName}_NORTH";
        Assert.Equal(PlantCertificationOperationStatus.Succeeded,
            (await customerService.SavePlantCertificationConfigurationAsync(configuration, formatter)).Status);

        var secondConfiguration = (await customerService.GetPlantCertificationDeliveryAsync(second.Id!.Value))!;
        Assert.Empty(secondConfiguration.Recipients);
        Assert.True(secondConfiguration.CertificationTypes.Single(x => x.Name == "Inspection Sheet").IsRequired);
        Assert.DoesNotContain(secondConfiguration.CertificationTypes.Where(x => x.Name != "Inspection Sheet"), x => x.IsRequired);
        Assert.Null(secondConfiguration.SinglePartMultiLotFilenameTemplate);
        Assert.Null(secondConfiguration.MultiPartFilenameTemplate);
    }

    [Fact]
    public async Task DuplicatePlantCertificationRequirementIsRejectedByTheDatabase()
    {
        var customerId = await customerService.CreateCustomerAsync(new CustomerEditModel { Name = "Acme" });
        var plantId = (await customerService.GetPlantsAsync(customerId)).Single().Id;
        await using var db = database.CreateDbContext();
        var typeId = await db.CertificationTypes.Where(x => x.Name == "Material").Select(x => x.Id).SingleAsync();
        db.PlantCertificationRequirements.Add(new PlantCertificationRequirement { PlantId = plantId, CertificationTypeId = typeId });
        await db.SaveChangesAsync();
        await using var duplicate = database.CreateDbContext();
        duplicate.PlantCertificationRequirements.Add(new PlantCertificationRequirement { PlantId = plantId, CertificationTypeId = typeId });
        var exception = await Record.ExceptionAsync(() => duplicate.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(exception!.GetBaseException()).SqlState);
    }
}
