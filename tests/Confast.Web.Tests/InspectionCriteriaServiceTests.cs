using Confast.Web.Features.Customers;
using Confast.Web.Features.Gages;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.Parts;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class InspectionCriteriaServiceTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly InspectionCriteriaService service = new(database);
    private readonly InspectionService inspectionService = new(database);

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task InitialRevisionCanBePublishedAndBecomesCurrent()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var draft = await service.CreateDraftRevisionAsync(partId, "Initial requirements");

        Assert.Equal(CriteriaOperationStatus.Succeeded, draft.Status);

        var add = await service.AddCriterionAsync(
            partId,
            draft.RevisionId!.Value,
            new InspectionCriterionEditModel
            {
                InspectionNumber = 1,
                Name = "Outside diameter",
                GageTypeId = gageTypeId,
                Minimum = "1.234567",
                MaximumOrTolerance = "1.234890",
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
        Assert.Equal("1.234567", details.Criteria.Single().Minimum);
    }

    [Fact]
    public async Task PublishedRevisionCanBeEditedUntilAnInspectionStarts()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var revisionId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        await service.AddCriterionAsync(partId, revisionId, new InspectionCriterionEditModel
        {
            InspectionNumber = 1,
            Name = "Length",
            GageTypeId = gageTypeId
        });
        var draft = await service.GetRevisionAsync(partId, revisionId);
        await service.PublishRevisionAsync(partId, revisionId, draft!.Version);

        var published = await service.GetRevisionAsync(partId, revisionId);
        Assert.True(published!.CanEdit);
        Assert.False(published.IsUsedByInspection);

        var headerSave = await service.SaveRevisionHeaderAsync(
            partId,
            revisionId,
            new InspectionCriteriaRevisionHeaderEditModel
            {
                PartDescription = "Corrected before receiving",
                Version = published.Version
            });
        Assert.Equal(CriteriaOperationStatus.Succeeded, headerSave.Status);

        published = await service.GetRevisionAsync(partId, revisionId);
        var criterion = published!.Criteria.Single();
        var criterionSave = await service.SaveCriterionAsync(
            partId,
            revisionId,
            new InspectionCriterionEditModel
            {
                Id = criterion.Id,
                RevisionId = revisionId,
                InspectionNumber = criterion.InspectionNumber,
                Name = "Corrected length",
                GageTypeId = criterion.GageTypeId,
                Version = criterion.Version
            });
        Assert.Equal(CriteriaOperationStatus.Succeeded, criterionSave.Status);

        await CreateInspectionAsync(partId);

        var protectedRevision = await service.GetRevisionAsync(partId, revisionId);
        Assert.False(protectedRevision!.CanEdit);
        Assert.True(protectedRevision.IsUsedByInspection);
        Assert.False((await service.GetRevisionHistoryAsync(partId)).Single().CanEdit);

        criterion = protectedRevision.Criteria.Single();
        var blockedSave = await service.SaveCriterionAsync(
            partId,
            revisionId,
            new InspectionCriterionEditModel
            {
                Id = criterion.Id,
                RevisionId = revisionId,
                InspectionNumber = criterion.InspectionNumber,
                Name = "Too late",
                GageTypeId = criterion.GageTypeId,
                Version = criterion.Version
            });
        Assert.Equal(CriteriaOperationStatus.RevisionInUse, blockedSave.Status);
    }

    [Fact]
    public async Task DraftRevisionCanBeDeletedWithItsRequirements()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var revisionId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        await service.AddCriterionAsync(partId, revisionId, new InspectionCriterionEditModel
        {
            InspectionNumber = 1,
            Name = "Length",
            GageTypeId = gageTypeId
        });
        await service.AddSecondaryProcessRequirementAsync(
            partId,
            revisionId,
            new SecondaryProcessRequirementEditModel
            {
                SecondaryProcessTypeId = 1,
                Specification = "SAE J429"
            });

        await using (var db = database.CreateDbContext())
        {
            db.RevisionCertificationRequirements.Add(new RevisionCertificationRequirement
            {
                InspectionCriteriaRevisionId = revisionId,
                CertificationTypeId = 1,
                CertificationTypeName = "CBP",
                RequirementLevel = CertificationRequirementLevel.Required
            });
            await db.SaveChangesAsync();
        }

        var revision = await service.GetRevisionAsync(partId, revisionId);
        var result = await service.DeleteRevisionAsync(partId, revisionId, revision!.Version);

        Assert.Equal(CriteriaOperationStatus.Succeeded, result.Status);
        await using var verify = database.CreateDbContext();
        Assert.False(await verify.InspectionCriteriaRevisions.AnyAsync(x => x.Id == revisionId));
        Assert.False(await verify.InspectionCriteria.AnyAsync(x => x.InspectionCriteriaRevisionId == revisionId));
        Assert.False(await verify.SecondaryProcessRequirements.AnyAsync(x => x.InspectionCriteriaRevisionId == revisionId));
        Assert.False(await verify.RevisionCertificationRequirements.AnyAsync(x => x.InspectionCriteriaRevisionId == revisionId));
        Assert.True(await verify.Parts.AnyAsync(x => x.Id == partId));
    }

    [Fact]
    public async Task UnusedPublishedRevisionCanBeDeleted()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var revisionId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        await service.AddCriterionAsync(partId, revisionId, new InspectionCriterionEditModel
        {
            InspectionNumber = 1,
            Name = "Length",
            GageTypeId = gageTypeId
        });
        var draft = await service.GetRevisionAsync(partId, revisionId);
        await service.PublishRevisionAsync(partId, revisionId, draft!.Version);
        var published = await service.GetRevisionAsync(partId, revisionId);

        var result = await service.DeleteRevisionAsync(partId, revisionId, published!.Version);

        Assert.Equal(CriteriaOperationStatus.Succeeded, result.Status);
        Assert.Null(await service.GetRevisionAsync(partId, revisionId));
    }

    [Fact]
    public async Task RevisionUsedByInspectionCannotBeDeleted()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var revisionId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        await service.AddCriterionAsync(partId, revisionId, new InspectionCriterionEditModel
        {
            InspectionNumber = 1,
            Name = "Length",
            GageTypeId = gageTypeId
        });
        var draft = await service.GetRevisionAsync(partId, revisionId);
        await service.PublishRevisionAsync(partId, revisionId, draft!.Version);
        await CreateInspectionAsync(partId);
        var protectedRevision = await service.GetRevisionAsync(partId, revisionId);

        var result = await service.DeleteRevisionAsync(partId, revisionId, protectedRevision!.Version);

        Assert.Equal(CriteriaOperationStatus.RevisionInUse, result.Status);
        Assert.NotNull(await service.GetRevisionAsync(partId, revisionId));
    }

    [Fact]
    public async Task TolerancesAcceptArbitraryText()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var revisionId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;

        var result = await service.AddCriterionAsync(
            partId,
            revisionId,
            new InspectionCriterionEditModel
            {
                InspectionNumber = 1,
                Name = "Threads",
                GageTypeId = gageTypeId,
                MaximumOrTolerance = "  M4 - 0.7 6H  ",
                Minimum = "GO / NO-GO"
            });

        Assert.Equal(CriteriaOperationStatus.Succeeded, result.Status);

        var criterion = Assert.Single((await service.GetRevisionAsync(partId, revisionId))!.Criteria);
        Assert.Equal("M4 - 0.7 6H", criterion.MaximumOrTolerance);
        Assert.Equal("GO / NO-GO", criterion.Minimum);
    }

    [Fact]
    public async Task UnitChoicesIncludeDefaultsAndSavedCustomUnits()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var revisionId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        await service.AddCriterionAsync(
            partId,
            revisionId,
            new InspectionCriterionEditModel
            {
                InspectionNumber = 1,
                Name = "Weight",
                GageTypeId = gageTypeId,
                Unit = "kg"
            });

        var choices = await service.GetUnitChoicesAsync();

        Assert.Contains("MM", choices);
        Assert.Contains("µM", choices);
        Assert.Contains("HV", choices);
        Assert.Contains("kg", choices);
    }

    [Fact]
    public async Task SecondaryProcessTypesAreSeededAndRevisionMayHaveNone()
    {
        var choices = await service.GetSecondaryProcessTypeChoicesAsync();
        var partId = await CreatePartAsync();
        var revisionId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;

        Assert.Equal(
            ["Heat Treat", "Clean", "Patch", "Plate", "Sort"],
            choices.Select(x => x.Name));
        Assert.Empty((await service.GetRevisionAsync(partId, revisionId))!.SecondaryProcessRequirements);
    }

    [Fact]
    public async Task RevisionDetailsAreInitializedCopiedAndHistoricallyStable()
    {
        var partId = await CreatePartAsync("Part description from master", "PRINT-A");
        var gageTypeId = await CreateGageTypeAsync();
        var firstId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        var firstDraft = await service.GetRevisionAsync(partId, firstId);

        Assert.Equal("PRINT-A", firstDraft!.PrintRevisionNumber);
        Assert.Equal("Part description from master", firstDraft.PartDescription);
        Assert.Null(firstDraft.SpecificationUsed);
        Assert.Null(firstDraft.Notes);

        var saveFirst = await service.SaveRevisionHeaderAsync(
            partId,
            firstId,
            new InspectionCriteriaRevisionHeaderEditModel
            {
                PrintRevisionNumber = "  PRINT-B  ",
                PartDescription = "  Historical description  ",
                SpecificationUsed = "  SPEC-100  ",
                Notes = "  Initial revision notes  ",
                Version = firstDraft.Version
            });
        Assert.Equal(CriteriaOperationStatus.Succeeded, saveFirst.Status);

        await service.AddCriterionAsync(partId, firstId, new InspectionCriterionEditModel
        {
            InspectionNumber = 1,
            Name = "Length",
            GageTypeId = gageTypeId
        });
        firstDraft = await service.GetRevisionAsync(partId, firstId);
        await service.PublishRevisionAsync(partId, firstId, firstDraft!.Version);
        await CreateInspectionAsync(partId);

        await using (var db = database.CreateDbContext())
        {
            var part = await db.Parts.SingleAsync(x => x.Id == partId);
            part.Revision = "PRINT-MASTER-CHANGED";
            part.Description = "Master description changed";
            await db.SaveChangesAsync();
        }

        var secondId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        var secondDraft = await service.GetRevisionAsync(partId, secondId);
        Assert.Equal("PRINT-B", secondDraft!.PrintRevisionNumber);
        Assert.Equal("Historical description", secondDraft.PartDescription);
        Assert.Equal("SPEC-100", secondDraft.SpecificationUsed);
        Assert.Equal("Initial revision notes", secondDraft.Notes);

        var saveSecond = await service.SaveRevisionHeaderAsync(
            partId,
            secondId,
            new InspectionCriteriaRevisionHeaderEditModel
            {
                PrintRevisionNumber = "PRINT-C",
                PartDescription = "New description",
                SpecificationUsed = "SPEC-200",
                Notes = "New revision notes",
                Version = secondDraft.Version
            });
        Assert.Equal(CriteriaOperationStatus.Succeeded, saveSecond.Status);

        var firstPublished = await service.GetRevisionAsync(partId, firstId);
        secondDraft = await service.GetRevisionAsync(partId, secondId);
        Assert.Equal("PRINT-B", firstPublished!.PrintRevisionNumber);
        Assert.Equal("Historical description", firstPublished.PartDescription);
        Assert.Equal("SPEC-100", firstPublished.SpecificationUsed);
        Assert.Equal("Initial revision notes", firstPublished.Notes);
        Assert.Equal("PRINT-C", secondDraft!.PrintRevisionNumber);
        Assert.Equal("New description", secondDraft.PartDescription);
        Assert.Equal("SPEC-200", secondDraft.SpecificationUsed);
        Assert.Equal("New revision notes", secondDraft.Notes);

        var attemptToEditHistory = await service.SaveRevisionHeaderAsync(
            partId,
            firstId,
            new InspectionCriteriaRevisionHeaderEditModel
            {
                PrintRevisionNumber = "Should not save",
                Version = firstPublished.Version
            });
        Assert.Equal(CriteriaOperationStatus.RevisionInUse, attemptToEditHistory.Status);

        await using var bypassContext = database.CreateDbContext();
        var publishedEntity = await bypassContext.InspectionCriteriaRevisions
            .SingleAsync(x => x.Id == firstId);
        publishedEntity.Notes = "Bypass attempt";
        var exception = await Record.ExceptionAsync(() => bypassContext.SaveChangesAsync());
        var postgresException = Assert.IsType<Npgsql.PostgresException>(exception!.GetBaseException());
        Assert.Equal("55000", postgresException.SqlState);
    }

    [Fact]
    public async Task MasterPrintIsValidatedCopiedAndHistoricallyStable()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var firstId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        var firstDraft = await service.GetRevisionAsync(partId, firstId);
        var firstPdf = "%PDF-1.7\nmaster print revision one\n%%EOF"u8.ToArray();

        var wrongExtension = await service.UploadMasterPrintAsync(
            partId,
            firstId,
            "master-print.txt",
            firstPdf,
            firstDraft!.Version);
        Assert.Equal(CriteriaOperationStatus.ValidationFailed, wrongExtension.Status);

        var invalidContent = await service.UploadMasterPrintAsync(
            partId,
            firstId,
            "master-print.pdf",
            "not actually a PDF"u8.ToArray(),
            firstDraft.Version);
        Assert.Equal(CriteriaOperationStatus.ValidationFailed, invalidContent.Status);

        var upload = await service.UploadMasterPrintAsync(
            partId,
            firstId,
            "master-print.pdf",
            firstPdf,
            firstDraft.Version);
        Assert.Equal(CriteriaOperationStatus.Succeeded, upload.Status);

        firstDraft = await service.GetRevisionAsync(partId, firstId);
        Assert.True(firstDraft!.HasMasterPrint);
        Assert.Equal("master-print.pdf", firstDraft.MasterPrintFileName);
        Assert.NotNull(firstDraft.MasterPrintUploadedAtUtc);
        Assert.Equal(firstPdf, (await service.GetMasterPrintAsync(partId, firstId))!.Content);

        await service.AddCriterionAsync(partId, firstId, new InspectionCriterionEditModel
        {
            InspectionNumber = 1,
            Name = "Length",
            GageTypeId = gageTypeId
        });
        firstDraft = await service.GetRevisionAsync(partId, firstId);
        Assert.Equal(
            CriteriaOperationStatus.Succeeded,
            (await service.PublishRevisionAsync(partId, firstId, firstDraft!.Version)).Status);
        await CreateInspectionAsync(partId);

        var firstPublished = await service.GetRevisionAsync(partId, firstId);
        Assert.Equal(
            CriteriaOperationStatus.RevisionInUse,
            (await service.UploadMasterPrintAsync(
                partId,
                firstId,
                "replacement.pdf",
                firstPdf,
                firstPublished!.Version)).Status);
        Assert.Equal(
            CriteriaOperationStatus.RevisionInUse,
            (await service.DeleteMasterPrintAsync(partId, firstId, firstPublished.Version)).Status);

        var secondId = (await service.CreateDraftRevisionAsync(partId, "Updated print")).RevisionId!.Value;
        var secondDraft = await service.GetRevisionAsync(partId, secondId);
        Assert.True(secondDraft!.HasMasterPrint);
        Assert.Equal(firstPdf, (await service.GetMasterPrintAsync(partId, secondId))!.Content);

        var secondPdf = "%PDF-1.7\nmaster print revision two\n%%EOF"u8.ToArray();
        Assert.Equal(
            CriteriaOperationStatus.Succeeded,
            (await service.UploadMasterPrintAsync(
                partId,
                secondId,
                "replacement.pdf",
                secondPdf,
                secondDraft.Version)).Status);
        Assert.Equal(firstPdf, (await service.GetMasterPrintAsync(partId, firstId))!.Content);
        Assert.Equal(secondPdf, (await service.GetMasterPrintAsync(partId, secondId))!.Content);

        secondDraft = await service.GetRevisionAsync(partId, secondId);
        Assert.Equal(
            CriteriaOperationStatus.Succeeded,
            (await service.DeleteMasterPrintAsync(partId, secondId, secondDraft!.Version)).Status);
        Assert.Null(await service.GetMasterPrintAsync(partId, secondId));

        await using var bypassContext = database.CreateDbContext();
        var publishedEntity = await bypassContext.InspectionCriteriaRevisions
            .SingleAsync(x => x.Id == firstId);
        publishedEntity.MasterPrintContent = secondPdf;
        var exception = await Record.ExceptionAsync(() => bypassContext.SaveChangesAsync());
        var postgresException = Assert.IsType<Npgsql.PostgresException>(exception!.GetBaseException());
        Assert.Equal("55000", postgresException.SqlState);
    }

    [Fact]
    public async Task SecondaryProcessRequirementsSaveOptionalValuesAndAllowDuplicateTypes()
    {
        var partId = await CreatePartAsync();
        var revisionId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        var heatTreatId = (await service.GetSecondaryProcessTypeChoicesAsync())
            .Single(x => x.Name == "Heat Treat")
            .Id;

        var first = await service.AddSecondaryProcessRequirementAsync(
            partId,
            revisionId,
            new SecondaryProcessRequirementEditModel
            {
                SecondaryProcessTypeId = heatTreatId,
                Specification = "  SAE J429  "
            });
        var second = await service.AddSecondaryProcessRequirementAsync(
            partId,
            revisionId,
            new SecondaryProcessRequirementEditModel
            {
                SecondaryProcessTypeId = heatTreatId,
                Specification = "  "
            });

        Assert.Equal(CriteriaOperationStatus.Succeeded, first.Status);
        Assert.Equal(CriteriaOperationStatus.Succeeded, second.Status);

        var requirements = (await service.GetRevisionAsync(partId, revisionId))!
            .SecondaryProcessRequirements;
        Assert.Equal(2, requirements.Count);
        Assert.All(requirements, x => Assert.Equal("Heat Treat", x.ProcessName));
        Assert.Equal("SAE J429", requirements[0].Specification);
        Assert.Null(requirements[1].Specification);

        var delete = await service.DeleteSecondaryProcessRequirementAsync(
            partId,
            revisionId,
            requirements[1].Id,
            requirements[1].Version);
        Assert.Equal(CriteriaOperationStatus.Succeeded, delete.Status);
        Assert.Single((await service.GetRevisionAsync(partId, revisionId))!.SecondaryProcessRequirements);
    }

    [Fact]
    public async Task SecondaryProcessTypeNamesMustBeNonBlankAndUnique()
    {
        await using (var blankContext = database.CreateDbContext())
        {
            blankContext.SecondaryProcessTypes.Add(new SecondaryProcessType { Id = 1001, Name = "   " });
            var exception = await Record.ExceptionAsync(() => blankContext.SaveChangesAsync());
            var postgresException = Assert.IsType<Npgsql.PostgresException>(exception!.GetBaseException());
            Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        }

        await using (var duplicateContext = database.CreateDbContext())
        {
            duplicateContext.SecondaryProcessTypes.Add(new SecondaryProcessType { Id = 1002, Name = "Plate" });
            var exception = await Record.ExceptionAsync(() => duplicateContext.SaveChangesAsync());
            var postgresException = Assert.IsType<Npgsql.PostgresException>(exception!.GetBaseException());
            Assert.Equal(Npgsql.PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        }
    }

    [Fact]
    public async Task NewRevisionCopiesSecondaryProcessesWithoutChangingHistory()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var processTypes = await service.GetSecondaryProcessTypeChoicesAsync();
        var heatTreatId = processTypes.Single(x => x.Name == "Heat Treat").Id;
        var plateId = processTypes.Single(x => x.Name == "Plate").Id;
        var sortId = processTypes.Single(x => x.Name == "Sort").Id;
        var firstId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        await service.AddCriterionAsync(partId, firstId, new InspectionCriterionEditModel
        {
            InspectionNumber = 1,
            Name = "Length",
            GageTypeId = gageTypeId
        });
        await service.AddSecondaryProcessRequirementAsync(
            partId,
            firstId,
            new SecondaryProcessRequirementEditModel
            {
                SecondaryProcessTypeId = heatTreatId,
                Specification = "SAE J429"
            });
        await service.AddSecondaryProcessRequirementAsync(
            partId,
            firstId,
            new SecondaryProcessRequirementEditModel
            {
                SecondaryProcessTypeId = plateId,
                Specification = "PS-11036"
            });
        var firstDraft = await service.GetRevisionAsync(partId, firstId);
        await service.PublishRevisionAsync(partId, firstId, firstDraft!.Version);
        await CreateInspectionAsync(partId);

        var secondId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        var secondDraft = await service.GetRevisionAsync(partId, secondId);
        var copiedPlate = secondDraft!.SecondaryProcessRequirements.Single(x => x.ProcessName == "Plate");
        var save = await service.SaveSecondaryProcessRequirementAsync(
            partId,
            secondId,
            new SecondaryProcessRequirementEditModel
            {
                Id = copiedPlate.Id,
                RevisionId = secondId,
                SecondaryProcessTypeId = sortId,
                Version = copiedPlate.Version
            });

        Assert.Equal(CriteriaOperationStatus.Succeeded, save.Status);
        var firstPublished = await service.GetRevisionAsync(partId, firstId);
        secondDraft = await service.GetRevisionAsync(partId, secondId);
        Assert.Equal(["Heat Treat", "Plate"], firstPublished!.SecondaryProcessRequirements.Select(x => x.ProcessName));
        Assert.Equal(["Heat Treat", "Sort"], secondDraft!.SecondaryProcessRequirements.Select(x => x.ProcessName));
        Assert.Equal("PS-11036", firstPublished.SecondaryProcessRequirements[1].Specification);

        var attemptToEditHistory = await service.SaveSecondaryProcessRequirementAsync(
            partId,
            firstId,
            new SecondaryProcessRequirementEditModel
            {
                Id = firstPublished.SecondaryProcessRequirements[0].Id,
                RevisionId = firstId,
                SecondaryProcessTypeId = sortId,
                Version = firstPublished.SecondaryProcessRequirements[0].Version
            });
        Assert.Equal(CriteriaOperationStatus.RevisionInUse, attemptToEditHistory.Status);
    }

    [Fact]
    public async Task DatabaseRejectsDirectChangesToPublishedSecondaryProcesses()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var heatTreatId = (await service.GetSecondaryProcessTypeChoicesAsync())
            .Single(x => x.Name == "Heat Treat")
            .Id;
        var revisionId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        await service.AddCriterionAsync(partId, revisionId, new InspectionCriterionEditModel
        {
            InspectionNumber = 1,
            Name = "Length",
            GageTypeId = gageTypeId
        });
        await service.AddSecondaryProcessRequirementAsync(
            partId,
            revisionId,
            new SecondaryProcessRequirementEditModel
            {
                SecondaryProcessTypeId = heatTreatId,
                Specification = "SAE J429"
            });
        var draft = await service.GetRevisionAsync(partId, revisionId);
        await service.PublishRevisionAsync(partId, revisionId, draft!.Version);
        await CreateInspectionAsync(partId);

        await using var db = database.CreateDbContext();
        var requirement = await db.SecondaryProcessRequirements.SingleAsync();
        requirement.Specification = "Bypass attempt";

        var exception = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        var postgresException = Assert.IsType<Npgsql.PostgresException>(exception!.GetBaseException());
        Assert.Equal("55000", postgresException.SqlState);
    }

    [Fact]
    public async Task InspectionNumbersCanSkipButMustBeUniqueAndAreCopiedToNewRevisions()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var revisionId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;

        var first = await service.AddCriterionAsync(partId, revisionId, new InspectionCriterionEditModel
        {
            InspectionNumber = 1,
            Name = "First dimension",
            GageTypeId = gageTypeId
        });
        var fourteenth = await service.AddCriterionAsync(partId, revisionId, new InspectionCriterionEditModel
        {
            InspectionNumber = 14,
            Name = "Skipped dimensions",
            GageTypeId = gageTypeId
        });
        var duplicate = await service.AddCriterionAsync(partId, revisionId, new InspectionCriterionEditModel
        {
            InspectionNumber = 14,
            Name = "Duplicate number",
            GageTypeId = gageTypeId
        });

        Assert.Equal(CriteriaOperationStatus.Succeeded, first.Status);
        Assert.Equal(CriteriaOperationStatus.Succeeded, fourteenth.Status);
        Assert.Equal(CriteriaOperationStatus.ValidationFailed, duplicate.Status);

        var draft = await service.GetRevisionAsync(partId, revisionId);
        Assert.Equal([1, 14], draft!.Criteria.Select(x => x.InspectionNumber));
        await service.PublishRevisionAsync(partId, revisionId, draft.Version);

        var copiedRevisionId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        var copied = await service.GetRevisionAsync(partId, copiedRevisionId);
        Assert.Equal([1, 14], copied!.Criteria.Select(x => x.InspectionNumber));
    }

    [Fact]
    public async Task NewRevisionCopiesCurrentWithoutChangingHistory()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync("Comparator");
        var firstId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        await service.AddCriterionAsync(
            partId,
            firstId,
            new InspectionCriterionEditModel
            {
                InspectionNumber = 1,
                Name = "Thread pitch",
                GageTypeId = gageTypeId
            });
        var firstDraft = await service.GetRevisionAsync(partId, firstId);
        await service.PublishRevisionAsync(partId, firstId, firstDraft!.Version);
        await CreateInspectionAsync(partId);

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
                InspectionNumber = copied.InspectionNumber,
                Name = "Thread pitch — revised",
                GageTypeId = copied.GageTypeId,
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
                InspectionNumber = firstPublished.Criteria.Single().InspectionNumber,
                Name = "Should not save",
                GageTypeId = firstPublished.Criteria.Single().GageTypeId,
                Version = firstPublished.Criteria.Single().Version
            });
        Assert.Equal(CriteriaOperationStatus.RevisionInUse, attemptToEditHistory.Status);
    }

    [Fact]
    public async Task DatabaseRejectsDirectChangesToPublishedCriteria()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var revisionId = (await service.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        await service.AddCriterionAsync(
            partId,
            revisionId,
            new InspectionCriterionEditModel
            {
                InspectionNumber = 1,
                Name = "Length",
                GageTypeId = gageTypeId
            });
        var draft = await service.GetRevisionAsync(partId, revisionId);
        await service.PublishRevisionAsync(partId, revisionId, draft!.Version);
        await CreateInspectionAsync(partId);

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

    private async Task<long> CreatePartAsync(string? description = null, string? revision = null)
    {
        await using var db = database.CreateDbContext();
        var customer = new Customer { Name = "Test Customer" };
        var part = new Part
        {
            Customer = customer,
            PartNumber = "TEST-100",
            Description = description,
            Revision = revision
        };
        db.Parts.Add(part);
        await db.SaveChangesAsync();
        return part.Id;
    }

    private async Task<long> CreateGageTypeAsync(string name = "Micrometer")
    {
        await using var db = database.CreateDbContext();
        var gageType = new GageType { Name = name };
        db.GageTypes.Add(gageType);
        await db.SaveChangesAsync();
        return gageType.Id;
    }

    private async Task<long> CreateInspectionAsync(long partId)
    {
        var result = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 28)
        });
        Assert.Equal(InspectionOperationStatus.Succeeded, result.Status);
        return result.InspectionId!.Value;
    }
}
