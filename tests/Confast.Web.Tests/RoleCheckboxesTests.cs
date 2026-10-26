using Confast.Web.Components;
using Confast.Web.Features.Identity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Confast.Web.Tests;

public sealed class RoleCheckboxesTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task ReadOnlyCheckboxReflectsActualSelectionAndServerAssignmentAuthority(bool selected, bool canChange)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<RoleCheckboxes>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(RoleCheckboxes.SelectedRoles)] = selected ? new HashSet<string> { AppRoles.ReadOnly } : new HashSet<string>(),
                [nameof(RoleCheckboxes.Choices)] = new UserRoleChoice[] { new(AppRoles.ReadOnlyId, AppRoles.ReadOnly, true, canChange, null) }
            }));
            return component.ToHtmlString();
        });
        if (selected) Assert.Contains("checked", html); else Assert.DoesNotContain("checked", html);
        if (canChange) Assert.DoesNotContain("disabled", html); else Assert.Contains("disabled", html);
    }
}
