namespace Confast.Web.Features.Identity;

public static class UserEditRecovery
{
    public static EditUserInput Copy(EditUserInput value) => new()
    {
        Id = value.Id, Username = value.Username, DisplayName = value.DisplayName, JobTitle = value.JobTitle,
        CaliperId = value.CaliperId, Email = value.Email, IsActive = value.IsActive, Version = value.Version,
        ConcurrencyStamp = value.ConcurrencyStamp, IsProtected = value.IsProtected, CanManageAccount = value.CanManageAccount,
        RoleChoices = value.RoleChoices, Roles = value.Roles.ToHashSet(StringComparer.Ordinal)
    };
    public static EditUserInput Reapply(EditUserInput original, EditUserInput proposed, EditUserInput latest)
    {
        if (original.Id != proposed.Id || original.Id != latest.Id) throw new ArgumentException("The account changed.");
        var result = Copy(latest);
        if (original.Username != proposed.Username) result.Username = proposed.Username;
        if (original.DisplayName != proposed.DisplayName) result.DisplayName = proposed.DisplayName;
        if (original.JobTitle != proposed.JobTitle) result.JobTitle = proposed.JobTitle;
        if (original.CaliperId != proposed.CaliperId) result.CaliperId = proposed.CaliperId;
        if (original.Email != proposed.Email) result.Email = proposed.Email;
        if (original.IsActive != proposed.IsActive) result.IsActive = proposed.IsActive;
        result.Roles.ExceptWith(original.Roles.Except(proposed.Roles));
        result.Roles.UnionWith(proposed.Roles.Except(original.Roles));
        return result;
    }
}
