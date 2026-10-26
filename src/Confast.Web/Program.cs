using System.Globalization;
using System.Security.Claims;
using Confast.Web.Components;
using Confast.Web.Data;
using Confast.Web.Features.Customers;
using Confast.Web.Features.Chat;
using Confast.Web.Features.ContainerTracking;
using Confast.Web.Features.Suppliers;
using Confast.Web.Features.Gages;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Inspections;
using Confast.Web.Features.Identity;
using Confast.Web.Features.Authorization;
using Confast.Web.Features.Parts;
using Confast.Web.Time;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
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
builder.Services.AddIdentity<ApplicationUser, ApplicationRole>(options =>
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
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
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
builder.Services.AddConfastAuthorization();
builder.Services.AddScoped<AuthenticationStateProvider, IdentityRevalidatingAuthenticationStateProvider>();
builder.Services.AddScoped<ICurrentUser, CurrentUser>();
builder.Services.AddScoped<ICurrentEmailSender, CurrentEmailSender>();
builder.Services.AddScoped<UserAdministrationService>();
builder.Services.Configure<BootstrapAdminOptions>(builder.Configuration.GetSection("BootstrapAdmin"));
builder.Services.Configure<BrowserTestUserOptions>(builder.Configuration.GetSection("BrowserTestUser"));
builder.Services.AddSingleton<CertificationPackageFilenameFormatter>();
builder.Services.AddSingleton<CertificationEmailHtmlSanitizer>();
builder.Services.AddSingleton<CertificationEmailTemplateRenderer>();
builder.Services.AddScoped<ICertificationEmailTemplateResolver, CertificationEmailTemplateResolver>();
builder.Services.AddScoped<ICertificationEmailTemplateService, CertificationEmailTemplateService>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<DevelopmentDateOverrideTimeProvider>();
builder.Services.AddSingleton<BusinessDateProvider>();
builder.Services.AddScoped<ContainerTrackingService>();
builder.Services.AddScoped<ReceivedPartsService>();
builder.Services.AddScoped<SupplierService>();
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
builder.Services.AddSingleton<ChatNotifications>();
builder.Services.AddScoped<ChatService>();
builder.Services.AddOptions<ChatGifCacheOptions>().BindConfiguration("Giphy")
    .Validate(x => x.SearchCacheLifetimeHours is > 0 and <= 87600, "GIF search lifetime must be between 1 hour and 10 years.")
    .Validate(x => x.HourlyRequestLimit > 0 && x.RefreshRequestReserve >= 0 && x.RefreshRequestReserve < x.HourlyRequestLimit,
        "GIF request reserve must be nonnegative and smaller than the hourly limit.")
    .ValidateOnStart();
builder.Services.AddSingleton<ChatGifCategoryBackgrounds>();
builder.Services.AddHostedService<ChatMessageDeliveryWorker>();
builder.Services.AddScoped<UserProfilePictureService>();
builder.Services.AddScoped<UserPresenceService>();
builder.Services.AddScoped<Confast.Web.Features.ProductionScheduling.ProductionService>();
builder.Services.AddScoped<Confast.Web.Features.ProductionTracking.ProductionTrackingService>();
builder.Services.AddScoped<Confast.Web.Features.MorningProductionReview.MorningProductionReviewService>();
builder.Services.AddScoped<PartFlipService>();

builder.AddConfastHostSecurity();
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddScoped<DevelopmentUtilities>();
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole();
}

var app = builder.Build();

// No implicit trust in loopback proxies or client-supplied forwarded headers.
if (builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() is { Length: > 0 })
    app.UseForwardedHeaders();

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
app.Use(async (context, next) =>
{
    if (context.Request.Query.ContainsKey("renderToken"))
    {
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers.XContentTypeOptions = "nosniff";
    }
    await next(context);
});
app.UseBusinessAuthorization();
app.UseAuthorization();
app.UseAntiforgery();
app.Use(async (context, next) =>
{
    try { await next(context); }
    catch (AuthorizationDeniedException exception) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.CacheControl = "private, no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        await context.Response.WriteAsync(exception.Message);
    }
});

if (app.Environment.IsDevelopment())
{
    app.MapPost("/development/smtp-test", async Task<IResult> (HttpContext context,
        Microsoft.AspNetCore.Antiforgery.IAntiforgery antiforgery, DevelopmentUtilities utilities,
        CancellationToken cancellationToken) =>
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context);
            return Results.Ok(await utilities.PrepareEmailAsync(context, cancellationToken));
        }
        catch (Microsoft.AspNetCore.Antiforgery.AntiforgeryValidationException)
        { return Results.BadRequest(new { message = "A valid antiforgery token is required." }); }
        catch (EmailDeliveryException)
        { return Results.BadRequest(new { message = "The current account needs a valid email address for this diagnostic." }); }
    }).RequireAuthorization(AuthorizationRegistration.PolicyName(Permissions.Certifications.SendEmail))
      .WithMetadata(new BusinessHttpBoundary("Development.EmailDiagnostic", Permissions.Certifications.SendEmail, _ => new("Development")));
}
app.MapStaticAssets().AllowAnonymous();
app.MapIdentityEndpoints();
app.MapGet("/profile-pictures/{userId}", async Task<IResult> (
    string userId, HttpContext context, UserProfilePictureService pictures, CancellationToken cancellationToken) =>
{
    var requesterUserId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (requesterUserId is null) return Results.Unauthorized();
    try
    {
        var picture = await pictures.GetForHttpAsync(userId, cancellationToken);
        if (picture is null) return Results.NotFound();
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.CacheControl = "private, no-store";
        return Results.File(picture.Value.Data, picture.Value.ContentType);
    }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
}).WithMetadata(new BusinessHttpBoundary("ChatProfilePicture", Permissions.Chat.Access,
    context => new("ChatProfilePicture", context.Request.RouteValues["userId"]!.ToString())))
    .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = IdentityConstants.ApplicationScheme });
app.MapGet("/chat/group-icons/{conversationId:long}", async Task<IResult> (
    long conversationId, ChatService chat, HttpContext context, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (userId is null) return Results.Unauthorized();
    try
    {
        var picture = await chat.GetGroupIconForHttpAsync(conversationId, cancellationToken);
        if (picture is null) return Results.NotFound();
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers.CacheControl = "private, no-store";
        return Results.File(picture.Value.Data, picture.Value.ContentType);
    }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
}).WithMetadata(new BusinessHttpBoundary("ChatGroupIcon", Permissions.Chat.Access,
    context => new("ChatGroupIcon", context.Request.RouteValues["conversationId"]!.ToString())))
    .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = IdentityConstants.ApplicationScheme });
app.MapGet("/chat/attachments/{attachmentId:long}", async Task<IResult> (
    long attachmentId, ChatService chat, HttpContext context, CancellationToken cancellationToken) =>
{
    var requesterUserId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (requesterUserId is null) return Results.Unauthorized();
    ChatAttachmentFile? file;
    try { file = await chat.GetAttachmentForHttpAsync(attachmentId, cancellationToken); }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    if (file is null || file.Kind is not (ChatAttachmentKind.Image or ChatAttachmentKind.Video))
        return Results.NotFound();
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "private, no-store";
    return Results.File(file.Content, file.ContentType, enableRangeProcessing: true);
}).WithMetadata(new BusinessHttpBoundary("ChatAttachment", Permissions.Chat.Access,
    context => new("ChatAttachment", context.Request.RouteValues["attachmentId"]!.ToString())))
    .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = IdentityConstants.ApplicationScheme });
app.MapGet("/chat/attachments/{attachmentId:long}/download", async Task<IResult> (
    long attachmentId, ChatService chat, HttpContext context, CancellationToken cancellationToken) =>
{
    var requesterUserId = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (requesterUserId is null) return Results.Unauthorized();
    ChatAttachmentFile? file;
    try { file = await chat.GetAttachmentForHttpAsync(attachmentId, cancellationToken); }
    catch (UnauthorizedAccessException) { return Results.Unauthorized(); }
    if (file is null) return Results.NotFound();
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.CacheControl = "private, no-store";
    return Results.File(file.Content, "application/octet-stream", fileDownloadName: file.FileName);
}).WithMetadata(new BusinessHttpBoundary("ChatAttachment", Permissions.Chat.Access,
    context => new("ChatAttachment", context.Request.RouteValues["attachmentId"]!.ToString())))
    .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = IdentityConstants.ApplicationScheme });
app.MapGet(
    "/parts/{partId:long}/inspection-criteria/{revisionId:long}/master-print",
    async Task<IResult> (
        long partId,
        long revisionId,
        InspectionCriteriaService criteriaService,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        var operation = BusinessHttpAuthorization.Require(httpContext, "MasterPrints.Read", new("CriteriaRevision", $"{partId}/{revisionId}"), Permissions.MasterPrints.Read);
        var file = await criteriaService.GetMasterPrintForHttpAsync(partId, revisionId, operation, cancellationToken);
        if (file is null)
        {
            return Results.NotFound();
        }

        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(
            file.Content,
            contentType: "application/pdf",
            enableRangeProcessing: true);
    }).RequireAuthorization(AuthorizationRegistration.PolicyName(Permissions.MasterPrints.Read))
    .WithMetadata(new BusinessHttpBoundary("MasterPrints.Read", Permissions.MasterPrints.Read, context => new("CriteriaRevision", $"{context.Request.RouteValues["partId"]}/{context.Request.RouteValues["revisionId"]}")));
app.MapGet(
    "/inspections/{inspectionId:long}/certifications/documents/{documentId:long}",
    async Task<IResult> (
        long inspectionId,
        long documentId,
        InspectionService inspectionService,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        var operation = BusinessHttpAuthorization.Require(httpContext, "Certifications.Read", new("Inspection", inspectionId.ToString()), Permissions.Certifications.Read);
        var file = await inspectionService.GetCertificationDocumentForHttpAsync(inspectionId, documentId, operation, cancellationToken);
        if (file is null)
        {
            return Results.NotFound();
        }

        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(
            file.Content,
            contentType: file.ContentType,
        enableRangeProcessing: true);
    }).RequireAuthorization(AuthorizationRegistration.PolicyName(Permissions.Certifications.Read))
    .WithMetadata(new BusinessHttpBoundary("Certifications.Read", Permissions.Certifications.Read, context => new("Inspection", context.Request.RouteValues["inspectionId"]!.ToString())));
app.MapGet(
    "/inspections/{inspectionId:long}/certifications/documents/{documentId:long}/preview",
    async Task<IResult> (
        long inspectionId,
        long documentId,
        InspectionService inspectionService,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        var operation = BusinessHttpAuthorization.Require(httpContext, "Certifications.Read", new("Inspection", inspectionId.ToString()), Permissions.Certifications.Read);
        var file = await inspectionService.GetCertificationDocumentPreviewForHttpAsync(inspectionId, documentId, operation, cancellationToken);
        if (file is null)
        {
            return Results.NotFound();
        }

        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        return Results.File(
            file.Content,
            contentType: "application/pdf",
            enableRangeProcessing: true);
    }).RequireAuthorization(AuthorizationRegistration.PolicyName(Permissions.Certifications.Read))
    .WithMetadata(new BusinessHttpBoundary("Certifications.Read", Permissions.Certifications.Read, context => new("Inspection", context.Request.RouteValues["inspectionId"]!.ToString())));
app.MapGet(
    "/inspections/{inspectionId:long}/certifications/documents/{documentId:long}/download",
    async Task<IResult> (
        long inspectionId,
        long documentId,
        InspectionService inspectionService,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        var operation = BusinessHttpAuthorization.Require(httpContext, "Certifications.Read", new("Inspection", inspectionId.ToString()), Permissions.Certifications.Read);
        var file = await inspectionService.GetCertificationDocumentForHttpAsync(inspectionId, documentId, operation, cancellationToken);
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
    }).RequireAuthorization(AuthorizationRegistration.PolicyName(Permissions.Certifications.Read))
    .WithMetadata(new BusinessHttpBoundary("Certifications.Read", Permissions.Certifications.Read, context => new("Inspection", context.Request.RouteValues["inspectionId"]!.ToString())));
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
        var operation = BusinessHttpAuthorization.Require(httpContext, "InspectionSheets.Export", new("Inspection", inspectionId.ToString()), Permissions.InspectionSheets.Export);
        var inspection = await inspectionService.GetInspectionForExportAsync(inspectionId, operation, cancellationToken);
        if (inspection is null)
        {
            return Results.NotFound();
        }

        httpContext.Response.Headers.XContentTypeOptions = "nosniff";
        var lotNumber = string.IsNullOrWhiteSpace(inspection.LotNumber)
            ? inspectionId.ToString(CultureInfo.InvariantCulture)
            : inspection.LotNumber.Trim();
        var fileName = $"Lot# {lotNumber}.pdf";
        var renderToken = Uri.EscapeDataString(renderTokenService.CreateForExport(operation, inspectionId, string.Equals(notesPage, "second", StringComparison.OrdinalIgnoreCase)));
        var previewUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/inspections/{inspectionId}/print?renderToken={renderToken}";
        if (string.Equals(notesPage, "second", StringComparison.OrdinalIgnoreCase))
        {
            previewUrl += "&notesPage=second";
        }
        return Results.File(
            await pdfRenderer.RenderAsync(previewUrl, cancellationToken),
            contentType: "application/pdf",
            fileDownloadName: fileName);
    }).RequireAuthorization(AuthorizationRegistration.PolicyName(Permissions.InspectionSheets.Export))
    .WithMetadata(new BusinessHttpBoundary("InspectionSheets.Export", Permissions.InspectionSheets.Export, context => new("Inspection", context.Request.RouteValues["inspectionId"]!.ToString())));
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
        var operation = BusinessHttpAuthorization.Require(httpContext, "InspectionSheets.Export", new("Inspection", inspectionId.ToString()), Permissions.InspectionSheets.Export);
        var inspection = await inspectionService.GetInspectionForExportAsync(inspectionId, operation, cancellationToken);
        if (inspection is null)
        {
            return Results.NotFound();
        }

        var renderToken = Uri.EscapeDataString(renderTokenService.CreateForExport(operation, inspectionId, !string.IsNullOrWhiteSpace(inspection.InHouseNotes)));
        var previewUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}/inspections/{inspectionId}/print?renderToken={renderToken}";
        if (!string.IsNullOrWhiteSpace(inspection.InHouseNotes))
        {
            previewUrl += "&notesPage=second";
        }

        var certifications = await inspectionService.GetDocumentsForExportAsync(inspectionId, operation, cancellationToken);
        var inspectionSheet = await pdfRenderer.RenderAsync(previewUrl, cancellationToken);
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
    }).RequireAuthorization(AuthorizationRegistration.PolicyName(Permissions.InspectionSheets.Export))
    .WithMetadata(new BusinessHttpBoundary("InspectionSheets.Export", Permissions.InspectionSheets.Export, context => new("Inspection", context.Request.RouteValues["inspectionId"]!.ToString())));
var componentEndpoints = app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
componentEndpoints.Add(endpoint =>
{
    if (endpoint is Microsoft.AspNetCore.Routing.RouteEndpointBuilder route
        && route.RoutePattern.RawText?.TrimStart('/') == "inspections/{InspectionId:long}/print")
        endpoint.Metadata.Add(new BusinessHttpBoundary("InspectionSheets.Export", Permissions.InspectionSheets.Export,
            context => new("Inspection", context.Request.RouteValues["InspectionId"]!.ToString())));
});

await IdentityBootstrapper.CreateInitialAdministratorAsync(app.Services);
if (app.Environment.IsDevelopment())
{
    await IdentityBootstrapper.EnsureBrowserTestUserAsync(app.Services);
}

app.Run();

public partial class Program;
