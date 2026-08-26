using Confast.Web.Features.Customers;
using Confast.Web.Features.Gages;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.Parts;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class InspectionServiceTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly InspectionCriteriaService criteriaService = new(database);
    private readonly InspectionService inspectionService = new(database);

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task InspectionsRetainTheRevisionThatWasCurrentWhenCreated()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var masterPrint = "%PDF-1.7\ninspection master print\n%%EOF"u8.ToArray();
        var firstRevisionId = await CreateAndPublishRevisionAsync(
            partId,
            gageTypeId,
            "20",
            "21",
            "Original part description",
            "SPEC-100",
            "PRINT-A",
            "Use the approved finishing source.",
            masterPrintContent: masterPrint);

        var createFirst = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            LotNumber = "LOT-A",
            ConformancePoNumber = "  CONF-PO-100  ",
            ManufacturerLotNumber = "  MFG-LOT-1  ",
            DateReceived = new DateOnly(2026, 8, 24),
            QuantityReceived = 100,
            QuantityInspected = 25,
            Inspector = "  Alice Inspector  ",
            InspectionDate = new DateOnly(2026, 8, 25)
        });
        Assert.Equal(InspectionOperationStatus.Succeeded, createFirst.Status);

        var firstInspection = await inspectionService.GetInspectionAsync(createFirst.InspectionId!.Value);
        var firstResult = Assert.Single(firstInspection!.Results);
        firstResult.ActualMin = "19.9";
        firstResult.ActualMax = "20.8";
        firstInspection.InspectorNotes = "  Visible surface acceptable.  ";
        firstInspection.InHouseNotes = "  Hold sample for 30 days.  ";
        Assert.Equal(
            InspectionOperationStatus.Succeeded,
            (await inspectionService.SaveInspectionAsync(firstInspection)).Status);

        var secondDraftResult = await criteriaService.CreateDraftRevisionAsync(
            partId,
            "Updated tolerance");
        var secondRevisionId = secondDraftResult.RevisionId!.Value;
        var secondDraft = await criteriaService.GetRevisionAsync(partId, secondRevisionId);
        var copiedCriterion = Assert.Single(secondDraft!.Criteria);
        var saveCriterion = await criteriaService.SaveCriterionAsync(
            partId,
            secondRevisionId,
            new InspectionCriterionEditModel
            {
                Id = copiedCriterion.Id,
                RevisionId = secondRevisionId,
                InspectionNumber = copiedCriterion.InspectionNumber,
                Name = copiedCriterion.Name,
                GageTypeId = copiedCriterion.GageTypeId,
                Minimum = "21",
                MaximumOrTolerance = "22",
                Unit = copiedCriterion.Unit,
                Version = copiedCriterion.Version
            });
        Assert.Equal(CriteriaOperationStatus.Succeeded, saveCriterion.Status);

        secondDraft = await criteriaService.GetRevisionAsync(partId, secondRevisionId);
        var saveHeader = await criteriaService.SaveRevisionHeaderAsync(
            partId,
            secondRevisionId,
            new InspectionCriteriaRevisionHeaderEditModel
            {
                PartDescription = "Updated part description",
                SpecificationUsed = "SPEC-200",
                PrintRevisionNumber = "PRINT-B",
                Version = secondDraft!.Version
            });
        Assert.Equal(CriteriaOperationStatus.Succeeded, saveHeader.Status);

        secondDraft = await criteriaService.GetRevisionAsync(partId, secondRevisionId);
        Assert.Equal(
            CriteriaOperationStatus.Succeeded,
            (await criteriaService.PublishRevisionAsync(
                partId,
                secondRevisionId,
                secondDraft!.Version)).Status);

        var createSecond = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            LotNumber = "LOT-B",
            InspectionDate = new DateOnly(2026, 8, 26)
        });
        Assert.Equal(InspectionOperationStatus.Succeeded, createSecond.Status);

        firstInspection = await inspectionService.GetInspectionAsync(createFirst.InspectionId.Value);
        var secondInspection = await inspectionService.GetInspectionAsync(createSecond.InspectionId!.Value);
        firstResult = Assert.Single(firstInspection!.Results);
        var secondResult = Assert.Single(secondInspection!.Results);

        Assert.Equal(firstRevisionId, firstInspection.InspectionCriteriaRevisionId);
        Assert.Equal("Original part description", firstInspection.PartDescription);
        Assert.Equal("SPEC-100", firstInspection.SpecificationUsed);
        Assert.Equal("PRINT-A", firstInspection.PrintRevisionNumber);
        Assert.Equal("Use the approved finishing source.", firstInspection.CriteriaNotes);
        Assert.True(firstInspection.HasMasterPrint);
        Assert.Equal("master-print.pdf", firstInspection.MasterPrintFileName);
        Assert.NotNull(firstInspection.MasterPrintUploadedAtUtc);
        Assert.Equal("CONF-PO-100", firstInspection.ConformancePoNumber);
        Assert.Equal("MFG-LOT-1", firstInspection.ManufacturerLotNumber);
        Assert.Equal(new DateOnly(2026, 8, 24), firstInspection.DateReceived);
        Assert.Equal(100, firstInspection.QuantityReceived);
        Assert.Equal(11, firstInspection.QuantityInspected);
        Assert.Equal("Alice Inspector", firstInspection.Inspector);
        Assert.Equal("Visible surface acceptable.", firstInspection.InspectorNotes);
        Assert.Equal("Hold sample for 30 days.", firstInspection.InHouseNotes);
        Assert.Equal("20", firstResult.SpecifiedMinimum);
        Assert.Equal("21", firstResult.SpecifiedMaximum);
        Assert.Equal(InspectionResultEvaluation.Fail, firstResult.Evaluation);

        Assert.Equal(secondRevisionId, secondInspection.InspectionCriteriaRevisionId);
        Assert.Equal("Updated part description", secondInspection.PartDescription);
        Assert.Equal("SPEC-200", secondInspection.SpecificationUsed);
        Assert.Equal("PRINT-B", secondInspection.PrintRevisionNumber);
        Assert.Equal("21", secondResult.SpecifiedMinimum);
        Assert.Equal("22", secondResult.SpecifiedMaximum);
        Assert.Equal(InspectionResultEvaluation.Incomplete, secondResult.Evaluation);
    }

    [Fact]
    public async Task ResultCannotReferenceCriterionFromAnotherRevision()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var firstRevisionId = await CreateAndPublishRevisionAsync(
            partId,
            gageTypeId,
            "20",
            "21");
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 26)
        });

        var secondRevisionId = (await criteriaService.CreateDraftRevisionAsync(partId, null))
            .RevisionId!.Value;
        var secondCriterionId = Assert.Single(
            (await criteriaService.GetRevisionAsync(partId, secondRevisionId))!.Criteria).Id;

        await using var db = database.CreateDbContext();
        var result = await db.InspectionResults.SingleAsync(x => x.InspectionId == create.InspectionId);
        result.InspectionCriterionId = secondCriterionId;
        var exception = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        var postgresException = Assert.IsType<Npgsql.PostgresException>(exception!.GetBaseException());

        Assert.Equal(Npgsql.PostgresErrorCodes.ForeignKeyViolation, postgresException.SqlState);
        Assert.Equal(firstRevisionId, result.InspectionCriteriaRevisionId);
    }

    [Fact]
    public async Task ActualMinimumCannotExceedActualMaximum()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        await CreateAndPublishRevisionAsync(partId, gageTypeId, "20", "21");
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 26)
        });
        var inspection = await inspectionService.GetInspectionAsync(create.InspectionId!.Value);
        var result = Assert.Single(inspection!.Results);
        result.ActualMin = "20.8";
        result.ActualMax = "20.2";

        var save = await inspectionService.SaveInspectionAsync(inspection);

        Assert.Equal(InspectionOperationStatus.ValidationFailed, save.Status);
    }

    [Fact]
    public async Task PassingTextCanBeSavedInEitherActualField()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        await CreateAndPublishRevisionAsync(partId, gageTypeId, "20", "21");
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 26)
        });
        var inspection = await inspectionService.GetInspectionAsync(create.InspectionId!.Value);
        var result = Assert.Single(inspection!.Results);
        result.ActualMin = "legacy text";
        result.ActualMax = "  oK  ";

        var save = await inspectionService.SaveInspectionAsync(inspection);
        var reopened = await inspectionService.GetInspectionAsync(create.InspectionId.Value);
        var reopenedResult = Assert.Single(reopened!.Results);

        Assert.Equal(InspectionOperationStatus.Succeeded, save.Status);
        Assert.Equal("OK", reopenedResult.ActualMin);
        Assert.Equal("OK", reopenedResult.ActualMax);
        Assert.Equal(InspectionResultEvaluation.Pass, reopenedResult.Evaluation);
    }

    [Fact]
    public async Task GageChoicesMatchTheCriterionTypeAndPreserveTheSelectedNumber()
    {
        var partId = await CreatePartAsync();
        var micrometerTypeId = await CreateGageTypeAsync("Inspection Test Micrometer");
        var caliperTypeId = await CreateGageTypeAsync("Inspection Test Caliper");
        var selectedGageId = await CreateGageAsync(micrometerTypeId, "MIC-001");
        var otherMicrometerId = await CreateGageAsync(micrometerTypeId, "MIC-002");
        var caliperId = await CreateGageAsync(caliperTypeId, "CAL-001");
        await CreateAndPublishRevisionAsync(partId, micrometerTypeId, "20", "21");
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 26)
        });

        var inspection = await inspectionService.GetInspectionAsync(create.InspectionId!.Value);
        var result = Assert.Single(inspection!.Results);
        Assert.Equal([selectedGageId, otherMicrometerId], result.GageChoices.Select(x => x.Id).ToArray());
        Assert.DoesNotContain(result.GageChoices, x => x.Id == caliperId);

        result.GageId = selectedGageId;
        Assert.Equal(
            InspectionOperationStatus.Succeeded,
            (await inspectionService.SaveInspectionAsync(inspection)).Status);

        await using (var db = database.CreateDbContext())
        {
            var gage = await db.Gages.SingleAsync(x => x.Id == selectedGageId);
            gage.GageNumber = "MIC-RENAMED";
            gage.IsActive = false;
            await db.SaveChangesAsync();
        }

        var reopened = await inspectionService.GetInspectionAsync(create.InspectionId.Value);
        var reopenedResult = Assert.Single(reopened!.Results);
        Assert.Equal(selectedGageId, reopenedResult.GageId);
        Assert.Equal("MIC-001", reopenedResult.GageNumber);
        Assert.Contains(
            reopenedResult.GageChoices,
            x => x.Id == selectedGageId && x.GageNumber == "MIC-001");
        Assert.Contains(reopenedResult.GageChoices, x => x.Id == otherMicrometerId);
        Assert.DoesNotContain(reopenedResult.GageChoices, x => x.Id == caliperId);
    }

    [Fact]
    public async Task SelectingAGageAppliesItToEveryResultWithTheSameMethod()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var gageId = await CreateGageAsync(gageTypeId, "MIC-001");
        await CreateGageAsync(gageTypeId, "MIC-002");
        await CreateAndPublishRevisionAsync(
            partId,
            gageTypeId,
            "20",
            "21",
            criterionCount: 2);
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 26)
        });
        var inspection = await inspectionService.GetInspectionAsync(create.InspectionId!.Value);
        var source = inspection!.Results[0];

        source.GageId = gageId;
        inspection.ApplyGageSelection(source);

        Assert.All(inspection.Results, result => Assert.Equal(gageId, result.GageId));
        Assert.Equal(
            InspectionOperationStatus.Succeeded,
            (await inspectionService.SaveInspectionAsync(inspection)).Status);
        var reopened = await inspectionService.GetInspectionAsync(create.InspectionId.Value);
        Assert.All(reopened!.Results, result =>
        {
            Assert.Equal(gageId, result.GageId);
            Assert.Equal("MIC-001", result.GageNumber);
        });

        reopened.Results[0].GageId = null;
        var inconsistentSave = await inspectionService.SaveInspectionAsync(reopened);
        Assert.Equal(InspectionOperationStatus.ValidationFailed, inconsistentSave.Status);
        Assert.Equal(
            "Criteria with the same inspection method must use the same gage number.",
            inconsistentSave.Message);
    }

    [Fact]
    public async Task CreationSelectsTheSoleActiveGageForEachMatchingResult()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var soleActiveGageId = await CreateGageAsync(gageTypeId, "MIC-001");
        await CreateGageAsync(gageTypeId, "MIC-INACTIVE", isActive: false);
        await CreateAndPublishRevisionAsync(
            partId,
            gageTypeId,
            "20",
            "21",
            criterionCount: 2);

        var firstCreate = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 26)
        });
        var firstInspection = await inspectionService.GetInspectionAsync(firstCreate.InspectionId!.Value);
        Assert.All(firstInspection!.Results, result =>
        {
            Assert.Equal(soleActiveGageId, result.GageId);
            Assert.Equal("MIC-001", result.GageNumber);
        });

        await CreateGageAsync(gageTypeId, "MIC-002");
        var secondCreate = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 26)
        });
        var secondInspection = await inspectionService.GetInspectionAsync(secondCreate.InspectionId!.Value);
        Assert.All(secondInspection!.Results, result =>
        {
            Assert.Null(result.GageId);
            Assert.Null(result.GageNumber);
        });
    }

    [Fact]
    public async Task GageSelectionMustMatchTheCriterionTypeInServiceAndDatabase()
    {
        var partId = await CreatePartAsync();
        var micrometerTypeId = await CreateGageTypeAsync("Inspection Test Micrometer");
        var caliperTypeId = await CreateGageTypeAsync("Inspection Test Caliper");
        var micrometerId = await CreateGageAsync(micrometerTypeId, "MIC-001");
        var caliperId = await CreateGageAsync(caliperTypeId, "CAL-001");
        await CreateAndPublishRevisionAsync(partId, micrometerTypeId, "20", "21");
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 26)
        });

        var inspection = await inspectionService.GetInspectionAsync(create.InspectionId!.Value);
        Assert.Single(inspection!.Results).GageId = caliperId;
        var invalidSave = await inspectionService.SaveInspectionAsync(inspection);
        Assert.Equal(InspectionOperationStatus.ValidationFailed, invalidSave.Status);
        Assert.Equal("The selected gage does not match the inspection method.", invalidSave.Message);

        await using (var db = database.CreateDbContext())
        {
            var result = await db.InspectionResults.SingleAsync(x => x.InspectionId == create.InspectionId);
            result.GageId = caliperId;
            var exception = await Record.ExceptionAsync(() => db.SaveChangesAsync());
            var postgresException = Assert.IsType<Npgsql.PostgresException>(exception!.GetBaseException());
            Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        }

        inspection = await inspectionService.GetInspectionAsync(create.InspectionId.Value);
        Assert.Single(inspection!.Results).GageId = micrometerId;
        Assert.Equal(
            InspectionOperationStatus.Succeeded,
            (await inspectionService.SaveInspectionAsync(inspection)).Status);

        await using (var db = database.CreateDbContext())
        {
            var usedGage = await db.Gages.SingleAsync(x => x.Id == micrometerId);
            usedGage.GageTypeId = caliperTypeId;
            var exception = await Record.ExceptionAsync(() => db.SaveChangesAsync());
            var postgresException = Assert.IsType<Npgsql.PostgresException>(exception!.GetBaseException());
            Assert.Equal(Npgsql.PostgresErrorCodes.RestrictViolation, postgresException.SqlState);
        }
    }

    [Fact]
    public async Task QuantitiesMustBePositiveInServiceAndDatabase()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        await CreateAndPublishRevisionAsync(partId, gageTypeId, "20", "21");
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 26)
        });
        var inspection = await inspectionService.GetInspectionAsync(create.InspectionId!.Value);
        Assert.Equal("PO-", inspection!.ConformancePoNumber);

        inspection.QuantityReceived = 0;
        var receivedSave = await inspectionService.SaveInspectionAsync(inspection);
        Assert.Equal(InspectionOperationStatus.ValidationFailed, receivedSave.Status);

        inspection.QuantityReceived = null;
        inspection!.QuantityInspected = 0;

        var save = await inspectionService.SaveInspectionAsync(inspection);
        Assert.Equal(InspectionOperationStatus.ValidationFailed, save.Status);

        await using (var receivedDb = database.CreateDbContext())
        {
            var entity = await receivedDb.Inspections.SingleAsync(x => x.Id == create.InspectionId);
            entity.QuantityReceived = 0;
            var exception = await Record.ExceptionAsync(() => receivedDb.SaveChangesAsync());
            var postgresException = Assert.IsType<Npgsql.PostgresException>(exception!.GetBaseException());
            Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        }

        await using (var inspectedDb = database.CreateDbContext())
        {
            var entity = await inspectedDb.Inspections.SingleAsync(x => x.Id == create.InspectionId);
            entity.QuantityInspected = 0;
            var exception = await Record.ExceptionAsync(() => inspectedDb.SaveChangesAsync());
            var postgresException = Assert.IsType<Npgsql.PostgresException>(exception!.GetBaseException());
            Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, postgresException.SqlState);
        }
    }

    [Fact]
    public async Task SecondaryProcessesAreCreatedFromThePinnedRevisionAndCanBeCompleted()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var processTypes = await criteriaService.GetSecondaryProcessTypeChoicesAsync();
        var heatTreatId = processTypes.Single(x => x.Name == "Heat Treat").Id;
        var plateId = processTypes.Single(x => x.Name == "Plate").Id;
        await CreateAndPublishRevisionAsync(
            partId,
            gageTypeId,
            "20",
            "21",
            secondaryProcesses:
            [
                (heatTreatId, "HT-100"),
                (plateId, null)
            ]);
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 26)
        });

        // Inspection-row identity values are not the criteria ordering key. Deliberately
        // reverse them to catch accidental ordering by the inspection row itself.
        await using (var db = database.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"""
                UPDATE inspection_secondary_processes
                SET id = 1000000 - secondary_process_requirement_id
                WHERE inspection_id = {create.InspectionId!.Value}
                """);
        }

        var inspection = await inspectionService.GetInspectionAsync(create.InspectionId!.Value);
        Assert.Collection(
            inspection!.SecondaryProcesses,
            process =>
            {
                Assert.Equal("Heat Treat", process.ProcessName);
                Assert.Equal("HT-100", process.Specification);
                Assert.Null(process.PurchaseOrderNumber);
                Assert.False(process.IsComplete);
            },
            process =>
            {
                Assert.Equal("Plate", process.ProcessName);
                Assert.Null(process.Specification);
                Assert.Null(process.PurchaseOrderNumber);
                Assert.False(process.IsComplete);
            });

        inspection.SecondaryProcesses[0].PurchaseOrderNumber = "  PO-HT-200  ";
        inspection.SecondaryProcesses[0].IsComplete = true;
        inspection.SecondaryProcesses[1].PurchaseOrderNumber = "   ";
        Assert.Equal(
            InspectionOperationStatus.Succeeded,
            (await inspectionService.SaveInspectionAsync(inspection)).Status);

        var reopened = await inspectionService.GetInspectionAsync(create.InspectionId.Value);
        Assert.Equal("PO-HT-200", reopened!.SecondaryProcesses[0].PurchaseOrderNumber);
        Assert.True(reopened.SecondaryProcesses[0].IsComplete);
        Assert.Null(reopened.SecondaryProcesses[1].PurchaseOrderNumber);
        Assert.False(reopened.SecondaryProcesses[1].IsComplete);
    }

    [Fact]
    public async Task SecondaryProcessCannotReferenceARequirementFromAnotherRevision()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var heatTreatId = (await criteriaService.GetSecondaryProcessTypeChoicesAsync())
            .Single(x => x.Name == "Heat Treat").Id;
        await CreateAndPublishRevisionAsync(
            partId,
            gageTypeId,
            "20",
            "21",
            secondaryProcesses: [(heatTreatId, "HT-100")]);
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 26)
        });
        var secondRevisionId = (await criteriaService.CreateDraftRevisionAsync(partId, null))
            .RevisionId!.Value;
        var secondRequirementId = Assert.Single(
            (await criteriaService.GetRevisionAsync(partId, secondRevisionId))!
                .SecondaryProcessRequirements).Id;

        await using var db = database.CreateDbContext();
        var process = await db.InspectionSecondaryProcesses.SingleAsync(
            x => x.InspectionId == create.InspectionId);
        process.SecondaryProcessRequirementId = secondRequirementId;
        var exception = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        var postgresException = Assert.IsType<Npgsql.PostgresException>(exception!.GetBaseException());
        Assert.Equal(Npgsql.PostgresErrorCodes.ForeignKeyViolation, postgresException.SqlState);
    }

    private async Task<long> CreateAndPublishRevisionAsync(
        long partId,
        long gageTypeId,
        string minimum,
        string maximum,
        string? partDescription = null,
        string? specificationUsed = null,
        string? printRevisionNumber = null,
        string? criteriaNotes = null,
        int criterionCount = 1,
        IReadOnlyList<(long TypeId, string? Specification)>? secondaryProcesses = null,
        byte[]? masterPrintContent = null)
    {
        var revisionId = (await criteriaService.CreateDraftRevisionAsync(partId, null))
            .RevisionId!.Value;
        var revision = await criteriaService.GetRevisionAsync(partId, revisionId);
        if (partDescription is not null
            || specificationUsed is not null
            || printRevisionNumber is not null
            || criteriaNotes is not null)
        {
            var saveHeader = await criteriaService.SaveRevisionHeaderAsync(
                partId,
                revisionId,
                new InspectionCriteriaRevisionHeaderEditModel
                {
                    PartDescription = partDescription,
                    SpecificationUsed = specificationUsed,
                    PrintRevisionNumber = printRevisionNumber,
                    Notes = criteriaNotes,
                    Version = revision!.Version
                });
            Assert.Equal(CriteriaOperationStatus.Succeeded, saveHeader.Status);
        }

        for (var index = 1; index <= criterionCount; index++)
        {
            var add = await criteriaService.AddCriterionAsync(
                partId,
                revisionId,
                new InspectionCriterionEditModel
                {
                    InspectionNumber = index,
                    Name = $"Overall Length {index}",
                    GageTypeId = gageTypeId,
                    Minimum = minimum,
                    MaximumOrTolerance = maximum,
                    Unit = "mm"
                });
            Assert.Equal(CriteriaOperationStatus.Succeeded, add.Status);
        }

        foreach (var process in secondaryProcesses ?? [])
        {
            var add = await criteriaService.AddSecondaryProcessRequirementAsync(
                partId,
                revisionId,
                new SecondaryProcessRequirementEditModel
                {
                    SecondaryProcessTypeId = process.TypeId,
                    Specification = process.Specification
                });
            Assert.Equal(CriteriaOperationStatus.Succeeded, add.Status);
        }

        revision = await criteriaService.GetRevisionAsync(partId, revisionId);
        if (masterPrintContent is not null)
        {
            Assert.Equal(
                CriteriaOperationStatus.Succeeded,
                (await criteriaService.UploadMasterPrintAsync(
                    partId,
                    revisionId,
                    "master-print.pdf",
                    masterPrintContent,
                    revision!.Version)).Status);
            revision = await criteriaService.GetRevisionAsync(partId, revisionId);
        }

        Assert.Equal(
            CriteriaOperationStatus.Succeeded,
            (await criteriaService.PublishRevisionAsync(partId, revisionId, revision!.Version)).Status);
        return revisionId;
    }

    private async Task<long> CreatePartAsync()
    {
        await using var db = database.CreateDbContext();
        var customer = new Customer { Name = "Inspection Test Customer" };
        var part = new Part
        {
            Customer = customer,
            PartNumber = "INSPECT-100"
        };
        db.Parts.Add(part);
        await db.SaveChangesAsync();
        return part.Id;
    }

    private async Task<long> CreateGageTypeAsync(
        string name = "Inspection Test Micrometer")
    {
        await using var db = database.CreateDbContext();
        var gageType = new GageType { Name = name };
        db.GageTypes.Add(gageType);
        await db.SaveChangesAsync();
        return gageType.Id;
    }

    private async Task<long> CreateGageAsync(
        long gageTypeId,
        string gageNumber,
        bool isActive = true)
    {
        await using var db = database.CreateDbContext();
        var gage = new Gage
        {
            GageTypeId = gageTypeId,
            GageNumber = gageNumber,
            IsActive = isActive
        };
        db.Gages.Add(gage);
        await db.SaveChangesAsync();
        return gage.Id;
    }
}
