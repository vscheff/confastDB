using Confast.Web.Features.Inspections;
using Microsoft.AspNetCore.Identity;

namespace Confast.Web.Features.Identity;

public interface ICurrentEmailSender
{
    Task<EmailSenderIdentity> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class CurrentEmailSender(
    ICurrentUser currentUser,
    UserManager<ApplicationUser> userManager) : ICurrentEmailSender
{
    public async Task<EmailSenderIdentity> GetAsync(CancellationToken cancellationToken = default)
    {
        var userId = await currentUser.GetUserIdAsync();
        var user = string.IsNullOrWhiteSpace(userId) ? null : await userManager.FindByIdAsync(userId);
        if (user is null || !user.IsActive)
        {
            throw new EmailDeliveryException("The logged-in user could not be identified. Reload and try again.");
        }
        if (string.IsNullOrWhiteSpace(user.Email))
        {
            throw new EmailDeliveryException("Your user account needs an email address before certification packages can be sent.");
        }
        return new EmailSenderIdentity(
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName ?? user.Email : user.DisplayName,
            user.Email,
            user.JobTitle);
    }
}
