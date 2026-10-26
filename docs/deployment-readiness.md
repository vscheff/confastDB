# First-Deployment Preparation

ConfastDB has never been deployed. This guide prepares a first installation; it
does not describe an existing production system. No deployment is performed by
the Phase 3G rehearsal. Read the [verification record](authorization-final-verification.md)
for the distinction between tested local behavior and hosting decisions.

## Requirements And Decisions

| Area | Requirement / decision before installation |
|---|---|
| Application | .NET 10 ASP.NET Core runtime; SDK selected by global.json for builds. Build and audit the exact release candidate. |
| PostgreSQL | PostgreSQL 18 was exercised locally, including constraints, triggers, xmin, pg_dump/pg_restore and advisory locks. Other major versions require qualification. |
| Database identities | Separate schema/operator login and restricted application login. No owner/superuser/operator membership for runtime. |
| Hosting | Host/OS/service manager is undecided. Choose restart policy, service identity, writable paths, resource limits, monitoring and patch ownership. No container or cloud dependency is required. |
| Transport | HTTPS at the listener or a controlled proxy. Restrict backend access to the proxy and authorized renderer. |
| Keys | Persistent private Data Protection ring, application discriminator, backup, encryption/access control, custody and restore procedure. |
| Files | Uploaded master prints, certification documents, chat attachments/icons and user pictures are database-backed. Back up PostgreSQL large byte columns too. GIF category artwork metadata under App_Data is a separate writable file; inventory any future external document store. |
| PDF | Install Chromium for the deployed Microsoft.Playwright package, a compatible PDF preview executable (PdfPreview:RendererPath), fonts and writable temporary storage. Execute under the actual service identity. |
| Email / GIFs | Supply SMTP and optional Giphy secrets through the host's secret mechanism. Validate sender policy and external delivery separately. |
| Instances | Begin with one instance. Blazor circuits need affinity. Cross-instance chat and permission-refresh notifications are process-local; there is no notification backplane. |
| Retention | Business owners must decide audit/document/chat retention, privacy access, archival, backup expiry and recovery objectives. This phase introduces no automatic evidence deletion. |

The development app continues to use its existing configuration. Do not run these
installation commands against confast_dev or reuse its provisioned Root for rehearsal.

## Reproduce The Isolated Verification

Prerequisites: .NET SDK/tool manifest, PostgreSQL command-line/server binaries on
PATH, installed Playwright Chromium, configured Development BrowserTestUser in
.NET User Secrets. The test uses those credentials only in newly created synthetic
accounts in the private cluster, through the real login UI.

From the repository root:

    ./scripts/security/run-authorization-rehearsal.ps1

The script creates a collision-safe directory in artifacts, initializes an empty
private PostgreSQL cluster on a free loopback port, runs build/audit/model/focused/full
checks sequentially, restores the caller's environment variables, stops the cluster
and removes its data directory. Results/logs remain. Local trust authentication is
limited to this temporary cluster with synthetic data; it is not a deployment recipe.
Do not use its connection string for an installed application.

For step-by-step work, start-disposable-rehearsal.ps1 writes cluster.json; assign its
ConnectionString to CONFAST_TEST_CONNECTION_STRING and set CONFAST_ISOLATED_REHEARSAL=1.
The new tests independently require the exact rehearsal database/operator names,
loopback host and a nondefault port before creating cluster-level resources.
Stop that exact cluster with stop-disposable-rehearsal.ps1 -OutputDirectory PATH
-RemoveClusterData. Never stop the developer's PostgreSQL service.

## Initial Installation

1. Select the actual host, database name, service identities and storage paths.
   Establish backups, restricted administrative access and secret custody first.
2. A PostgreSQL administrator creates distinct logins and an empty database, for example:

       CREATE ROLE confast_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
       CREATE ROLE confast_runtime LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
       \password confast_owner
       \password confast_runtime
       CREATE DATABASE confast OWNER confast_owner TEMPLATE template0 ENCODING 'UTF8';

   These are example identifiers for a new installation, not instructions to modify
   existing developer roles. Supply passwords interactively; require SCRAM and
   appropriate TLS/HBA rules. Runtime must not inherit any other privileged role.
3. Build the reviewed source. Run migrations using the operator connection in a
   separate process, with ConnectionStrings__Confast supplied from secure configuration:

       dotnet ef database update --project src/Confast.Web --startup-project src/Confast.Web --configuration Release

   Normal startup never migrates schema or repairs grants. The migration history
   and permission manifest belong to the schema owner. Initial catalog creation
   is performed by migrations: 164 keys, 163 ordinary, one Root-only; prerequisites
   and seed lists are code-owned. No wildcard grant reconstruction runs on startup.
4. As the schema owner, review and apply current runtime object grants:

       psql -X -v ON_ERROR_STOP=1 -d confast -U confast_owner -v database=confast -v runtime_role=confast_runtime -f scripts/security/runtime-grants.sql

   Review connection host/port/TLS separately. The script checks target database,
   schema ownership and runtime attributes, revokes PUBLIC schema creation/function
   execution, allows ordinary Identity/business DML, restricts manifest/state to
   SELECT and security/moderation history to SELECT/INSERT. Only the parameterless
   authorization-lock function is executable by runtime. There are no automatic
   future-table grants. Re-review/reapply privileges after each migration.
5. Launch the application with runtime credentials. For the first account only,
   provide BootstrapAdmin Username/Email/Password/DisplayName through secure
   installation configuration. Startup creates the account and initial Administrator
   assignment atomically only when no accounts exist. Remove those settings afterward.
   This does not designate Root. BrowserTestUser settings must be absent outside Development.
6. Verify that initial account can sign in, then explicitly approve the immutable
   Identity ID to designate Root. Inspect readiness, generation, epoch, catalog,
   initialized credentials, Active status and existing memberships. Use the offline
   owner-only provision_confast_root procedure in
   [the foundation record](authorization-foundation.md#explicit-installation-provisioning).
   Supply the configured browser-test account ID as the exclusion in Development;
   use NULL only where no such account exists. Never select Root by role name,
   account age, bootstrap username, or first-user ordering.
7. Verify Ready state, one matching active credentialed Root membership, normal Root
   sign-in, ordinary-account denial of security management, private-chat restrictions,
   optional ReadOnly behavior and a representative business save/PDF. Provisioning
   adds no implicit Administrator claim or private conversation membership.
8. Restart the service and verify that accounts, designation, edited grants and
   key material remain stable. Test a pre-restart session and print capability,
   then re-run current authorization checks. Do not import pre-rewrite assumptions
   that all users must retain ReadOnly.

## Data Protection And Authentication

Configure DataProtection:KeyRingPath as an absolute persistent directory and
DataProtection:ApplicationName as an installation-specific discriminator shared
by instances of that installation. Separate development/test installations use
different rings/discriminators. With no configured ring, Development intentionally
uses ephemeral keys and sessions do not survive restart.

Limit directory read/write/create permissions to the application identity and
approved backup/recovery operators. Setting a filesystem persistence path requires
an explicit at-rest protection decision. DataProtection:ProtectWithDpapi=true
uses current-user Windows DPAPI; it is unsuitable for sharing keys across unrelated
Windows accounts/machines. For another host arrangement, choose encrypted storage
or add and qualify a certificate/secret-store protector before release. The temporary
rehearsal ring is unencrypted synthetic material, not proof of production key custody.

Back up the complete ring and any wrapping credentials separately from the database.
Keep old keys for decryption during rotation. Validate restore with the actual service
identity. Changing purpose/application/ring rejects protected tokens; losing the ring
invalidates cookies, reset links and render capabilities. Rotating installation
generation invalidates stored generation-bound delegation; it deliberately does not
retroactively revoke an already-issued two-minute print capability with intact keys.

Cookies are HttpOnly, SameSite=Lax, eight-hour sliding expiration and Secure outside
Development. Identity validates Active status and stamps; protected operations also
check current Active/stamp/catalog/generation/epoch on the primary database. Grant
and assignment edits need no forced logout. UI refresh on another instance may lag;
it is never authorization authority. Keep normal antiforgery handling for login,
logout, reset and other form endpoints.

See [Microsoft's Data Protection configuration guidance](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0).

## HTTPS, Host And Proxy Configuration

Choose the real external hosts before deployment and set AllowedHosts to that
explicit semicolon-separated allowlist. The development wildcard remains useful
locally; it is not a completed production host policy.

With a proxy, set ReverseProxy:KnownProxies to its exact backend-facing IP addresses.
Only X-Forwarded-For and X-Forwarded-Proto from that allowlist are consumed, for one
hop. Forwarded Host is ignored. Leave the list empty for a direct listener. Do not set
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true as a shortcut that trusts arbitrary proxies.
IIS/host integration can add its own forwarding behavior and needs validation on the
selected host.

A safe one-hop proxy must reject unknown external Host values, terminate HTTPS,
overwrite rather than append untrusted forwarding fields, preserve the validated
Host, set X-Forwarded-Proto from its actual TLS connection, and reach a private
backend listener. Example pseudoconfiguration using nginx directive names:

    # In the selected HTTPS virtual host with its real certificate/server_name:
    location / {
        proxy_pass http://127.0.0.1:5105;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_set_header X-Forwarded-Host "";
        proxy_http_version 1.1;
        # Configure WebSocket Upgrade/Connection handling for Blazor.
    }

Also configure a default virtual host that rejects unmatched names, WebSocket
forwarding/timeouts, request-size limits, HTTPS redirection and HSTS. The snippet
is a requirements example, not a complete server installation.

InspectionRendering:ApplicationOrigin is an operator-controlled absolute HTTP(S)
origin for the internal callback, with no userinfo/path/query. Use a reachable
listener covered by AllowedHosts; HTTPS must have a valid certificate trusted by
Chromium. Without it, the renderer uses the application's actual bound listener,
mapping wildcard addresses to loopback. Request Host/forwarding never choose the
bearer capability destination. Test a real PDF with forged Host/forwarding, denied
nested downloads/redirects, wrong resource/options and exact expiry under the actual
proxy. Do not alter the legitimate two-minute in-flight completion policy.

Default Microsoft.AspNetCore logging is Warning. Do not enable request/query logging,
HTTP body logging, EF sensitive-data logging, browser traces containing cookies, or
raw proxy access queries on token-bearing routes. Redact renderToken and reset
tokens at every log/tracing layer. Application renderer exceptions deliberately
exclude Playwright bearer URLs; this does not sanitize an unconfigured external proxy.
See [Microsoft's proxy guidance](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer?view=aspnetcore-10.0).

## Backup, Restore And Offline Recovery

Only operators run these steps. Record the exact source/target identifiers, take
a verified backup, stop all application writes/workers and obtain approval before
changing designation or generation. The SQL scripts require explicit target,
expected epoch, reason and approved=yes; stale approvals fail without commit.

1. Back up using pg_dump -Fc with operator connection configuration. Protect
   dumps like the source database; they include password hashes and private data.
   Back up the key ring and file configuration separately.
2. Restore into a separate, explicitly approved empty database using pg_restore
   --exit-on-error. Validate owners, extensions, function ACLs/search paths,
   triggers, constraints and runtime grants; the database dump does not create
   cluster-level logins. Reapply reviewed runtime-grants.sql for the new target.
3. Compare Identity/membership/catalog/state/history plus representative historical
   inspection/revision and chat/receipt data. Verify an actual login; hashes or
   row counts alone do not prove authentication works.
4. Rotate generation before accepting restored traffic:

       psql -X -v ON_ERROR_STOP=1 -d APPROVED_TARGET -v database=APPROVED_TARGET -v approved=yes -v expected_epoch=OBSERVED_EPOCH -v reason="Approved restore" -f scripts/security/rotate-generation.sql

   This records operator history and advances epoch. Old scheduled instructions
   become retained InstallationChanged failures; old password delegations fail.
   Do not edit their provenance to revive them.
   A restore also returns grants, activation and credential stamps to the backup's
   state. Generation rotation does not replay later revocations or automatically
   invalidate human cookies. Reconcile security changes since the backup and make
   an explicit session-invalidation decision (affected stamps or key custody) before
   reopening traffic, particularly after a security incident.
5. If Root credentials are lost, deliberately choose an active credentialed
   replacement human, verify their sign-in, approve the identity, and run:

       psql -X -v ON_ERROR_STOP=1 -d APPROVED_TARGET -v database=APPROVED_TARGET -v approved=yes -v expected_epoch=OBSERVED_EPOCH -v expected_root=OBSERVED_ROOT_ID -v account_id=APPROVED_REPLACEMENT_ID -v excluded_browser_id=BROWSER_TEST_ID_OR_EMPTY -v reason="Approved Root recovery" -f scripts/security/recover-root.sql

   Empty expected_root is permitted only when the inspected designation is null.
   Exclude the configured BrowserTestUser whenever it exists. Recovery requires
   the table owner, replaces the designation/membership atomically, rotates
   generation, records before/after designation, flushes consistency constraints
   and restores the one temporarily suspended state guard before commit.
   It changes no password and preserves old history/data. No public recovery
   endpoint exists. Do not pass a password/hash in SQL history or support tickets.
6. Validate exactly one matching Root membership, enabled protected roles, valid
   catalog, all expected triggers enabled, new Root login, old/ordinary-account
   denial, private-resource denial, and generation-bound delegation failure.
   Reopen traffic only after the operator records those results.

The local tests restore a separate disposable database, compare content hashes
across 13 tables, verify password authentication, reject stale approval, recover
lost-credential designation, deliberately introduce a membership/designation
mismatch, and recover again. They never use the developer's Root.

## Audit Retention And Operations

Authorization history/details and moderation history are append-only for runtime.
Role/user/reset mutations record trusted actors, targets, before/after details,
operation IDs and authorization versions in the mutation transaction. Moderation
records reason/actor/target/time/operation without duplicating deleted bodies.
Scheduled rows retain immutable request identity, issuing version, bounded attempts
and terminal receipt; ordinary queue cleanup must not erase that security evidence.
Production/receiving records preserve their existing actor/time/business lineage.
Their schema is not a uniform security-event framework and no new one was invented.

There is no organization-approved retention period. Decide legal/business inspection
history requirements, chat privacy/deletion expectations, security incident needs,
storage growth (attachments dominate), restore objectives, archive access/encryption
and eventual partitioning/archival mechanisms before automating deletion. Keep
security receipts distinct from temporary render files, stale presence sessions,
GIF caches and other bounded operational cleanup.

Monitor readiness/catalog failures, denied operations, database availability,
worker failures/receipt age, storage, backup restoration and SMTP delivery without
recording credentials, hashes, token URLs or chat bodies. Do not use a generic
background superuser. Hosted workers derive current authority from persisted
instructions and start automatically with the application.

Local authorization correctness does not qualify multi-instance real-time chat:
permission and chat advisory notifications are process-local. Use one instance
initially; a future multi-instance rollout needs affinity, shared key custody,
notification delivery/reconnect design and host-specific failover acceptance.
