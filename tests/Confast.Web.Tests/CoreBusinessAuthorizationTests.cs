using System.Security.Claims;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Customers;
using Confast.Web.Features.Gages;
using Confast.Web.Features.Identity;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.Parts;
using Confast.Web.Features.Suppliers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Confast.Web.Data;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CoreBusinessAuthorizationTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly AuthorizationTestSession session = new(database);
    private EffectivePermissionService evaluator = null!;
    private ApplicationAuthorization guard = null!;
    private CustomerService customers = null!;
    private PartService parts = null!;
    private GageService gages = null!;
    private InspectionCriteriaService criteria = null!;
    private InspectionService inspections = null!;
    private SupplierService suppliers = null!;
    private PartFlipService flips = null!;
    private NominalToleranceSettingsService tolerance = null!;
    private CertificationEmailTemplateService templates = null!;
    private CertificationEmailTemplateResolver resolver = null!;
    private CertificationPackageService packages = null!;
    private CertificationEmailService emails = null!;
    private readonly CapturingTransport transport = new();
    private readonly InspectionPrintRenderTokenService tokens = new(new EphemeralDataProtectionProvider());
    private string rootId = null!;
    private long customerId, plantId, partId, gageTypeId, gageId, revisionId, draftId, inspectionId, documentId, certificationTypeId;
    private uint documentVersion;
    private CustomerEditModel customer = null!;
    private PlantEditModel plant = null!;
    private PartEditModel part = null!;
    private InspectionEditModel inspection = null!;
    private PlantCertificationDeliveryEditModel delivery = null!;
    private PlantCertificationRecipientEditModel recipient = null!;
    private CertificationEmailTemplateEditModel template = null!;
    private NominalToleranceSettingsEditModel toleranceModel = null!;
    private static byte[] Pdf => "%PDF-1.4\nfixture\n%%EOF"u8.ToArray();

    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        rootId = await session.ProvisionRootAsync(); await session.SignInAsync(rootId);
        evaluator = session.Evaluator; guard = new(evaluator, new HttpContextAccessor());
        customers = new(database, guard); parts = new(database, guard); gages = new(database, guard);
        criteria = new(database, guard); suppliers = new(database, guard); flips = new(database, guard);
        tolerance = new(database, guard);
        templates = new(database, new(), new(), guard); resolver = new(database, guard);
        inspections = new(database, evaluator, guard, new HttpContextAccessor(), tokens);
        packages = new(database, inspections, new(null!, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), tokens), new(), new(), tokens, guard);
        emails = new(transport, database, guard);
        customerId = await customers.CreateCustomerAsync(new() { Name = "Authorization Fixture" });
        customer = (await customers.GetCustomerAsync(customerId))!;
        plant = Assert.Single(await customers.GetPlantsAsync(customerId)); plantId = plant.Id;
        partId = (await parts.CreatePartAsync(new() { CustomerId = customerId, PartNumber = "AUTH-CORE", PlantIds = [plantId] })).Id!.Value;
        part = (await parts.GetPartAsync(partId))!;
        await gages.SaveGageTypeAsync(new() { Name = "Authorization Caliper" });
        gageTypeId = Assert.Single(await gages.GetGageTypesAsync()).Id;
        await gages.SaveGageAsync(new() { GageTypeId = gageTypeId, GageNumber = "AUTH-GAGE" });
        gageId = Assert.Single(await gages.GetGagesAsync()).Id;
        revisionId = (await criteria.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        await criteria.AddCriterionAsync(partId, revisionId, new() { Name = "Length", GageTypeId = gageTypeId, Minimum = "1", MaximumOrTolerance = "2" });
        var revision = (await criteria.GetRevisionAsync(partId, revisionId))!;
        Assert.Equal(CriteriaOperationStatus.Succeeded, (await criteria.PublishRevisionAsync(partId, revisionId, revision.Version)).Status);
        inspectionId = (await inspections.CreateInspectionAsync(new() { PartId = partId, LotNumber = "AUTH-LOT", QuantityReceived = 500, InspectionDate = new(2026, 10, 14) })).InspectionId!.Value;
        inspection = (await inspections.GetInspectionAsync(inspectionId))!;
        draftId = (await criteria.CreateDraftRevisionAsync(partId, null)).RevisionId!.Value;
        await criteria.UploadMasterPrintAsync(partId, draftId, "fixture.pdf", Pdf, (await criteria.GetRevisionAsync(partId, draftId))!.Version);
        await using var db = database.CreateDbContext();
        certificationTypeId = await db.CertificationTypes.Where(x => x.Name != "Inspection Sheet").Select(x => x.Id).FirstAsync();
        var uploaded = await inspections.UploadCertificationDocumentAsync(inspectionId, certificationTypeId, "fixture.pdf", Pdf);
        Assert.Equal(InspectionOperationStatus.Succeeded, uploaded.Status);
        var doc = await db.CertificationDocuments.SingleAsync(); documentId = doc.Id; documentVersion = doc.Version;
        var savedRecipient = await customers.SavePlantCertificationRecipientAsync(new() { PlantId = plantId, EmailAddress = "customer@example.test" });
        delivery = (await customers.GetPlantCertificationDeliveryAsync(plantId))!;
        recipient = Assert.Single(delivery.Recipients);
        template = (await templates.GetAllAsync())[0]; toleranceModel = await tolerance.GetAsync();
    }
    public Task DisposeAsync() { session.Dispose(); return Task.CompletedTask; }

    private async Task<ApplicationUser> Actor(params string[] keys)
    {
        await using var db = database.CreateDbContext();
        var role = new ApplicationRole { Name = "Restricted " + Guid.NewGuid(), NormalizedName = Guid.NewGuid().ToString(), IsEnabled = true };
        db.Roles.Add(role);
        db.RolePermissions.AddRange(keys.Distinct().Select(k => new RolePermission { RoleId = role.Id, PermissionKey = k }));
        var user = new ApplicationUser { UserName = "actor-" + Guid.NewGuid(), DisplayName = "Restricted Actor", Email = "actor@example.test",
            SecurityStamp = Guid.NewGuid().ToString(), ConcurrencyStamp = Guid.NewGuid().ToString(), IsActive = true };
        db.Users.Add(user); db.UserRoles.Add(new IdentityUserRole<string> { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        await db.UserRoles.Where(x => x.UserId == user.Id && x.RoleId == AppRoles.ReadOnlyId).ExecuteDeleteAsync();
        await session.SignInAsync(user.Id); return user;
    }
    private Task<ApplicationUser> Authorized(string key) => Actor(PermissionCatalog.ExpandRequirements([key]).ToArray());

    [Fact]
    public async Task DelegatedTemporaryCompletionChangesOnlyTheRenderedModel_AndCannotAddUnapprovedOptions()
    {
        var type = (await criteria.GetSecondaryProcessTypeChoicesAsync()).First();
        await criteria.AddSecondaryProcessRequirementAsync(partId, draftId, new() { SecondaryProcessTypeId = type.Id });
        var revision = (await criteria.GetRevisionAsync(partId, draftId))!;
        await criteria.PublishRevisionAsync(partId, draftId, revision.Version);
        var id = (await inspections.CreateInspectionAsync(new() { PartId = partId, LotNumber = "TEMP-RENDER", InspectionDate = new(2026,10,16) })).InspectionId!.Value;
        using var operation = await guard.BeginAsync("Certifications.BuildPackage", new("Inspection", id.ToString()),
            [Permissions.Certifications.BuildPackage, Permissions.Certifications.TemporarilyCompletePackage]);
        var token = tokens.CreateForPackage(operation, id, id, true);
        await using var services = new ServiceCollection().AddSingleton(tokens).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, User = new(new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, "inspection-print-renderer")], InspectionPrintRenderAuthenticationHandler.SchemeName)) };
        context.Request.Path = $"/inspections/{id}/print";
        context.Request.QueryString = new("?renderToken=" + Uri.EscapeDataString(token));
        session.Principal = new(new ClaimsIdentity());
        var adapter = new InspectionService(database, evaluator, guard, new HttpContextAccessor { HttpContext = context }, tokens);
        var model = await adapter.GetPrintableInspectionAsync(id, token, false);
        Assert.True(model!.IsTemporarilyCompletedForCertificationPackage);
        Assert.All(model.SecondaryProcesses, process => Assert.True(process.IsComplete));
        await using var db = database.CreateDbContext();
        Assert.False(Assert.Single(await db.InspectionSecondaryProcesses.Where(x => x.InspectionId == id).ToArrayAsync()).IsComplete);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => adapter.GetPrintableInspectionAsync(id, token, true));
    }

    private Dictionary<string, Func<Task>> Boundaries() => new()
    {
        ["Customers.Create"] = () => customers.CreateCustomerAsync(new() { Name = "New" }),
        ["Customers.List"] = () => customers.GetCustomersAsync(), ["Customers.Get"] = () => customers.GetCustomerAsync(customerId),
        ["Customers.Update"] = () => customers.SaveCustomerAsync(customer), ["Plants.Read"] = () => customers.GetPlantsAsync(customerId),
        ["Plants.Create"] = () => customers.SavePlantAsync(new() { CustomerId = customerId, Name = "New" }),
        ["Plants.Update"] = () => customers.SavePlantAsync(plant), ["Plants.Delete"] = () => customers.DeletePlantAsync(customerId, plantId, plant.Version),
        ["PlantDelivery.Read"] = () => customers.GetPlantCertificationDeliveryAsync(plantId),
        ["Recipients.Create"] = () => customers.SavePlantCertificationRecipientAsync(new() { PlantId = plantId, EmailAddress = "new@example.test" }),
        ["Recipients.Update"] = () => customers.SavePlantCertificationRecipientAsync(recipient),
        ["Recipients.Delete"] = () => customers.DeletePlantCertificationRecipientAsync(plantId, recipient.Id, recipient.Version),
        ["PlantSettings.Update"] = () => customers.SavePlantCertificationConfigurationAsync(delivery, new()),
        ["Parts.List"] = () => parts.GetPartsAsync(), ["Parts.ByCustomer"] = () => parts.GetPartsForCustomerAsync(customerId),
        ["Parts.Get"] = () => parts.GetPartAsync(partId), ["Parts.DeleteView"] = () => parts.GetPartForDeleteAsync(partId),
        ["Parts.CustomerChoices"] = () => parts.GetCustomerOptionsAsync(), ["Parts.PlantChoices"] = () => parts.GetPlantOptionsAsync(customerId),
        ["Parts.SupplierChoices"] = () => parts.GetSupplierOptionsAsync(), ["Parts.Eligibility"] = () => parts.HasEligibleMachineAsync(partId),
        ["Parts.Create"] = () => parts.CreatePartAsync(new() { CustomerId = customerId, PartNumber = "NEW" }),
        ["Parts.Update"] = () => parts.SavePartAsync(part), ["Parts.Delete"] = () => parts.DeletePartAsync(partId, part.Version),
        ["Suppliers.Read"] = () => suppliers.SearchAsync(), ["Suppliers.Create"] = () => suppliers.SaveAsync(new() { Name = "New" }),
        ["Suppliers.Update"] = () => suppliers.SaveAsync(new() { Id = 1, Name = "Changed" }),
        ["GageTypes.Read"] = () => gages.GetGageTypesAsync(), ["GageTypes.Choices"] = () => gages.GetGageTypeChoicesAsync(),
        ["Gages.Read"] = () => gages.GetGagesAsync(), ["Gages.MethodChoices"] = () => gages.GetGageMethodChoicesAsync(),
        ["Criteria.MethodChoices"] = () => gages.GetCriteriaMethodChoicesAsync(),
        ["GageTypes.Create"] = () => gages.SaveGageTypeAsync(new() { Name = "New" }),
        ["GageTypes.Update"] = () => gages.SaveGageTypeAsync(new() { Id = gageTypeId, Name = "Changed" }),
        ["Gages.Create"] = () => gages.SaveGageAsync(new() { GageTypeId = gageTypeId, GageNumber = "New" }),
        ["Gages.Update"] = () => gages.SaveGageAsync(new() { Id = gageId, GageTypeId = gageTypeId, GageNumber = "Changed" }),
        ["Criteria.Units"] = () => criteria.GetUnitChoicesAsync(), ["Criteria.SecondaryTypes"] = () => criteria.GetSecondaryProcessTypeChoicesAsync(),
        ["Criteria.CertificationTypes"] = () => criteria.GetCertificationTypeChoicesAsync(), ["Criteria.Summary"] = () => criteria.GetPartSummaryAsync(partId),
        ["Criteria.History"] = () => criteria.GetRevisionHistoryAsync(partId), ["Criteria.Revision"] = () => criteria.GetRevisionAsync(partId, draftId),
        ["Criteria.Criterion"] = () => criteria.GetCriterionAsync(partId, draftId, 1),
        ["Criteria.Create"] = () => criteria.CreateDraftRevisionAsync(partId, null), ["Criteria.Publish"] = () => criteria.PublishRevisionAsync(partId, draftId, 0),
        ["Criteria.Delete"] = () => criteria.DeleteRevisionAsync(partId, draftId, 0),
        ["Criteria.Header"] = () => criteria.SaveRevisionHeaderAsync(partId, draftId, new()),
        ["Criteria.AddRow"] = () => criteria.AddCriterionAsync(partId, draftId, new() { Name = "New" }),
        ["Criteria.UpdateRow"] = () => criteria.SaveCriterionAsync(partId, draftId, new() { Id = 1, Name = "Changed" }),
        ["Criteria.DeleteRow"] = () => criteria.DeleteCriterionAsync(partId, draftId, 1, 0),
        ["Criteria.MoveRow"] = () => criteria.MoveCriterionAsync(partId, draftId, 1, true),
        ["Criteria.MoveBefore"] = () => criteria.MoveCriterionBeforeAsync(partId, draftId, 1, 2),
        ["Criteria.AlignDecimals"] = () => criteria.AlignCriterionDecimalPlacesAsync(partId, draftId),
        ["Criteria.AddProcess"] = () => criteria.AddSecondaryProcessRequirementAsync(partId, draftId, new()),
        ["Criteria.SaveProcess"] = () => criteria.SaveSecondaryProcessRequirementAsync(partId, draftId, new()),
        ["Criteria.DeleteProcess"] = () => criteria.DeleteSecondaryProcessRequirementAsync(partId, draftId, 1, 0),
        ["Criteria.Certifications"] = () => criteria.SaveCertificationRequirementsAsync(partId, draftId, []),
        ["MasterPrints.Read"] = () => criteria.GetMasterPrintAsync(partId, draftId),
        ["MasterPrints.Upload"] = () => criteria.UploadMasterPrintAsync(partId, draftId, "new.pdf", Pdf, 0),
        ["MasterPrints.Delete"] = () => criteria.DeleteMasterPrintAsync(partId, draftId, 0),
        ["Inspections.List"] = () => inspections.GetInspectionsAsync(), ["Inspections.ByIds"] = () => inspections.GetInspectionsAsync([inspectionId]),
        ["Inspections.Find"] = () => inspections.FindInspectionsAsync(new()), ["Inspections.CreationReferences"] = () => inspections.GetPartOptionsAsync(),
        ["Inspections.Get"] = () => inspections.GetInspectionAsync(inspectionId), ["Inspections.DeleteView"] = () => inspections.GetInspectionForDeleteAsync(inspectionId),
        ["Inspections.Create"] = () => inspections.CreateInspectionAsync(new() { PartId = partId, LotNumber = "New" }),
        ["Inspections.Update"] = () => inspections.SaveInspectionAsync(inspection), ["Inspections.Delete"] = () => inspections.DeleteInspectionAsync(inspectionId, inspection.Version),
        ["Inspections.Duplicate"] = () => inspections.DuplicateInspectionAsync(inspectionId, 1, "New"),
        ["Inspections.FlipPreview"] = () => inspections.GetFlipPreviewAsync(inspectionId), ["Inspections.Flip"] = () => inspections.FlipInspectionAsync(inspectionId, 1, 1, "New"),
        ["Inspections.Transfer"] = () => inspections.MoveAdditionalLineageQuantityAsync(inspectionId, inspectionId, 1, 1),
        ["Inspections.Undo"] = () => inspections.UndoLineageOperationAsync(inspectionId, InspectionLineageOperation.Duplicate, 1, false),
        ["Certifications.Read"] = () => inspections.GetCertificationDocumentAsync(inspectionId, documentId),
        ["Certifications.Preview"] = () => inspections.GetCertificationDocumentPreviewAsync(inspectionId, documentId),
        ["Certifications.ForPdf"] = () => inspections.GetCertificationDocumentsForPdfAsync(inspectionId),
        ["Certifications.Upload"] = () => inspections.UploadCertificationDocumentAsync(inspectionId, certificationTypeId, "new.pdf", Pdf),
        ["Certifications.Delete"] = () => inspections.DeleteCertificationDocumentAsync(inspectionId, documentId, documentVersion),
        ["Packages.LotChoices"] = () => inspections.GetCertificationPackageLotOptionsAsync(inspectionId),
        ["Packages.PlantChoices"] = () => inspections.GetCertificationPackagePlantOptionsAsync(inspectionId),
        ["Packages.Build"] = () => packages.BuildAsync(new(inspectionId, customerId, [inspectionId], new(2026,10,14), plantId), "https://localhost"),
        ["Packages.Send"] = () => emails.SendAsync(new(new("Forged", "forged@example.test"), ["customer@example.test"], [], "Test", "Test", "Test.pdf", Pdf)),
        ["Templates.Resolve"] = () => resolver.ResolveEmailTemplateAsync(CertificationEmailTemplateType.SingleLot),
        ["Templates.Read"] = () => templates.GetAllAsync(), ["Templates.Update"] = () => templates.SaveAsync(template),
        ["EmailSettings.Read"] = () => templates.GetSettingsAsync(), ["EmailSettings.Update"] = () => templates.SaveSettingsAsync(new(null)),
        ["Tolerance.Read"] = () => tolerance.GetAsync(), ["Tolerance.Update"] = () => tolerance.SaveAsync(toleranceModel),
        ["FlipDefinitions.Read"] = () => flips.GetConfigurationAsync(partId), ["FlipDefinitions.Criteria"] = () => flips.GetCurrentCriterionOptionsAsync(partId),
        ["FlipDefinitions.Create"] = () => flips.SaveDefinitionAsync(partId, 1), ["FlipDefinitions.Update"] = () => flips.ReplaceMappingsAsync(1, []),
        ["FlipDefinitions.Delete"] = () => flips.DeleteDefinitionAsync(1),
        ["InspectionSheets.Print"] = () => inspections.GetPrintableInspectionAsync(inspectionId, null, false)
    };

    [Theory]
    [InlineData("anonymous")]
    [InlineData("no roles")]
    [InlineData("inactive")]
    [InlineData("legacy Administrator claim")]
    [InlineData("stale session")]
    [InlineData("revoked")]
    [InlineData("PendingRoot")]
    public async Task EveryPublicBoundaryRejectsUntrustedOrUnauthorizedActors(string condition)
    {
        var actor = await Actor(condition is "inactive" or "stale session" or "revoked" ? PermissionCatalog.All.Where(x => x.Authority == PermissionAuthority.Ordinary).Select(x => x.Key).ToArray() : []);
        await using (var db = database.CreateDbContext())
        {
            if (condition == "no roles") await db.UserRoles.Where(x => x.UserId == actor.Id).ExecuteDeleteAsync();
            if (condition == "inactive") await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
            if (condition == "stale session") await db.Users.Where(x => x.Id == actor.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SecurityStamp, "rotated"));
            if (condition == "revoked") await db.UserRoles.Where(x => x.UserId == actor.Id).ExecuteDeleteAsync();
        }
        if (condition == "anonymous") session.Principal = new(new ClaimsIdentity());
        if (condition == "legacy Administrator claim") ((ClaimsIdentity)session.Principal.Identity!).AddClaim(new(ClaimTypes.Role, AppRoles.Administrator));
        if (condition == "PendingRoot")
        {
            await database.ResetAsync(); await Actor(PermissionCatalog.All.Where(x => x.Authority == PermissionAuthority.Ordinary).Select(x => x.Key).ToArray());
        }
        foreach (var (name, invoke) in Boundaries())
        {
            var error = await Record.ExceptionAsync(invoke);
            Assert.True(error is AuthorizationDeniedException, $"{condition}: {name} returned {error?.GetType().Name ?? "success"} instead of an authorization denial.");
        }
        Assert.Null(transport.Message);
    }

    [Theory]
    [InlineData("Customers")]
    [InlineData("Plants")]
    [InlineData("Parts")]
    [InlineData("Suppliers")]
    [InlineData("Gages")]
    [InlineData("GageTypes")]
    [InlineData("PlantCertificationRecipients")]
    [InlineData("PlantCertificationSettings")]
    [InlineData("InspectionCriteria")]
    [InlineData("MasterPrints")]
    [InlineData("Certifications")]
    [InlineData("CertificationEmailTemplates")]
    [InlineData("CertificationEmailSettings")]
    [InlineData("NominalToleranceSettings")]
    [InlineData("PartFlipDefinitions")]
    [InlineData("Inspections")]
    public async Task ResourceReadDoesNotPermitAnySupportedWrites(string resource)
    {
        await Authorized(resource + ".Read");
        var boundaries = Boundaries();
        var aliases = new Dictionary<string,string> { ["PlantCertificationRecipients"] = "Recipients", ["PlantCertificationSettings"] = "PlantSettings",
            ["InspectionCriteria"] = "Criteria", ["CertificationEmailTemplates"] = "Templates", ["CertificationEmailSettings"] = "EmailSettings",
            ["NominalToleranceSettings"] = "Tolerance", ["PartFlipDefinitions"] = "FlipDefinitions" };
        var prefix = aliases.GetValueOrDefault(resource, resource);
        var writeNames = boundaries.Keys.Where(n => n.StartsWith(prefix + ".") && new[] {"Create","Update","Delete","Upload","AddRow","UpdateRow","DeleteRow","MoveRow","MoveBefore","AlignDecimals","AddProcess","SaveProcess","DeleteProcess","Header","Certifications","Publish","Duplicate","Flip","Transfer","Undo"}.Contains(n.Split('.')[1]));
        foreach (var name in writeNames) await Assert.ThrowsAsync<AuthorizationDeniedException>(boundaries[name]);
        var readName = resource switch { "PlantCertificationRecipients" or "PlantCertificationSettings" => "PlantDelivery.Read", "InspectionCriteria" => "Criteria.History", "Customers" or "Parts" or "Inspections" => prefix + ".List", _ => prefix + ".Read" };
        await boundaries[readName]();
    }

    [Theory]
    [InlineData("Customers.Create")]
    [InlineData("Plants.Create")]
    [InlineData("Parts.Create")]
    [InlineData("Suppliers.Create")]
    [InlineData("Gages.Create")]
    [InlineData("GageTypes.Create")]
    [InlineData("PlantCertificationRecipients.Create")]
    [InlineData("InspectionCriteria.Create")]
    public async Task CreateDoesNotRequireOrGrantUpdate(string key)
    {
        await Authorized(key);
        switch (key)
        {
            case "Customers.Create": Assert.True(await customers.CreateCustomerAsync(new() { Name = "Created" }) > 0); break;
            case "Plants.Create": Assert.Equal(PlantOperationStatus.Succeeded, (await customers.SavePlantAsync(new() { CustomerId = customerId, Name = "Created" })).Status); break;
            case "Parts.Create": Assert.Equal(SavePartStatus.Saved, (await parts.CreatePartAsync(new() { CustomerId = customerId, PartNumber = "Created", PlantIds = [plantId] })).Status); break;
            case "Suppliers.Create": Assert.True((await suppliers.SaveAsync(new() { Name = "Created" })).Succeeded); break;
            case "Gages.Create": Assert.Equal(SaveGageStatus.Saved, (await gages.SaveGageAsync(new() { GageTypeId = gageTypeId, GageNumber = "Created" })).Status); break;
            case "GageTypes.Create": Assert.Equal(SaveGageStatus.Saved, (await gages.SaveGageTypeAsync(new() { Name = "Created" })).Status); break;
            case "PlantCertificationRecipients.Create": Assert.Equal(PlantCertificationOperationStatus.Succeeded, (await customers.SavePlantCertificationRecipientAsync(new() { PlantId = plantId, EmailAddress = "created@example.test" })).Status); break;
            case "InspectionCriteria.Create": Assert.Equal(CriteriaOperationStatus.DraftAlreadyExists, (await criteria.CreateDraftRevisionAsync(partId, null)).Status); break;
        }
        var resource = key.Split('.')[0];
        Assert.False((await evaluator.EvaluateCurrentAsync()).Has(resource + ".Update"));
        var updateName = resource switch { "PlantCertificationRecipients" => "Recipients.Update", "InspectionCriteria" => "Criteria.Header", _ => resource + ".Update" };
        await Assert.ThrowsAsync<AuthorizationDeniedException>(Boundaries()[updateName]);
    }

    [Fact]
    public async Task DeviationChangesAreDetectedFromPersistedState_AndRevocationIsFresh()
    {
        var actor = await Authorized(Permissions.Inspections.Update);
        inspection.Results[0].ActualMin = "1.1"; inspection.Results[0].ActualMax = "1.2";
        var before = evaluator.SnapshotCount;
        Assert.Equal(InspectionOperationStatus.Succeeded, (await inspections.SaveInspectionAsync(inspection)).Status);
        Assert.Equal(before + 1, evaluator.SnapshotCount);
        inspection.Results[0].DeviationApproved = true;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => inspections.SaveInspectionAsync(inspection));
        await using (var db = database.CreateDbContext()) Assert.False(await db.InspectionResults.Select(x => x.DeviationApproved).SingleAsync());
        await Authorized(Permissions.Inspections.ApproveDeviation);
        Assert.Equal(InspectionOperationStatus.Succeeded, (await inspections.SaveInspectionAsync(inspection)).Status);
        await session.SignInAsync(actor.Id); inspection.Results[0].DeviationApproved = false;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => inspections.SaveInspectionAsync(inspection));
        inspection.Results[0].DeviationApproved = true;
        await using (var db = database.CreateDbContext()) await db.UserRoles.Where(x => x.UserId == actor.Id).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => inspections.SaveInspectionAsync(inspection));
    }

    [Fact]
    public async Task CriteriaUpdateDoesNotPublishOrManageMasterPrints_AndRootRetainsHistoryRestrictions()
    {
        var draft = (await criteria.GetRevisionAsync(partId, draftId))!;
        await Authorized(Permissions.InspectionCriteria.Update);
        Assert.Equal(CriteriaOperationStatus.Succeeded, (await criteria.SaveRevisionHeaderAsync(partId, draftId, new() { Notes = "Changed", Version = draft.Version })).Status);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => criteria.PublishRevisionAsync(partId, draftId, draft.Version));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => criteria.UploadMasterPrintAsync(partId, draftId, "new.pdf", Pdf, 0));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => criteria.DeleteMasterPrintAsync(partId, draftId, draft.Version));
        await session.SignInAsync(rootId);
        var published = (await criteria.GetRevisionAsync(partId, revisionId))!;
        Assert.Equal(CriteriaOperationStatus.RevisionInUse, (await criteria.SaveRevisionHeaderAsync(partId, revisionId, new() { Version = published.Version })).Status);
        Assert.Equal(CriteriaOperationStatus.RevisionInUse, (await criteria.UploadMasterPrintAsync(partId, revisionId, "new.pdf", Pdf, published.Version)).Status);
        Assert.Equal(PlantOperationStatus.Blocked, (await customers.DeletePlantAsync(customerId, plantId, plant.Version)).Status);
    }

    [Fact]
    public async Task SettingsAndRecipientReadsAreIndependent()
    {
        await Authorized(Permissions.PlantCertificationRecipients.Read);
        var recipientOnly = (await customers.GetPlantCertificationDeliveryAsync(plantId))!;
        Assert.Single(recipientOnly.Recipients); Assert.Empty(recipientOnly.CertificationTypes); Assert.Null(recipientOnly.FilenameTemplate);
        await Authorized(Permissions.PlantCertificationSettings.Read);
        var settingsOnly = (await customers.GetPlantCertificationDeliveryAsync(plantId))!;
        Assert.Empty(settingsOnly.Recipients); Assert.NotEmpty(settingsOnly.CertificationTypes);
    }

    [Fact]
    public async Task BuildDoesNotSendEmailOrTemporarilyComplete_AndDependentReadersNeedNoEditorGrants()
    {
        await Authorized(Permissions.Certifications.BuildPackage);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(Boundaries()["Packages.Send"]);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => packages.BuildAsync(new(inspectionId, customerId, [inspectionId], new(2026,10,14), plantId, true), "https://localhost"));
        Assert.Null(transport.Message);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => templates.GetSettingsAsync());
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => resolver.ResolveEmailTemplateAsync(CertificationEmailTemplateType.SingleLot));
        await Authorized(Permissions.Certifications.SendEmail);
        Assert.NotNull(await resolver.ResolveEmailTemplateAsync(CertificationEmailTemplateType.SingleLot));
        await emails.SendAsync(new(new("Forged", "forged@example.test"), ["customer@example.test"], [], "Test", "Test", "Test.pdf", Pdf));
        Assert.Equal("actor@example.test", transport.Message!.Sender.EmailAddress);
        Assert.NotEqual("forged@example.test", transport.Message.Sender.EmailAddress);
    }

    [Fact]
    public async Task InspectionReadsAndFindsEvaluateOnce_RegardlessOfListSize()
    {
        await Authorized(Permissions.Inspections.Read);
        var before = evaluator.SnapshotCount; await inspections.FindInspectionsAsync(new()); Assert.Equal(before + 1, evaluator.SnapshotCount);
        before = evaluator.SnapshotCount; await inspections.GetInspectionsAsync(); Assert.Equal(before + 1, evaluator.SnapshotCount);
        before = evaluator.SnapshotCount; await inspections.GetInspectionsAsync(Enumerable.Repeat(inspectionId, 100).ToArray()); Assert.Equal(before + 1, evaluator.SnapshotCount);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => inspections.GetPrintableInspectionAsync(inspectionId, null, false));
    }

    [Fact]
    public async Task PrerequisitesAreChecked_AndOptionalReadOnlyIsNeverRestored()
    {
        var actor = await Actor(Permissions.Parts.Update);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => parts.SavePartAsync(part));
        await using var db = database.CreateDbContext();
        Assert.False(await db.UserRoles.AnyAsync(x => x.UserId == actor.Id && x.RoleId == AppRoles.ReadOnlyId));
        await db.UserRoles.Where(x => x.UserId == rootId && x.RoleId == AppRoles.ReadOnlyId).ExecuteDeleteAsync();
        await session.SignInAsync(rootId);
        Assert.False(await db.UserRoles.AnyAsync(x => x.UserId == rootId && x.RoleId == AppRoles.ReadOnlyId));
        Assert.NotEmpty(await parts.GetPartsAsync());
    }

    [Theory]
    [InlineData("Customers.Update")]
    [InlineData("Plants.Update")]
    [InlineData("Parts.Update")]
    [InlineData("Suppliers.Update")]
    [InlineData("Gages.Update")]
    [InlineData("GageTypes.Update")]
    [InlineData("PlantCertificationRecipients.Update")]
    public async Task UpdateDoesNotRequireOrGrantCreate(string key)
    {
        var savedSupplier = await suppliers.SaveAsync(new() { Name = "Existing Supplier" });
        var supplier = Assert.Single(await suppliers.SearchAsync());
        var gage = Assert.Single(await gages.GetGagesAsync()); var type = Assert.Single(await gages.GetGageTypesAsync());
        await Authorized(key);
        switch (key)
        {
            case "Customers.Update": customer.Name = "Updated"; Assert.Equal(SaveCustomerStatus.Saved, (await customers.SaveCustomerAsync(customer)).Status); break;
            case "Plants.Update": plant.Name = "Updated"; Assert.Equal(PlantOperationStatus.Succeeded, (await customers.SavePlantAsync(plant)).Status); break;
            case "Parts.Update": part.Description = "Updated"; Assert.Equal(SavePartStatus.Saved, (await parts.SavePartAsync(part)).Status); break;
            case "Suppliers.Update": supplier.Name = "Updated"; Assert.True((await suppliers.SaveAsync(supplier)).Succeeded); break;
            case "Gages.Update": Assert.Equal(SaveGageStatus.Saved, (await gages.SaveGageAsync(new() { Id = gageId, GageTypeId = gageTypeId, GageNumber = "Updated", Version = gage.Version })).Status); break;
            case "GageTypes.Update": Assert.Equal(SaveGageStatus.Saved, (await gages.SaveGageTypeAsync(new() { Id = gageTypeId, Name = "Updated", Version = type.Version })).Status); break;
            case "PlantCertificationRecipients.Update": recipient.Name = "Updated"; Assert.Equal(PlantCertificationOperationStatus.Succeeded, (await customers.SavePlantCertificationRecipientAsync(recipient)).Status); break;
        }
        var prefix = key.Split('.')[0] == "PlantCertificationRecipients" ? "Recipients" : key.Split('.')[0];
        await Assert.ThrowsAsync<AuthorizationDeniedException>(Boundaries()[prefix + ".Create"]);
    }

    [Theory]
    [InlineData("Plants.Delete")]
    [InlineData("Parts.Delete")]
    [InlineData("PlantCertificationRecipients.Delete")]
    [InlineData("InspectionCriteria.Delete")]
    [InlineData("MasterPrints.Delete")]
    [InlineData("Certifications.Delete")]
    [InlineData("Inspections.Delete")]
    public async Task DeleteIsIndependentOfUpdate(string key)
    {
        var auxiliaryPlant = await customers.SavePlantAsync(new() { CustomerId = customerId, Name = "Unused" });
        var auxiliaryPart = await parts.CreatePartAsync(new() { CustomerId = customerId, PartNumber = "Unused", PlantIds = [plantId] });
        var draft = (await criteria.GetRevisionAsync(partId, draftId))!;
        await Authorized(key);
        switch (key)
        {
            case "Plants.Delete": Assert.Equal(PlantOperationStatus.Succeeded, (await customers.DeletePlantAsync(customerId, auxiliaryPlant.Id!.Value, auxiliaryPlant.Version!.Value)).Status); break;
            case "Parts.Delete": Assert.Equal(DeletePartStatus.Deleted, await parts.DeletePartAsync(auxiliaryPart.Id!.Value, auxiliaryPart.Version!.Value)); break;
            case "PlantCertificationRecipients.Delete": Assert.Equal(PlantCertificationOperationStatus.Succeeded, (await customers.DeletePlantCertificationRecipientAsync(plantId, recipient.Id, recipient.Version)).Status); break;
            case "InspectionCriteria.Delete": Assert.Equal(CriteriaOperationStatus.Succeeded, (await criteria.DeleteRevisionAsync(partId, draftId, draft.Version)).Status); break;
            case "MasterPrints.Delete": Assert.Equal(CriteriaOperationStatus.Succeeded, (await criteria.DeleteMasterPrintAsync(partId, draftId, draft.Version)).Status); break;
            case "Certifications.Delete": Assert.Equal(InspectionOperationStatus.Succeeded, (await inspections.DeleteCertificationDocumentAsync(inspectionId, documentId, documentVersion)).Status); break;
            case "Inspections.Delete": Assert.Equal(InspectionOperationStatus.Succeeded, (await inspections.DeleteInspectionAsync(inspectionId, inspection.Version)).Status); break;
        }
        Assert.False((await evaluator.EvaluateCurrentAsync()).Has(key.Replace("Delete", "Update")));
    }

    [Theory]
    [InlineData("Inspections.Update")]
    [InlineData("Inspections.Duplicate")]
    [InlineData("Inspections.Flip")]
    [InlineData("Inspections.TransferQuantity")]
    [InlineData("Inspections.UndoLineage")]
    [InlineData("InspectionSheets.Export")]
    public async Task SpecializedInspectionActionsDoNotConferOtherWorkflowAuthority(string key)
    {
        await Authorized(key);
        var own = key.Split('.')[1];
        var boundaries = Boundaries();
        foreach (var action in new[] { "Update", "Delete", "Duplicate", "Flip", "Transfer", "Undo" })
        {
            if (own == action || own == "TransferQuantity" && action == "Transfer" || own == "UndoLineage" && action == "Undo") continue;
            await Assert.ThrowsAsync<AuthorizationDeniedException>(boundaries["Inspections." + action]);
        }
        if (key == Permissions.Inspections.Duplicate)
            Assert.Equal(InspectionOperationStatus.Succeeded, (await inspections.DuplicateInspectionAsync(inspectionId, 100, "AUTHORIZED-DUPLICATE")).Status);
        if (key == Permissions.InspectionSheets.Export)
        {
            using var operation = await guard.BeginAsync("InspectionSheets.Export", new("Inspection", inspectionId.ToString()), [key]);
            Assert.True(tokens.IsValid(tokens.CreateForExport(operation, inspectionId), inspectionId));
            Assert.NotNull(await inspections.GetInspectionForExportAsync(inspectionId, operation, default));
            await Assert.ThrowsAsync<AuthorizationDeniedException>(() => inspections.GetDocumentsForExportAsync(inspectionId, operation, default));
        }
    }

    [Fact]
    public async Task IndependentPublishAndMasterPrintAuthorityWorksWithoutCriteriaUpdate()
    {
        var draft = (await criteria.GetRevisionAsync(partId, draftId))!;
        await Authorized(Permissions.MasterPrints.Update);
        Assert.Equal(CriteriaOperationStatus.Succeeded, (await criteria.UploadMasterPrintAsync(partId, draftId, "replacement.pdf", Pdf, draft.Version)).Status);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => criteria.SaveRevisionHeaderAsync(partId, draftId, new()));
        await session.SignInAsync(rootId); draft = (await criteria.GetRevisionAsync(partId, draftId))!;
        await Authorized(Permissions.InspectionCriteria.Publish);
        Assert.Equal(CriteriaOperationStatus.Succeeded, (await criteria.PublishRevisionAsync(partId, draftId, draft.Version)).Status);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => criteria.SaveRevisionHeaderAsync(partId, draftId, new()));
    }

    [Fact]
    public async Task TransferAndUndoNeedTheirAction_ButDoNotNeedGenericUpdateOrDelete()
    {
        var split = await inspections.DuplicateInspectionAsync(inspectionId, 100, "TRANSFER-FIXTURE");
        var destination = split.InspectionId!.Value;
        await Authorized(Permissions.Inspections.TransferQuantity);
        Assert.Equal(InspectionOperationStatus.Succeeded, (await inspections.MoveAdditionalLineageQuantityAsync(inspectionId, inspectionId, destination, 10)).Status);
        await using var db = database.CreateDbContext();
        var transferId = await db.LotTransfers.Select(x => x.Id).SingleAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => inspections.UndoLineageOperationAsync(inspectionId, InspectionLineageOperation.Transfer, transferId, false));
        await Authorized(Permissions.Inspections.UndoLineage);
        Assert.Equal(InspectionOperationStatus.Succeeded, (await inspections.UndoLineageOperationAsync(inspectionId, InspectionLineageOperation.Transfer, transferId, false)).Status);
        Assert.Equal(400, await db.Inspections.Where(x => x.Id == inspectionId).Select(x => x.QuantityReceived).SingleAsync());
    }

    [Fact]
    public async Task FlipDefinitionCreationMappingUpdatesAndDeletionRemainIndependent()
    {
        var targetPart = (await parts.CreatePartAsync(new() { CustomerId = customerId, PartNumber = "CONFIG-TARGET", PlantIds = [plantId] })).Id!.Value;
        var revisionId = (await criteria.CreateDraftRevisionAsync(targetPart, null)).RevisionId!.Value;
        await criteria.AddCriterionAsync(targetPart, revisionId, new() { Name = "Length", GageTypeId = gageTypeId, Minimum = "1", MaximumOrTolerance = "2" });
        var revision = (await criteria.GetRevisionAsync(targetPart, revisionId))!;
        await criteria.PublishRevisionAsync(targetPart, revisionId, revision.Version);

        await Authorized(Permissions.PartFlipDefinitions.Create);
        var created = await flips.SaveDefinitionAsync(partId, targetPart);
        Assert.Equal(SavePartFlipStatus.Saved, created.Status);
        var definitionId = created.DefinitionId!.Value;
        var configuration = (await flips.GetConfigurationAsync(partId))!;
        var mappings = Assert.Single(configuration.Definitions).Mappings;
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => flips.ReplaceMappingsAsync(definitionId, mappings));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => flips.DeleteDefinitionAsync(definitionId));

        await Authorized(Permissions.PartFlipDefinitions.Update);
        Assert.Equal(SavePartFlipStatus.Saved, (await flips.ReplaceMappingsAsync(definitionId, mappings)).Status);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => flips.SaveDefinitionAsync(targetPart, partId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => flips.DeleteDefinitionAsync(definitionId));

        await Authorized(Permissions.PartFlipDefinitions.Delete);
        Assert.True(await flips.DeleteDefinitionAsync(definitionId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => flips.ReplaceMappingsAsync(definitionId, mappings));
        Assert.Empty((await flips.GetConfigurationAsync(partId))!.Definitions);
        Assert.Single((await flips.GetConfigurationAsync(targetPart))!.Definitions);
    }

    [Fact]
    public async Task FlipExecutionUsesBoundedMappingsWithoutGrantingTheConfigurationEditor()
    {
        var targetPart = (await parts.CreatePartAsync(new() { CustomerId = customerId, PartNumber = "FLIP-TARGET", PlantIds = [plantId] })).Id!.Value;
        var targetRevision = (await criteria.CreateDraftRevisionAsync(targetPart, null)).RevisionId!.Value;
        await criteria.AddCriterionAsync(targetPart, targetRevision, new() { Name = "Length", GageTypeId = gageTypeId, Minimum = "1", MaximumOrTolerance = "2" });
        var target = (await criteria.GetRevisionAsync(targetPart, targetRevision))!;
        await criteria.PublishRevisionAsync(targetPart, targetRevision, target.Version);
        var definition = await flips.SaveDefinitionAsync(partId, targetPart);
        Assert.Equal(SavePartFlipStatus.Saved, definition.Status);
        await Authorized(Permissions.Inspections.Flip);
        Assert.NotNull(await inspections.GetFlipPreviewAsync(inspectionId));
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => flips.GetConfigurationAsync(partId));
        Assert.Equal(InspectionOperationStatus.Succeeded, (await inspections.FlipInspectionAsync(inspectionId, definition.DefinitionId!.Value, 100, "AUTHORIZED-FLIP")).Status);
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => flips.DeleteDefinitionAsync(definition.DefinitionId.Value));
    }

    [Fact]
    public async Task BuildingAnApprovedDocumentPackageEvaluatesOnce_AndRevocationStopsReexport()
    {
        inspection.Results[0].GageId = gageId; inspection.Results[0].ActualMin = "1.1"; inspection.Results[0].ActualMax = "1.2";
        await inspections.SaveInspectionAsync(inspection);
        await using (var db = database.CreateDbContext())
        {
            // Exercise real PDF assembly without an SMTP send or a headless-print dependency.
            using var pdf = new PdfSharp.Pdf.PdfDocument(); pdf.AddPage(); using var bytes = new MemoryStream(); pdf.Save(bytes);
            await db.CertificationDocuments.Where(x => x.Id == documentId).ExecuteUpdateAsync(s => s.SetProperty(x => x.Content, bytes.ToArray()));
            await db.PlantCertificationRequirements.Where(x => x.PlantId == plantId).ExecuteDeleteAsync();
            db.PlantCertificationRequirements.Add(new() { PlantId = plantId, CertificationTypeId = certificationTypeId });
            db.InspectionCertificationRequirements.Add(new() { InspectionId = inspectionId, CertificationTypeId = certificationTypeId,
                CertificationTypeName = await db.CertificationTypes.Where(x => x.Id == certificationTypeId).Select(x => x.Name).SingleAsync(), RequirementLevel = CertificationRequirementLevel.Required });
            await db.SaveChangesAsync();
        }
        var actor = await Authorized(Permissions.Certifications.BuildPackage);
        var before = evaluator.SnapshotCount;
        var request = new CertificationPackageRequest(inspectionId, customerId, [inspectionId], new(2026,10,14), plantId);
        var package = await packages.BuildAsync(request, "https://localhost");
        Assert.Equal(before + 1, evaluator.SnapshotCount);
        Assert.True(package.Content.AsSpan().StartsWith("%PDF-"u8));
        await using (var db = database.CreateDbContext()) await db.UserRoles.Where(x => x.UserId == actor.Id).ExecuteDeleteAsync();
        await Assert.ThrowsAsync<AuthorizationDeniedException>(() => packages.BuildAsync(request, "https://localhost"));
    }

    [Fact]
    public async Task ListPermissionQueriesAreBoundedAsTheResultCountGrows()
    {
        var actor = await Authorized(Permissions.Inspections.Read);
        var counter = new CommandCounter(); var factory = new CountingFactory(database.ConnectionString, counter);
        using var cache = new PermissionCache();
        var countedEvaluator = new EffectivePermissionService(factory, session, Options.Create(new IdentityOptions()), cache, NullLogger<EffectivePermissionService>.Instance);
        var counted = new InspectionService(factory, countedEvaluator, new(countedEvaluator, new HttpContextAccessor()), new HttpContextAccessor(), tokens);
        await counted.GetInspectionsAsync(); // Warm the versioned cache.
        counter.Count = 0; await counted.GetInspectionsAsync(); var small = counter.Count;
        await session.SignInAsync(rootId);
        for (var i = 0; i < 25; i++)
            await inspections.CreateInspectionAsync(new() { PartId = partId, LotNumber = $"BULK-{i}", InspectionDate = new(2026,10,14) });
        await session.SignInAsync(actor.Id);
        counter.Count = 0; var before = countedEvaluator.SnapshotCount;
        Assert.Equal(26, (await counted.GetInspectionsAsync()).Count);
        Assert.Equal(before + 1, countedEvaluator.SnapshotCount); Assert.Equal(small, counter.Count); Assert.InRange(counter.Count, 1, 10);
    }

    private sealed class CommandCounter : DbCommandInterceptor
    {
        internal int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Count++; return ValueTask.FromResult(result); }
    }

    [Fact]
    public async Task AdvisoryComponentsPreserveParametersBeforeAwaiting_AndDoNotQueryForEveryControl()
    {
        var services = new ServiceCollection().AddLogging().AddSingleton(evaluator).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var before = evaluator.SnapshotCount;
        var markup = await renderer.Dispatcher.InvokeAsync(async () => (await renderer.RenderComponentAsync<PermissionParent>()).ToHtmlString());
        Assert.Contains("Copied parameter", markup); Assert.Equal(before + 1, evaluator.SnapshotCount);
        Assert.Contains("Cascading parameter", markup);
        Assert.Equal(25, markup.Split("Allowed").Length - 1);
    }

    public sealed class PermissionParent : ComponentBase
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<string>>(0);
            builder.AddAttribute(1, "Value", "Cascading parameter");
            builder.AddAttribute(2, "ChildContent", (RenderFragment)(child =>
            { child.OpenComponent<PermissionProbe>(0); child.AddAttribute(1, "Text", "Copied parameter"); child.CloseComponent(); }));
            builder.CloseComponent();
        }
    }
    public sealed class PermissionProbe : Confast.Web.Components.PermissionComponentBase
    {
        [Parameter] public string? Text { get; set; }
        [CascadingParameter] public string? Cascade { get; set; }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.AddContent(0, Text);
            builder.AddContent(1, Cascade);
            for (var i = 0; i < 25; i++) builder.AddContent(1, Can(Permissions.Parts.Read) ? "Allowed" : "Denied");
        }
    }
    private sealed class CountingFactory(string connection, CommandCounter counter) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).AddInterceptors(counter).Options);
    }

    private sealed class CapturingTransport : IEmailService
    {
        internal EmailMessage? Message;
        public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) { Message = message; return Task.CompletedTask; }
    }
}
