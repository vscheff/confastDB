using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Confast.Web.Features.Identity;

public sealed record UserListItem(
    string Id,
    string Username,
    string DisplayName,
    string? JobTitle,
    string Email,
    bool IsActive,
    IReadOnlyList<string> Roles);

public sealed class CreateUserInput
{
    [Required, StringLength(256)]
    public string Username { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(200)]
    public string? JobTitle { get; set; }

    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    public HashSet<string> Roles { get; set; } = [];
}

public sealed class EditUserInput
{
    [Required]
    public string Id { get; set; } = string.Empty;

    [Required, StringLength(256)]
    public string Username { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(200)]
    public string? JobTitle { get; set; }

    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public HashSet<string> Roles { get; set; } = [];
}

public sealed record UserAdministrationResult(
    bool Succeeded,
    string? UserId,
    IReadOnlyList<string> Errors)
{
    public static UserAdministrationResult Success(string userId) =>
        new(true, userId, []);

    public static UserAdministrationResult Failure(params string[] errors) =>
        new(false, null, errors);
}

public sealed class UserAdministrationService(
    UserManager<ApplicationUser> userManager)
{
    public async Task<IReadOnlyList<UserListItem>> GetUsersAsync(
        CancellationToken cancellationToken = default)
    {
        var users = await userManager.Users
            .OrderBy(x => x.DisplayName)
            .ThenBy(x => x.UserName)
            .ToListAsync(cancellationToken);
        var result = new List<UserListItem>(users.Count);
        foreach (var user in users)
        {
            result.Add(new UserListItem(
                user.Id,
                user.UserName ?? string.Empty,
                user.DisplayName,
                user.JobTitle,
                user.Email ?? string.Empty,
                user.IsActive,
                (await userManager.GetRolesAsync(user)).Order().ToArray()));
        }

        return result;
    }

    public async Task<IReadOnlyList<string>> GetQualityUserDisplayNamesAsync()
    {
        var users = await userManager.GetUsersInRoleAsync(AppRoles.Quality);
        return users
            .OrderBy(x => x.DisplayName)
            .ThenBy(x => x.UserName)
            .Select(x => x.DisplayName)
            .ToArray();
    }

    public async Task<EditUserInput?> GetUserForEditAsync(string userId)
    {
        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return null;
        }

        return new EditUserInput
        {
            Id = user.Id,
            Username = user.UserName ?? string.Empty,
            DisplayName = user.DisplayName,
            JobTitle = user.JobTitle,
            Email = user.Email ?? string.Empty,
            IsActive = user.IsActive,
            Roles = new HashSet<string>(await userManager.GetRolesAsync(user), StringComparer.Ordinal)
        };
    }

    public async Task<UserAdministrationResult> CreateUserAsync(CreateUserInput input)
    {
        var invalidRoles = GetInvalidRoles(input.Roles);
        if (invalidRoles.Length > 0)
        {
            return UserAdministrationResult.Failure($"Unknown roles: {string.Join(", ", invalidRoles)}.");
        }

        var username = input.Username.Trim();
        var email = input.Email.Trim();
        var user = new ApplicationUser
        {
            UserName = username,
            Email = email,
            EmailConfirmed = true,
            DisplayName = input.DisplayName.Trim(),
            JobTitle = NullIfWhiteSpace(input.JobTitle),
            IsActive = true
        };
        var createResult = await userManager.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            return FromIdentityResult(createResult);
        }

        if (input.Roles.Count > 0)
        {
            var roleResult = await userManager.AddToRolesAsync(user, input.Roles);
            if (!roleResult.Succeeded)
            {
                await userManager.DeleteAsync(user);
                return FromIdentityResult(roleResult);
            }
        }

        return UserAdministrationResult.Success(user.Id);
    }

    public async Task<UserAdministrationResult> UpdateUserAsync(EditUserInput input)
    {
        var invalidRoles = GetInvalidRoles(input.Roles);
        if (invalidRoles.Length > 0)
        {
            return UserAdministrationResult.Failure($"Unknown roles: {string.Join(", ", invalidRoles)}.");
        }

        var user = await userManager.FindByIdAsync(input.Id);
        if (user is null)
        {
            return UserAdministrationResult.Failure("The user no longer exists.");
        }

        var existingRoles = new HashSet<string>(await userManager.GetRolesAsync(user), StringComparer.Ordinal);
        var removesLastActiveAdministrator = user.IsActive
            && existingRoles.Contains(AppRoles.Administrator)
            && (!input.IsActive || !input.Roles.Contains(AppRoles.Administrator));
        if (removesLastActiveAdministrator && await IsLastActiveAdministratorAsync(user.Id))
        {
            return UserAdministrationResult.Failure(
                "The last active administrator cannot be deactivated or removed from the Administrator role.");
        }

        var username = input.Username.Trim();
        var email = input.Email.Trim();
        user.DisplayName = input.DisplayName.Trim();
        user.JobTitle = NullIfWhiteSpace(input.JobTitle);
        user.IsActive = input.IsActive;
        user.Email = email;
        user.UserName = username;
        user.EmailConfirmed = true;

        var updateResult = await userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            return FromIdentityResult(updateResult);
        }

        var rolesToRemove = existingRoles.Except(input.Roles, StringComparer.Ordinal).ToArray();
        if (rolesToRemove.Length > 0)
        {
            var removeResult = await userManager.RemoveFromRolesAsync(user, rolesToRemove);
            if (!removeResult.Succeeded)
            {
                return FromIdentityResult(removeResult);
            }
        }

        var rolesToAdd = input.Roles.Except(existingRoles, StringComparer.Ordinal).ToArray();
        if (rolesToAdd.Length > 0)
        {
            var addResult = await userManager.AddToRolesAsync(user, rolesToAdd);
            if (!addResult.Succeeded)
            {
                return FromIdentityResult(addResult);
            }
        }

        await userManager.UpdateSecurityStampAsync(user);
        return UserAdministrationResult.Success(user.Id);
    }

    public async Task<(string? Token, IReadOnlyList<string> Errors)> GeneratePasswordResetTokenAsync(
        string userId)
    {
        var user = await userManager.FindByIdAsync(userId);
        return user is null
            ? (null, ["The user no longer exists."])
            : (await userManager.GeneratePasswordResetTokenAsync(user), []);
    }

    public async Task<UserAdministrationResult> DeleteUserAsync(
        string userId,
        string? actingUserId)
    {
        if (string.IsNullOrWhiteSpace(actingUserId))
        {
            return UserAdministrationResult.Failure(
                "The current administrator could not be identified. Reload the page and try again.");
        }

        if (userId == actingUserId)
        {
            return UserAdministrationResult.Failure("You cannot delete your own account.");
        }

        var user = await userManager.FindByIdAsync(userId);
        if (user is null)
        {
            return UserAdministrationResult.Failure("The user no longer exists.");
        }

        if (user.IsActive
            && await userManager.IsInRoleAsync(user, AppRoles.Administrator)
            && await IsLastActiveAdministratorAsync(user.Id))
        {
            return UserAdministrationResult.Failure(
                "The last active administrator cannot be deleted.");
        }

        try
        {
            var deleteResult = await userManager.DeleteAsync(user);
            return deleteResult.Succeeded
                ? UserAdministrationResult.Success(user.Id)
                : FromIdentityResult(deleteResult);
        }
        catch (DbUpdateException)
        {
            return UserAdministrationResult.Failure(
                "This user cannot be deleted because other records reference the account. Deactivate it instead.");
        }
    }

    private async Task<bool> IsLastActiveAdministratorAsync(string excludedUserId)
    {
        var administrators = await userManager.GetUsersInRoleAsync(AppRoles.Administrator);
        return !administrators.Any(x => x.Id != excludedUserId && x.IsActive);
    }

    private static string[] GetInvalidRoles(IEnumerable<string> roles) =>
        roles.Except(AppRoles.All, StringComparer.Ordinal).Order().ToArray();

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static UserAdministrationResult FromIdentityResult(IdentityResult result) =>
        new(false, null, result.Errors.Select(x => x.Description).ToArray());
}
