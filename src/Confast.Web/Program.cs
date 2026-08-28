using System.Globalization;
using Confast.Web.Components;
using Confast.Web.Data;
using Confast.Web.Features.Customers;
using Confast.Web.Features.Gages;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.Parts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

if (builder.Environment.IsDevelopment())
{
    // The sandboxed development process may not be able to use the Windows
    // user-profile services that the default providers rely on. Neither
    // antiforgery state nor development logs need those machine integrations.
    for (var index = builder.Services.Count - 1; index >= 0; index--)
    {
        var descriptor = builder.Services[index];
        if (descriptor.ServiceType == typeof(IDataProtectionProvider)
            || descriptor.ServiceType == typeof(IKeyManager)
            || descriptor.ServiceType.FullName
                == "Microsoft.AspNetCore.DataProtection.KeyManagement.Internal.IKeyRingProvider"
            || descriptor.ImplementationType?.FullName
                == "Microsoft.AspNetCore.DataProtection.KeyManagement.KeyRingProvider"
            || (descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType?.FullName
                    == "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService"))
        {
            builder.Services.RemoveAt(index);
        }
    }

    builder.Services.AddSingleton<IDataProtectionProvider, EphemeralDataProtectionProvider>();
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
}

var connectionString = builder.Configuration.GetConnectionString("Confast")
    ?? throw new InvalidOperationException(
        "Connection string 'Confast' is not configured. Use appsettings or .NET user secrets.");

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddSingleton<CertificationPackageFilenameFormatter>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<GageService>();
builder.Services.AddScoped<InspectionCriteriaService>();
builder.Services.AddSingleton(new CertificationPreviewOptions
{
    RendererPath = builder.Configuration["PdfPreview:RendererPath"] ?? "pdftoppm",
    ResolutionDpi = int.TryParse(builder.Configuration["PdfPreview:ResolutionDpi"], out var dpi) ? dpi : 150,
    MaximumPages = int.TryParse(builder.Configuration["PdfPreview:MaximumPages"], out var pages) ? pages : 50
});
builder.Services.AddSingleton<CertificationPreviewRenderer>();
builder.Services.AddSingleton<InspectionPdfRenderer>();
builder.Services.AddSingleton<PdfDocumentMerger>();
builder.Services.AddScoped<ICertificationPackageService, CertificationPackageService>();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddScoped<IEmailService>(services =>
    new SmtpEmailService(services.GetRequiredService<Microsoft.Extensions.Options.IOptions<EmailOptions>>().Value));
builder.Services.AddScoped<InspectionService>();
builder.Services.AddScoped<PartService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapGet(
    "/parts/{partId:long}/inspection-criteria/{revisionId:long}/master-print",
    async Task<IResult> (
        long partId,
        long revisionId,
        InspectionCriteriaService criteriaService,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        var file = await criteriaService.GetMasterPrintAsync(
            partId,
            revisionId,
            cancellationToken);
        if (file is null)
        {
            return Results.NotFound();
        }

        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(
            file.Content,
            contentType: "application/pdf",
            enableRangeProcessing: true);
    });
app.MapGet(
    "/inspections/{inspectionId:long}/certifications/documents/{documentId:long}",
    async Task<IResult> (
        long inspectionId,
        long documentId,
        InspectionService inspectionService,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        var file = await inspectionService.GetCertificationDocumentAsync(
            inspectionId,
            documentId,
            cancellationToken);
        if (file is null)
        {
            return Results.NotFound();
        }

        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(
            file.Content,
            contentType: file.ContentType,
        enableRangeProcessing: true);
    });
app.MapGet(
    "/inspections/{inspectionId:long}/certifications/documents/{documentId:long}/preview",
    async Task<IResult> (
        long inspectionId,
        long documentId,
        InspectionService inspectionService,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        var file = await inspectionService.GetCertificationDocumentPreviewAsync(
            inspectionId,
            documentId,
            cancellationToken);
        if (file is null)
        {
            return Results.NotFound();
        }

        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(
            file.Content,
            contentType: "application/pdf",
            enableRangeProcessing: true);
    });
app.MapGet(
    "/inspections/{inspectionId:long}/certifications/documents/{documentId:long}/download",
    async Task<IResult> (
        long inspectionId,
        long documentId,
        InspectionService inspectionService,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        var file = await inspectionService.GetCertificationDocumentAsync(
            inspectionId,
            documentId,
            cancellationToken);
        if (file is null)
        {
            return Results.NotFound();
        }

        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(
            file.Content,
            contentType: file.ContentType,
            fileDownloadName: file.OriginalFileName,
            enableRangeProcessing: true);
    });
app.MapGet(
    "/inspections/{inspectionId:long}/print/download",
    async Task<IResult> (
        long inspectionId,
        InspectionService inspectionService,
        InspectionPdfRenderer pdfRenderer,
        HttpContext httpContext,
        string? notesPage,
        CancellationToken cancellationToken) =>
    {
        var inspection = await inspectionService.GetInspectionAsync(inspectionId, cancellationToken);
        if (inspection is null)
        {
            return Results.NotFound();
        }

        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        var lotNumber = string.IsNullOrWhiteSpace(inspection.LotNumber)
            ? inspectionId.ToString(CultureInfo.InvariantCulture)
            : inspection.LotNumber.Trim();
        var fileName = $"Lot# {lotNumber}.pdf";
        var previewUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/inspections/{inspectionId}/print";
        if (string.Equals(notesPage, "second", StringComparison.OrdinalIgnoreCase))
        {
            previewUrl += "?notesPage=second";
        }
        return Results.File(
            await pdfRenderer.RenderAsync(previewUrl, cancellationToken),
            contentType: "application/pdf",
            fileDownloadName: fileName);
    });
app.MapGet(
    "/inspections/{inspectionId:long}/inspection-sheet/download",
    async Task<IResult> (
        long inspectionId,
        InspectionService inspectionService,
        InspectionPdfRenderer pdfRenderer,
        PdfDocumentMerger pdfMerger,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        var inspection = await inspectionService.GetInspectionAsync(inspectionId, cancellationToken);
        if (inspection is null)
        {
            return Results.NotFound();
        }

        var previewUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/inspections/{inspectionId}/print";
        if (!string.IsNullOrWhiteSpace(inspection.InHouseNotes))
        {
            previewUrl += "?notesPage=second";
        }

        var inspectionSheet = await pdfRenderer.RenderAsync(previewUrl, cancellationToken);
        var certifications = await inspectionService.GetCertificationDocumentsForPdfAsync(
            inspectionId,
            cancellationToken);
        var mergedPdf = pdfMerger.Merge(
            inspectionSheet,
            certifications.Select(x => x.Content));

        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        var lotNumber = string.IsNullOrWhiteSpace(inspection.LotNumber)
            ? inspectionId.ToString(CultureInfo.InvariantCulture)
            : inspection.LotNumber.Trim();
        return Results.File(
            mergedPdf,
            contentType: "application/pdf",
            fileDownloadName: $"Lot# {lotNumber} Inspection Sheet.pdf");
    });
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
