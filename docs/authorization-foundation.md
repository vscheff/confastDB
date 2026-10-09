# Phase 3B Authorization Foundation

Implemented October 9, 2026. The approved [architecture](authorization-design.md) and [boundary audit](authorization-audit.md) remain the migration references. This document records the foundation and its deployment limits **as completed in Phase 3B**. It is a historical implementation record: its legacy-editor/reset and future-Phase-3C descriptions have since been superseded by [Phase 3C delegated administration](authorization-administration.md). The catalog, initial seeds, structural invariants and offline provisioning procedure below still apply; consult the Phase 3C record for currently migrated boundaries and reset-link compatibility.

## What is active

ASP.NET Core Identity still owns users, roles, credentials, cookies, sign-in and circuit revalidation. `ApplicationRole` extends IdentityRole; the four existing IDs and their assignments survive the additive migration. Existing role checks, business services, chat membership/privacy checks, anonymous/token endpoints and the print authentication scheme continue to operate. No business page/service has been switched to permission policies.

Every Identity user is a human and permanently holds ReadOnly. A PostgreSQL insert trigger enrolls each new user atomically; migration backfills missing memberships idempotently. A deferred constraint prevents removal while the user exists. Ordinary user deletion can still remove that user's memberships. ReadOnly's Identity role claim is now included for all users; this implements the requested baseline membership, rather than globally enforcing the new read-only business boundaries. The existing editor preserves it even if the checkbox is omitted. User creation enlists the actual scoped Identity store, initial role assignments and public-channel enrollment in one transaction.

The legacy editor blocks updates, password-reset issuance and deletion of the explicitly designated Root account. The existing anonymous reset endpoint also rejects Root, including tokens issued before designation, while keeping ordinary token redemption available in PendingRoot. Its actual Identity store transaction locks state to serialize with provisioning. The editor still uses its existing authorization for ordinary accounts. Full peer/lower delegation, account-takeover checks, atomic ordinary-user updates and the protected Root self-security workflow belong to Phase 3C. Ordinary Administrator remains distinct from Root. Bootstrap still manages its configured legacy Administrator as before; neither startup bootstrap nor BrowserTestUser provisioning invokes Root provisioning.

## Catalog and seeds

`Features/Authorization/Permissions.cs` defines named constants; `PermissionCatalog.cs` defines all 164 keys, labels, categories, prerequisites, resource/action classifications and baseline eligibility. SHA-256 of sorted semantic definitions is the deterministic CatalogVersion. Prerequisites are code definitions and included in the fingerprint. They are required when checking an operation; possessing a key does not implicitly grant its prerequisites. Inspections.Create has no read prerequisite. Context-dependent extras such as scheduling a poll remain the future caller's responsibility.

| Role | Direct grants | Effective human grants |
|---|---:|---:|
| ReadOnly | 16 | 16 |
| Quality | 69 | 85 |
| Production | 61 | 77 |
| Administrator | 57 | 163 |
| Root Administrator Prime | 0 | 164, only when designated and ready |

The initial graph contains Administrator → Quality and Administrator → Production. A child inherits its parents. Quality/Production have no ReadOnly parent edge: users already hold baseline directly. All grant lists are explicit; future keys do not automatically become Administrator grants. ReadOnly has no Chat.Access. The sole Root-only key is Authorization.ManageSecurity; obsolete delegation flags/keys are absent.

The manifest table is immutable to normal DML, including owner DML while its guard is enabled. A reviewed future catalog migration must deliberately remove/recreate the manifest guard around definition changes and update the stored fingerprint atomically. Updating a code definition alone is insufficient. Root's new permission coverage does not add the legacy Administrator role claim: a Root account without ordinary Administrator membership still encounters existing role-gated screens until their planned migration.

`RoleGraph` computes additive permissions and provenance by directly held anchor and actual granting role, deduplicates diamonds, and stops at disabled branches. It also exposes direct grants, stored envelopes ignoring disabled state, ancestor/descendant sets and actual role position for future delegation validation. It rejects unsupported grants, duplicate edges, cycles and protected child/Root edges. This is structural infrastructure, not an implemented delegated editor.

## Persistence and locking

Migration `20261009183927_AddAuthorizationFoundation` adds role metadata and six authorization tables: permissions, role_permissions, role_inheritance, authorization_state, authorization_change_history and authorization_change_details. It preserves Identity/business tables and IDs. Root's deployment-stable role ID is `e378b8c0-7723-4b5b-a00e-7c9ab8ce3fc6`; immutable system keys are ReadOnlyBaseline and RootAdministratorPrime.

The singleton contains protected role IDs, nullable staged RootUserId, readiness, generation, catalog version and a concurrency-token GlobalEpoch. PostgreSQL before-statement triggers acquire its row lock before relevant security writes. Grant/edge/role/assignment writes advance the epoch transactionally; security-relevant account changes do too. Display/presence-only changes do not. The epoch is a monotonically increasing version, not an edit count: one transaction may advance it several times, and a no-op security statement can advance it. Rollback rolls back the epoch.

FKs, composite unique keys, protected-role triggers, code-manifest immutability, baseline enrollment/retention, inheritance cycle checks, deferred ready-state Root consistency and protected truncate checks provide structural backstops. Root cannot have grants/edges, lose its designation/membership, become inactive or lose initialized credentials in normal writes. Protected state cannot be deleted. Security history and normalized details are append-only. No passwords or credential tokens are stored in history.

Future mutation cores must start one explicit transaction on the **same DbContext used by the Identity store**, acquire `AuthorizationMutationLock` before authorization-dependent mutation reads, reject a stale expected version, validate the whole proposed graph/actor/delegation state under that lock, save all changes and record history through `AuthorizationAudit` before committing. A retry must repeat authorization and validation. The internal helpers do not expose a public mutation API or establish a human's delegation rights by themselves. Direct SQL writes enforce structure/versioning; trusted application/operator adapters supply human authorization and audit provenance. Application mutation integration remains Phase 3C.

## Pending and ready installations

The migration always starts in **PendingRoot**, with no designated user and no Root membership. This is the explicit Phase 3B refinement to section 8's final nonnull Root design. Existing legacy authorization continues; every new permission-evaluated operation denies with PendingRoot. There is no permissive evaluator fallback. Do not cut over a service to the new guard until provisioning and integration tests are complete.

In **Ready**, exactly one active, credentialed human matches RootUserId and the sole Root membership. The evaluator checks that structure for ordinary actors as well as Root. Unexpected missing/mismatched protected state denies with InvalidState; catalog mismatch denies separately. Diagnostic reason codes are logged without permissions, credentials or message bodies. Root receives all recognized keys; unknown keys deny, including for Root. Root permissions do not establish chat membership, ownership, record validity or other business context.

## Explicit installation provisioning

Provisioning is an **offline installation action**, never an HTTP endpoint or startup action. Use separately controlled migration/operator credentials and a maintenance window. Verify the intentional target account ID against your account inventory, confirm it is active with an initialized password, and verify an actual sign-in before cutover. Do not choose an account by age, migration identity, first Administrator or configured username. Do not designate the Development BrowserTestUser account. This operation never changes passwords or adds Administrator.

1. Record the intentional target's immutable Identity ID. In Development, obtain the configured BrowserTestUser's existing account ID separately and use it as the exclusion parameter. This exclusion is supplied by the trusted operator; SQL does not know the application's configuration. In environments without BrowserTestUser use NULL.
2. Inspect `SELECT readiness, root_user_id, global_epoch, installation_generation, catalog_version FROM authorization_state WHERE id = 1;` and confirm PendingRoot, no designation and a code-matching catalog.
3. In one operator transaction call the owner-only function below with the observed epoch. Use actual IDs and a meaningful reason in place of placeholders; do not run this example as-is.

```sql
BEGIN;
SELECT provision_confast_root(
    'INTENTIONAL-ACCOUNT-ID',
    OBSERVED_EPOCH,
    'Approved installation Root designation',
    'DEVELOPMENT-BROWSER-TEST-ACCOUNT-ID'); -- NULL only when no BrowserTestUser exists
COMMIT;
```

The function locks state, rejects stale/nonpending requests, inactive/missing/passwordless accounts and the excluded browser account, adds only the protected Root membership, closes readiness, advances the epoch and writes structured operator history atomically. PUBLIC has no execution privilege. Its owner can execute it; do not grant it to the normal runtime login. A supplied exclusion ID is operator validation, not proof of human authorization.

Foundation tests provision a dedicated test account by ID separately from BrowserTestUser; their helper never runs in application startup. For manual development provisioning, apply the migration only when deliberately deploying to that development database, then use the operator procedure with a dedicated account. No development/production migration was applied during Phase 3B implementation.

## Runtime database privileges and recovery

Current application and EF tooling use the same ConnectionStrings:Confast configuration. There is no separately configured migrator login in the application. README's local setup makes confast_app the development/test database owner; the configured disposable login is confast_app and the fixture requires owner privileges. Therefore the local credential arrangement **does not establish a security boundary against compromised application database credentials**. Triggers do not protect against an owner disabling them. No claim about deployed production privileges is made.

Before production cutover, use a separately controlled schema/migration owner and a nonowner runtime role. Runtime needs SELECT on state/manifest, ordinary Identity/business DML, relevant ordinary authorization DML, append-only history INSERT and sequence privileges. It must have no schema/DDL/trigger-disable authority, manifest DML, history UPDATE/DELETE/TRUNCATE, protected state DML or provisioning-function execution. The epoch/baseline trigger functions use SECURITY DEFINER with qualified tables and a fixed search_path so runtime writes need no direct state-update privilege. Review grants for the real deployment instead of copying local owner credentials. Database permissions cannot establish which human is acting.

Recovery stays offline: stop application writes, take a verified backup, use separately controlled owner/recovery credentials, inspect state/Identity/history, repair or replace the protected account/designation with its matching membership in one controlled maintenance transaction, preserve credential and audit confidentiality, advance epoch and rotate InstallationGeneration after a restore/repair, re-enable/revalidate constraints/triggers, verify Root login and ordinary denials, then reopen traffic. Ready designation cannot transfer through the ordinary function. A privileged operator can perform explicit trigger/constraint repair; no client-controlled SQL session flag bypass is provided. Detailed recovery tooling, custody and rehearsal remain later work. Downgrading this migration is refused once Ready; Pending downgrade removes the new schema but retains already-added ReadOnly memberships.

## Evaluator, cache and composition

`EffectivePermissionService` resolves the trusted circuit principal first; an anonymous circuit cannot borrow a different ambient HTTP actor. The HTTP adapter/framework handler use their trusted request/framework principal explicitly. Public guards accept purpose/scope/permission requirements, never a caller-supplied actor ID. Cookie role claims do not supply current grants. Account Active and the current Identity security stamp are checked immediately, without waiting for cookie/circuit revalidation.

Each independent operation starts a short primary-database Repeatable Read snapshot. One projected account/state query validates readiness, protected roles/membership, current account/stamp, generation, epoch and catalog. A cache hit uses only the exact validated version/actor. A miss loads the manifest, projected graph and assignments in that **same** snapshot, validates code definitions and computes once. Database failure denies; stale computed grants never authorize an outage.

`PermissionCache` is a dedicated bounded in-memory cache (4096 size units) for versioned graphs and actor snapshots. A graph is charged per role, an actor per entry. Oversized entries are not retained. Fifteen-minute sliding expiry manages memory; generation/catalog/epoch matching establishes correctness. Multiple app instances independently validate against the primary database. No replicas, distributed cache, forced logout for grant edits or circuit polling are introduced.

```csharp
using var operation = await authorization.BeginAsync(
    "Receiving.BeginInspection", new AuthorizationScope("Container", containerId.ToString()),
    [Permissions.Receiving.BeginInspection], cancellationToken);
// Internal cores receive this server-created frame and the same purpose/scope.
operation.Require("Receiving.BeginInspection", operation.Scope, Permissions.Inspections.Create);
```

The operation's authority snapshot, purpose, actor, scope, version and initial required keys are immutable. Dispose closes its lifetime; a completed frame cannot authorize another call. Nested checks expand prerequisites and use the existing snapshot without database access. Never store it in a component field, singleton or later callback; every independent event/request/job creates a fresh frame. A change committed after the initial snapshot can leave already-authorized work running, as specified; the next operation observes it.

Known policies are registered with the standard provider as `Permission:<Key>`. Use named constants through `AuthorizationRegistration.PolicyName`. Unknown policies are not registered. A framework handler can reuse an explicitly supplied live AuthorizedOperation resource only for its matching actor/stamp; otherwise it obtains a new trusted snapshot. Future endpoint/service adapters must deliberately share the matching frame to avoid double evaluation; independent route rendering and later events remain separate operations. No existing route attributes have been replaced.

Snapshot/computation/cache-hit counters and structured timing/denial logs support foundation tests and initial diagnostics. Full aggregate production telemetry and permission-aware UI refresh are deferred.

## Validation and remaining phases

Read README's Integration tests instructions. Use the configured disposable PostgreSQL database, never development/production; its name must contain test. The fixture acquires the suite-lifetime advisory lock before migrating/resetting. Its new security reset requires **test database owner** credentials: it temporarily disables user triggers, restores canonical seeds and a fresh PendingRoot generation and re-enables triggers in one transaction. This deliberate disposable-database reset is not application code or a runtime bypass. Do not run concurrent suites.

```powershell
dotnet build Confast.sln --no-restore -c Release -m:1 -p:UseSharedCompilation=false
dotnet ef migrations has-pending-model-changes --project src/Confast.Web --startup-project src/Confast.Web --configuration Release --no-build
dotnet test tests/Confast.Web.Tests/Confast.Web.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~PermissionCatalogTests|FullyQualifiedName~RoleGraphTests|FullyQualifiedName~AuthorizationFoundationTests|FullyQualifiedName~IdentityTests"
dotnet test Confast.sln -c Release --no-build
```

Tests cover the exact catalog/seed manifest, graph/provenance/disabled branches, real migration preservation/backfill, concurrent creation/cycle attempts, PostgreSQL constraints and transactional epochs, explicit Root staging/provisioning/corruption, immediate revocation/current account checks, trusted actor isolation, operation composition, policies and outage denial. The existing regression suite remains the business compatibility check. Authentication tests exercise existing Identity sign-in/provisioning; they are not an authenticated browser/UI verification.

Phase 3C should integrate user/role administration under the serialized authorization boundary, enforce peer/lower and dormant-envelope/account-takeover rules (including the approved safe lower-source edits), close ordinary-user partial-update paths, add password-reset delegation validation, migrate inspector identity/eligibility, and integrate account-facing policies/advisory refresh with focused browser tests. Later phases cover the permissions editor, business boundaries, all Chat.Access/media paths, moderation/history, scheduled senders, PDF authorization, complete offline recovery and final production privilege/cutover verification. None are implemented by this foundation.

## Implementation files and verified results

New source is in `src/Confast.Web/Features/Authorization/`: Permissions, PermissionCatalog, AuthorizationSeeds, AuthorizationEntities, AuthorizationMapping, RoleGraph, AuthorizationContext, EffectivePermissionService, PermissionCache, ApplicationAuthorization, AuthorizationMutationLock and AuthorizationAudit. ApplicationRole is new under Features/Identity. Properties/AssemblyInfo grants the test assembly access to internal mutation/audit helpers without exposing them to clients.

Modified integration points are Program, AppDbContext, AppRoles, ApplicationUserClaimsPrincipalFactory, IdentityBootstrapper, UserAdministrationService and IdentityEndpoints. Migration files are AddAuthorizationFoundation and its designer plus historical AuthorizationFoundationSql; the model snapshot retains the preceding uncommitted chat migration. README links this document. The approved design and audit were preserved.

New test files are PermissionCatalogTests, RoleGraphTests and AuthorizationFoundationTests. IdentityTests and AuthorizationEndpointTests cover baseline/provisioning compatibility and actual reset-token redemption. PostgresTestDatabase restores authorization seeds safely between cases. ContainerTrackingTests, ProductionServiceTests and one ChatServiceTests role-removal statement were adjusted to preserve the intentionally mandatory baseline; their business expectations and existing uncommitted chat work were preserved.

Validation on October 9, 2026:

- Release solution build: passed, **0 warnings / 0 errors**. Shared compiler process access failed in the restricted environment; `-m:1 -p:UseSharedCompilation=false` produced the clean build.
- EF pending-model check: passed, no model changes since the latest migration. EF emitted two existing Conversation query-filter relationship warnings for ChatChannelThread/ChatMessage; these are separate from the clean compilation result.
- Focused catalog/graph/foundation/Identity run: **72 passed** before the last three internal audit/version/cycle tests were added; all three also passed in the final full suite.
- Actual ordinary-versus-Root anonymous reset endpoint regressions: **2 passed**.
- Final solution regression suite: **537 passed, 0 failed, 0 skipped**, duration **2 minutes 8 seconds**. A restricted test-host connection attempt aborted before assertions; the successful runs used normal process access and the same guarded disposable database.
- The migration was applied and its Pending downgrade/upgrade preservation/backfill exercised only on the configured disposable confast_test database. No development or production migration/account/password change occurred.
- Diff whitespace check: passed. Existing chat/UI changes were compared with their preimplementation diff; the single baseline-aware chat test adjustment is intentional.
- Authenticated browser/UI verification: not performed. Server-side Identity and real endpoint regressions passed; no browser-facing authorization UI was introduced.
