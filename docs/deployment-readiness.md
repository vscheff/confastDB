# Deployment and Recovery

Use this runbook to prepare a ConfastDB installation, verify its configuration,
and recover a database or Root account. Validate the exact release, PostgreSQL
version, service identity, and proxy settings on the intended host.

## Requirements

| Area | Requirement / decision before installation |
|---|---|
| Application | .NET 10 ASP.NET Core runtime. Use the SDK selected by [global.json](../global.json) for builds. |
| PostgreSQL | PostgreSQL 18 was used for the local rehearsal. Include PostgreSQL client tools for migrations, backups, and recovery checks. |
| Database identities | Use a schema owner for migrations and recovery, and a restricted nonowner login with no role memberships for the application. |
| Hosting | Set service identity, restart policy, writable paths, resource limits, monitoring, and patch ownership on the chosen host. |
| Transport | HTTPS at the listener or a controlled proxy. Restrict backend access to the proxy and authorized renderer. |
| Keys | Persistent private Data Protection ring, application discriminator, backup, encryption/access control, custody and restore procedure. |
| Files | PostgreSQL stores uploaded master prints, certification documents, chat attachments and icons, and user pictures. Preserve the writable GIF category URL cache under `App_Data` separately. |
| PDF | Install Playwright Chromium and Poppler's `pdftoppm` for the PDF features. Provide fonts and writable temporary storage under the service identity. |
| Email / GIFs | Configure the optional integrations through host configuration and verify SMTP sender policy and delivery with the configured provider. |
| Instances | Begin with one instance. Blazor circuits need affinity, and chat and permission-refresh notifications stay within one process. |

For integration setting names and Chromium installation, see
[Optional integrations](../README.md#optional-integrations).

## Isolated deployment rehearsal

Install the repository's .NET tools and Playwright Chromium. Put the PostgreSQL
server and command-line tools on `PATH` and configure the Development
[BrowserTestUser](../README.md#authenticated-browser-testing) in .NET User Secrets.
The rehearsal uses those credentials for disposable accounts in its private
cluster and signs in through the real login page.

Run the rehearsal from the repository root.

```powershell
dotnet tool restore
./scripts/security/run-authorization-rehearsal.ps1
```

The script creates a private PostgreSQL cluster on a free loopback port. It runs
the build, dependency audit, EF model check, deployment tests, and full suite
in sequence. It restores environment variables and stops and removes the temporary
cluster on exit. Logs and test results remain under `artifacts/` by default.
Loopback trust authentication applies to the disposable rehearsal cluster only.

For a manual rehearsal, `start-disposable-rehearsal.ps1 -OutputDirectory PATH`
writes `cluster.json`. Use its `ConnectionString` as `CONFAST_TEST_CONNECTION_STRING`
and set `CONFAST_ISOLATED_REHEARSAL=1` to enable the opt-in deployment tests. Stop
that cluster with `stop-disposable-rehearsal.ps1 -OutputDirectory PATH -RemoveClusterData`.

## Initial installation

1. Select the actual host, database name, service identities and storage paths.
   Establish backups, restricted administrative access and secret custody first.
2. As a PostgreSQL administrator, create distinct logins and an empty database.

       CREATE ROLE confast_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
       CREATE ROLE confast_runtime LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
       \password confast_owner
       \password confast_runtime
       CREATE DATABASE confast OWNER confast_owner TEMPLATE template0 ENCODING 'UTF8';

   Substitute the database and role names for the target installation. Set
   passwords interactively and configure SCRAM and the appropriate TLS and HBA rules.
3. Restore local tools. Run migrations using the schema owner's connection in a
   separate process with `ConnectionStrings__Confast` supplied from secure configuration.

       dotnet tool restore
       dotnet ef database update --project src/Confast.Web --configuration Release -- --environment Production

   The migration history and permission manifest belong to the schema owner.
   Migrations apply the catalog and seed grants.
4. As the schema owner, review and apply the runtime grants.

       psql -X -v ON_ERROR_STOP=1 -d confast -U confast_owner -v database=confast -v runtime_role=confast_runtime -f scripts/security/runtime-grants.sql

   Use the intended database host, port, and TLS settings. The script checks the
   target and role, grants ordinary Identity and business data access, and limits
   runtime to read-only access for the manifest and authorization state and
   append-only access for authorization and moderation history. The runtime role
   can execute the authorization-lock function. Review and reapply grants after
   each migration.
5. Launch the application with runtime credentials. Supply the
   [bootstrap settings](../README.md#authentication-and-users) through secure
   installation configuration. Startup creates the first account with the
   Administrator role when the database has no users. Remove the settings after creation.
6. Follow [offline Root provisioning](authorization.md#explicit-installation-provisioning)
   to verify sign-in and designate the intended account by Identity ID.
7. Verify Ready state, one matching active credentialed Root membership, normal Root
   sign-in, ordinary-account denial of security management, private-chat restrictions,
   optional ReadOnly behavior and a representative business save and PDF export.
8. Restart the service and verify that accounts, designation, edited grants and
   key material remain stable. Test a pre-restart session and a print capability
   still within its lifetime, then repeat the authorization checks.

## Data Protection and authentication

Configure `DataProtection:KeyRingPath` as an absolute persistent directory and
`DataProtection:ApplicationName` as an installation-specific discriminator shared
by instances of that installation. Separate development/test installations use
different rings/discriminators. With no configured ring, Development intentionally
uses ephemeral keys and sessions do not survive restart.

Limit directory read/write/create permissions to the application identity and
approved backup/recovery operators. Setting a filesystem persistence path requires
an explicit at-rest protection decision. `DataProtection:ProtectWithDpapi=true`
uses current-user Windows DPAPI. For a ring shared across unrelated Windows
accounts or machines, use storage or key protection compatible with those identities.

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

## HTTPS, host and proxy configuration

Set `AllowedHosts` to a semicolon-separated list of the public and internal hosts
the application serves.

With a proxy, set ReverseProxy:KnownProxies to its exact backend-facing IP addresses.
Only X-Forwarded-For and X-Forwarded-Proto from that allowlist are consumed, for one
hop. Forwarded Host is ignored. Leave the list empty for a direct listener. Do not set
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true as a shortcut that trusts arbitrary proxies.
IIS/host integration can add its own forwarding behavior and needs validation on the
selected host.

A one-hop proxy should reject unknown external hosts, terminate HTTPS, overwrite
incoming forwarding fields, and reach a private backend listener. The following
nginx location block illustrates the header settings.

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
forwarding and timeouts, request-size limits, HTTPS redirection, and HSTS.

`InspectionRendering:ApplicationOrigin` sets the trusted internal HTTP or HTTPS
origin for PDF callbacks. Supply only the origin and use a reachable listener
covered by `AllowedHosts`. HTTPS requires a valid certificate trusted by
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

## Backup, restore and offline recovery

Run these steps as the schema owner. Record the exact source and target, take a
verified backup, and stop application writes and workers before changing the
designation or generation. The SQL scripts require an explicit target, expected
epoch, reason, and `approved=yes`. Read the epoch and designation from the target's
`public.authorization_state` row immediately before each operator command.

1. Back up with `pg_dump -Fc` using the operator connection. Protect dumps like
   the source database because they contain password hashes and private data.
   Back up the key ring and file configuration separately.
2. Prepare the cluster roles and a separate empty database, then restore with
   `pg_restore --exit-on-error --dbname=APPROVED_TARGET BACKUP_FILE`. Validate
   owners, extensions, function permissions and search paths, triggers, and
   constraints. Reapply the reviewed runtime grants to the restored database.
   See [PostgreSQL's restore options](https://www.postgresql.org/docs/18/app-pgrestore.html).
3. Compare Identity/membership/catalog/state/history plus representative historical
   inspection/revision and chat/receipt data. Verify an actual login; hashes or
   row counts alone do not prove authentication works.
4. Rotate the generation before accepting restored traffic.

       psql -X -v ON_ERROR_STOP=1 -d APPROVED_TARGET -v database=APPROVED_TARGET -v approved=yes -v expected_epoch=OBSERVED_EPOCH -v reason="Approved restore" -f scripts/security/rotate-generation.sql

   This records operator history and advances epoch. Old scheduled instructions
   become retained InstallationChanged failures; old password delegations fail.
   Do not edit their provenance to revive them.
   A restore also returns grants, activation and credential stamps to the backup's
   state. Generation rotation does not replay later revocations or automatically
   invalidate human cookies. Reconcile security changes since the backup and make
   an explicit session-invalidation decision (affected stamps or key custody) before
   reopening traffic, particularly after a security incident.
5. If Root credentials are lost, select an active replacement account whose
   credentials and sign-in have been verified. Run the recovery script as the
   schema owner using that account's Identity ID.

       psql -X -v ON_ERROR_STOP=1 -d APPROVED_TARGET -v database=APPROVED_TARGET -v approved=yes -v expected_epoch=OBSERVED_EPOCH -v expected_root=OBSERVED_ROOT_ID -v account_id=APPROVED_REPLACEMENT_ID -v excluded_browser_id=BROWSER_TEST_ID_OR_EMPTY -v reason="Approved Root recovery" -f scripts/security/recover-root.sql

   Pass `-v expected_root=` only when the inspected designation is null.
   Supply the configured BrowserTestUser's ID as `excluded_browser_id` when that
   account exists. Otherwise pass `-v excluded_browser_id=`. Recovery atomically
   replaces the designation and membership, rotates the generation, records the
   previous and resulting designations, and validates the consistency constraints.
6. Validate exactly one matching Root membership, enabled protected roles, valid
   catalog, all expected triggers enabled, new Root login, old/ordinary-account
   denial, private-resource denial, and generation-bound delegation failure.
   Reopen traffic only after the operator records those results.

## Audit retention and operations

Authorization history/details and moderation history are append-only for runtime.
Role/user/reset mutations record trusted actors, targets, before/after details,
operation IDs and authorization versions in the mutation transaction. Moderation
records reason/actor/target/time/operation without duplicating deleted bodies.
Scheduled rows retain immutable request identity, issuing version, bounded attempts
and terminal receipt. Production and receiving records preserve business-specific
actor, date, and source history.

Set retention and access policies for inspection history, chat and attachments,
authorization and moderation history, and scheduled-message receipts. Include
backup expiry and restore objectives before automating any deletion.

Monitor readiness/catalog failures, denied operations, database availability,
worker failures/receipt age, storage, backup restoration and SMTP delivery without
recording credentials, hashes, token URLs or chat bodies. Do not use a generic
background superuser. Hosted workers derive current authority from persisted
instructions and start automatically with the application.
