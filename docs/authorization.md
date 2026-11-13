# Authorization

ASP.NET Core Identity handles sign-in, credentials, and sessions. The application
evaluates permissions from PostgreSQL for each protected operation. Navigation
and editor controls reflect the permissions available to the user. The service
still checks authority, record state, and any applicable ownership or membership
rules when an action is submitted.

## Roles and effective permissions

- `ReadOnly` is the initial selection for a new account. An authorized editor
  can omit or remove it. An account with no roles has no ordinary grants.
- Enabled roles contribute their direct grants and grants from enabled parents.
  A child inherits from its parents. Disabled branches stop contributing
  effective grants but retain their stored grants and relationships.
- A permission can require other permissions. Granting the key does not grant
  those prerequisites. The Roles matrix shows direct grants, inherited grants,
  their sources, and whether the prerequisites are satisfied.
- The designated `Root Administrator Prime` account receives all recognized
  permissions. Root has a protected designation and no editable grants or
  inheritance. Business rules and private conversation membership still apply.
  Ordinary `Administrator` membership is managed independently.

[PermissionCatalog.cs](../src/Confast.Web/Features/Authorization/PermissionCatalog.cs)
defines the keys and prerequisites.
[AuthorizationSeeds.cs](../src/Confast.Web/Features/Authorization/AuthorizationSeeds.cs)
defines the initial grants and inheritance. Use the Roles matrix to inspect a
database's current grants.

## Managing users and roles

**Administration → Users** supports account creation, activation, role assignment,
and password-reset links. Administrative access depends on both the permission for
the operation and the editor's authority over the target. Ordinary administrators
can assign enabled peer or lower roles within their authority, plus ReadOnly
when its stored grants are within their authority. They can edit strictly lower
ordinary roles outside their own assignments. Stored grants on disabled roles
count when checking delegated authority. The ordinary account editor blocks
self-administration and changes to the protected Root account.

Use **Review Current Access** and **Preview Assignment Changes** on Users to see
the effect of direct assignments. In **Administration → Roles**, use **Preview
Changes** to inspect grant, inheritance, and assigned-user impact before saving.
Deletion requires a current impact preview and explicit confirmation. Assigned
roles cannot be deleted. If a concurrent edit makes a draft stale, **Reload And
Reapply** carries local changes onto the latest saved state for review.

An administrative password-reset link is single-use. Redemption checks the
target account, the installation, and the issuer's current authority. Deactivation
invalidates the affected user's sessions. Assignment changes take effect on the
next protected operation without forcing a new login.

## Explicit installation provisioning

A new database starts in `PendingRoot` (`readiness = 0`). Sign-in is available
for the bootstrap account. Permission-protected workflows require `Ready`
(`readiness = 1`) and exactly one active, credentialed account matching the Root
designation and membership. Startup creates the initial ordinary Administrator
when configured for an empty database. Root designation is an offline owner
operation.

1. Verify the intended account can sign in and record its immutable Identity ID
   from `public.identity_users`. Identify the Development browser-test account
   separately when present.
2. Stop the application. Connect to the intended database in `psql` as the schema
   owner with `ON_ERROR_STOP` enabled.
   Inspect the installation state and record the current epoch.

   ```sql
   SELECT readiness, root_user_id, global_epoch, installation_generation, catalog_version
   FROM public.authorization_state
   WHERE id = 1;
   ```

3. Confirm `readiness = 0` and `root_user_id IS NULL`. Replace the account IDs,
   epoch, and reason below with the reviewed values. Pass SQL `NULL` as the last
   argument only if this database has no BrowserTestUser account.

   ```sql
   BEGIN;
   SELECT public.provision_confast_root(
       'INTENTIONAL-ACCOUNT-ID',
       OBSERVED_EPOCH,
       'Initial Root designation',
       'EXCLUDED-BROWSER-TEST-ACCOUNT-ID');
   COMMIT;
   ```

4. Confirm Ready state and that the sole Root role membership matches the
   designation. Restart the app and verify that the designated account can sign
   in and open **Administration → Roles**.

Provisioning atomically records the designation, membership, new epoch, and
operator history. For a Ready installation with lost Root credentials, use
[offline recovery](deployment-readiness.md#backup-restore-and-offline-recovery).

## Server-side enforcement

- Begin each protected request or service operation with a trusted principal
  and the required catalog keys. Keep its purpose and resource scope bound to
  that operation. UI snapshots and previews are advisory.
- The evaluator validates the account, security stamp, installation state,
  catalog version, and authorization epoch against the primary database.
  Cached grants are reused only for the validated actor and version.
- Role and user security edits validate the current graph under the authorization
  lock, check the editor's expected version, and commit with audit history.
  Process-local refresh notifications update connected UI after commit. Other
  instances observe changes on their next protected operation.
- Inspector selection uses the ID of an active account with `Inspections.Create`.
  The inspection retains the inspector's display name and selected identity for
  historical attribution, along with its own criteria revision.
- Scheduled chat delivery rechecks the sender's current authority, membership,
  destination, and installation before sending. The worker commits the message
  and delivery receipt together. Delivery has a seven-day window and up to five
  attempts for transient failures. Failed drafts remain available to the sender
  with current chat access and destination membership.
- **Moderation History** shows recent channel moderation records to current
  members with `Chat.Access` and `Chat.DeleteOthersMessages`. It displays
  the moderator, sender, time, and reason.
- An inspection print capability permits only its exact print resource and
  options for two minutes.

The [source inventory script](../scripts/security/authorization-inventory.py)
identifies candidate guard locations for manual review. With Python 3 installed,
run `python scripts/security/authorization-inventory.py` from the repository
root. It writes service-method and Razor route CSVs under
`artifacts/authorization-inventory/`.
