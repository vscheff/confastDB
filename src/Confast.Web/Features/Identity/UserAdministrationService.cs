using System.ComponentModel.DataAnnotations;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using Confast.Web.Features.Authorization;
using Confast.Web.Data;
using Confast.Web.Features.Chat;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Confast.Web.Features.Identity;

public sealed record UserListItem(
    string Id,
    string Username,
    string DisplayName,
    string? JobTitle,
    string Email,
    bool IsActive,
    IReadOnlyList<string> Roles, bool IsProtected = false);

public sealed record UserRoleChoice(string Id, string Name, bool IsEnabled, bool CanChange, string? Reason);
public sealed record UserAdministrationState(AuthorizationVersion Version, IReadOnlySet<string> Permissions, IReadOnlyList<UserRoleChoice> RoleChoices);

public sealed record DigitalCaliperChoice(long Id, string GageNumber, bool IsActive);

public sealed class CreateUserInput
{
    [Required, StringLength(256)]
    public string Username { get; set; } = string.Empty;

    [Required, StringLength(200)]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(200)]
    public string? JobTitle { get; set; }

    public long? CaliperId { get; set; }

    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    public AuthorizationVersion Version { get; set; }
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

    public long? CaliperId { get; set; }

    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    public bool IsActive { get; set; }

    public AuthorizationVersion Version { get; set; }
    public string? ConcurrencyStamp { get; set; }
    public bool IsProtected { get; set; }
    public bool CanManageAccount { get; set; }
    public IReadOnlyList<UserRoleChoice> RoleChoices { get; set; } = [];
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

public sealed class UserAdministrationService(UserManager<ApplicationUser> userManager, AppDbContext db,
    IDbContextFactory<AppDbContext> factory, TimeProvider clock, EffectivePermissionService permissions)
{
    public async Task<UserAdministrationState> GetAdministrationStateAsync(CancellationToken ct = default)
    {
        await using var read = await factory.CreateDbContextAsync(ct);
        await using var tx = await read.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var current = await permissions.EvaluateCurrentInTransactionAsync(read, ct);
        using var op = current.Begin("Users.Read", new("Users"), Permissions.Users.Read);
        var choices = await RoleChoices(read, current, null, [], ct);
        await tx.CommitAsync(ct);
        return new(current.Snapshot.Version, current.Snapshot.Permissions, choices);
    }

    public async Task<IReadOnlyList<UserListItem>> GetUsersAsync(CancellationToken cancellationToken = default)
    {
        await using var read = await factory.CreateDbContextAsync(cancellationToken);
        await using var tx = await read.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var current = await permissions.EvaluateCurrentInTransactionAsync(read, cancellationToken);
        using var op = current.Begin("Users.Read", new("Users"), Permissions.Users.Read);
        var rootId = await read.AuthorizationState.Select(x => x.RootUserId).SingleAsync(cancellationToken);
        var users = await read.Users.AsNoTracking().OrderBy(x => x.DisplayName).ThenBy(x => x.UserName).ToListAsync(cancellationToken);
        var assignments = await (from ur in read.UserRoles join r in read.Roles on ur.RoleId equals r.Id
            select new { ur.UserId, r.Name }).ToListAsync(cancellationToken);
        var grouped = assignments.ToLookup(x => x.UserId, x => x.Name ?? "");
        await tx.CommitAsync(cancellationToken);
        return users.Select(x => new UserListItem(x.Id, x.UserName ?? "", x.DisplayName, x.JobTitle, x.Email ?? "",
            x.IsActive, grouped[x.Id].Order().ToArray(), x.Id == rootId)).ToArray();
    }

    public async Task<EditUserInput?> GetUserForEditAsync(string userId)
    {
        await using var read = await factory.CreateDbContextAsync();
        await using var tx = await read.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        var current = await permissions.EvaluateCurrentInTransactionAsync(read);
        using var op = current.Begin("Users.Read", new("User", userId), Permissions.Users.Read);
        var user = await read.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId);
        if (user is null) return null;
        var assigned = await read.UserRoles.Where(x => x.UserId == userId).Select(x => x.RoleId).ToListAsync();
        var roleNames = await read.Roles.Where(x => assigned.Contains(x.Id)).Select(x => x.Name!).ToListAsync();
        var rootId = await read.AuthorizationState.Select(x => x.RootUserId).SingleAsync();
        var authority = new AdministrativeAuthority(current);
        var canManage = true;
        try { authority.RequireTarget(userId, assigned, rootId); }
        catch (AuthorizationDeniedException) { canManage = false; }
        var protectedRoot = userId == rootId;
        var result = new EditUserInput { Id = userId, Username = protectedRoot ? "" : user.UserName ?? "",
            DisplayName = user.DisplayName, Email = protectedRoot ? "" : user.Email ?? "", JobTitle = user.JobTitle,
            CaliperId = user.CaliperId, IsActive = user.IsActive, Roles = roleNames.ToHashSet(StringComparer.Ordinal),
            Version = current.Snapshot.Version, ConcurrencyStamp = user.ConcurrencyStamp, IsProtected = protectedRoot,
            CanManageAccount = canManage, RoleChoices = await RoleChoices(read, current, userId, assigned, CancellationToken.None) };
        await tx.CommitAsync();
        return result;
    }

    public async Task<IReadOnlyList<DigitalCaliperChoice>> GetDigitalCaliperChoicesAsync(long? includeGageId = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await permissions.EvaluateCurrentAsync(cancellationToken);
        using var op = new AuthorizedOperation("Users.Read", new("Users"), PermissionCatalog.ExpandRequirements([Permissions.Users.Read]), snapshot);
        op.Require(op.Purpose, op.Scope, Permissions.Users.Read);
        await using var read = await factory.CreateDbContextAsync(cancellationToken);
        return await read.Gages.AsNoTracking().Where(x => (x.IsActive || x.Id == includeGageId)
            && EF.Functions.ILike(x.GageType.Name, "Digital Caliper%"))
            .OrderBy(x => x.GageNumber).Select(x => new DigitalCaliperChoice(x.Id, x.GageNumber, x.IsActive)).ToListAsync(cancellationToken);
    }

    public async Task<UserAdministrationResult> CreateUserAsync(CreateUserInput input)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync();
        var current = await LockAndEvaluate(input.Version);
        using var op = current.Begin("Users.Create", new("Users"), Permissions.Users.Create);
        var authority = new AdministrativeAuthority(current);
        var errors = ValidateInput(input);
        if (errors.Length > 0) return UserAdministrationResult.Failure(errors);
        var requested = await ResolveRoles(input.Roles.Append(AppRoles.ReadOnly));
        if (requested.Any(id => id != AppRoles.ReadOnlyId))
        {
            op.Require(op.Purpose, op.Scope, Permissions.Users.ManageRoles);
            foreach (var id in requested.Where(id => id != AppRoles.ReadOnlyId))
                AdministrativeAuthority.Require(authority.CanAssign(id, true), "Initial roles must be enabled peers or lower roles within your authority.");
        }
        var caliper = await ValidCaliper(input.CaliperId, false);
        if (input.CaliperId is not null && caliper is null) return UserAdministrationResult.Failure("Select a valid active digital caliper.");
        var user = new ApplicationUser { UserName = input.Username.Trim(), Email = input.Email.Trim(), EmailConfirmed = true,
            DisplayName = input.DisplayName.Trim(), JobTitle = NullIfBlank(input.JobTitle), CaliperId = caliper, IsActive = true };
        var create = await userManager.CreateAsync(user);
        if (!create.Succeeded) return FromIdentity(create);
        // The baseline trigger enrolled ReadOnly. Use this same Identity store/transaction for extras.
        var extras = await db.Roles.Where(x => requested.Contains(x.Id) && x.Id != AppRoles.ReadOnlyId).Select(x => x.Name!).ToListAsync();
        if (extras.Count > 0)
        { var added = await userManager.AddToRolesAsync(user, extras); if (!added.Succeeded) return FromIdentity(added); }
        await ChatService.EnsurePublicChannelMembershipsAsync(db, user.Id, clock.GetUtcNow().UtcDateTime);
        var details = requested.Select(id => new AuthorizationChangeDetail { Kind = AuthorizationChangeKind.Assignment,
            UserId = user.Id, RoleId = id, WasPresent = false, IsPresent = true }).ToList();
        details.Add(new() { Kind = AuthorizationChangeKind.Account, UserId = user.Id, WasPresent = false, IsPresent = true, ResultingName = user.UserName });
        await AuthorizationAudit.RecordAsync(db, op, input.Version.Epoch, "Create ordinary account", details, clock, CancellationToken.None);
        await tx.CommitAsync();
        return UserAdministrationResult.Success(user.Id);
    }

    public async Task<UserAdministrationResult> UpdateUserAsync(EditUserInput input)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync();
        var current = await LockAndEvaluate(input.Version);
        using var op = current.Begin("Users.Edit", new("User", input.Id), Permissions.Users.Read);
        var authority = new AdministrativeAuthority(current);
        var user = await GetTarget(input.Id, input.ConcurrencyStamp);
        var rootId = await db.AuthorizationState.Select(x => x.RootUserId).SingleAsync();
        AdministrativeAuthority.Require(user.Id != rootId, "Root account settings require the protected security workflow.");
        var errors = ValidateInput(input);
        if (errors.Length > 0) return UserAdministrationResult.Failure(errors);
        var assigned = await db.UserRoles.Where(x => x.UserId == user.Id).Select(x => x.RoleId).ToListAsync();
        var requested = await ResolveRoles(input.Roles);
        var roleChanged = !assigned.ToHashSet().SetEquals(requested);
        var metadataChanged = user.DisplayName != input.DisplayName.Trim() || user.JobTitle != NullIfBlank(input.JobTitle)
            || user.CaliperId != input.CaliperId || user.UserName != input.Username.Trim() || user.Email != input.Email.Trim()
            || user.IsActive != input.IsActive;
        var details = new List<AuthorizationChangeDetail>();
        if (metadataChanged)
        {
            op.Require(op.Purpose, op.Scope, Permissions.Users.Update);
            authority.RequireTarget(user.Id, assigned, rootId);
            var caliper = await ValidCaliper(input.CaliperId, true);
            if (input.CaliperId is not null && caliper is null) return UserAdministrationResult.Failure("Select a valid digital caliper.");
            AddField(details, user.Id, "Username", user.UserName, input.Username.Trim());
            AddField(details, user.Id, "Email", user.Email, input.Email.Trim());
            AddField(details, user.Id, "DisplayName", user.DisplayName, input.DisplayName.Trim());
            AddField(details, user.Id, "JobTitle", user.JobTitle, NullIfBlank(input.JobTitle));
            AddField(details, user.Id, "CaliperId", user.CaliperId?.ToString(), caliper?.ToString());
            AddField(details, user.Id, "IsActive", user.IsActive.ToString(), input.IsActive.ToString());
            user.UserName = input.Username.Trim(); user.Email = input.Email.Trim(); user.EmailConfirmed = true;
            user.DisplayName = input.DisplayName.Trim(); user.JobTitle = NullIfBlank(input.JobTitle);
            user.CaliperId = caliper; user.IsActive = input.IsActive;
        }
        if (roleChanged)
        {
            op.Require(op.Purpose, op.Scope, Permissions.Users.ManageRoles);
            authority.RequireAssignment(user.Id, rootId, assigned, requested);
            foreach (var id in assigned.Except(requested))
                details.Add(new() { Kind = AuthorizationChangeKind.Assignment, UserId = user.Id, RoleId = id, WasPresent = true, IsPresent = false });
            foreach (var id in requested.Except(assigned))
                details.Add(new() { Kind = AuthorizationChangeKind.Assignment, UserId = user.Id, RoleId = id, WasPresent = false, IsPresent = true });
        }
        if (!metadataChanged && !roleChanged) return UserAdministrationResult.Success(user.Id);
        var update = await userManager.UpdateAsync(user);
        if (!update.Succeeded) return FromIdentity(update);
        if (roleChanged)
        {
            var names = await db.Roles.ToDictionaryAsync(x => x.Id, x => x.Name!);
            var removed = await userManager.RemoveFromRolesAsync(user, assigned.Except(requested).Select(id => names[id]));
            if (!removed.Succeeded) return FromIdentity(removed);
            var added = await userManager.AddToRolesAsync(user, requested.Except(assigned).Select(id => names[id]));
            if (!added.Succeeded) return FromIdentity(added);
        }
        // Administrative edits invalidate old sessions/delegations, including metadata-only edits.
        var stamp = await userManager.UpdateSecurityStampAsync(user);
        if (!stamp.Succeeded) return FromIdentity(stamp);
        if (user.IsActive) await ChatService.EnsurePublicChannelMembershipsAsync(db, user.Id, clock.GetUtcNow().UtcDateTime);
        await AuthorizationAudit.RecordAsync(db, op, input.Version.Epoch, "Edit ordinary account", details, clock, CancellationToken.None);
        await tx.CommitAsync();
        return UserAdministrationResult.Success(user.Id);
    }

    public async Task<(string? Token, IReadOnlyList<string> Errors)> GeneratePasswordResetTokenAsync(string userId,
        AuthorizationVersion version, string? concurrencyStamp)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync();
        var current = await LockAndEvaluate(version);
        using var op = current.Begin("Users.ResetPasswords.Issue", new("User", userId), Permissions.Users.ResetPasswords);
        var user = await GetTarget(userId, concurrencyStamp);
        var assigned = await db.UserRoles.Where(x => x.UserId == userId).Select(x => x.RoleId).ToListAsync();
        new AdministrativeAuthority(current).RequireTarget(userId, assigned, await db.AuthorizationState.Select(x => x.RootUserId).SingleAsync());
        var token = await userManager.GeneratePasswordResetTokenAsync(user);
        var now = clock.GetUtcNow().UtcDateTime;
        db.PasswordResetDelegations.Add(new() { IssuerUserId = op.ActorUserId, TargetUserId = userId,
            IssuerSecurityStamp = op.Snapshot.SecurityStamp, InstallationGeneration = version.Generation,
            TokenDigest = Digest(token), IssuedAtUtc = now, ExpiresAtUtc = now.AddHours(24) });
        await db.SaveChangesAsync();
        await AuthorizationAudit.RecordAsync(db, op, version.Epoch, "Issue delegated password reset", [new() {
            Kind = AuthorizationChangeKind.PasswordDelegation, UserId = userId, FieldName = "ResetIssued", IsPresent = true }], clock, CancellationToken.None);
        await tx.CommitAsync();
        return (token, []);
    }

    // Anonymous endpoint; authorization comes exclusively from stored issuance provenance.
    public async Task<UserAdministrationResult> RedeemPasswordResetAsync(string? userId, string? token, string password)
    {
        const string invalid = "The password reset link is invalid or expired.";
        if (string.IsNullOrEmpty(userId) || string.IsNullOrEmpty(token)) return UserAdministrationResult.Failure(invalid);
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync();
        var state = await AuthorizationMutationLock.AcquireCurrentAsync(db, CancellationToken.None);
        var digest = Digest(token);
        var delegation = await db.PasswordResetDelegations.SingleOrDefaultAsync(x => x.TokenDigest == digest && x.TargetUserId == userId);
        var now = clock.GetUtcNow().UtcDateTime;
        if (delegation is null || delegation.ConsumedAtUtc is not null || delegation.ExpiresAtUtc <= now
            || delegation.InstallationGeneration != state.InstallationGeneration || state.RootUserId == userId)
            return UserAdministrationResult.Failure(invalid);
        AuthorizationEvaluation current;
        try
        {
            current = await permissions.EvaluateResetIssuerAsync(db, delegation, CancellationToken.None);
            using var check = current.Begin("Users.ResetPasswords.Redeem", new("User", userId), Permissions.Users.ResetPasswords);
            var assigned = await db.UserRoles.Where(x => x.UserId == userId).Select(x => x.RoleId).ToListAsync();
            new AdministrativeAuthority(current).RequireTarget(userId, assigned, state.RootUserId);
        }
        catch (AuthorizationDeniedException) { return UserAdministrationResult.Failure(invalid); }
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId);
        if (user is null) return UserAdministrationResult.Failure(invalid);
        if (!await userManager.VerifyUserTokenAsync(user, userManager.Options.Tokens.PasswordResetTokenProvider,
            UserManager<ApplicationUser>.ResetPasswordTokenPurpose, token)) return UserAdministrationResult.Failure(invalid);
        var reset = await userManager.ResetPasswordAsync(user, token, password);
        if (!reset.Succeeded) return FromIdentity(reset);
        delegation.ConsumedAtUtc = now;
        await db.SaveChangesAsync();
        using var op = current.Begin("Users.ResetPasswords.Redeem", new("User", userId), Permissions.Users.ResetPasswords);
        await AuthorizationAudit.RecordAsync(db, op, state.GlobalEpoch, "Redeem delegated password reset", [new() {
            Kind = AuthorizationChangeKind.PasswordDelegation, UserId = userId, FieldName = "ResetConsumed", IsPresent = false }], clock, CancellationToken.None);
        await tx.CommitAsync();
        return UserAdministrationResult.Success(userId);
    }

    public async Task<UserAdministrationResult> DeleteUserAsync(string userId, AuthorizationVersion version, string? concurrencyStamp)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync();
        var current = await LockAndEvaluate(version);
        using var op = current.Begin("Users.Delete", new("User", userId), Permissions.Users.Delete);
        var user = await GetTarget(userId, concurrencyStamp);
        var assigned = await db.UserRoles.Where(x => x.UserId == userId).Select(x => x.RoleId).ToListAsync();
        new AdministrativeAuthority(current).RequireTarget(userId, assigned, await db.AuthorizationState.Select(x => x.RootUserId).SingleAsync());
        try
        {
            var deleted = await userManager.DeleteAsync(user);
            if (!deleted.Succeeded) return FromIdentity(deleted);
            await AuthorizationAudit.RecordAsync(db, op, version.Epoch, "Delete ordinary account", [new() {
                Kind = AuthorizationChangeKind.Account, UserId = userId, WasPresent = true, IsPresent = false, PreviousName = user.UserName }], clock, CancellationToken.None);
            await tx.CommitAsync();
            return UserAdministrationResult.Success(userId);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        { return UserAdministrationResult.Failure("This account is referenced by historical records. Deactivate it instead."); }
    }

    private async Task<AuthorizationEvaluation> LockAndEvaluate(AuthorizationVersion version)
    { await AuthorizationMutationLock.AcquireAsync(db, version, CancellationToken.None); return await permissions.EvaluateCurrentInTransactionAsync(db); }
    private async Task<ApplicationUser> GetTarget(string id, string? stamp)
    {
        var user = await db.Users.SingleOrDefaultAsync(x => x.Id == id) ?? throw new InvalidOperationException("The user no longer exists.");
        if (string.IsNullOrEmpty(stamp) || stamp != user.ConcurrencyStamp) throw new DbUpdateConcurrencyException("The account changed. Reload the editor.");
        return user;
    }
    private async Task<HashSet<string>> ResolveRoles(IEnumerable<string> names)
    {
        var requested = names.ToHashSet(StringComparer.Ordinal);
        var values = await db.Roles.Where(x => requested.Contains(x.Name!)).ToListAsync();
        if (values.Count != requested.Count) throw new ArgumentException("A submitted role no longer exists. Reload the editor.");
        return values.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
    }
    private async Task<long?> ValidCaliper(long? id, bool inactive) => id is null ? null : await db.Gages.Where(x => x.Id == id
        && (inactive || x.IsActive) && EF.Functions.ILike(x.GageType.Name, "Digital Caliper%"))
        .Select(x => (long?)x.Id).SingleOrDefaultAsync();
    private static async Task<IReadOnlyList<UserRoleChoice>> RoleChoices(AppDbContext read, AuthorizationEvaluation current,
        string? targetId, IEnumerable<string> assigned, CancellationToken ct)
    {
        var values = await read.Roles.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);
        var old = assigned.ToHashSet(); var authority = new AdministrativeAuthority(current);
        var protectedTarget = targetId == current.Snapshot.ActorUserId || old.Contains(AppRoles.RootId);
        return values.Select(x => {
            var can = !protectedTarget && current.Snapshot.Has(Permissions.Users.ManageRoles) && authority.CanAssign(x.Id, !old.Contains(x.Id));
            var reason = x.SystemKind != SystemRoleKind.Ordinary ? "Protected membership" : !x.IsEnabled && !old.Contains(x.Id)
                ? "Disabled; cannot add" : !can ? "Outside assignment authority" : null;
            return new UserRoleChoice(x.Id, x.Name ?? "", x.IsEnabled, can, reason);
        }).ToArray();
    }
    private static void AddField(List<AuthorizationChangeDetail> details, string id, string field, string? before, string? after)
    { if (before != after) details.Add(new() { Kind = AuthorizationChangeKind.Account, UserId = id, FieldName = field, PreviousValue = before, ResultingValue = after }); }
    private static string[] ValidateInput(object input)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(input, new ValidationContext(input), results, validateAllProperties: true);
        return results.Select(x => x.ErrorMessage ?? "Invalid account input.").ToArray();
    }
    private static string Digest(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string? NullIfBlank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    private static UserAdministrationResult FromIdentity(IdentityResult result) => new(false, null, result.Errors.Select(x => x.Description).ToArray());
}
