using Confast.Web.Features.Customers;
using Confast.Web.Features.Gages;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Parts;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class GageServiceTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly GageService service = new(database);

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CreatesGageTypeAndRejectsDuplicateName()
    {
        var first = await service.SaveGageTypeAsync(new GageTypeEditModel { Name = " Micrometer " });
        var duplicate = await service.SaveGageTypeAsync(new GageTypeEditModel { Name = "Micrometer" });

        Assert.Equal(SaveGageStatus.Saved, first.Status);
        Assert.Equal(SaveGageStatus.Duplicate, duplicate.Status);
        var type = Assert.Single(await service.GetGageTypesAsync());
        Assert.Equal("Micrometer", type.Name);
        Assert.True(type.IsActive);
    }

    [Fact]
    public async Task CreatesGageWithTypeAndRejectsDuplicateNumber()
    {
        var typeId = await CreateGageTypeAsync("Caliper");
        var first = await service.SaveGageAsync(new GageEditModel
        {
            GageTypeId = typeId,
            GageNumber = " CAL-001 "
        });
        var duplicate = await service.SaveGageAsync(new GageEditModel
        {
            GageTypeId = typeId,
            GageNumber = "CAL-001"
        });

        Assert.Equal(SaveGageStatus.Saved, first.Status);
        Assert.Equal(SaveGageStatus.Duplicate, duplicate.Status);
        var gage = Assert.Single(await service.GetGagesAsync());
        Assert.Equal("CAL-001", gage.GageNumber);
        Assert.Equal(typeId, gage.GageTypeId);
        Assert.Equal("Caliper", gage.GageTypeName);
    }

    [Fact]
    public async Task InspectionMethodChoicesOnlyOfferActiveTypesForNewCriteria()
    {
        var activeId = await CreateGageTypeAsync("Micrometer");
        var inactiveId = await CreateGageTypeAsync("Comparator", false);

        var choices = await service.GetGageTypeChoicesAsync(activeOnly: true);

        Assert.Collection(choices, choice => Assert.Equal(activeId, choice.Id));
        Assert.DoesNotContain(choices, x => x.Id == inactiveId);
    }

    [Fact]
    public async Task CriterionStoresTypeAndHistoricalMethodNameSnapshot()
    {
        var typeId = await CreateGageTypeAsync("Thread Ring Gage");
        var partId = await CreatePartAsync();
        var criteriaService = new InspectionCriteriaService(database);
        var revisionId = (await criteriaService.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;

        var save = await criteriaService.AddCriterionAsync(partId, revisionId, new InspectionCriterionEditModel
        {
            InspectionNumber = 1,
            Name = "Thread fit",
            GageTypeId = typeId
        });
        var draft = await criteriaService.GetRevisionAsync(partId, revisionId);
        await criteriaService.PublishRevisionAsync(partId, revisionId, draft!.Version);

        await using (var db = database.CreateDbContext())
        {
            var type = await db.GageTypes.SingleAsync(x => x.Id == typeId);
            type.Name = "Thread Ring Gage — renamed";
            await db.SaveChangesAsync();
        }

        var published = await criteriaService.GetRevisionAsync(partId, revisionId);
        var criterion = Assert.Single(published!.Criteria);
        Assert.Equal(CriteriaOperationStatus.Succeeded, save.Status);
        Assert.Equal(typeId, criterion.GageTypeId);
        Assert.Equal("Thread Ring Gage", criterion.InspectionMethod);
    }

    [Fact]
    public async Task InactiveTypeCannotBeSelectedForNewCriterion()
    {
        var typeId = await CreateGageTypeAsync("Inactive method", false);
        var partId = await CreatePartAsync();
        var criteriaService = new InspectionCriteriaService(database);
        var revisionId = (await criteriaService.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;

        var result = await criteriaService.AddCriterionAsync(partId, revisionId, new InspectionCriterionEditModel
        {
            InspectionNumber = 1,
            Name = "Length",
            GageTypeId = typeId
        });

        Assert.Equal(CriteriaOperationStatus.ValidationFailed, result.Status);
    }

    private async Task<long> CreateGageTypeAsync(string name, bool isActive = true)
    {
        await using var db = database.CreateDbContext();
        var type = new GageType { Name = name, IsActive = isActive };
        db.GageTypes.Add(type);
        await db.SaveChangesAsync();
        return type.Id;
    }

    private async Task<long> CreatePartAsync()
    {
        await using var db = database.CreateDbContext();
        var part = new Part
        {
            Customer = new Customer { Name = "Test Customer" },
            PartNumber = "TEST-GAGE"
        };
        db.Parts.Add(part);
        await db.SaveChangesAsync();
        return part.Id;
    }
}
