using Confast.Web.Features.Customers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CustomerCertificationDeliveryTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly CustomerService customerService = new(database);
    private readonly CertificationPackageFilenameFormatter formatter = new();

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RecipientCanBeCreatedUpdatedAndDeletedWithValidation()
    {
        var customerId = await CreateCustomerAsync();
        var invalid = await customerService.SaveCertificationRecipientAsync(
            new CustomerCertificationRecipientEditModel
            {
                CustomerId = customerId,
                EmailAddress = "definitely not email"
            });
        Assert.Equal(CustomerCertificationOperationStatus.ValidationFailed, invalid.Status);

        var created = await customerService.SaveCertificationRecipientAsync(
            new CustomerCertificationRecipientEditModel
            {
                CustomerId = customerId,
                Name = "  Quality Desk  ",
                EmailAddress = "  quality@example.com  ",
                RecipientType = CertificationRecipientType.Cc
            });
        Assert.Equal(CustomerCertificationOperationStatus.Succeeded, created.Status);

        var configuration = await customerService.GetCertificationDeliveryAsync(customerId);
        var recipient = Assert.Single(configuration!.Recipients);
        Assert.Equal("Quality Desk", recipient.Name);
        Assert.Equal("quality@example.com", recipient.EmailAddress);
        Assert.Equal(CertificationRecipientType.Cc, recipient.RecipientType);

        recipient.Name = "Receiving";
        recipient.RecipientType = CertificationRecipientType.To;
        var updated = await customerService.SaveCertificationRecipientAsync(recipient);
        Assert.Equal(CustomerCertificationOperationStatus.Succeeded, updated.Status);

        var deleted = await customerService.DeleteCertificationRecipientAsync(
            customerId,
            recipient.Id,
            updated.Version!.Value);
        Assert.Equal(CustomerCertificationOperationStatus.Succeeded, deleted.Status);
        Assert.Empty((await customerService.GetCertificationDeliveryAsync(customerId))!.Recipients);
    }

    [Fact]
    public async Task StaleRecipientUpdateIsRejected()
    {
        var customerId = await CreateCustomerAsync();
        await customerService.SaveCertificationRecipientAsync(
            new CustomerCertificationRecipientEditModel
            {
                CustomerId = customerId,
                EmailAddress = "quality@example.com"
            });
        var firstCopy = Assert.Single(
            (await customerService.GetCertificationDeliveryAsync(customerId))!.Recipients);
        var staleCopy = new CustomerCertificationRecipientEditModel
        {
            Id = firstCopy.Id,
            CustomerId = firstCopy.CustomerId,
            EmailAddress = firstCopy.EmailAddress,
            RecipientType = firstCopy.RecipientType,
            Version = firstCopy.Version
        };

        firstCopy.Name = "First editor";
        Assert.Equal(
            CustomerCertificationOperationStatus.Succeeded,
            (await customerService.SaveCertificationRecipientAsync(firstCopy)).Status);
        staleCopy.Name = "Stale editor";

        Assert.Equal(
            CustomerCertificationOperationStatus.Conflict,
            (await customerService.SaveCertificationRecipientAsync(staleCopy)).Status);
    }

    [Fact]
    public async Task RequirementsAndCustomerFilenameTemplatePersist()
    {
        var customerId = await CreateCustomerAsync();
        var configuration = (await customerService.GetCertificationDeliveryAsync(customerId))!;
        configuration.CertificationTypes.Single(x => x.Name == "Material").IsRequired = true;
        configuration.CertificationTypes.Single(x => x.Name == "Plate").IsRequired = true;
        configuration.FilenameTemplate = "{PartNumber}_{LotNumber}_CERTS";
        configuration.MultiLotFilenameTemplate = "{CustomerName}_COMBINED_CERTS";

        var saved = await customerService.SaveCertificationConfigurationAsync(configuration, formatter);

        Assert.Equal(CustomerCertificationOperationStatus.Succeeded, saved.Status);
        var reloaded = (await customerService.GetCertificationDeliveryAsync(customerId))!;
        Assert.Equal(
            ["Material", "Plate"],
            reloaded.CertificationTypes.Where(x => x.IsRequired).Select(x => x.Name));
        Assert.Equal("{PartNumber}_{LotNumber}_CERTS", reloaded.FilenameTemplate);
        Assert.Equal("{CustomerName}_COMBINED_CERTS", reloaded.MultiLotFilenameTemplate);
    }

    [Fact]
    public async Task MultiLotTemplatePersistsWithoutCreatingASingleLotOverride()
    {
        var customerId = await CreateCustomerAsync();
        var configuration = (await customerService.GetCertificationDeliveryAsync(customerId))!;
        configuration.MultiLotFilenameTemplate = "{CustomerName}_BATCH_CERTS";

        var saved = await customerService.SaveCertificationConfigurationAsync(configuration, formatter);

        Assert.Equal(CustomerCertificationOperationStatus.Succeeded, saved.Status);
        var reloaded = (await customerService.GetCertificationDeliveryAsync(customerId))!;
        Assert.Null(reloaded.FilenameTemplate);
        Assert.Equal("{CustomerName}_BATCH_CERTS", reloaded.MultiLotFilenameTemplate);
        Assert.Equal(
            "ABC123_856342.pdf",
            formatter.Format(
                reloaded.FilenameTemplate,
                new CertificationPackageFilenameValues(
                    "Example Customer",
                    "ABC123",
                    "856342",
                    "PO-1001",
                    new DateOnly(2026, 8, 28))));
    }

    [Fact]
    public async Task DuplicateCustomerRequirementIsRejectedByTheDatabase()
    {
        var customerId = await CreateCustomerAsync();
        await using var db = database.CreateDbContext();
        var materialTypeId = await db.CertificationTypes
            .Where(x => x.Name == "Material")
            .Select(x => x.Id)
            .SingleAsync();
        db.CustomerCertificationRequirements.Add(new CustomerCertificationRequirement
        {
            CustomerId = customerId,
            CertificationTypeId = materialTypeId
        });
        await db.SaveChangesAsync();

        await using var duplicateContext = database.CreateDbContext();
        duplicateContext.CustomerCertificationRequirements.Add(new CustomerCertificationRequirement
        {
            CustomerId = customerId,
            CertificationTypeId = materialTypeId
        });
        var exception = await Record.ExceptionAsync(() => duplicateContext.SaveChangesAsync());
        var postgresException = Assert.IsType<PostgresException>(exception!.GetBaseException());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
    }

    [Fact]
    public async Task ConfigurationRejectsUnknownFilenameToken()
    {
        var customerId = await CreateCustomerAsync();
        var configuration = (await customerService.GetCertificationDeliveryAsync(customerId))!;
        configuration.FilenameTemplate = "{PartNumber}_{CertificationType}";

        var result = await customerService.SaveCertificationConfigurationAsync(configuration, formatter);

        Assert.Equal(CustomerCertificationOperationStatus.ValidationFailed, result.Status);
        Assert.Contains("{CertificationType}", result.Message);
    }

    [Fact]
    public async Task ConfigurationRejectsLotTokenInMultiLotFilename()
    {
        var customerId = await CreateCustomerAsync();
        var configuration = (await customerService.GetCertificationDeliveryAsync(customerId))!;
        configuration.MultiLotFilenameTemplate = "{CustomerName}_{LotNumber}";

        var result = await customerService.SaveCertificationConfigurationAsync(configuration, formatter);

        Assert.Equal(CustomerCertificationOperationStatus.ValidationFailed, result.Status);
        Assert.Contains("{LotNumber}", result.Message);
    }

    [Fact]
    public async Task CustomerDeletionCascadesOwnedDeliveryConfiguration()
    {
        var customerId = await CreateCustomerAsync();
        await customerService.SaveCertificationRecipientAsync(
            new CustomerCertificationRecipientEditModel
            {
                CustomerId = customerId,
                EmailAddress = "quality@example.com"
            });
        var configuration = (await customerService.GetCertificationDeliveryAsync(customerId))!;
        configuration.CertificationTypes.Single(x => x.Name == "Material").IsRequired = true;
        configuration.FilenameTemplate = "{PartNumber}_CERTS";
        await customerService.SaveCertificationConfigurationAsync(configuration, formatter);

        await using (var db = database.CreateDbContext())
        {
            db.Customers.Remove(await db.Customers.SingleAsync(x => x.Id == customerId));
            await db.SaveChangesAsync();
        }

        await using var verification = database.CreateDbContext();
        Assert.Empty(await verification.CustomerCertificationRecipients.ToListAsync());
        Assert.Empty(await verification.CustomerCertificationRequirements.ToListAsync());
        Assert.Empty(await verification.CustomerCertificationSettings.ToListAsync());
    }

    [Fact]
    public async Task CertificationTypeDeletionIsRestrictedWhenCustomerRequiresIt()
    {
        var customerId = await CreateCustomerAsync();
        var configuration = (await customerService.GetCertificationDeliveryAsync(customerId))!;
        configuration.CertificationTypes.Single(x => x.Name == "Material").IsRequired = true;
        await customerService.SaveCertificationConfigurationAsync(configuration, formatter);

        await using var db = database.CreateDbContext();
        db.CertificationTypes.Remove(await db.CertificationTypes.SingleAsync(x => x.Name == "Material"));
        var exception = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        var postgresException = Assert.IsType<PostgresException>(exception!.GetBaseException());
        Assert.Equal(PostgresErrorCodes.RestrictViolation, postgresException.SqlState);
    }

    private async Task<long> CreateCustomerAsync()
    {
        await using var db = database.CreateDbContext();
        var customer = new Customer { Name = $"Delivery Customer {Guid.NewGuid():N}" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        return customer.Id;
    }
}
