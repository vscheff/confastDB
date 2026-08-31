using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace Confast.Web.Features.Identity;

public static class IdentityEndpoints
{
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(
                "/account/session",
                (HttpContext httpContext) =>
                    httpContext.User.Identity?.IsAuthenticated == true
                        ? Results.NoContent()
                        : Results.Text("Unauthorized", statusCode: StatusCodes.Status401Unauthorized))
            .AllowAnonymous();

        endpoints.MapPost(
                "/account/login",
                async Task<IResult> (
                    [FromForm] LoginRequest request,
                    ApplicationSignInManager signInManager,
                    UserManager<ApplicationUser> userManager) =>
                {
                    var user = string.IsNullOrWhiteSpace(request.Username)
                        ? null
                        : await userManager.FindByNameAsync(request.Username.Trim());
                    var result = user is null
                        ? Microsoft.AspNetCore.Identity.SignInResult.Failed
                        : await signInManager.PasswordSignInAsync(
                            user,
                            request.Password ?? string.Empty,
                            request.RememberMe,
                            lockoutOnFailure: true);
                    if (!result.Succeeded)
                    {
                        var reason = result.IsLockedOut ? "locked" : "invalid";
                        return Results.LocalRedirect(
                            $"/login?error={reason}&returnUrl={Uri.EscapeDataString(GetSafeReturnUrl(request.ReturnUrl))}");
                    }

                    return Results.LocalRedirect(GetSafeReturnUrl(request.ReturnUrl));
                })
            .AllowAnonymous();

        endpoints.MapPost(
                "/account/logout",
                async Task<IResult> (
                    [FromForm] LogoutRequest request,
                    SignInManager<ApplicationUser> signInManager) =>
                {
                    await signInManager.SignOutAsync();
                    return Results.LocalRedirect(GetSafeReturnUrl(request.ReturnUrl, "/login"));
                })
            .RequireAuthorization();

        endpoints.MapPost(
                "/account/reset-password",
                async Task<IResult> (
                    [FromForm] ResetPasswordRequest request,
                    UserManager<ApplicationUser> userManager) =>
                {
                    if (string.IsNullOrEmpty(request.Password)
                        || request.Password != request.ConfirmPassword)
                    {
                        return ResetRedirect(request, "Passwords do not match.");
                    }

                    var user = string.IsNullOrWhiteSpace(request.UserId)
                        ? null
                        : await userManager.FindByIdAsync(request.UserId);
                    if (user is null)
                    {
                        return ResetRedirect(request, "The password reset link is invalid or expired.");
                    }

                    var result = await userManager.ResetPasswordAsync(
                        user,
                        request.Token ?? string.Empty,
                        request.Password);
                    if (!result.Succeeded)
                    {
                        return ResetRedirect(
                            request,
                            string.Join(" ", result.Errors.Select(x => x.Description)));
                    }

                    return Results.LocalRedirect("/login?passwordReset=true");
                })
            .AllowAnonymous();

        return endpoints;
    }

    private static IResult ResetRedirect(ResetPasswordRequest request, string error) =>
        Results.LocalRedirect(
            $"/reset-password?userId={Uri.EscapeDataString(request.UserId ?? string.Empty)}" +
            $"&token={Uri.EscapeDataString(request.Token ?? string.Empty)}" +
            $"&error={Uri.EscapeDataString(error)}");

    private static string GetSafeReturnUrl(string? returnUrl, string fallback = "/") =>
        !string.IsNullOrWhiteSpace(returnUrl)
        && Uri.TryCreate(returnUrl, UriKind.Relative, out _)
        && returnUrl.StartsWith('/')
        && (returnUrl.Length == 1 || returnUrl[1] is not ('/' or '\\'))
            ? returnUrl
            : fallback;

    public sealed class LoginRequest
    {
        public string? Username { get; set; }

        public string? Password { get; set; }

        public bool RememberMe { get; set; }

        public string? ReturnUrl { get; set; }
    }

    public sealed class LogoutRequest
    {
        public string? ReturnUrl { get; set; }
    }

    public sealed class ResetPasswordRequest
    {
        public string? UserId { get; set; }

        public string? Token { get; set; }

        public string? Password { get; set; }

        public string? ConfirmPassword { get; set; }
    }
}
