namespace Confast.Web.Features.Inspections;

internal static class InspectionRenderRequestPolicy
{
    internal static bool IsAllowed(Uri document, Uri request, string resourceType, string method)
    {
        if (method != "GET" || request.GetLeftPart(UriPartial.Authority) != document.GetLeftPart(UriPartial.Authority)) return false;
        if (resourceType == "document") return request == document;
        if (resourceType is not ("stylesheet" or "font" or "image" or "script") || request.Query.Length != 0) return false;
        var path = request.AbsolutePath;
        var rootAsset = path.LastIndexOf('/') == 0;
        return (rootAsset || path.StartsWith("/images/", StringComparison.Ordinal) || path.StartsWith("/fonts/", StringComparison.Ordinal)
                || path.StartsWith("/_framework/", StringComparison.Ordinal) || path.StartsWith("/lib/", StringComparison.Ordinal))
            && Path.GetExtension(path).ToLowerInvariant() is ".css" or ".js" or ".png" or ".ico" or ".woff" or ".woff2" or ".ttf";
    }
}
