# Phase 3C Delegated Administration and Inspector Authorization

Implemented October 9, 2026. This is the implementation record for Phase 3C, following the approved [design](authorization-design.md), the [boundary audit](authorization-audit.md), and the historical [Phase 3B foundation](authorization-foundation.md). The 164-key catalog, initial grant manifest, protected role IDs, additive evaluator, versioned cache, logical-operation contexts, PostgreSQL security locks and offline Root provisioning are preserved.

## Active boundaries and readiness

`/admin/users` now requires `Users.Read`; `/admin/roles` requires `Roles.Read`. The corresponding navigation links use those policies. Designated Root can access both without an Administrator cookie role. No Administrator claim is manufactured and unrelated legacy routes retain their existing gates.

All new permission-controlled operations fail closed while PendingRoot, including account administration, reset issuance/redemption, new inspection creation and the receipt operations listed below. Existing legacy inspection reads/measurement saves with unchanged attribution, and unmigrated business operations, retain their existing authorization. Deployment must explicitly provision Root through the existing owner-controlled offline procedure before using the migrated boundaries. Neither startup, the migration, bootstrap Administrator, nor BrowserTestUser chooses Root.

Ordinary account administration cannot modify or delete the designated Root account, issue a Root reset, redeem a predesignation Root token, or transfer its roles/designation. The editor shows the protected account with disabled controls and omits its editable security identity. Root can manage ordinary users and roles; normal business validity, historical references, and assigned-role deletion restrictions still apply.

## Delegated role administration

Services use the existing `RoleGraph`. The enabled directly assigned ordinary roles are the actor's current management anchors. Disabled directly assigned roles remain protected from self-editing and participate in stored-envelope and per-anchor reachability validation.

- `Users.ManageRoles`: each added or removed ordinary role must be an exact direct-role peer or an actual lower role, and its full stored envelope must fit within the actor's current effective permissions. Adds require an enabled role. Unchanged higher/unrelated assignments remain intact; omission of an unauthorized assignment is rejected. ReadOnly is permanent, Root cannot be assigned/removed, and administrative self-assignment is denied.
- `Roles.Create`: creates a fresh, empty, unassigned ordinary role and atomically attaches `Selected Direct Anchor → New Role`. Ordinary actors must choose an enabled direct anchor. Root can also create an independent role or use an enabled ordinary anchor. Creation does not confer `ManagePermissions` or `ManageInheritance` implicitly.
- `Roles.Update`: edits eligible lower-role name/description. Changing enabled state also requires `Roles.ManagePermissions` and the same final-state checks as other security edits. Classification, system keys and protected designation are outside this input.
- `Roles.Delete`: rejects any direct user assignment, including inactive users. It removes the role, its grants and incident edges atomically, without reparenting remaining roles.
- `Roles.ManagePermissions`: changes only the target's direct grants. Additions must be recognized ordinary keys already possessed by the actor. Existing unavailable grants can be removed when the resulting stored envelope is safe. Protected role grants cannot be edited.
- `Roles.ManageInheritance`: replaces the target's direct parents. Multiple parents are supported; duplicates, self-edges, cycles and protected relationships are rejected. Added parents must be eligible strictly lower ordinary roles whose stored envelope fits the actor's current permissions.

Ordinary edits require a strictly lower target and cannot modify any directly held role, even a disabled one. Root can edit all ordinary roles. Lower inherited permission sources are editable: the earlier blanket source-edit prohibition is not reinstated.

Every proposed resulting graph must remain structurally valid. Validation limits the target's resulting stored envelope to the actor's pre-edit effective permissions, rejects unauthorized new grants propagating to affected descendants, and prevents any increase in the actor's effective permissions. Management reach is compared **independently for every directly held ordinary role**, including disabled roles, against pre-existing role IDs. Checking only the union of permissions/reach is insufficient. A fresh empty subordinate created through the narrow Create endpoint is the sole existing-role reachability exception. Existing unrelated authority in a descendant is not silently stripped; the edit must introduce no unauthorized grants there.

The Roles page is deliberately small: list/details, direct anchor, metadata/enabled state, one-key-per-line direct grants, parent checkboxes, and guarded deletion. It is not the Phase 3F permission matrix or impact preview.

## Transactions and account security

Each administrative mutation clears previously tracked Identity entities, starts one transaction on the actual scoped Identity `AppDbContext`, acquires `AuthorizationMutationLock` **before** authorization-dependent reads, then resolves the current trusted actor/session and graph. Stale installation generation/catalog/epoch or target concurrency stamp rejects the edit. RoleManager/UserManager writes, assignments, public-channel enrollment where applicable, audit details and final epoch all commit together. Failed Identity validation, audit failure, graph failure and concurrency failure roll back the operation.

The epoch is a version, not an edit counter: triggers can advance it more than once per logical edit. Forms reload the committed state after successful operations. No public caller-supplied actor, alternate permission engine or bypass flag was added.

User service reads require `Users.Read`. Creation requires `Users.Create` and receives ReadOnly automatically; additional roles independently require `Users.ManageRoles`. The existing form remains passwordless. Account metadata/security changes require `Users.Update`, role changes require `Users.ManageRoles`, deletion requires `Users.Delete`, and reset issuance requires `Users.ResetPasswords`, together with catalog prerequisites.

For account management, an ordinary actor must cover the target's complete stored envelope, including disabled roles and inactive targets, and every ordinary target role must be an actual peer/lower role. Matching permission sets alone do not establish hierarchy. Username/email/Active/credential changes cannot take over unrelated or higher accounts. Administrative self-security and self-role changes are denied. The existing dedicated self-profile feature is separate.

The first implementation conservatively applies the same account-target authority to display-name, job-title and caliper metadata as to security fields. Every actual administrative edit rotates the target security stamp, including metadata-only and assignment-only edits. This invalidates old target sessions and issuer-stamp delegations; it is an explicit tightening of the old editor's behavior.

Security history records account field changes, role metadata, grants, assignments, complete edge additions/removals, reset issuance/consumption and resulting committed epochs. Raw tokens, password hashes, passwords and security-stamp values are absent from audit history. Account IDs in history/delegations are historical provenance rather than cascading user relationships.

## Administrative password-reset delegation

Issuance rechecks the trusted actor's current `Users.ResetPasswords` and target-account authority under the shared security lock. It generates the normal Identity token and stores its SHA-256 digest, trusted issuer/target IDs, issuer security-stamp snapshot, installation generation, issuance/24-hour expiry and nullable consumption time. The link carries the normal Identity token; the persisted delegation does not store that raw token.

Anonymous redemption locks current security state before resolving delegation. It requires matching digest/target, current generation, unexpired/unconsumed state, a non-Root target, the normal Identity token, and a currently Active issuer whose stamp matches issuance. The same evaluator and hierarchy/envelope checks revalidate the issuer's **current** reset permission and the target's **current** stored authority. Issuer revocation/deactivation/stamp change, target privilege elevation, installation invalidity and Root designation invalidate outstanding links.

Credential update, stamp change, one-use consumption, audit and epoch commit in one transaction. Concurrent redemption can succeed once; invalid links and failed password validation do not consume the delegation. PostgreSQL guards provenance immutability and one-time consumption, takes the common security lock and updates the common epoch. These database structural guards do not replace the application's human delegation checks.

**Compatibility cutover:** pre-3C untracked administrative reset links now fail; an authorized administrator must reissue them. PendingRoot also denies redemption. No password/stamp is changed by the migration. The repository had no public Forgot Password issuer or separate supported self-service reset flow to preserve. Login/logout and dedicated profile behavior remain. No public reset recovery or protected Root self-security endpoint was invented; complete operator recovery remains deferred.

## Inspector eligibility and historical attribution

`InspectorEligibilityService` uses the existing evaluator/graph and batches Active-user/assignment queries rather than evaluating each candidate separately. A candidate qualifies through current effective `Inspections.Create`, whether direct, inherited, combined across roles, or designated Root. Quality-name membership is no longer the selector's criterion. Bounded candidate lookup requires the acting user's creation authority or update authority, including Update's prerequisite.

New creation takes nullable `InspectorUserId`. The server resolves the selected Active, currently qualified account, stores that stable ID and snapshots its display name into the existing `Inspector` field. A typed name alone cannot select another person. Labels include username so duplicate display names remain distinguishable. The existing optional no-inspector case is retained. Digital-caliper selection resolves through the selected user ID, not name matching.

`InspectionService.CreateInspectionAsync` enforces `Inspections.Create` alone. Its published-part lookup is a bounded creation input, not a general Part read API. Create does not imply Read in the catalog. Since every human permanently holds ReadOnly and its approved seed includes `Inspections.Read`, current humans still receive baseline read permission; the tests distinguish that provenance from an implicit Create prerequisite.

Receipt Begin checks `Receiving.BeginInspection` **and** `Inspections.Create`, creates a purpose/scope-bound operation for the receipt line and passes it to the internal inspection core. That composed operation evaluates the actor once. Choosing a qualified inspector never grants permission to the acting user. Receipt list/candidate reads require `Receiving.Read`, Bump requires `Receiving.BumpQuantity`, and Reverse requires `Receiving.ReverseAllocation`. Other container/tracking receive, unreceive and quantity-correction paths retain their legacy gates until Phase 3D.

Unchanged historical inspector ID/name is accepted during ordinary legacy inspection saving, including after deactivation/revocation and while PendingRoot. Changing attribution requires current `Inspections.Update` and a currently eligible new selection. Name-only forging is denied; successful save updates the in-memory attribution/version for subsequent autosaves. Duplicate/flip operations preserve ID and historical name as lineage, rather than silently substituting current account names. Existing gage/result history is not rewritten by attribution changes.

The nullable inspector FK uses **RESTRICT**, so linked inspection history prevents hard account deletion; the editor returns a deactivate-instead message. Deactivation and renaming preserve stored inspection IDs/names. Unlinked old names remain available as historical options rather than blocking measurement saves.

## Additive migration

`20261009194655_AddDelegatedAdministrationAndInspectorIdentity` and its designer add:

- Nullable `inspections.inspector_user_id`, an index and the restrictive user FK.
- `password_reset_delegations`, unique token digest and target/expiry indexes, expiry constraints, immutable-provenance/consumption guards, common lock/epoch triggers and truncate protection.
- Nullable structured field/before/after values on authorization change details.

Backfill matches only a **unique trimmed display name** across the entire existing user inventory. Duplicate or unknown names remain NULL. Case-sensitive matching is deliberate; no username/order guess is made. Every original inspector string, including whitespace, remains untouched. The migration does not modify account credentials, roles/assignments, Root designation, lot history, chat or other business records.

Real migration down/up tests confirmed unique matching, ambiguous/unknown NULL attribution and original-string preservation on the configured disposable `confast_test` database. No development or production migration was run. Before deployment, follow the foundation's migration/runtime privilege separation and explicit Root provisioning procedure; rotate installation generation on restore as already documented there.

## Implementation file inventory

Paths below are relative to the repository root; the foundation's catalog/seed files and historical foundation migration SQL are unchanged.

| Area | Added | Modified |
|---|---|---|
| Authorization | `Features/Authorization/AdministrativeAuthority.cs`, `RoleAdministrationService.cs`, `InspectorEligibilityService.cs` | `EffectivePermissionService.cs`, `RoleGraph.cs`, `AuthorizationContext.cs`, `AuthorizationMutationLock.cs`, `AuthorizationAudit.cs`, `AuthorizationEntities.cs`, `AuthorizationMapping.cs`, `ApplicationAuthorization.cs` |
| Identity | `Features/Identity/PasswordResetDelegation.cs` | `UserAdministrationService.cs`, `IdentityEndpoints.cs` |
| Inspection / receiving | — | `Features/Inspections/Inspection.cs`, `InspectionModels.cs`, `InspectionService.cs`; `Features/ContainerTracking/TrackingModels.cs`, `ReceivedPartsService.cs` |
| UI | `Components/Pages/Users/Roles.razor` | Users/Index, Inspections/Create, Inspections/Edit, ReceivedParts/Index, RoleCheckboxes, MainLayout and `_Imports.razor` |
| Database | Migration and designer listed above | `Data/AppDbContext.cs`, migration model snapshot |
| Tests | `AuthorizationTestSession.cs`, `DelegatedAdministrationTests.cs`, `InspectorAuthorizationTests.cs` | AuthorizationEndpointTests, AuthorizationFoundationTests, IdentityTests, ReceivedPartsServiceTests, InspectionServiceTests, InspectionCriteriaServiceTests, InspectionCertificationTests and PostgresTestDatabase |
| Documentation | This implementation record | README and the historical foundation's status note |

Production entries in this table live under `src/Confast.Web/`; tests live under `tests/Confast.Web.Tests/`. Existing dirty chat/UI work, earlier foundation changes, approved design/audit and catalog/seed definitions were preserved.

## Verified results and limits

- Release solution build: **0 warnings, 0 errors**, including the final UI denial-handling/navigation fixes.
- EF pending-model check: **no pending changes**. Two pre-existing required-relationship/query-filter warnings concern chat entities, separate from compilation.
- PostgreSQL regression suite: **621 passed, 0 failed, 0 skipped**, 3 minutes 8 seconds, versus 537 at the end of Phase 3B. One runner used the configured disposable database; no development database substitution.
- Tests cover ordinary/Root/Pending authority, peer/lower versus equal-envelope unrelated roles, disabled/dormant grants, independent multiple-anchor reach, safe inherited-source edits, intermediate-role escalation, cycles, protected metadata, assigned-role deletion, stale versions/stamps, waiting-lock reauthorization, concurrent edits, audit rollback, real scoped Identity writes, actual HTTP policies and anonymous reset redemption, forgery/revocation/expiry/replay/concurrent resets, inspector qualification/query bounds, stable IDs/calipers, historical saves and real migration backfill.
- Authenticated browser verification used the **configured Development BrowserTestUser credentials through the actual login UI**, against an isolated local server backed only by `confast_test`. A separate explicitly provisioned fixture owner was Root; BrowserTestUser was not Root. Verified protected Root editor, ReadOnly-only passwordless creation, peer assignment and operation-specific reset denial, empty anchored role creation and safe grant persistence, duplicate-name inspector selection, account-specific caliper resolution, measurement autosave and persistence after reload. Screenshots are saved outside the repository. No real employee account or production data was used.
- No browser password-changing action was performed. Password reset is verified through PostgreSQL Identity/service and actual HTTP/antiforgery endpoint tests. Physical iPad/touch, multi-instance deployment and production privilege verification remain unverified.

There is no global claim that every business mutation now enforces ReadOnly: unmigrated services retain their previous authorization. Broader inspection actions, including duplicate/flip execution, deviation approval, deletion and print, retain their existing boundaries; the lineage copy preserves attribution rather than accepting a new inspector selection. SQL structural guards cannot substitute for trusted service adapters or proper deployment privileges. Automatic event-driven editor refresh, full effective-access/impact UI, protected Root recovery/security workflows, and production cutover are outside this slice.

## Remaining phases

- **3D:** migrate remaining tracking/receiving/production business boundaries with exact keys and operation matrices.
- **3E:** chat access/content/media/subscriptions, moderation plus history, scheduled/delegated senders, PDF/print authorization and maintenance/bootstrap isolation.
- **3F:** complete role permission matrix, direct/inherited/remove-only/impact presentation, advisory live refresh and cookie/circuit/desktop/iPad validation.
- **3G:** audit disposition coverage, seed parity, restore/recovery rehearsal, Root verification, multi-instance/catalog/cache tests, production privileges, cutover and rollback.

Phase 3C ends here; later phases were not begun.
