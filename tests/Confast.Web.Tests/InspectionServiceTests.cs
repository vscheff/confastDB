using Confast.Web.Features.Customers;
using Confast.Web.Features.Gages;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Parts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PdfSharp.Pdf;

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
        var partId = await CreatePartAsync("SPEC-100");
        var gageTypeId = await CreateGageTypeAsync();
        var masterPrint = "%PDF-1.7\ninspection master print\n%%EOF"u8.ToArray();
        var firstRevisionId = await CreateAndPublishRevisionAsync(
            partId,
            gageTypeId,
            "20",
            "21",
            "Original part description",
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

        await using (var db = database.CreateDbContext())
        {
            var part = await db.Parts.SingleAsync(x => x.Id == partId);
            part.SpecificationUsed = "SPEC-200";
            await db.SaveChangesAsync();
        }

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
        Assert.Equal("SPEC-200", firstInspection.SpecificationUsed);
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
    public async Task LotNumbersAreGloballyUniqueAcrossCreateEditAndDirectDatabaseWrites()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var revisionId = await CreateAndPublishRevisionAsync(partId, gageTypeId, "20", "21");

        var first = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            LotNumber = "  LOT-UNIQUE  ",
            InspectionDate = new DateOnly(2026, 8, 25)
        });
        Assert.Equal(InspectionOperationStatus.Succeeded, first.Status);

        var duplicateCreate = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            LotNumber = "LOT-UNIQUE",
            InspectionDate = new DateOnly(2026, 8, 25)
        });
        Assert.Equal(InspectionOperationStatus.ValidationFailed, duplicateCreate.Status);
        Assert.Equal(
            "Lot number must be unique. An inspection already uses that lot number.",
            duplicateCreate.Message);
        Assert.Equal(first.InspectionId, duplicateCreate.RelatedInspectionId);

        var second = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            LotNumber = "LOT-OTHER",
            InspectionDate = new DateOnly(2026, 8, 25)
        });
        var secondInspection = await inspectionService.GetInspectionAsync(second.InspectionId!.Value);
        secondInspection!.LotNumber = "LOT-UNIQUE";

        var duplicateEdit = await inspectionService.SaveInspectionAsync(secondInspection);
        Assert.Equal(InspectionOperationStatus.ValidationFailed, duplicateEdit.Status);
        Assert.Equal(
            "Lot number must be unique. An inspection already uses that lot number.",
            duplicateEdit.Message);
        Assert.Equal(first.InspectionId, duplicateEdit.RelatedInspectionId);

        await using var db = database.CreateDbContext();
        db.Inspections.Add(new Inspection
        {
            PartId = partId,
            InspectionCriteriaRevisionId = revisionId,
            LotNumber = "LOT-UNIQUE",
            InspectionDate = new DateOnly(2026, 8, 25)
        });

        var exception = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        var postgresException = Assert.IsType<PostgresException>(exception!.GetBaseException());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
        Assert.Equal("UX_inspections_lot_number", postgresException.ConstraintName);
    }

    [Fact]
    public async Task CertificationPackagePlantsAreLimitedToTheActiveInspectionsPart()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        await CreateAndPublishRevisionAsync(partId, gageTypeId, "20", "21");
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 31)
        });

        long customerId;
        long selectedPlantId;
        long unselectedPlantId;
        await using (var db = database.CreateDbContext())
        {
            customerId = await db.Parts
                .Where(x => x.Id == partId)
                .Select(x => x.CustomerId)
                .SingleAsync();
            var selectedPlant = new Plant { CustomerId = customerId, Name = "Selected Plant" };
            var unselectedPlant = new Plant { CustomerId = customerId, Name = "Unselected Plant" };
            db.Plants.AddRange(selectedPlant, unselectedPlant);
            db.PartPlants.Add(new PartPlant { PartId = partId, Plant = selectedPlant });
            await db.SaveChangesAsync();
            selectedPlantId = selectedPlant.Id;
            unselectedPlantId = unselectedPlant.Id;
        }

        var options = await inspectionService.GetCertificationPackagePlantOptionsAsync(
            create.InspectionId!.Value);

        var option = Assert.Single(options);
        Assert.Equal(selectedPlantId, option.Id);
        Assert.Equal("Selected Plant", option.Name);
        Assert.DoesNotContain(options, x => x.Id == unselectedPlantId);

        var packageService = new CertificationPackageService(
            database,
            inspectionService,
            null!,
            null!,
            new CertificationPackageFilenameFormatter(),
            new InspectionPrintRenderTokenService(new EphemeralDataProtectionProvider()));
        var exception = await Assert.ThrowsAsync<CertificationPackageException>(() =>
            packageService.BuildAsync(
                new CertificationPackageRequest(
                    create.InspectionId.Value,
                    customerId,
                    [create.InspectionId.Value],
                    new DateOnly(2026, 9, 1),
                    unselectedPlantId),
                "https://localhost/"));
        Assert.Equal(
            "Select a destination plant assigned to this inspection's part.",
            exception.Message);
    }

    [Fact]
    public async Task CertificationPackageLotsAreLimitedToSharedPlantsAndThenTheDestinationPlant()
    {
        var activePartId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        await CreateAndPublishRevisionAsync(activePartId, gageTypeId, "20", "21");
        var activeInspection = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = activePartId,
            InspectionDate = new DateOnly(2026, 8, 31)
        });
        var activeInspectionId = activeInspection.InspectionId!.Value;

        long sharedPlantId;
        long customerId;
        long activeOnlyPartId;
        long sharedPartId;
        long unrelatedPartId;
        long sharedInspectionId;
        long activeOnlyInspectionId;
        long unrelatedInspectionId;
        await using (var db = database.CreateDbContext())
        {
            customerId = await db.Parts.Where(x => x.Id == activePartId).Select(x => x.CustomerId).SingleAsync();
            var sharedPlant = new Plant { CustomerId = customerId, Name = "Shared Plant" };
            var activeOnlyPlant = new Plant { CustomerId = customerId, Name = "Active Only Plant" };
            var activeOnlyPart = new Part { CustomerId = customerId, PartNumber = "ACTIVE-ONLY" };
            var sharedPart = new Part { CustomerId = customerId, PartNumber = "SHARED" };
            var unrelatedPart = new Part { CustomerId = customerId, PartNumber = "UNRELATED" };
            db.AddRange(sharedPlant, activeOnlyPlant, activeOnlyPart, sharedPart, unrelatedPart);
            db.PartPlants.AddRange(
                new PartPlant { PartId = activePartId, Plant = sharedPlant },
                new PartPlant { PartId = activePartId, Plant = activeOnlyPlant },
                new PartPlant { Part = sharedPart, Plant = sharedPlant },
                new PartPlant { Part = activeOnlyPart, Plant = activeOnlyPlant });
            await db.SaveChangesAsync();
            sharedPlantId = sharedPlant.Id;
            activeOnlyPartId = activeOnlyPart.Id;
            sharedPartId = sharedPart.Id;
            unrelatedPartId = unrelatedPart.Id;

        }

        await CreateAndPublishRevisionAsync(activeOnlyPartId, gageTypeId, "20", "21");
        await CreateAndPublishRevisionAsync(sharedPartId, gageTypeId, "20", "21");
        await CreateAndPublishRevisionAsync(unrelatedPartId, gageTypeId, "20", "21");

        activeOnlyInspectionId = (await inspectionService.CreateInspectionAsync(new CreateInspectionModel { PartId = activeOnlyPartId, InspectionDate = new DateOnly(2026, 8, 30) })).InspectionId!.Value;
        sharedInspectionId = (await inspectionService.CreateInspectionAsync(new CreateInspectionModel { PartId = sharedPartId, InspectionDate = new DateOnly(2026, 8, 29) })).InspectionId!.Value;
        unrelatedInspectionId = (await inspectionService.CreateInspectionAsync(new CreateInspectionModel { PartId = unrelatedPartId, InspectionDate = new DateOnly(2026, 8, 28) })).InspectionId!.Value;
        var packageGageId = await CreateGageAsync(gageTypeId, "PKG-001");
        await MarkInspectionAcceptedAsync(activeInspectionId, packageGageId);
        await MarkInspectionAcceptedAsync(sharedInspectionId, packageGageId);
        await MarkInspectionAcceptedAsync(unrelatedInspectionId, packageGageId);

        var sharedPlantLots = await inspectionService.GetCertificationPackageLotOptionsAsync(activeInspectionId);
        Assert.Contains(sharedPlantLots, x => x.InspectionId == activeInspectionId);
        Assert.DoesNotContain(sharedPlantLots, x => x.InspectionId == activeOnlyInspectionId);
        Assert.Contains(sharedPlantLots, x => x.InspectionId == sharedInspectionId);
        Assert.DoesNotContain(sharedPlantLots, x => x.InspectionId == unrelatedInspectionId);

        var destinationLots = await inspectionService.GetCertificationPackageLotOptionsAsync(activeInspectionId, sharedPlantId);
        Assert.Contains(destinationLots, x => x.InspectionId == activeInspectionId);
        Assert.Contains(destinationLots, x => x.InspectionId == sharedInspectionId);
        Assert.DoesNotContain(destinationLots, x => x.InspectionId == activeOnlyInspectionId);
        Assert.DoesNotContain(destinationLots, x => x.InspectionId == unrelatedInspectionId);

        var packageService = new CertificationPackageService(
            database,
            inspectionService,
            null!,
            null!,
            new CertificationPackageFilenameFormatter(),
            new InspectionPrintRenderTokenService(new EphemeralDataProtectionProvider()));
        var exception = await Assert.ThrowsAsync<CertificationPackageException>(() =>
            packageService.BuildAsync(
                new CertificationPackageRequest(
                    activeInspectionId,
                    customerId,
                    [activeInspectionId, activeOnlyInspectionId],
                    new DateOnly(2026, 9, 1),
                    sharedPlantId),
                "https://localhost/"));
        Assert.Equal("All selected lots must ship to the selected destination plant.", exception.Message);
    }

    [Fact]
    public async Task CertificationPackageRequiresOnlyCustomerCertificationsRequiredByTheLotsPart()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        await CreateAndPublishRevisionAsync(partId, gageTypeId, "20", "21");
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 31)
        });
        var inspectionId = create.InspectionId!.Value;
        var packageGageId = await CreateGageAsync(gageTypeId, "PKG-001");
        await MarkInspectionAcceptedAsync(inspectionId, packageGageId);

        long customerId;
        long plantId;
        await using (var db = database.CreateDbContext())
        {
            customerId = await db.Parts.Where(x => x.Id == partId).Select(x => x.CustomerId).SingleAsync();
            var material = await db.CertificationTypes.SingleAsync(x => x.Name == "Material");
            var plate = await db.CertificationTypes.SingleAsync(x => x.Name == "Plate");
            var plant = new Plant { CustomerId = customerId, Name = "Package Plant" };
            db.Plants.Add(plant);
            db.PartPlants.Add(new PartPlant { PartId = partId, Plant = plant });
            db.PlantCertificationRequirements.AddRange(
                new PlantCertificationRequirement { Plant = plant, CertificationTypeId = material.Id },
                new PlantCertificationRequirement { Plant = plant, CertificationTypeId = plate.Id });
            db.InspectionCertifications.Add(new InspectionCertification
            {
                InspectionId = inspectionId,
                CertificationTypeId = material.Id,
                CertificationTypeName = material.Name,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Documents =
                {
                    new CertificationDocument
                    {
                        OriginalFileName = "material.pdf",
                        ContentType = "application/pdf",
                        Content = CreatePdf(),
                        UploadedAtUtc = DateTimeOffset.UtcNow
                    }
                }
            });
            await db.SaveChangesAsync();
            plantId = plant.Id;
        }

        var packageService = new CertificationPackageService(
            database,
            inspectionService,
            null!,
            new PdfDocumentMerger(),
            new CertificationPackageFilenameFormatter(),
            new InspectionPrintRenderTokenService(new EphemeralDataProtectionProvider()));
        var package = await packageService.BuildAsync(
            new CertificationPackageRequest(
                inspectionId,
                customerId,
                [inspectionId],
                new DateOnly(2026, 9, 1),
                plantId),
            "https://localhost/");

        var lot = Assert.Single(package.Lots);
        Assert.Equal(["Material"], lot.RequiredCertificationNames);
        Assert.Empty(lot.MissingCertificationNames);

        await using (var db = database.CreateDbContext())
        {
            await db.CertificationDocuments
                .Where(document => document.InspectionCertification.InspectionId == inspectionId)
                .ExecuteDeleteAsync();
        }

        var exception = await Assert.ThrowsAsync<CertificationPackageException>(() =>
            packageService.BuildAsync(
                new CertificationPackageRequest(
                    inspectionId,
                    customerId,
                    [inspectionId],
                    new DateOnly(2026, 9, 1),
                    plantId),
                "https://localhost/"));
        Assert.Equal($"Lot {inspectionId} is missing: Material.", exception.Message);
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
    public async Task ApprovedDeviationPersistsAndAcceptsAnOutOfToleranceResult()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        await CreateGageAsync(gageTypeId, "MIC-001");
        await CreateAndPublishRevisionAsync(partId, gageTypeId, "20", "21");
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 26)
        });

        var inspection = await inspectionService.GetInspectionAsync(create.InspectionId!.Value);
        var result = Assert.Single(inspection!.Results);
        result.ActualMin = "19.9";
        result.ActualMax = "21.1";
        result.DeviationApproved = true;

        Assert.Equal(
            InspectionOperationStatus.Succeeded,
            (await inspectionService.SaveInspectionAsync(inspection)).Status);

        var reopened = await inspectionService.GetInspectionAsync(create.InspectionId.Value);
        var reopenedResult = Assert.Single(reopened!.Results);
        Assert.True(reopenedResult.DeviationApproved);
        Assert.Equal(InspectionResultEvaluation.Pass, reopenedResult.Evaluation);
        Assert.True(Assert.Single(await inspectionService.GetInspectionsAsync()).Accepted);
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
    public async Task NewInspectionUsesTheInspectorsDigitalCaliper()
    {
        var partId = await CreatePartAsync("CALIPER-DEFAULT");
        var digitalCaliperTypeId = await CreateGageTypeAsync("Digital Calipers");
        await CreateAndPublishRevisionAsync(partId, digitalCaliperTypeId, "20", "21");
        var firstCaliperId = await CreateGageAsync(digitalCaliperTypeId, "CAL-001");
        var selectedCaliperId = await CreateGageAsync(digitalCaliperTypeId, "CAL-002");
        var userId = Guid.NewGuid().ToString();

        await using (var db = database.CreateDbContext())
        {
            db.Users.Add(new ApplicationUser
            {
                Id = userId,
                UserName = "inspection.user",
                NormalizedUserName = "INSPECTION.USER",
                Email = "inspection.user@example.com",
                NormalizedEmail = "INSPECTION.USER@EXAMPLE.COM",
                DisplayName = "Inspection User",
                SecurityStamp = Guid.NewGuid().ToString(),
                ConcurrencyStamp = Guid.NewGuid().ToString(),
                CaliperId = selectedCaliperId
            });
            await db.SaveChangesAsync();
        }

        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            Inspector = "Inspection User",
            InspectionDate = new DateOnly(2026, 9, 3)
        });

        var inspection = await inspectionService.GetInspectionAsync(create.InspectionId!.Value);
        Assert.Equal(selectedCaliperId, Assert.Single(inspection!.Results).GageId);
        Assert.NotEqual(firstCaliperId, inspection.Results[0].GageId);
    }

    [Fact]
    public async Task NewInspectionLeavesDigitalCaliperUnselectedWhenUserHasNoCaliper()
    {
        var partId = await CreatePartAsync("CALIPER-NONE");
        var digitalCaliperTypeId = await CreateGageTypeAsync("Digital Caliper");
        await CreateAndPublishRevisionAsync(partId, digitalCaliperTypeId, "20", "21");
        await CreateGageAsync(digitalCaliperTypeId, "CAL-ONLY");

        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 9, 3)
        });

        var inspection = await inspectionService.GetInspectionAsync(create.InspectionId!.Value);
        Assert.Null(Assert.Single(inspection!.Results).GageId);
    }

    [Fact]
    public async Task SelectingAGageFillsOnlyUnselectedResultsWithTheSameMethod()
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
            criterionCount: 3);
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 26)
        });
        var inspection = await inspectionService.GetInspectionAsync(create.InspectionId!.Value);
        var source = inspection!.Results[0];
        var alreadySelected = inspection.Results[1];
        var unselected = inspection.Results[2];

        source.GageId = gageId;
        alreadySelected.GageId = (await CreateGageAsync(gageTypeId, "MIC-003"));
        inspection.ApplyGageSelection(source);

        Assert.Equal(gageId, source.GageId);
        Assert.NotEqual(gageId, alreadySelected.GageId);
        Assert.Equal(gageId, unselected.GageId);
        Assert.Equal(
            InspectionOperationStatus.Succeeded,
            (await inspectionService.SaveInspectionAsync(inspection)).Status);
        var reopened = await inspectionService.GetInspectionAsync(create.InspectionId.Value);
        Assert.Equal(gageId, reopened!.Results[0].GageId);
        Assert.Equal("MIC-001", reopened.Results[0].GageNumber);
        Assert.Equal(alreadySelected.GageId, reopened.Results[1].GageId);
        Assert.Equal("MIC-003", reopened.Results[1].GageNumber);
        Assert.Equal(gageId, reopened.Results[2].GageId);
        Assert.Equal("MIC-001", reopened.Results[2].GageNumber);
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

        firstInspection.Results[1].GageId = null;
        firstInspection.ApplyGageSelection(firstInspection.Results[0]);
        Assert.All(firstInspection.Results, result =>
            Assert.Equal(soleActiveGageId, result.GageId));

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
    public async Task CreateRejectsFutureAndOutOfOrderDates()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        await CreateAndPublishRevisionAsync(partId, gageTypeId, "20", "21");
        var today = DateOnly.FromDateTime(DateTime.Today);

        var futureReceived = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            DateReceived = today.AddDays(1),
            InspectionDate = today
        });
        Assert.Equal(InspectionOperationStatus.ValidationFailed, futureReceived.Status);
        Assert.Equal("Date received cannot be in the future.", futureReceived.Message);

        var futureInspected = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            DateReceived = today,
            InspectionDate = today.AddDays(1)
        });
        Assert.Equal(InspectionOperationStatus.ValidationFailed, futureInspected.Status);
        Assert.Equal("Date inspected cannot be in the future.", futureInspected.Message);

        var outOfOrder = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            DateReceived = today,
            InspectionDate = today.AddDays(-1)
        });
        Assert.Equal(InspectionOperationStatus.ValidationFailed, outOfOrder.Status);
        Assert.Equal("Date inspected cannot be before Date Received.", outOfOrder.Message);
    }

    [Fact]
    public async Task SaveRejectsFutureAndOutOfOrderDates()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        await CreateAndPublishRevisionAsync(partId, gageTypeId, "20", "21");
        var today = DateOnly.FromDateTime(DateTime.Today);
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            DateReceived = today,
            InspectionDate = today
        });
        var inspection = await inspectionService.GetInspectionAsync(create.InspectionId!.Value);

        inspection!.DateReceived = today.AddDays(1);
        var futureReceived = await inspectionService.SaveInspectionAsync(inspection);
        Assert.Equal(InspectionOperationStatus.ValidationFailed, futureReceived.Status);
        Assert.Equal("Date received cannot be in the future.", futureReceived.Message);

        inspection.DateReceived = today;
        inspection.InspectionDate = today.AddDays(1);
        var futureInspected = await inspectionService.SaveInspectionAsync(inspection);
        Assert.Equal(InspectionOperationStatus.ValidationFailed, futureInspected.Status);
        Assert.Equal("Date inspected cannot be in the future.", futureInspected.Message);

        inspection.InspectionDate = today.AddDays(-1);
        var outOfOrder = await inspectionService.SaveInspectionAsync(inspection);
        Assert.Equal(InspectionOperationStatus.ValidationFailed, outOfOrder.Status);
        Assert.Equal("Date inspected cannot be before Date Received.", outOfOrder.Message);
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
    public async Task DuplicatingAnInspectionMovesTheRequestedQuantityAndClearsOnlyTheLotNumber()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var gageId = await CreateGageAsync(gageTypeId, "MIC-001");
        var heatTreatId = (await criteriaService.GetSecondaryProcessTypeChoicesAsync())
            .Single(x => x.Name == "Heat Treat").Id;
        var revisionId = await CreateAndPublishRevisionAsync(
            partId,
            gageTypeId,
            "20",
            "21",
            secondaryProcesses: [(heatTreatId, "HT-100")]);
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            LotNumber = "SOURCE-LOT",
            ConformancePoNumber = "CONF-100",
            ManufacturerLotNumber = "MFG-100",
            DateReceived = new DateOnly(2026, 8, 20),
            QuantityReceived = 100,
            QuantityInspected = 11,
            Inspector = "Alice",
            InspectionDate = new DateOnly(2026, 8, 21)
        });
        var sourceId = create.InspectionId!.Value;
        var source = await inspectionService.GetInspectionAsync(sourceId);
        source!.InspectorNotes = "Inspector notes";
        source.InHouseNotes = "In-house notes";
        var sourceResult = Assert.Single(source.Results);
        sourceResult.GageId = gageId;
        sourceResult.ActualMin = "20.1";
        sourceResult.ActualMax = "20.2";
        sourceResult.DeviationApproved = true;
        var sourceProcess = Assert.Single(source.SecondaryProcesses);
        sourceProcess.PurchaseOrderNumber = "HT-PO-100";
        sourceProcess.IsComplete = true;
        Assert.Equal(
            InspectionOperationStatus.Succeeded,
            (await inspectionService.SaveInspectionAsync(source)).Status);

        await using (var db = database.CreateDbContext())
        {
            var material = await db.CertificationTypes.SingleAsync(x => x.Name == "Material");
            db.InspectionCertifications.Add(new InspectionCertification
            {
                InspectionId = sourceId,
                CertificationTypeId = material.Id,
                CertificationTypeName = material.Name,
                Description = "Material certification",
                Notes = "Use this lot's material cert.",
                Documents =
                {
                    new CertificationDocument
                    {
                        OriginalFileName = "material.pdf",
                        ContentType = "application/pdf",
                        Content = "%PDF-1.7\nmaterial\n%%EOF"u8.ToArray(),
                        PreviewContent = "preview"u8.ToArray()
                    }
                }
            });
            await db.SaveChangesAsync();
        }

        var duplicate = await inspectionService.DuplicateInspectionAsync(sourceId, 30, "DUPLICATE-LOT");
        Assert.Equal(InspectionOperationStatus.Succeeded, duplicate.Status);

        var copied = await inspectionService.GetInspectionAsync(duplicate.InspectionId!.Value);
        Assert.NotNull(copied);
        Assert.Equal("DUPLICATE-LOT", copied!.LotNumber);
        Assert.Equal(partId, copied.PartId);
        Assert.Equal(revisionId, copied.InspectionCriteriaRevisionId);
        Assert.Equal("CONF-100", copied.ConformancePoNumber);
        Assert.Equal("MFG-100", copied.ManufacturerLotNumber);
        Assert.Equal(new DateOnly(2026, 8, 20), copied.DateReceived);
        Assert.Equal(30, copied.QuantityReceived);
        Assert.Equal(11, copied.QuantityInspected);
        Assert.Equal("Alice", copied.Inspector);
        Assert.Equal("Inspector notes", copied.InspectorNotes);
        Assert.Equal("In-house notes", copied.InHouseNotes);
        Assert.Equal(new DateOnly(2026, 8, 21), copied.InspectionDate);
        var copiedResult = Assert.Single(copied.Results);
        Assert.Equal(gageId, copiedResult.GageId);
        Assert.Equal("20.1", copiedResult.ActualMin);
        Assert.Equal("20.2", copiedResult.ActualMax);
        Assert.True(copiedResult.DeviationApproved);
        var copiedProcess = Assert.Single(copied.SecondaryProcesses);
        Assert.Equal("HT-PO-100", copiedProcess.PurchaseOrderNumber);
        Assert.True(copiedProcess.IsComplete);
        var copiedCertification = Assert.Single(copied.Certifications, x => x.CertificationTypeName == "Material");
        Assert.Equal("Material certification", copiedCertification.Description);
        Assert.Equal("Use this lot's material cert.", copiedCertification.Notes);
        Assert.Equal("material.pdf", Assert.Single(copiedCertification.Documents).OriginalFileName);
        var copiedHistory = Assert.Single(copied.LineageHistory);
        Assert.Equal(InspectionLineageOperation.Duplicate, copiedHistory.Operation);
        Assert.Equal(sourceId, copiedHistory.SourceInspectionId);
        Assert.Equal("SOURCE-LOT", copiedHistory.SourceLotNumber);
        Assert.Equal(copied.Id, copiedHistory.DestinationInspectionId);
        Assert.Equal("DUPLICATE-LOT", copiedHistory.DestinationLotNumber);
        Assert.Equal(30, copiedHistory.QuantityMoved);

        await using var verification = database.CreateDbContext();
        var copiedDocument = await verification.CertificationDocuments.SingleAsync(
            x => x.InspectionCertification.InspectionId == duplicate.InspectionId);
        Assert.Equal("%PDF-1.7\nmaterial\n%%EOF"u8.ToArray(), copiedDocument.Content);
        Assert.Equal("preview"u8.ToArray(), copiedDocument.PreviewContent);

        var updatedSource = await inspectionService.GetInspectionAsync(sourceId);
        Assert.Equal(70, updatedSource!.QuantityReceived);
        Assert.Equal("In-house notes", updatedSource.InHouseNotes);
        Assert.Equal(copiedHistory, Assert.Single(updatedSource.LineageHistory));

        var confirmation = await inspectionService.UndoLineageOperationAsync(
            sourceId,
            copiedHistory.Operation,
            copiedHistory.Id,
            confirmDestinationDeletion: false);
        Assert.Equal(InspectionOperationStatus.ConfirmationRequired, confirmation.Status);

        var undone = await inspectionService.UndoLineageOperationAsync(
            sourceId,
            copiedHistory.Operation,
            copiedHistory.Id,
            confirmDestinationDeletion: true);
        Assert.Equal(InspectionOperationStatus.Succeeded, undone.Status);
        Assert.Equal(100, (await inspectionService.GetInspectionAsync(sourceId))!.QuantityReceived);
        Assert.Null(await inspectionService.GetInspectionAsync(copied.Id));
        Assert.Empty((await inspectionService.GetInspectionAsync(sourceId))!.LineageHistory);
    }

    [Fact]
    public async Task AdditionalQuantityMovesCreateSeparateTransferHistoryRowsInEitherDirection()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        await CreateAndPublishRevisionAsync(partId, gageTypeId, "20", "21");
        var sourceCreate = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            LotNumber = "TRANSFER-SOURCE",
            QuantityReceived = 100,
            InspectionDate = new DateOnly(2026, 9, 2)
        });
        var duplicate = await inspectionService.DuplicateInspectionAsync(sourceCreate.InspectionId!.Value, 30, "TRANSFER-DESTINATION");
        var sourceId = sourceCreate.InspectionId.Value;
        var destinationId = duplicate.InspectionId!.Value;

        var forward = await inspectionService.MoveAdditionalLineageQuantityAsync(sourceId, sourceId, destinationId, 20);
        Assert.Equal(InspectionOperationStatus.Succeeded, forward.Status);
        var reverse = await inspectionService.MoveAdditionalLineageQuantityAsync(sourceId, destinationId, sourceId, 10);
        Assert.Equal(InspectionOperationStatus.Succeeded, reverse.Status);

        var source = await inspectionService.GetInspectionAsync(sourceId);
        var destination = await inspectionService.GetInspectionAsync(destinationId);
        Assert.Equal(60, source!.QuantityReceived);
        Assert.Equal(40, destination!.QuantityReceived);
        Assert.Equal(
            [InspectionLineageOperation.Duplicate, InspectionLineageOperation.Transfer, InspectionLineageOperation.Transfer],
            source.LineageHistory.Select(x => x.Operation).OrderBy(x => x).ToArray());
        Assert.Contains(source.LineageHistory, x => x.Operation == InspectionLineageOperation.Transfer && x.SourceInspectionId == sourceId && x.DestinationInspectionId == destinationId && x.QuantityMoved == 20);
        Assert.Contains(source.LineageHistory, x => x.Operation == InspectionLineageOperation.Transfer && x.SourceInspectionId == destinationId && x.DestinationInspectionId == sourceId && x.QuantityMoved == 10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100)]
    [InlineData(101)]
    public async Task DuplicatingAnInspectionRejectsQuantitiesThatDoNotLeaveSomeOfTheLotOnTheOriginal(
        int quantityToMove)
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        await CreateAndPublishRevisionAsync(partId, gageTypeId, "20", "21");
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            QuantityReceived = 100,
            InspectionDate = new DateOnly(2026, 8, 21)
        });

        var result = await inspectionService.DuplicateInspectionAsync(
            create.InspectionId!.Value,
            quantityToMove,
            "DUPLICATE-LOT");

        Assert.Equal(InspectionOperationStatus.ValidationFailed, result.Status);
        var source = await inspectionService.GetInspectionAsync(create.InspectionId.Value);
        Assert.Equal(100, source!.QuantityReceived);
    }

    [Fact]
    public async Task DuplicatingAnInspectionRequiresANewLotNumber()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        await CreateAndPublishRevisionAsync(partId, gageTypeId, "20", "21");
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            QuantityReceived = 100,
            InspectionDate = new DateOnly(2026, 8, 21)
        });

        var result = await inspectionService.DuplicateInspectionAsync(
            create.InspectionId!.Value,
            50,
            "   ");

        Assert.Equal(InspectionOperationStatus.ValidationFailed, result.Status);
        Assert.Equal("Lot number is required for the duplicated inspection.", result.Message);
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

    [Fact]
    public async Task DeletingAnInspectionRemovesItsOwnedRecordsAndHonorsConcurrency()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        var heatTreatId = (await criteriaService.GetSecondaryProcessTypeChoicesAsync())
            .Single(x => x.Name == "Heat Treat").Id;
        var revisionId = await CreateAndPublishRevisionAsync(
            partId,
            gageTypeId,
            "20",
            "21",
            secondaryProcesses: [(heatTreatId, "HT-100")]);
        var create = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 28)
        });
        var inspectionId = create.InspectionId!.Value;

        await using (var db = database.CreateDbContext())
        {
            var materialType = await db.CertificationTypes.SingleAsync(x => x.Name == "Material");
            db.InspectionCertifications.Add(new InspectionCertification
            {
                InspectionId = inspectionId,
                CertificationTypeId = materialType.Id,
                CertificationTypeName = materialType.Name,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Documents =
                {
                    new CertificationDocument
                    {
                        OriginalFileName = "material.pdf",
                        ContentType = "application/pdf",
                        Content = "%PDF-1.7\nmaterial\n%%EOF"u8.ToArray(),
                        UploadedAtUtc = DateTimeOffset.UtcNow
                    }
                }
            });
            await db.SaveChangesAsync();
        }

        var deleteModel = await inspectionService.GetInspectionForDeleteAsync(inspectionId);
        Assert.NotNull(deleteModel);
        Assert.Equal("INSPECT-100", deleteModel.PartNumber);

        var conflict = await inspectionService.DeleteInspectionAsync(
            inspectionId,
            deleteModel.Version + 1);
        Assert.Equal(InspectionOperationStatus.Conflict, conflict.Status);
        Assert.NotNull(await inspectionService.GetInspectionAsync(inspectionId));

        var deleted = await inspectionService.DeleteInspectionAsync(
            inspectionId,
            deleteModel.Version);
        Assert.Equal(InspectionOperationStatus.Succeeded, deleted.Status);

        await using var verification = database.CreateDbContext();
        Assert.False(await verification.Inspections.AnyAsync(x => x.Id == inspectionId));
        Assert.False(await verification.InspectionResults.AnyAsync(x => x.InspectionId == inspectionId));
        Assert.False(await verification.InspectionSecondaryProcesses.AnyAsync(x => x.InspectionId == inspectionId));
        Assert.False(await verification.InspectionCertificationRequirements.AnyAsync(x => x.InspectionId == inspectionId));
        Assert.False(await verification.InspectionCertifications.AnyAsync(x => x.InspectionId == inspectionId));
        Assert.Empty(await verification.CertificationDocuments.ToListAsync());
        Assert.True(await verification.InspectionCriteriaRevisions.AnyAsync(x => x.Id == revisionId));
        Assert.True(await verification.CertificationTypes.AnyAsync(x => x.Name == "Material"));
    }

    [Fact]
    public async Task DeletingADuplicatedInspectionRemovesItsLineageRecord()
    {
        var partId = await CreatePartAsync();
        var gageTypeId = await CreateGageTypeAsync();
        await CreateAndPublishRevisionAsync(partId, gageTypeId, "20", "21");
        var source = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            LotNumber = "DUPLICATE-DELETE-SOURCE",
            QuantityReceived = 10,
            InspectionDate = new DateOnly(2026, 9, 2)
        });
        var duplicate = await inspectionService.DuplicateInspectionAsync(
            source.InspectionId!.Value,
            4,
            "DUPLICATE-DELETE-DESTINATION");
        var deleteModel = await inspectionService.GetInspectionForDeleteAsync(duplicate.InspectionId!.Value);

        var deleted = await inspectionService.DeleteInspectionAsync(
            duplicate.InspectionId!.Value,
            deleteModel!.Version);

        Assert.Equal(InspectionOperationStatus.Succeeded, deleted.Status);
        await using var verification = database.CreateDbContext();
        Assert.False(await verification.Inspections.AnyAsync(x => x.Id == duplicate.InspectionId));
        Assert.False(await verification.LotDuplications.AnyAsync(x =>
            x.SourceInspectionId == source.InspectionId || x.DestinationInspectionId == duplicate.InspectionId));
    }

    [Fact]
    public async Task DeletingAFlippedInspectionRemovesItsLineageRecord()
    {
        var sourcePartId = await CreatePartAsync(partNumber: "FLIP-DELETE-SOURCE");
        var targetPartId = await CreatePartAsync(partNumber: "FLIP-DELETE-TARGET");
        var gageTypeId = await CreateGageTypeAsync();
        await CreateAndPublishRevisionAsync(sourcePartId, gageTypeId, "20", "21");
        await CreateAndPublishRevisionAsync(targetPartId, gageTypeId, "20", "21");
        var definition = await new PartFlipService(database).SaveDefinitionAsync(sourcePartId, targetPartId);
        var source = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = sourcePartId,
            LotNumber = "FLIP-DELETE-SOURCE-LOT",
            QuantityReceived = 10,
            InspectionDate = new DateOnly(2026, 9, 2)
        });
        var flipped = await inspectionService.FlipInspectionAsync(
            source.InspectionId!.Value,
            definition.DefinitionId!.Value,
            4,
            "FLIP-DELETE-DESTINATION-LOT");
        var deleteModel = await inspectionService.GetInspectionForDeleteAsync(flipped.InspectionId!.Value);

        var deleted = await inspectionService.DeleteInspectionAsync(
            flipped.InspectionId!.Value,
            deleteModel!.Version);

        Assert.Equal(InspectionOperationStatus.Succeeded, deleted.Status);
        await using var verification = database.CreateDbContext();
        Assert.False(await verification.Inspections.AnyAsync(x => x.Id == flipped.InspectionId));
        Assert.False(await verification.LotFlips.AnyAsync(x =>
            x.SourceInspectionId == source.InspectionId || x.DestinationInspectionId == flipped.InspectionId));
    }

    private async Task<long> CreateAndPublishRevisionAsync(
        long partId,
        long gageTypeId,
        string minimum,
        string maximum,
        string? partDescription = null,
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
            || printRevisionNumber is not null
            || criteriaNotes is not null)
        {
            var saveHeader = await criteriaService.SaveRevisionHeaderAsync(
                partId,
                revisionId,
                new InspectionCriteriaRevisionHeaderEditModel
                {
                    PartDescription = partDescription,
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

    [Fact]
    public async Task FlipCreatesTargetInspectionTransfersObservationsAndPreservesLineage()
    {
        var sourcePartId = await CreatePartAsync(partNumber: "FLIP-SOURCE");
        var targetPartId = await CreatePartAsync(partNumber: "FLIP-TARGET");
        var gageTypeId = await CreateGageTypeAsync();
        var gageId = await CreateGageAsync(gageTypeId, "FLIP-MIC-001");
        var heatTreatId = (await criteriaService.GetSecondaryProcessTypeChoicesAsync())
            .Single(x => x.Name == "Heat Treat").Id;
        await CreateAndPublishRevisionAsync(sourcePartId, gageTypeId, "20", "21", secondaryProcesses: [(heatTreatId, "Source heat-treat specification")]);
        await CreateAndPublishRevisionAsync(targetPartId, gageTypeId, "20.2", "21", secondaryProcesses: [(heatTreatId, "Target heat-treat specification")]);
        var flipService = new PartFlipService(database);
        var definition = await flipService.SaveDefinitionAsync(sourcePartId, targetPartId);
        Assert.Equal(SavePartFlipStatus.Saved, definition.Status);
        var sourceCreate = await inspectionService.CreateInspectionAsync(new CreateInspectionModel { PartId = sourcePartId, LotNumber = "FLIP-SOURCE-LOT", QuantityReceived = 10, InspectionDate = new DateOnly(2026, 9, 1) });
        var source = await inspectionService.GetInspectionAsync(sourceCreate.InspectionId!.Value);
        source!.Results[0].ActualMin = "20.1";
        source.Results[0].ActualMax = "20.1";
        source.Results[0].GageId = gageId;
        source.Results[0].DeviationApproved = true;
        source.InHouseNotes = "Source internal note";
        source.SecondaryProcesses[0].PurchaseOrderNumber = "HT-PO-100";
        source.SecondaryProcesses[0].IsComplete = true;
        Assert.Equal(InspectionOperationStatus.Succeeded, (await inspectionService.SaveInspectionAsync(source)).Status);

        await using (var certificationDb = database.CreateDbContext())
        {
            var material = await certificationDb.CertificationTypes.SingleAsync(x => x.Name == "Material");
            certificationDb.InspectionCertifications.Add(new InspectionCertification
            {
                InspectionId = source.Id,
                CertificationTypeId = material.Id,
                CertificationTypeName = material.Name,
                Description = "Material certification",
                Notes = "Use this lot's material cert.",
                Documents =
                {
                    new CertificationDocument
                    {
                        OriginalFileName = "material.pdf",
                        ContentType = "application/pdf",
                        Content = "%PDF-1.7\nmaterial\n%%EOF"u8.ToArray(),
                        PreviewContent = "preview"u8.ToArray()
                    }
                }
            });
            await certificationDb.SaveChangesAsync();
        }

        var invalidQuantity = await inspectionService.FlipInspectionAsync(source.Id, definition.DefinitionId!.Value, 10, "INVALID-FLIP-LOT");
        Assert.Equal(InspectionOperationStatus.ValidationFailed, invalidQuantity.Status);

        var flipped = await inspectionService.FlipInspectionAsync(source.Id, definition.DefinitionId!.Value, 4, "FLIP-TARGET-LOT");

        Assert.Equal(InspectionOperationStatus.Succeeded, flipped.Status);
        var target = await inspectionService.GetInspectionAsync(flipped.InspectionId!.Value);
        Assert.Equal(targetPartId, target!.PartId);
        Assert.Equal(4, target.QuantityReceived);
        Assert.Equal("20.1", target.Results[0].ActualMin);
        Assert.Equal("20.1", target.Results[0].ActualMax);
        Assert.Equal(gageId, target.Results[0].GageId);
        Assert.Equal("FLIP-MIC-001", target.Results[0].GageNumber);
        Assert.True(target.Results[0].DeviationApproved);
        Assert.Equal("20.2", target.Results[0].SpecifiedMinimum); // target tolerance, not copied source specification
        Assert.Equal("Source internal note", target.InHouseNotes);
        var targetProcess = Assert.Single(target.SecondaryProcesses);
        Assert.Equal("Target heat-treat specification", targetProcess.Specification);
        Assert.Equal("HT-PO-100", targetProcess.PurchaseOrderNumber);
        Assert.True(targetProcess.IsComplete);
        var targetCertification = Assert.Single(target.Certifications, x => x.CertificationTypeName == "Material");
        Assert.Equal("Material certification", targetCertification.Description);
        Assert.Equal("Use this lot's material cert.", targetCertification.Notes);
        Assert.Equal("material.pdf", Assert.Single(targetCertification.Documents).OriginalFileName);
        var unchangedSource = await inspectionService.GetInspectionAsync(source.Id);
        Assert.Equal("FLIP-SOURCE-LOT", unchangedSource!.LotNumber);
        Assert.Equal(6, unchangedSource.QuantityReceived);
        Assert.Equal("20.1", unchangedSource.Results[0].ActualMin);
        Assert.Equal("Source internal note", unchangedSource.InHouseNotes);
        await using var db = database.CreateDbContext();
        var copiedDocument = await db.CertificationDocuments.SingleAsync(x => x.InspectionCertification.InspectionId == target.Id);
        Assert.Equal("%PDF-1.7\nmaterial\n%%EOF"u8.ToArray(), copiedDocument.Content);
        Assert.Equal("preview"u8.ToArray(), copiedDocument.PreviewContent);
        var lineage = await db.LotFlips.SingleAsync();
        Assert.Equal(source.Id, lineage.SourceInspectionId);
        Assert.Equal(target.Id, lineage.DestinationInspectionId);
        Assert.Equal(4, lineage.QuantityMoved);
        Assert.Collection(
            target.LineageHistory,
            entry =>
            {
                Assert.Equal(InspectionLineageOperation.Flip, entry.Operation);
                Assert.Equal(source.Id, entry.SourceInspectionId);
                Assert.Equal("FLIP-SOURCE-LOT", entry.SourceLotNumber);
                Assert.Equal(target.Id, entry.DestinationInspectionId);
                Assert.Equal("FLIP-TARGET-LOT", entry.DestinationLotNumber);
                Assert.Equal(4, entry.QuantityMoved);
            });

        var flipHistory = Assert.Single(target.LineageHistory);
        var undoConfirmation = await inspectionService.UndoLineageOperationAsync(
            source.Id,
            flipHistory.Operation,
            flipHistory.Id,
            confirmDestinationDeletion: false);
        Assert.Equal(InspectionOperationStatus.ConfirmationRequired, undoConfirmation.Status);

        var undo = await inspectionService.UndoLineageOperationAsync(
            source.Id,
            flipHistory.Operation,
            flipHistory.Id,
            confirmDestinationDeletion: true);
        Assert.Equal(InspectionOperationStatus.Succeeded, undo.Status);
        Assert.Equal(10, (await inspectionService.GetInspectionAsync(source.Id))!.QuantityReceived);
        Assert.Null(await inspectionService.GetInspectionAsync(target.Id));
    }

    [Fact]
    public async Task FlipDefinitionsRejectSelfAndDuplicateDestinations()
    {
        var sourcePartId = await CreatePartAsync(partNumber: "FLIP-A");
        var targetPartId = await CreatePartAsync(partNumber: "FLIP-B");
        var gageTypeId = await CreateGageTypeAsync();
        await CreateAndPublishRevisionAsync(sourcePartId, gageTypeId, "20", "21");
        await CreateAndPublishRevisionAsync(targetPartId, gageTypeId, "20", "21");
        var service = new PartFlipService(database);
        Assert.Equal(SavePartFlipStatus.Invalid, (await service.SaveDefinitionAsync(sourcePartId, sourcePartId)).Status);
        Assert.Equal(SavePartFlipStatus.Saved, (await service.SaveDefinitionAsync(sourcePartId, targetPartId)).Status);
        await using (var db = database.CreateDbContext())
        {
            var definitions = await db.PartFlipDefinitions
                .Include(x => x.CriterionMappings)
                .OrderBy(x => x.SourcePartId)
                .ToListAsync();
            Assert.Equal(2, definitions.Count);
            var reverse = Assert.Single(definitions, x => x.SourcePartId == targetPartId);
            Assert.Equal(sourcePartId, reverse.TargetPartId);
            var original = Assert.Single(definitions, x => x.SourcePartId == sourcePartId);
            Assert.Equal(original.CriterionMappings.Single().SourceCriterionId, reverse.CriterionMappings.Single().TargetCriterionId);
            Assert.Equal(original.CriterionMappings.Single().TargetCriterionId, reverse.CriterionMappings.Single().SourceCriterionId);
        }
        Assert.Equal(SavePartFlipStatus.Duplicate, (await service.SaveDefinitionAsync(sourcePartId, targetPartId)).Status);
    }

    private async Task<long> CreatePartAsync(string? specificationUsed = null, string partNumber = "INSPECT-100")
    {
        await using var db = database.CreateDbContext();
        var customer = new Customer { Name = "Inspection Test Customer" };
        var part = new Part
        {
            Customer = customer,
            PartNumber = partNumber,
            SpecificationUsed = specificationUsed
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

    private async Task MarkInspectionAcceptedAsync(long inspectionId, long gageId)
    {
        var inspection = await inspectionService.GetInspectionAsync(inspectionId);
        Assert.NotNull(inspection);
        foreach (var result in inspection.Results)
        {
            result.GageId = gageId;
            result.ActualMin = "20.5";
            result.ActualMax = "20.5";
        }

        Assert.Equal(
            InspectionOperationStatus.Succeeded,
            (await inspectionService.SaveInspectionAsync(inspection)).Status);
    }

    private static byte[] CreatePdf()
    {
        using var document = new PdfDocument();
        document.AddPage();
        using var output = new MemoryStream();
        document.Save(output, closeStream: false);
        return output.ToArray();
    }

}
