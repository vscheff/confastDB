using Confast.Web.Features.Inspections;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace Confast.Web.Tests;

public sealed partial class DelegatedAdministrationTests
{
    [RoleNavigationBrowserFact]
    public async Task CertificationEmailAppearanceFollowsThemeWithoutSavingDisplayColors()
    {
        const string templateBody = """
            <p><span style="color:#a10000">Signature name</span></p>
            <p><span style="color:#000030 !important">Pasted signature</span></p>
            <p><span style="color:#82e8a9">Bright text</span></p>
            <p><img alt="Company logo" width="24" height="24" src="data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7"></p>
            <p>Plain note</p>
            """;
        await using (var db = database.CreateDbContext())
            await db.CertificationEmailTemplates.Where(x => x.TemplateType == CertificationEmailTemplateType.SingleLot)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.HtmlBodyTemplate, templateBody));

        await WithNavigationBrowserAsync("email-appearance", async (browser, origin, username, password, _, output) =>
        {
            await using var context = await browser.NewContextAsync(new()
            {
                ColorScheme = ColorScheme.Light,
                ViewportSize = new() { Width = 1440, Height = 940 }
            });
            var page = await context.NewPageAsync();
            var errors = ObserveErrors(page);
            await LoginForNavigationAsync(page, origin, username, password);
            await page.GotoAsync(origin + "/admin/certification-email-templates");

            var editor = page.Locator(".rich-text-editor .ql-editor");
            var preview = page.Locator("#email-sample-preview .email-html-preview");
            var lightReference = page.GetByRole(AriaRole.Checkbox, new() { Name = "Show light background" });
            var badge = preview.GetByText("Signature name", new() { Exact = true });
            var pasted = preview.GetByText("Pasted signature", new() { Exact = true });
            var bright = preview.GetByText("Bright text", new() { Exact = true });
            var logo = preview.GetByRole(AriaRole.Img, new() { Name = "Company logo" });
            var editBadge = editor.GetByText("Signature name", new() { Exact = true });
            await Expect(badge).ToHaveCSSAsync("color", "rgb(161, 0, 0)");
            await Expect(editBadge).ToHaveCSSAsync("color", "rgb(161, 0, 0)");
            await Expect(lightReference).ToBeHiddenAsync();
            var lightBackground = await BackgroundAsync(preview);
            var originalHtml = await editor.InnerHTMLAsync();

            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
            await Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
            await Expect(preview).ToHaveCSSAsync("background-color", "rgb(25, 35, 46)");
            await Expect(editor).ToHaveCSSAsync("background-color", "rgb(25, 35, 46)");
            await Expect(badge).Not.ToHaveCSSAsync("color", "rgb(161, 0, 0)");
            await Expect(editBadge).ToHaveCSSAsync("color", await badge.EvaluateAsync<string>("node => getComputedStyle(node).color"));
            await Expect(pasted).Not.ToHaveCSSAsync("-webkit-text-fill-color", "rgb(0, 0, 48)");
            await Expect(bright).ToHaveCSSAsync("color", "rgb(130, 232, 169)");
            await Expect(logo).ToHaveCSSAsync("background-color", "rgb(255, 255, 255)");
            Assert.Equal(originalHtml, await editor.InnerHTMLAsync());
            await page.ScreenshotAsync(new() { Path = Path.Combine(output, "email-dark.png") });

            var lightReferenceLabel = page.Locator(".email-light-reference");
            var labelTop = (await lightReferenceLabel.BoundingBoxAsync())!.Y;
            await lightReference.CheckAsync();
            await Expect(badge).ToHaveCSSAsync("color", "rgb(161, 0, 0)");
            await Expect(pasted).ToHaveCSSAsync("-webkit-text-fill-color", "rgb(0, 0, 48)");
            await Expect(preview).ToHaveCSSAsync("background-color", lightBackground);
            await Expect(editor).ToHaveCSSAsync("background-color", "rgb(25, 35, 46)");
            Assert.Equal(labelTop, (await lightReferenceLabel.BoundingBoxAsync())!.Y);
            await lightReference.UncheckAsync();
            await Expect(badge).Not.ToHaveCSSAsync("color", "rgb(161, 0, 0)");
            Assert.Equal(labelTop, (await lightReferenceLabel.BoundingBoxAsync())!.Y);

            await editor.PressAsync("Control+End");
            await page.Keyboard.InsertTextAsync(" edited");
            await Expect(preview).ToContainTextAsync("Plain note edited");
            await page.GetByRole(AriaRole.Button, new() { Name = "Save template" }).ClickAsync();
            await Expect(page.GetByText("Certification email template saved.", new() { Exact = true })).ToBeVisibleAsync();
            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
            await Expect(preview).ToHaveCSSAsync("background-color", lightBackground);
            await Expect(badge).ToHaveCSSAsync("color", "rgb(161, 0, 0)");
            await Expect(editBadge).ToHaveCSSAsync("color", "rgb(161, 0, 0)");

            await using var savedDb = database.CreateDbContext();
            var saved = await savedDb.CertificationEmailTemplates
                .Where(x => x.TemplateType == CertificationEmailTemplateType.SingleLot)
                .Select(x => x.HtmlBodyTemplate).SingleAsync();
            Assert.Contains("Plain note edited", saved);
            Assert.DoesNotContain("-webkit-text-fill-color", saved);
            Assert.DoesNotContain("data-email-surface", saved);
            Assert.Empty(errors);
        });
    }
}
