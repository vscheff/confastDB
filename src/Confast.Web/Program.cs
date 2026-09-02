using System.Globalization;
using Confast.Web.Components;
using Confast.Web.Data;
using Confast.Web.Features.Customers;
using Confast.Web.Features.Gages;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Parts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

var connectionString = builder.Configuration.GetConnectionString("Confast")
    ?? throw new InvalidOperationException(
        "Connection string 'Confast' is not configured. Use appsettings or .NET user secrets.");

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
    {
        options.User.RequireUniqueEmail = true;
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireLowercase = true;
        options.Password.RequireUppercase = true;
        options.Password.RequireNonAlphanumeric = true;
        options.Lockout.AllowedForNewUsers = true;
        options.Lockout.MaxFailedAccessAttempts = 5;
        options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
    })
    .AddEntityFrameworkStores<AppDbContext>()
    .AddClaimsPrincipalFactory<ApplicationUserClaimsPrincipalFactory>()
    .AddSignInManager<ApplicationSignInManager>()
    .AddDefaultTokenProviders();
builder.Services.AddAuthentication(options =>
    options.DefaultAuthenticateScheme = "Confast.Default")
    .AddPolicyScheme("Confast.Default", null, options =>
    {
        options.ForwardDefaultSelector = context =>
        {
            var renderTokenService = context.RequestServices
                .GetRequiredService<InspectionPrintRenderTokenService>();
            return InspectionPrintRenderAuthenticationHandler.CanAuthenticate(
                context.Request,
                renderTokenService)
                ? InspectionPrintRenderAuthenticationHandler.SchemeName
                : IdentityConstants.ApplicationScheme;
        };
    })
    .AddScheme<AuthenticationSchemeOptions, InspectionPrintRenderAuthenticationHandler>(
        InspectionPrintRenderAuthenticationHandler.SchemeName,
        _ => { });
builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.AccessDeniedPath = "/access-denied";
    options.Cookie.Name = "Confast.Authentication";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.SlidingExpiration = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(8);
    options.Events.OnValidatePrincipal = async context =>
    {
        await SecurityStampValidator.ValidatePrincipalAsync(context);
        if (context.Principal?.Identity?.IsAuthenticated != true)
        {
            return;
        }

        var userManager = context.HttpContext.RequestServices
            .GetRequiredService<UserManager<ApplicationUser>>();
        var user = await userManager.GetUserAsync(context.Principal);
        if (user is null || !user.IsActive)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        }
    };
});
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<ICurrentEmailSender, CurrentEmailSender>();
builder.Services.AddScoped<UserAdministrationService>();
builder.Services.Configure<BootstrapAdminOptions>(builder.Configuration.GetSection("BootstrapAdmin"));
builder.Services.AddSingleton<CertificationPackageFilenameFormatter>();
builder.Services.AddSingleton<CertificationEmailHtmlSanitizer>();
builder.Services.AddSingleton<CertificationEmailTemplateRenderer>();
builder.Services.AddScoped<ICertificationEmailTemplateResolver, CertificationEmailTemplateResolver>();
builder.Services.AddScoped<ICertificationEmailTemplateService, CertificationEmailTemplateService>();
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
builder.Services.AddSingleton<InspectionPrintRenderTokenService>();
builder.Services.AddSingleton<PdfDocumentMerger>();
builder.Services.AddScoped<ICertificationPackageService, CertificationPackageService>();
builder.Services.AddOptions<EmailOptions>()
    .Bind(builder.Configuration.GetSection("Email"));
builder.Services.AddSingleton<Microsoft.Extensions.Options.IValidateOptions<EmailOptions>, EmailOptionsValidator>();
builder.Services.AddScoped<IEmailService, SmtpEmailService>();
builder.Services.AddScoped<ICertificationEmailService, CertificationEmailService>();
builder.Services.AddScoped<NominalToleranceSettingsService>();
builder.Services.AddScoped<InspectionService>();
builder.Services.AddScoped<InspectionSearchNavigationContext>();
builder.Services.AddScoped<PartService>();
builder.Services.AddScoped<PartFlipService>();

if (builder.Environment.IsDevelopment())
{
    // The sandboxed development process may not be able to use the Windows
    // user-profile services that the default providers rely on. Identity also
    // registers Data Protection, so this replacement must happen after all
    // application services have been added.
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

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

if (app.Environment.IsDevelopment())
{
    app.MapPost(
        "/development/smtp-test",
        async Task<IResult> (
            ICurrentEmailSender currentEmailSender,
            IEmailService emailService,
            Microsoft.Extensions.Options.IOptions<EmailOptions> emailOptions,
            ILoggerFactory loggerFactory,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var recipient = emailOptions.Value.TestRecipient;
                if (string.IsNullOrWhiteSpace(recipient))
                {
                    return Results.BadRequest(new { message = "Set Email:TestRecipient before running the SMTP proof of concept." });
                }
                var sender = await currentEmailSender.GetAsync(cancellationToken);
                await emailService.SendAsync(new EmailMessage(
                    sender,
                    [recipient],
                    [],
                    "Confast DB SMTP proof of concept",
                    "This message verifies the configured certification-email sender and Reply-To behavior.",
                    "confast-smtp-test.pdf",
                    [37, 80, 68, 70, 45, 49, 46, 52]));
                return Results.Ok(new { message = "SMTP test message submitted. Inspect the received From, Reply-To, Return-Path, and authentication headers." });
            }
            catch (EmailDeliveryException exception)
            {
                return Results.BadRequest(new { message = exception.Message });
            }
            catch (Exception exception)
            {
                loggerFactory.CreateLogger("DevelopmentSmtpTest")
                    .LogError(exception, "Development SMTP proof-of-concept send failed.");
                return Results.Problem("The SMTP test could not be sent.");
            }
        })
        .DisableAntiforgery()
        .RequireAuthorization(policy => policy.RequireRole(AppRoles.Administrator));
}

app.MapStaticAssets().AllowAnonymous();
app.MapIdentityEndpoints();
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
        InspectionPrintRenderTokenService renderTokenService,
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
        var renderToken = Uri.EscapeDataString(renderTokenService.Create(inspectionId));
        var previewUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/inspections/{inspectionId}/print?renderToken={renderToken}";
        if (string.Equals(notesPage, "second", StringComparison.OrdinalIgnoreCase))
        {
            previewUrl += "&notesPage=second";
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
        InspectionPrintRenderTokenService renderTokenService,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        var inspection = await inspectionService.GetInspectionAsync(inspectionId, cancellationToken);
        if (inspection is null)
        {
            return Results.NotFound();
        }

        var renderToken = Uri.EscapeDataString(renderTokenService.Create(inspectionId));
        var previewUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/inspections/{inspectionId}/print?renderToken={renderToken}";
        if (!string.IsNullOrWhiteSpace(inspection.InHouseNotes))
        {
            previewUrl += "&notesPage=second";
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

await IdentityBootstrapper.CreateInitialAdministratorAsync(app.Services);

app.Run();

public partial class Program;
