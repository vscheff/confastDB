using Confast.Web.Features.Identity;
using Confast.Web.Features.Inspections;

namespace Confast.Web.Tests;

[Collection(PostgresCollection.Name)]
public sealed class CertificationEmailTemplatePersistenceTests(PostgresTestDatabase database) : IAsyncLifetime
{
    private readonly AuthorizationTestSession authorization = new(database);
    public async Task InitializeAsync()
    {
        await database.ResetAsync();
        await authorization.SignInAsync(await authorization.ProvisionRootAsync());

    }
    public Task DisposeAsync() { authorization.Dispose(); return Task.CompletedTask; }

    [Fact]
    public async Task MigrationDefaultsCanBeResolvedSavedAndRestored()
    {
        var renderer = new CertificationEmailTemplateRenderer();
        var sanitizer = new CertificationEmailHtmlSanitizer();
        var service = new CertificationEmailTemplateService(database, renderer, sanitizer, authorization.Guard);
        var templates = await service.GetAllAsync();
        Assert.Equal(3, templates.Count);

        var original = templates.Single(x => x.TemplateType == CertificationEmailTemplateType.SingleLot);
        var changed = original with { SubjectTemplate = "Updated {CustomerName}", HtmlBodyTemplate = "<p><strong>Updated</strong> {LotNumber}</p>" };
        await service.SaveAsync(changed);

        var resolved = await new CertificationEmailTemplateResolver(database, authorization.Guard)
            .ResolveEmailTemplateAsync(CertificationEmailTemplateType.SingleLot);
        Assert.Equal(changed.SubjectTemplate, resolved.SubjectTemplate);
        Assert.Equal(changed.HtmlBodyTemplate, resolved.HtmlBodyTemplate);

        await service.SaveAsync(original);

        var settings = await service.GetSettingsAsync();
        Assert.Equal("quality@conformancefasteners.com", settings.ImplicitCcAddress);

        await service.SaveSettingsAsync(new CertificationEmailSettingsEditModel(null));
        Assert.Null((await service.GetSettingsAsync()).ImplicitCcAddress);

        await service.SaveSettingsAsync(settings);
    }

    private sealed class NoCurrentUser : ICurrentUser
    {
        public ValueTask<string?> GetUserIdAsync() => ValueTask.FromResult<string?>(null);
    }
}
