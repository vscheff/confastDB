# Phase 3G Authorization Verification

Verification date: October 26, 2026. ConfastDB has never been deployed. All Phase 3G
database mutations, recovery, backup/restore and authenticated browser work use a
new private PostgreSQL cluster with synthetic identities/data. The existing
development database, PostgreSQL accounts/grants and designated Root are untouched.
No deployment, commit or push is part of this phase.

Final execution results are recorded below. Earlier
phase results are historical context, not substitutes for current verification.

## Architecture Retained

The catalog remains exactly 164 recognized keys: 163 ordinary and one protected
Authorization.ManageSecurity key. PermissionCatalog/AuthorizationSeeds retain the
existing keys, explicit initial grants and prerequisites. ReadOnly is optional
after initial enrollment; no-role accounts receive no implicit baseline grants.
Its role identity/read-only envelope remain protected.

Root is the explicitly designated active credentialed human with matching sole
protected-role membership, not an Administrator name or a cookie role claim.
Ordinary roles support multiple assignments and multiple parents. RoleGraph and
AdministrativeAuthority retain disabled/dormant envelopes, peer/lower assignment,
strictly-lower editing and per-direct-anchor reachability checks.

Each independent operation obtains a trusted principal and a primary-database
authorization snapshot. Generation/catalog/epoch plus current Active/security stamp
validate the bounded local cache. Internal composition reuses only a live matching
purpose/scope frame; disposal prevents later reuse. A committed revocation denies
the next operation. Work authorized before a concurrent change may finish according
to the established operation-snapshot policy.

Database guards preserve structural state, epoch, catalog, protected roles, Root,
and append-only history. They do not infer human identity from a SQL login. Normal
administrative changes re-evaluate and validate under the common lock, then mutate
and audit in one enlisted transaction. Root still cannot read private chat without
membership. Scoped print capabilities remain resource/options/purpose-bound and
valid for two minutes, including legitimate in-flight completion after initiating
grant revocation. Scheduled work rechecks current authority and generation.

## Fresh Inventory And Historical Disposition

The historical audit is preserved. Its 404 G identifiers and 248 method checklist
rows are individually reconciled in
[historical-reconciliation.csv](security-evidence/historical-reconciliation.csv).
Each row includes a current source boundary/helper or removal rationale and a
regression family. Historical line numbers identify old evidence, not current code.
The row dispositions are 570 resolved/verified, 23 internal to authorized operations,
18 anonymous/self-scoped, six narrow delegated capabilities and 35 obsolete sites.
None of these 652 historical rows remains classified as open.

[current-methods.csv](security-evidence/current-methods.csv) and
[current-routes.csv](security-evidence/current-routes.csv) are regenerated from source
with scripts/security/authorization-inventory.py. The method file is a lexical
candidate inventory that includes pure helpers/EF configuration/record factories;
its count is not a security-coverage percentage. A guard-text match alone is not
proof. Review follows public wrappers to their guarded core, contextual checks,
direct HTTP adapters, workers and trusted installation composition. Routes are
one layer of evidence and never substitute for service checks.

| Finding | Final disposition and current evidence |
|---|---|
| F01 broad unguarded business writes | Resolved and verified: CustomerService, PartService, GageService, InspectionCriteriaService and InspectionService begin trusted specific operations. CoreBusinessAuthorizationTests directly exercise absent/revoked permissions and privileged fields. |
| F02 user administration / supplied actor | Resolved and verified: UserAdministrationService derives current actor, locks state, validates target/assignment/reset delegation and audits atomically. DelegatedAdministrationTests and HTTP tests cover forged claims, Root and revoked actors. |
| F03 settings/flip service omissions | Resolved and verified: nominal tolerance, email template/settings and PartFlip service guards are independent of pages. Core business tests cover direct calls. |
| F04 package/temp completion/email conflation | Resolved and verified: BuildPackage, TemporarilyCompletePackage and SendEmail are distinct; sender is trusted and temporary completion is nonpersistent. CertificationEmailAuthorizationTests/CoreBusinessAuthorizationTests/delegated print tests. |
| F05 stale roles/circuit claims | Resolved and verified: current state is authoritative on every independent operation; role claims do not supply grants. Multi-process/HTTP tests supplement foundation revocation tests. Advisory display refresh remains process-local. |
| F06 concurrent admin loss / partial updates | Resolved and verified: designated credentialed Root is structurally protected; mutations serialize through the common state lock and transaction. Foundation/delegation concurrency and rollback tests cover these invariants. |
| F07 read-triggered schedule maintenance | Intentionally internal to an authorized operation: fixed-purpose schedule catch-up remains bounded domain maintenance with existing System attribution; it grants no general write authority. ProductionAuthorizationTests. |
| F08 schedule read/configuration exposure | Resolved and verified: schedule projection and configuration editing use their established separate permissions; direct configuration boundary tests exercise exact requirements. |
| F09 receiving scope broadened by module role | Resolved and verified: receipt and allocation actions use separate keys; BeginInspection composes Inspections.Create once. Logistics/inspector/core tests preserve exclusions and business invariants. |
| F10 chat owner/admin/member asymmetries | Resolved and verified: each action uses its catalog key plus retained context. Metadata override does not imply private membership; duplication does not copy messages. ChatAuthorizationTests. |
| F11 arbitrary notification subscription ID | Intentionally internal to an authorized operation: raw notification transport is internal and content-free; ChatService's subscription adapter derives trusted actor and checks Access. No mapped impersonation endpoint exists. |
| F12 Development date/email utility | Resolved and verified: date changes require Root-only security authority in Development; email diagnostic requires current SendEmail and antiforgery and uses an in-memory invalid-recipient transport. Endpoint/bootstrap tests; no external diagnostic email send. |
| F13 bootstrap restores privileges | Resolved and verified: create-only initial account/browser provisioning never repairs existing identity, activation, assignments, grants, graph or Root. BootstrapAuthorizationTests exercise restart/concurrency and Production exclusions. |
| F14 former member's own queued drafts | Legitimately self-scoped: own receipts/drafts remain visible with Access; cancellation requires ownership. Delivery independently rechecks permission/membership and retains terminal failure. Scheduled/chat tests. |
| F15 races between checks and mutations | Resolved under the approved operation-snapshot contract: next-operation revocation is immediate; no claim of retroactively canceling all in-flight business work. Chat membership locks cover send/moderation races; queue row locks and atomic receipts prevent duplicate delivery. |
| F16 fallback mistaken for callback protection | Resolved and verified: routed permission metadata is paired with trusted service checks; anonymous/inactive/stale-session direct calls deny. Static capability print remains outside Interactive Server. |
| F17 passwordless ordinary admin as recovery proof | No longer applicable to the old last-admin count: protected Root must be credentialed and Active. Passwordless ordinary accounts cannot replace that invariant. Offline operator recovery is separately rehearsed. |

No known critical authorization bypass was identified in the reviewed boundaries.
This is a source review plus regression evidence, not a claim that all possible
application inputs or host configurations have been exhaustively proved secure.

## Corrections In This Phase

Restricted-role rehearsal exposed a real compatibility gap: SELECT FOR UPDATE on
authorization_state requires UPDATE privilege. Granting direct protected-state
UPDATE to solve it would defeat the intended split. AuthorizationMutationLock now
calls a parameterless SECURITY DEFINER function that only returns/locks singleton
state. New SQL-only EF migration 20261026140000_AddRuntimeAuthorizationLock installs
it with a fixed pg_catalog/public search path and PUBLIC execution revoked. The
operator explicitly grants runtime execution. It accepts no actor, SQL or mutation
arguments and writes no state. The EF model/snapshot is unchanged.

HostSecurityConfiguration adds opt-in persistent/shared Data Protection with an
explicit discriminator, optional Windows current-user DPAPI, and an explicit trusted
proxy IP list. No implicit forwarding trust is added; forwarded Host is ignored.
Cookies require Secure outside Development. Unconfigured Development retains the
profile-independent ephemeral provider and existing local launch experience.

Operator SQL implements reviewed runtime grants, generation rotation and explicit
offline Root replacement. Recovery validates target/owner/epoch/reason/excluded
browser identity, preserves history, records before/after designation and rotates
generation. No application recovery endpoint or general system actor was added.

## Database Privileges, Recovery And Multiple Instances

The existing disposable login has neither CREATEDB nor CREATEROLE. A new private
PostgreSQL cluster provides isolated operator/runtime roles without altering the
developer's service or requesting its superuser credentials.

The restricted runtime has ordinary Identity/business DML, SELECT on manifest/state,
append-only security/moderation history insertion, sequence access and the fixed lock
function. It is not an owner, superuser, schema creator, operator-role member, trigger
administrator or Root provisioner. Tests verify runtime Identity password checks,
customer create/read, normal role administration with audit, scheduled delivery and
real Chromium PDF rendering. Direct protected-state/manifest/audit rewriting,
history truncation, schema creation, trigger disabling, operator impersonation and
provision_confast_root execution are rejected. Root inactivation remains a database
constraint failure. SECURITY DEFINER ownership/search paths are inspected.

Backup/restore uses pg_dump custom format and pg_restore into a separate disposable
database. Content fingerprints compare 13 Identity/security/historical inspection
and chat tables before any recovery. Restored credentials authenticate through the
real login/antiforgery/cookie pipeline and can reach the protected role page. Generation
rotation changes the cache version and old queued delegation becomes a retained
InstallationChanged failure. Stale recovery approval is rejected; approved
lost-credential replacement and deliberately inconsistent designation are repaired
offline, with ordinary/old Root denial and the state guard restored.

Two actual application processes use the same disposable database through runtime
credentials, independent caches, shared test keys and separate real login sessions.
Their cookie compatibility, stale editor behavior, private membership removal,
permission revocation and worker behavior are exercised by DeploymentSecurityTests.
No Redis/backplane/new infrastructure is introduced. Real-time chat/visual refresh
across instances remains unqualified; recommend a single initial application instance.

## Data Protection, Proxy And Audit Review

HostSecurityTests verify cross-provider capabilities, rotation retaining old keys,
wrong resource/purpose/application/key-ring rejection and exact two-minute expiry.
Controlled HTTP tests reject untrusted forwarded protocol and ignore forwarded Host.
The actual PDF test attempts forged Host/forwarding and still renders through the
trusted callback. Existing delegated tests cover redirects/nested resources/options
and initiating-grant revocation. Production host allowlists, proxy configuration,
TLS, key filesystem ACLs/wrapping/backup and log redaction remain installation
acceptance requirements, documented in deployment-readiness.md.

Security mutation history has trusted actor, target/details, operation identity,
timestamp and generation/epoch, atomically with the mutation. Reset history stores
delegation metadata rather than raw tokens/passwords. Moderation records metadata
and reason rather than deleted message bodies; runtime cannot update/delete it.
Scheduled receipts preserve immutable origin and bounded retry/final outcome.
Production/receiving retain existing business actor/time/change history; they are
not presented as a uniform authorization-event log.

No retention policy is invented. Business/legal owners must decide retention,
access, archival, backup expiry and recovery objectives for inspection history,
private chat/attachments, mutation/reset/moderation history and receipts. No security
history deletion, legacy data rewrite or new retention worker is implemented.

## Files, Validation And Readiness

Source changes: AuthorizationMutationLock, Program and HostSecurityConfiguration.
One SQL-only EF migration is new; no catalog/seed/model changes. New tests:
DeploymentSecurityTests, DeploymentBrowserTests and HostSecurityTests. New scripts
under scripts/security provide inventory, private-cluster lifecycle, runtime grants,
generation rotation, Root recovery and sequential rehearsal orchestration.
ScheduledChatAuthorizationTests now restores the latest migrations in finally after
its deliberately refused old downgrade, so it cannot leave newer infrastructure
removed for subsequent tests.

Evidence is retained in artifacts/phase3g-20261026-a and separately named
artifacts/phase3g-browser-* directories, excluded from source control. Earlier failed
checkpoint logs are preserved. New scripts operate only on explicitly identified
resources; the suite remains serialized and the existing running app is not stopped.

The actual browser pass uses only the configured Development BrowserTestUser, copied
into the disposable installation and assigned ordinary delegated roles, never Root.
Root uses synthetic identities in service/SQL and real-cookie HTTP tests; no Root
browser session is claimed. Desktop is 1440×1000 and Chromium tablet emulation is
834×1194 with mobile/touch settings. Physical iPad/Safari is unverified.

Browser coverage includes business landing-route authorization, lower-role
matrix/preview/Save, stale conflict/reapply on the second process, user assignment
remaining local until Save, private channel/history UI, custom scheduled submission
and cancellation, real PDF output, and current content denials on both instances.
Receiving allocations and production scheduling/tracking mutations are verified at
service/HTTP/database boundaries in the full suite; this browser pass does not claim
an exhaustive manual execution of those business workflows.

Early checkpoints failed on the new recovery script's precondition exit behavior
and deferred-trigger re-enable ordering; both were corrected and re-rehearsed.
Browser harness failures included premature interaction with the Create User form
before Edit User loaded, omitted Channels-tab selection, overlay focus and a hidden
duplicate text selector. Those were test synchronization/selector defects. Temporary
diagnostics and speculative account-UI edits were removed; the existing account UI
is preserved. Failed logs remain separate from passing final results.

| Check | Final result |
|---|---|
| Release solution build | Passed; 0 warnings / 0 errors. Final repeat saved in final-build.log. |
| NuGet restore and vulnerability audit | Force-evaluate restore succeeded without warnings; direct/transitive vulnerability report lists no vulnerable packages and no feed error. No package changes in this phase. |
| EF pending-model check | Passed; no pending model changes. |
| Latest disposable migration | 20261026140000_AddRuntimeAuthorizationLock confirmed after the full suite. Applied only to disposable infrastructure. |
| Focused Phase 3G tests | 6 passed, 0 failed, 0 skipped; 43 seconds. Includes real restricted app processes, SQL denials, backup/restore, real restored cookie login, both recovery cases, keys/proxy behavior and browser acceptance. |
| Full PostgreSQL regression | 901 passed, 0 failed, 0 skipped; 24 minutes 35 seconds; one runner, final source behavior. Includes catalog/roles/delegation, business, chat/privacy/moderation, workers, rendering, constraints/concurrency and Development isolation. |
| Authenticated Chromium acceptance | 20 checkpoints, 0 page errors/unhandled circuit errors; repeated successfully inside the full run. |
| Source inventory | 404 historical sites + 248 method rows reconciled; 43 current routes; 454 lexical feature-method candidates, including pure/infrastructure methods. |
| Scripts / whitespace | PowerShell scripts parsed; inventory generator ran successfully; Phase 3G unstaged diff and new text files are clean. Two pre-existing staged EOF findings are preserved, as noted below. |
| Cleanup | Private cluster stopped and data removed; temporary runtime roles absent; app/test processes stopped; remaining temporary test key stores removed. Logs/screenshots/PDFs and synthetic rehearsal backups retained. |

The full private-cluster run used unpooled connections and was substantially slower
than earlier runs against the configured database. Its original runner was monitored
to completion; no replacement or concurrent suite was launched. Earlier failed
focused checkpoints remain in the evidence directory and are not described as passes.
Only documentation and indentation-only cleanup of the migration test followed the
full run; the final Release rebuild passed. No application or test behavior changed.
The additional cached-diff whitespace check reports existing extra EOF blank lines
in AddScheduledChatDelegation.cs (historical staged line 270) and
ScheduledChatAuthorizationTests.cs (staged line 278). The original index was not
rewritten to hide or fix those unrelated baseline findings.

Final browser evidence: artifacts/phase3g-browser-d471d084660541a58c2f618f7545fe62/results.json
and its desktop/tablet screenshots and restricted-runtime-inspection.pdf. The separate
focused acceptance evidence is in phase3g-browser-9dedeb29ab754c6e8f6921836013f5a7;
readable viewport extracts of its long matrix screenshots were visually inspected.
The full TRX/log, focused-final-state TRX/log, final build/model logs and dependency
report are under artifacts/phase3g-20261026-a.

**Readiness: Ready for controlled first-deployment preparation.**

No selected deployed environment has been qualified. Remaining gates are actual
host/runtime/TLS/AllowedHosts/proxy configuration, runtime credential and key-ring
custody/ACLs/wrapping/backup, SMTP/Chromium/document-storage service identity checks,
organizational retention/recovery objectives and host-specific acceptance. Begin
with one instance; affinity, cross-instance advisory notifications, real-time chat
delivery and failover remain a separate deployment qualification. Physical
iPad/Safari and exhaustive browser receiving/production mutation workflows are not
claimed by this pass.

The new lock function must be installed deliberately with its migration before new
binaries use role/user mutation paths. That migration was not applied to the
developer's database under this task's explicit safety constraint. Existing staged
work (172 files at task start), developer identities/grants/Root and the development
database are preserved. No actual deployment, production modification, commit or
push occurred.
