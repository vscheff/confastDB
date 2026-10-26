namespace Confast.Web.Features.Authorization;

// Fixed endpoint metadata describes the operation. Only middleware can issue its frame.
internal sealed record BusinessHttpBoundary(string Purpose, string Permission, Func<HttpContext, AuthorizationScope> Scope);

internal static class BusinessHttpAuthorization
{
    private static readonly object FrameKey = new();

    internal static AuthorizedOperation? Frame(HttpContext context) => context.Items[FrameKey] as AuthorizedOperation;

    internal static AuthorizedOperation Require(HttpContext context, string purpose, AuthorizationScope scope, params string[] keys)
    {
        var operation = Frame(context) ?? throw new AuthorizationDeniedException(AuthorizationDenial.WrongOperation);
        operation.Require(purpose, scope, keys);
        return operation;
    }

    internal static void UseBusinessAuthorization(this WebApplication app)
    {
        app.Use(async (context, next) =>
        {
            var boundary = context.GetEndpoint()?.Metadata.GetMetadata<BusinessHttpBoundary>();
            if (boundary is null || context.User.Identity?.IsAuthenticated != true
                || context.User.Identity.AuthenticationType == Confast.Web.Features.Inspections.InspectionPrintRenderAuthenticationHandler.SchemeName)
            {
                await next(context);
                return;
            }
            try
            {
                using var operation = await context.RequestServices.GetRequiredService<ApplicationAuthorization>()
                    .BeginHttpAsync(boundary.Purpose, boundary.Scope(context), [boundary.Permission], context.RequestAborted);
                context.Items[FrameKey] = operation;
                context.Response.Headers.CacheControl = "private, no-store";
                await next(context);
            }
            catch (AuthorizationDeniedException exception) when (!context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.Headers.CacheControl = "private, no-store";
                context.Response.Headers.XContentTypeOptions = "nosniff";
                await context.Response.WriteAsync(exception.Message);
            }
            finally { context.Items.Remove(FrameKey); }
        });
    }
}
