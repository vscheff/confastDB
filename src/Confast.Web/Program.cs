using Confast.Web.Components;
using Confast.Web.Data;
using Confast.Web.Features.Customers;
using Confast.Web.Features.Gages;
using Confast.Web.Features.InspectionCriteria;
using Confast.Web.Features.Parts;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var connectionString = builder.Configuration.GetConnectionString("Confast")
    ?? throw new InvalidOperationException(
        "Connection string 'Confast' is not configured. Use appsettings or .NET user secrets.");

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseNpgsql(connectionString));
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<GageService>();
builder.Services.AddScoped<InspectionCriteriaService>();
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
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
