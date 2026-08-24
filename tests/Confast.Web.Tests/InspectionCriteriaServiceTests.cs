using Confast.Web.Features.Customers;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Parts;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class InspectionCriteriaServiceTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly InspectionCriteriaService service = new(database);

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task InitialRevisionCanBePublishedAndBecomesCurrent()
    {
        var partId = await CreatePartAsync();
        var draft = await service.CreateDraftRevisionAsync(partId, "Initial requirements");

        Assert.Equal(CriteriaOperationStatus.Succeeded, draft.Status);

        var add = await service.AddCriterionAsync(
            partId,
            draft.RevisionId!.Value,
            new InspectionCriterionEditModel
            {
                Name = "Outside diameter",
                MinimumValue = 1.234567m,
                MaximumValue = 1.234890m,
                Unit = "in"
            });
        Assert.Equal(CriteriaOperationStatus.Succeeded, add.Status);

        var details = await service.GetRevisionAsync(partId, draft.RevisionId.Value);
        var publish = await service.PublishRevisionAsync(
            partId,
            draft.RevisionId.Value,
            details!.Version);

        Assert.Equal(CriteriaOperationStatus.Succeeded, publish.Status);

        var summary = await service.GetPartSummaryAsync(partId);
        Assert.Equal(1, summary!.CurrentRevision!.RevisionNumber);
        Assert.Null(summary.DraftRevision);
        Assert.Equal(1.234567m, details.Criteria.Single().MinimumValue);
    }

    [Fact]
    public async Task NewRevisionCopiesCurrentWithoutChangingHistory()
    {
        var partId = await CreatePartAsync();
        var firstId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        await service.AddCriterionAsync(
            partId,
            firstId,
            new InspectionCriterionEditModel { Name = "Thread pitch", InspectionMethod = "Comparator" });
        var firstDraft = await service.GetRevisionAsync(partId, firstId);
        await service.PublishRevisionAsync(partId, firstId, firstDraft!.Version);

        var secondId = (await service.CreateDraftRevisionAsync(partId, "Tolerance update")).RevisionId!.Value;
        var secondDraft = await service.GetRevisionAsync(partId, secondId);
        var copied = secondDraft!.Criteria.Single();

        var save = await service.SaveCriterionAsync(
            partId,
            secondId,
            new InspectionCriterionEditModel
            {
                Id = copied.Id,
                RevisionId = secondId,
                Name = "Thread pitch — revised",
                InspectionMethod = copied.InspectionMethod,
                Version = copied.Version
            });
        Assert.Equal(CriteriaOperationStatus.Succeeded, save.Status);

        secondDraft = await service.GetRevisionAsync(partId, secondId);
        await service.PublishRevisionAsync(partId, secondId, secondDraft!.Version);

        var firstPublished = await service.GetRevisionAsync(partId, firstId);
        var summary = await service.GetPartSummaryAsync(partId);

        Assert.Equal("Thread pitch", firstPublished!.Criteria.Single().Name);
        Assert.Equal(2, summary!.CurrentRevision!.RevisionNumber);
        Assert.Equal(2, (await service.GetRevisionHistoryAsync(partId)).Count);

        var attemptToEditHistory = await service.SaveCriterionAsync(
            partId,
            firstId,
            new InspectionCriterionEditModel
            {
                Id = firstPublished.Criteria.Single().Id,
                RevisionId = firstId,
                Name = "Should not save",
                Version = firstPublished.Criteria.Single().Version
            });
        Assert.Equal(CriteriaOperationStatus.PublishedRevision, attemptToEditHistory.Status);
    }

    [Fact]
    public async Task DatabaseRejectsDirectChangesToPublishedCriteria()
    {
        var partId = await CreatePartAsync();
        var revisionId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        await service.AddCriterionAsync(
            partId,
            revisionId,
            new InspectionCriterionEditModel { Name = "Length" });
        var draft = await service.GetRevisionAsync(partId, revisionId);
        await service.PublishRevisionAsync(partId, revisionId, draft!.Version);

        await using var db = database.CreateDbContext();
        var criterion = await db.InspectionCriteria.SingleAsync();
        criterion.Name = "Bypass attempt";

        var exception = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        var postgresException = Assert.IsType<Npgsql.PostgresException>(exception!.GetBaseException());
        Assert.Equal("55000", postgresException.SqlState);
    }

    [Fact]
    public async Task OnlyOneDraftCanExistForAPart()
    {
        var partId = await CreatePartAsync();
        var first = await service.CreateDraftRevisionAsync(partId, null);
        var second = await service.CreateDraftRevisionAsync(partId, null);

        Assert.Equal(CriteriaOperationStatus.Succeeded, first.Status);
        Assert.Equal(CriteriaOperationStatus.DraftAlreadyExists, second.Status);
        Assert.Equal(first.RevisionId, second.RevisionId);
    }

    [Fact]
    public async Task PartWithCriteriaCannotBeDeleted()
    {
        var partId = await CreatePartAsync();
        await service.CreateDraftRevisionAsync(partId, null);

        await using var db = database.CreateDbContext();
        var version = await db.Parts
            .Where(x => x.Id == partId)
            .Select(x => x.Version)
            .SingleAsync();
        var partService = new PartService(database);

        var status = await partService.DeletePartAsync(partId, version);

        Assert.Equal(DeletePartStatus.Blocked, status);
    }

    private async Task<long> CreatePartAsync()
    {
        await using var db = database.CreateDbContext();
        var customer = new Customer { Name = "Test Customer" };
        var part = new Part { Customer = customer, PartNumber = "TEST-100" };
        db.Parts.Add(part);
        await db.SaveChangesAsync();
        return part.Id;
    }
}
