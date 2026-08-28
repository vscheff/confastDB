using Confast.Web.Features.Customers;
using Confast.Web.Features.Gages;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.Parts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class InspectionCertificationTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly InspectionCriteriaService criteriaService = new(database);
    private readonly InspectionService inspectionService = new(database);

    public Task InitializeAsync() => database.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CertificationTypesAreSeededInDisplayOrder()
    {
        var types = await criteriaService.GetCertificationTypeChoicesAsync();

        Assert.Equal(
            [
                "CBP", "Clean", "C of C", "Gall", "Hardness", "Heat", "Material",
                "Patch", "Plate", "Salt Spray", "SPC", "Supplier Inspection",
                "Tensile/Proof Load/Yield", "Torque", "Notes/Misc"
            ],
            types.Select(x => x.Name));
        Assert.Equal([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15], types.Select(x => x.DisplayOrder));

        await using var db = database.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var additionalType = new CertificationType
        {
            Name = "Future Certification",
            DisplayOrder = 17
        };
        db.CertificationTypes.Add(additionalType);
        await db.SaveChangesAsync();
        Assert.True(additionalType.Id > 15);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task InitialRevisionHasDefaultCertificationRequirements()
    {
        var partId = await CreatePartAsync();
        var revisionId = (await criteriaService.CreateDraftRevisionAsync(partId, null))
            .RevisionId!.Value;

        var revision = await criteriaService.GetRevisionAsync(partId, revisionId);

        Assert.Collection(
            revision!.CertificationRequirements,
            requirement =>
            {
                Assert.Equal("Material", requirement.CertificationTypeName);
                Assert.Equal(CertificationRequirementLevel.Required, requirement.RequirementLevel);
            },
            requirement =>
            {
                Assert.Equal("Supplier Inspection", requirement.CertificationTypeName);
                Assert.Equal(CertificationRequirementLevel.Required, requirement.RequirementLevel);
            },
            requirement =>
            {
                Assert.Equal("Notes/Misc", requirement.CertificationTypeName);
                Assert.Equal(CertificationRequirementLevel.Optional, requirement.RequirementLevel);
            });
    }

    [Fact]
    public async Task RevisionRequirementsAreStoredAndDuplicateTypesAreRejected()
    {
        var partId = await CreatePartAsync();
        var revisionId = await CreateDraftWithCriterionAsync(partId);
        await SaveRequirementsAsync(
            partId,
            revisionId,
            ("Material", CertificationRequirementLevel.Required, "Mill certificate"),
            ("Heat", CertificationRequirementLevel.Optional, null));

        var revision = await criteriaService.GetRevisionAsync(partId, revisionId);
        Assert.Collection(
            revision!.CertificationRequirements,
            requirement =>
            {
                Assert.Equal("Heat", requirement.CertificationTypeName);
                Assert.Equal(CertificationRequirementLevel.Optional, requirement.RequirementLevel);
            },
            requirement =>
            {
                Assert.Equal("Material", requirement.CertificationTypeName);
                Assert.Equal(CertificationRequirementLevel.Required, requirement.RequirementLevel);
                Assert.Equal("Mill certificate", requirement.Notes);
            });

        await using var db = database.CreateDbContext();
        var material = revision.CertificationRequirements.Single(x => x.CertificationTypeName == "Material");
        db.RevisionCertificationRequirements.Add(new RevisionCertificationRequirement
        {
            InspectionCriteriaRevisionId = revisionId,
            CertificationTypeId = material.CertificationTypeId,
            CertificationTypeName = material.CertificationTypeName,
            RequirementLevel = CertificationRequirementLevel.Required
        });
        var exception = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        var postgresException = Assert.IsType<PostgresException>(exception!.GetBaseException());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
    }

    [Fact]
    public async Task InspectionRequirementsAreSnapshotsAndLaterRevisionsDoNotChangeThem()
    {
        var partId = await CreatePartAsync();
        var firstRevisionId = await CreateDraftWithCriterionAsync(partId);
        await SaveRequirementsAsync(
            partId,
            firstRevisionId,
            ("Material", CertificationRequirementLevel.Required, "Original requirement"),
            ("Heat", CertificationRequirementLevel.Optional, null));
        await PublishAsync(partId, firstRevisionId);

        var firstInspectionId = await CreateInspectionAsync(partId);
        var secondRevisionId = (await criteriaService.CreateDraftRevisionAsync(partId, "Changed certs"))
            .RevisionId!.Value;
        await SaveRequirementsAsync(
            partId,
            secondRevisionId,
            ("Material", CertificationRequirementLevel.Optional, "Changed requirement"),
            ("Plate", CertificationRequirementLevel.Required, null));
        await PublishAsync(partId, secondRevisionId);
        var secondInspectionId = await CreateInspectionAsync(partId);

        var first = await inspectionService.GetInspectionAsync(firstInspectionId);
        Assert.Equal(
            CertificationRequirementLevel.Required,
            first!.Certifications.Single(x => x.CertificationTypeName == "Material").RequirementLevel);
        Assert.Equal(
            "Original requirement",
            first.Certifications.Single(x => x.CertificationTypeName == "Material").RequirementNotes);
        Assert.Equal(
            CertificationRequirementLevel.Optional,
            first.Certifications.Single(x => x.CertificationTypeName == "Heat").RequirementLevel);
        Assert.Null(first.Certifications.Single(x => x.CertificationTypeName == "Plate").RequirementLevel);

        var second = await inspectionService.GetInspectionAsync(secondInspectionId);
        Assert.Equal(
            CertificationRequirementLevel.Optional,
            second!.Certifications.Single(x => x.CertificationTypeName == "Material").RequirementLevel);
        Assert.Equal(
            CertificationRequirementLevel.Required,
            second.Certifications.Single(x => x.CertificationTypeName == "Plate").RequirementLevel);
        Assert.Null(second.Certifications.Single(x => x.CertificationTypeName == "Heat").RequirementLevel);
    }

    [Fact]
    public async Task RequiredAndOptionalStatusUsesDocumentPresenceAndSupportsMultipleDocuments()
    {
        var partId = await CreatePartAsync();
        var revisionId = await CreateDraftWithCriterionAsync(partId);
        await SaveRequirementsAsync(
            partId,
            revisionId,
            ("Material", CertificationRequirementLevel.Required, null),
            ("Heat", CertificationRequirementLevel.Optional, null));
        await PublishAsync(partId, revisionId);
        var inspectionId = await CreateInspectionAsync(partId);

        var inspection = await inspectionService.GetInspectionAsync(inspectionId);
        Assert.True(inspection!.Certifications.Single(x => x.CertificationTypeName == "Material").IsMissingRequired);
        Assert.False(inspection.Certifications.Single(x => x.CertificationTypeName == "Heat").IsMissingRequired);
        Assert.True(inspection.IsMissingRequiredCertifications);

        var materialId = inspection.Certifications.Single(x => x.CertificationTypeName == "Material").CertificationTypeId;
        var firstPdf = "%PDF-1.7\nMill certificate\n%%EOF"u8.ToArray();
        var secondPdf = "%PDF-1.7\nChemical analysis\n%%EOF"u8.ToArray();
        Assert.Equal(
            InspectionOperationStatus.Succeeded,
            (await inspectionService.UploadCertificationDocumentAsync(
                inspectionId,
                materialId,
                "Mill Certificate.pdf",
                firstPdf)).Status);
        Assert.Equal(
            InspectionOperationStatus.Succeeded,
            (await inspectionService.UploadCertificationDocumentAsync(
                inspectionId,
                materialId,
                "Chemical Analysis.pdf",
                secondPdf)).Status);

        inspection = await inspectionService.GetInspectionAsync(inspectionId);
        var material = inspection!.Certifications.Single(x => x.CertificationTypeName == "Material");
        Assert.False(material.IsMissingRequired);
        Assert.False(inspection.IsMissingRequiredCertifications);
        Assert.Equal(2, material.Documents.Count);
        var downloaded = await inspectionService.GetCertificationDocumentAsync(
            inspectionId,
            material.Documents[1].Id);
        Assert.Equal("Chemical Analysis.pdf", downloaded!.OriginalFileName);
        Assert.Equal(secondPdf, downloaded.Content);
        var pdfDocuments = await inspectionService.GetCertificationDocumentsForPdfAsync(inspectionId);
        Assert.Collection(
            pdfDocuments,
            document =>
            {
                Assert.Equal("Mill Certificate.pdf", document.OriginalFileName);
                Assert.Equal(firstPdf, document.Content);
            },
            document =>
            {
                Assert.Equal("Chemical Analysis.pdf", document.OriginalFileName);
                Assert.Equal(secondPdf, document.Content);
            });

        Assert.Equal(
            InspectionOperationStatus.Succeeded,
            (await inspectionService.DeleteCertificationDocumentAsync(
                inspectionId,
                material.Documents[0].Id,
                material.Documents[0].Version)).Status);
        inspection = await inspectionService.GetInspectionAsync(inspectionId);
        material = inspection!.Certifications.Single(x => x.CertificationTypeName == "Material");
        Assert.Single(material.Documents);
        Assert.False(material.IsMissingRequired);

        Assert.Equal(
            InspectionOperationStatus.Succeeded,
            (await inspectionService.DeleteCertificationDocumentAsync(
                inspectionId,
                material.Documents[0].Id,
                material.Documents[0].Version)).Status);
        inspection = await inspectionService.GetInspectionAsync(inspectionId);
        Assert.True(inspection!.Certifications.Single(x => x.CertificationTypeName == "Material").IsMissingRequired);
    }

    [Fact]
    public async Task InspectionRequirementDuplicatesAreRejectedByTheDatabase()
    {
        var partId = await CreatePartAsync();
        var revisionId = await CreateDraftWithCriterionAsync(partId);
        await SaveRequirementsAsync(
            partId,
            revisionId,
            ("Material", CertificationRequirementLevel.Required, null));
        await PublishAsync(partId, revisionId);
        var inspectionId = await CreateInspectionAsync(partId);

        await using var db = database.CreateDbContext();
        var snapshot = await db.InspectionCertificationRequirements.SingleAsync();
        db.InspectionCertificationRequirements.Add(new InspectionCertificationRequirement
        {
            InspectionId = inspectionId,
            CertificationTypeId = snapshot.CertificationTypeId,
            CertificationTypeName = snapshot.CertificationTypeName,
            RequirementLevel = snapshot.RequirementLevel
        });
        var exception = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        var postgresException = Assert.IsType<PostgresException>(exception!.GetBaseException());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresException.SqlState);
    }

    [Fact]
    public async Task PublishedRevisionCertificationRequirementsAreDatabaseProtected()
    {
        var partId = await CreatePartAsync();
        var revisionId = await CreateDraftWithCriterionAsync(partId);
        await SaveRequirementsAsync(
            partId,
            revisionId,
            ("Material", CertificationRequirementLevel.Required, null));
        await PublishAsync(partId, revisionId);
        await CreateInspectionAsync(partId);

        await using var db = database.CreateDbContext();
        var requirement = await db.RevisionCertificationRequirements.SingleAsync();
        requirement.RequirementLevel = CertificationRequirementLevel.Optional;
        var exception = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        var postgresException = Assert.IsType<PostgresException>(exception!.GetBaseException());
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, postgresException.SqlState);
    }

    private async Task<long> CreateDraftWithCriterionAsync(long partId)
    {
        var revisionId = (await criteriaService.CreateDraftRevisionAsync(partId, null))
            .RevisionId!.Value;
        var gageTypeId = await CreateGageTypeAsync();
        var add = await criteriaService.AddCriterionAsync(
            partId,
            revisionId,
            new InspectionCriterionEditModel
            {
                InspectionNumber = 1,
                Name = "Overall Length",
                GageTypeId = gageTypeId,
                Minimum = "20",
                MaximumOrTolerance = "21",
                Unit = "mm"
            });
        Assert.Equal(CriteriaOperationStatus.Succeeded, add.Status);
        return revisionId;
    }

    private async Task SaveRequirementsAsync(
        long partId,
        long revisionId,
        params (string Name, CertificationRequirementLevel Level, string? Notes)[] requirements)
    {
        var requested = requirements.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var saved = (await criteriaService.GetRevisionAsync(partId, revisionId))!
            .CertificationRequirements
            .ToDictionary(x => x.CertificationTypeId);
        var models = (await criteriaService.GetCertificationTypeChoicesAsync())
            .Select(type =>
            {
                saved.TryGetValue(type.Id, out var existing);
                return new RevisionCertificationRequirementEditModel
                {
                    Id = existing?.Id ?? 0,
                    CertificationTypeId = type.Id,
                    CertificationTypeName = existing?.CertificationTypeName ?? type.Name,
                    RequirementLevel = requested.TryGetValue(type.Name, out var requirement)
                        ? requirement.Level
                        : null,
                    Notes = requested.TryGetValue(type.Name, out requirement)
                        ? requirement.Notes
                        : null,
                    Version = existing?.Version ?? 0
                };
            })
            .ToList();
        var result = await criteriaService.SaveCertificationRequirementsAsync(
            partId,
            revisionId,
            models);
        Assert.Equal(CriteriaOperationStatus.Succeeded, result.Status);
    }

    private async Task PublishAsync(long partId, long revisionId)
    {
        var revision = await criteriaService.GetRevisionAsync(partId, revisionId);
        var result = await criteriaService.PublishRevisionAsync(partId, revisionId, revision!.Version);
        Assert.Equal(CriteriaOperationStatus.Succeeded, result.Status);
    }

    private async Task<long> CreateInspectionAsync(long partId)
    {
        var result = await inspectionService.CreateInspectionAsync(new CreateInspectionModel
        {
            PartId = partId,
            InspectionDate = new DateOnly(2026, 8, 26)
        });
        Assert.Equal(InspectionOperationStatus.Succeeded, result.Status);
        return result.InspectionId!.Value;
    }

    private async Task<long> CreatePartAsync()
    {
        await using var db = database.CreateDbContext();
        var customer = new Customer { Name = $"Certification Customer {Guid.NewGuid():N}" };
        var part = new Part
        {
            Customer = customer,
            PartNumber = $"CERT-{Guid.NewGuid():N}"
        };
        db.Parts.Add(part);
        await db.SaveChangesAsync();
        return part.Id;
    }

    private async Task<long> CreateGageTypeAsync()
    {
        await using var db = database.CreateDbContext();
        var type = new GageType { Name = $"Certification Gage {Guid.NewGuid():N}" };
        db.GageTypes.Add(type);
        await db.SaveChangesAsync();
        return type.Id;
    }
}
