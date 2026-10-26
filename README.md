# Confast

**Phase 3G verification (October 26, 2026):** runtime privilege separation, isolated restore/Root recovery, shared keys, proxy controls and two-process authorization are covered by the [final verification record](docs/authorization-final-verification.md). Use the [first-deployment preparation guide](docs/deployment-readiness.md) for operator procedures and unresolved hosting decisions. ConfastDB has never been deployed; this work performs no deployment.

**Phase 3F status (October 16, 2026):** the catalog-backed role matrix, provenance and impact previews, safe role/user editing, process-local advisory refresh, and channel moderation-history UI are implemented. See [the administration UI record](docs/authorization-administration-ui.md) for behavior, validation, and remaining Phase 3G limits. This supersedes historical matrix/refresh/history-UI deferrals below.

**Phase 3E-2 status (October 16, 2026):** scheduled delegation/lifecycle, scoped renderer execution, create-only bootstrap and guarded Development diagnostics are implemented. See [the background and delegation record](docs/authorization-background-delegation.md) for the complete execution inventory, migration, validation and remaining Phase 3F/3G boundaries. This supersedes historical background/bootstrap deferrals below.

**Phase 3E-1 status (October 15, 2026):** chat permissions, media/subscription privacy and scoped moderation are implemented in [the chat authorization record](docs/authorization-chat.md). Its boundary inventory and verification supersede older chat deferrals below; the full hosted-worker redesign remains Phase 3E-2.

**Phase 3D-2 status (October 15, 2026):** logistics, remaining receiving, production configuration/scheduling/tracking/reporting and fixed schedule-read maintenance now use trusted permission boundaries. See [the logistics and production implementation record](docs/authorization-production-logistics.md) for the complete method inventory, verification and remaining Phase 3E boundaries. Historical deferrals below are superseded for this scope.

A ground-up replacement for the Conformance Fasteners FileMaker applications.

The application currently contains Customers, Parts, and versioned Part inspection
criteria vertical slices using .NET 10, Blazor Interactive Server, Entity Framework
Core, and PostgreSQL.

## Local setup

1. Run PostgreSQL. From `psql` as the `postgres` superuser, create a dedicated
   development login and database. `\password` prompts without putting the password
   in this repository or your shell history:

   ```sql
   CREATE ROLE confast_app LOGIN;
   \password confast_app
   CREATE DATABASE confast_dev OWNER confast_app TEMPLATE template0 ENCODING 'UTF8';
   ```

   PostgreSQL fixes a database's encoding when it is created. To move an existing
   non-UTF-8 database, use `pg_dump` and restore into a new database created with
   `TEMPLATE template0 ENCODING 'UTF8'`; keep the original until the restored database
   has been verified.

2. Store the complete development connection string outside source control:

   ```powershell
   dotnet user-secrets set --project src/Confast.Web "ConnectionStrings:Confast" "Host=localhost;Port=5432;Database=confast_dev;Username=confast_app;Password=YOUR_PASSWORD"
   ```

3. Restore local tools and apply migrations:

   ```powershell
   dotnet tool restore
   dotnet ef database update --project src/Confast.Web
   ```

4. Create the first administrator as described in [Authentication and users](#authentication-and-users).
5. Add a few development customers and parts with `scripts/seed-development.sql`.
6. Run the application:

   ```powershell
   dotnet run --project src/Confast.Web
   ```

Database migrations are applied explicitly; the web application does not modify the
schema automatically on startup.

Development startup uses an ephemeral Data Protection key ring and Console logging.
This keeps local sandboxed runs independent of Windows DPAPI and Event Log permissions;
production uses the normal persistent Data Protection and logging configuration.

To enable authenticated browser testing, configure a dedicated Development-only account
with User Secrets. The account is created on Development startup with the initial `Quality` and
`Production` roles, which exercise normal floor workflows without granting administration privileges:

```powershell
dotnet user-secrets set --project src/Confast.Web "BrowserTestUser:Username" "browser-test"
dotnet user-secrets set --project src/Confast.Web "BrowserTestUser:Password" "REPLACE-WITH-A-UNIQUE-LONG-PASSWORD"
```

The password placeholder above is not a credential; replace it locally with a password
that meets the application's normal Identity policy. Environment variables can override
the same settings as `BrowserTestUser__Username` and `BrowserTestUser__Password`.
When either setting is missing, browser-test-user provisioning is skipped and Production
never provisions this account. Existing accounts are left untouched: startup does not reactivate them, reset credentials or restore removed roles.

### Browser-testing startup

Codex or another local browser tester can start the app without coordinating with an
already-open Visual Studio instance:

```powershell
.\scripts\start-browser-test.ps1
```

The helper first probes `http://127.0.0.1:5105/` and reuses it when it is healthy. If
that port is occupied but not serving ConfastDB, it selects the next available port
starting at `5167`. New instances explicitly run in Development, and normal builds use
the isolated `src/Confast.Web/.codex/browser-build-output` path so a separately running
Debug instance does not lock the browser-test build. Use `-NoBuild` only after the
isolated output has already been built. Development data-protection keys are ephemeral,
so restarting an instance invalidates old login/antiforgery cookies; use a fresh browser
profile or clear the local site's cookies when testing across a restart.

## Authentication and users

Confast DB uses ASP.NET Core Identity in the existing PostgreSQL database. Applying
the migrations creates the `identity_*` tables and seeds these roles:
`Administrator`, `Quality`, `Production`, `ReadOnly`, and the protected, initially
unassigned `Root Administrator Prime`. New accounts default to ReadOnly; authorized role selection can replace or remove it. Accounts with no roles have no implicit permission grants. The rest of the application
requires an authenticated user. `/admin/users` requires `Users.Read`; `/admin/roles`
requires `Roles.Read`, with separate permissions for each administrative operation.

The [Phase 3B authorization foundation](docs/authorization-foundation.md) adds the
164-key catalog, stored grants/inheritance, versioned evaluator/cache, audit infrastructure
and explicit PendingRoot provisioning. Existing business authorization paths remain in use;
new permission guards fail closed until Root is deliberately provisioned by account ID.
Startup never selects Root. Read that document before deployment or manual provisioning,
especially the migration/runtime credential separation and offline recovery requirements.

[Phase 3C delegated administration](docs/authorization-administration.md) implements
peer/lower role assignment, safe lower-role editing, guarded account administration,
tracked one-use password-reset delegation, and stable permission-qualified inspector
identity. These migrated boundaries require a Ready installation; PendingRoot has no
legacy Administrator fallback. Old untracked administrative reset links must be reissued.
New inspection creation and selected receipt operations also use permission guards;
unmigrated business services retain their existing authorization. Read the implementation
record for exact boundaries, additive backfill behavior, tests and remaining phases.

[Phase 3D-1 core business authorization](docs/authorization-core-business.md) now guards Customers, Plants, Parts, Suppliers, Gages/Types, Inspection Criteria and Master Prints, all inspection actions, certification documents/packages/email, and their settings services. Services resolve the current authenticated actor and effective grants for each operation. Document/export HTTP policies share a scoped operation with their service adapters; page access never authorizes a later save. ReadOnly remains optional, and Root receives permission authority only through its protected designation. Logistics, production, remaining receiving and chat migration remain later work.

There is no public registration. To create the first administrator, set the bootstrap
values outside source control before starting the application for the first time. In
PowerShell, environment variables are the least surprising option:

```powershell
$env:BootstrapAdmin__Username = "admin"
$env:BootstrapAdmin__Email = "admin@example.com"
$env:BootstrapAdmin__DisplayName = "Confast Administrator"
$env:BootstrapAdmin__Password = "USE-A-UNIQUE-LONG-PASSWORD-HERE"
dotnet run --project src/Confast.Web
```

The username is used to log in; email remains separate contact/profile information.
The password must be at least 12 characters and include uppercase, lowercase, a
number, and a symbol. Bootstrap creation only creates a new account when no users
exist. If any account already exists, bootstrap does nothing; it never restores roles, activation or credentials. Remove all four variables after the account has been created;
leaving a privileged bootstrap password in a service definition forever is not a
bootstrap mechanism, it is a back door with paperwork.

Administrators manage subsequent accounts and role membership from **Users** in the
application header. New users are created without a password. The Users page produces
a password-reset link that should be given to that user through a trusted channel so
the administrator never needs to know the password. The same action initiates later
password resets. Deactivation revokes the user's security stamp so existing sessions
are rejected. Administrators may also permanently delete another user after an
explicit confirmation; they cannot delete their own account. Once business records
reference users, those relationships should use restrictive foreign keys so referenced
accounts must be deactivated instead of deleted. The designated Root account is protected from ordinary account editing and role assignment; ordinary Administrator membership is configurable.

Identity cookies and password-reset tokens use ASP.NET Core Data Protection. The
development process deliberately uses ephemeral keys, so restarting it invalidates
development cookies and outstanding reset links. Production must retain the normal
persistent Data Protection key ring, and multiple application instances must share a
protected key ring.

After pulling a migration that changes Identity or application data, update the
database explicitly before starting the application:

```powershell
dotnet restore
dotnet ef database update --project src/Confast.Web
```

## Certification email delivery

Certification packages are assembled by the server and sent through one Rackspace SMTP
mailbox. No client software or per-user SMTP passwords are used. Configure the SMTP
secret outside source control, for example with user secrets during development:

```powershell
dotnet user-secrets set --project src/Confast.Web "Email:Host" "secure.emailsrvr.com"
dotnet user-secrets set --project src/Confast.Web "Email:Port" "465"
dotnet user-secrets set --project src/Confast.Web "Email:UseSsl" "true"
dotnet user-secrets set --project src/Confast.Web "Email:UserName" "certifications@example.com"
dotnet user-secrets set --project src/Confast.Web "Email:Password" "YOUR_RACKSPACE_SMTP_PASSWORD"
dotnet user-secrets set --project src/Confast.Web "Email:DefaultFrom" "certifications@example.com"
dotnet user-secrets set --project src/Confast.Web "Email:TestRecipient" "your.test.inbox@example.com"
```

The preferred setting is `Email:SenderMode=LoggedInUser`: Confast authenticates as the
certifications mailbox while using the logged-in user's configured email as `From`,
`Reply-To`, and (when enabled) the SMTP envelope sender. Each user must therefore have
an email address in the Users page. This is deliberately a proof-of-concept setting:
Rackspace must be tested with a real same-domain user address and the received headers
must be checked for rewritten `From`, `Return-Path`, SPF/DKIM, and DMARC results.

### Development email preparation diagnostic

`POST /development/smtp-test` is registered only in Development. It requires current
`Certifications.SendEmail`, a loopback client and a valid antiforgery token. It validates
the current account's sender and prepares a MIME attachment using an in-memory transport
and the fixed `fixture@browser-test.invalid` recipient. It never reads `Email:*` settings
or connects to SMTP. This replaces the former live Rackspace proof-of-concept endpoint.

From the signed-in local browser's developer console, fetch the normal login form to obtain
an antiforgery token, then submit the diagnostic using the same browser cookies:

```js
const html = await (await fetch('/login', { credentials: 'same-origin' })).text();
const form = new DOMParser().parseFromString(html, 'text/html');
const token = form.querySelector('[name="__RequestVerificationToken"]').value;
const result = await fetch('/development/smtp-test', {
  method: 'POST', credentials: 'same-origin',
  body: new URLSearchParams({ __RequestVerificationToken: token })
});
console.log(result.status, await result.json());
```

A successful response means MIME preparation succeeded, not mailbox delivery or Rackspace
sender/header acceptance. Live deliverability verification belongs to a separately controlled
operator procedure. The Development business-date control requires the current protected
`Authorization.ManageSecurity` permission and never changes audit/expiry clocks.

The PDF renderer uses the actual server listener for its internal callback. Reverse-proxy
installations can set `InspectionRendering:ApplicationOrigin` to an explicitly trusted internal
HTTP/HTTPS origin. HTTPS certificates must validate; request Host headers never select the
render callback destination. Keep render-token query strings out of proxy/request tracing.
## Certification PDF previews

Certification originals remain byte-for-byte unchanged in the database. The embedded
viewer uses a separate rasterized preview generated on first view with Poppler's
`pdftoppm` executable. Install Poppler on the web server and either put `pdftoppm` on
the process `PATH` or set `PdfPreview:RendererPath` to its full path. Preview settings
can be adjusted with `PdfPreview:ResolutionDpi` and `PdfPreview:MaximumPages`.

If the renderer is unavailable, uploads and original downloads continue to work, but
the embedded viewer falls back to the original PDF and may still fail for PDFs that
PDF.js cannot decode.

## Internal chat

Chat uses one conversation model for direct messages and channels. Direct messages
have exactly two members in this slice. Their sorted Identity user IDs form a
unique `direct_pair_key`, so concurrent attempts to open the same pair resolve
to one conversation. A future multi-user private conversation can use the same
conversation/member tables without a pair key.

Public channels include all Active accounts. Authorized chat operations reconcile
missing public memberships, starting new members read through the latest existing message.
Chat.Access is required before this maintenance or any protected chat read. Private
channels, direct messages and group conversations require current membership for all
content reads and writes; permission authority never substitutes for private membership. Private channel owners manage membership. Unread counts
use one `last_read_message_id` per member; a composite foreign key keeps the
marker inside its conversation. Messages are soft-deleted and retain their
stored body and deletion audit fields while the UI hides deleted text.

Live updates use the application's existing Blazor Interactive Server SignalR
connection. A process-local publisher prompts connected chat panels to reload
committed state from PostgreSQL. Opening the panel also reloads state after a
disconnect. If the application is deployed on multiple server instances, this
publisher will need a distributed backplane to reach circuits on other instances.

Chat presence is derived from database-backed client heartbeats. A client becomes
Idle after five minutes without activity, and its session expires 45 seconds after
the last heartbeat; users with no live sessions appear Offline. Multiple open
clients count as one user, so activity in any client keeps the user Online.
Users may choose Online, Do Not Disturb, or Invisible and may save an emoji and
short status message. Invisible appears Offline to other users and hides the
custom status from them. The selected presence preference and custom status
survive sign-out; actual Online and Idle state always requires a live client.

## Chat GIF provider

The chat GIF picker uses GIPHY. Configure `Giphy:ApiKey` through .NET User Secrets
for Development, or `Giphy__ApiKey` in the deployment environment. For example:

```powershell
dotnet user-secrets set "Giphy:ApiKey" "YOUR_GIPHY_API_KEY" --project src/Confast.Web
```

Use a key intended for the web integration: GIPHY requires direct browser API
requests, so this key is visible to users of the picker. No key is committed in
application settings. Without a key the GIF tab explains that search is unavailable;
Emoji continues to work. Search uses a PG-13 rating filter. Favorites are private to
each user and persist in PostgreSQL as GIF IDs. Messages also store IDs rather than
media URLs or downloaded GIFs, and display a fallback when GIPHY cannot resolve them.
Apply the `AddChatGifs` migration before running the updated app.

The GIF tab initially shows categories only. Selecting a category, searching, or
opening Favorites replaces the categories with results; the Categories button
returns to the initial screen. Blank searches return to categories without an API call.
Category image URLs are selected once and saved in
`src/Confast.Web/App_Data/chat-gif-category-backgrounds.json`, shared across users
and retained across application restarts. Keep that file between deployments and
allow the app to write to `App_Data`. Once saved, category backgrounds are not
searched again.

Search pages and individual GIF metadata now persist in PostgreSQL (apply the
`AddChatGifCache` migration). Search keys use trimmed, case-insensitive text, page
offset/size, PG-13 rating, and English language. Successful pages, including empty
results, are fresh for `Giphy:SearchCacheLifetimeHours` (default 168, or seven days);
stale pages refresh on demand if request budget permits. If the refresh fails or
the budget is low, the existing page is returned without changing its cache age.
Errors are not cached. Identical
concurrent searches share the first lookup within one application process.
Search results populate the shared GIF metadata cache, so sending a selected GIF,
reopening its chat, or viewing it in Favorites requires no further API lookup.
Older uncached GIF IDs are resolved once on demand and then saved. Cached Favorites
remain visible if another uncached favorite cannot be resolved. GIF image bytes
still load directly from GIPHY's CDN, and removed/broken images may become unavailable.
This deliberately retains media URLs; GIPHY's published guidelines require explicit
approval and revalidation for media caching. The application does not automatically
expire chat GIF metadata or download GIF files. Successful search refreshes update
metadata for returned GIFs; previously sent GIFs remain cached regardless of search age.

Apply `AddChatGifRequestBudget` for persisted API usage accounting. Configure
`Giphy:HourlyRequestLimit` (default 100) and `Giphy:RefreshRequestReserve` (default 20)
to match the API key's actual allowance. Request admission uses a rolling 60-minute
window shared across users and application instances. Stale refreshes stop when
remaining capacity reaches the reserve. Uncached searches and GIF ID lookups always
query the provider, even above the local limit, so the cache can continue growing;
they still count toward usage and may encounter GIPHY's own rate limit.
Category initialization searches also count toward usage.
Cached responses and GIF image downloads do not consume this API budget.
Search attempts are reserved before invoking the browser; each batched ID lookup
reserves one request immediately before fetching. Failed/aborted attempts count
conservatively, and a search reservation is not refunded if the browser cannot
send it (for example, a disconnected circuit or missing key). Usage records are
retained for seven days in `chat_gif_api_requests`; grouping `requested_at_utc` by
UTC hour gives historical hourly counts. Calls made outside this application's
database are not included, so the configured limit is a local safety budget,
not an authoritative reading of GIPHY's remaining quota.

Clicking a GIF sends it immediately to the current conversation or thread, with the
current reply target. Text drafts, uploads, and scheduled text remain in the composer.

## Integration tests

The integration tests use PostgreSQL because they exercise PostgreSQL-specific
constraints, transactions, triggers, and `xmin` concurrency tokens. They deliberately
truncate application and Identity user tables between cases, so they need a disposable database whose name
contains `test`. Never aim this at the development or production database unless you are
trying to turn a test run into a postmortem.

### Create the test database and set the login password

From `psql` as the PostgreSQL `postgres` superuser, make sure the application login has
a password, then create a dedicated test database owned by that login:

```sql
CREATE ROLE confast_app LOGIN; -- only if the role does not already exist
\password confast_app
   CREATE DATABASE confast_test OWNER confast_app TEMPLATE template0 ENCODING 'UTF8';
```

`\password confast_app` prompts for the password and executes the equivalent of
`ALTER ROLE confast_app PASSWORD ...` without leaving the secret in shell history. Use
the same password in the test connection string below. If `confast_app` already exists,
skip `CREATE ROLE`; use `\password confast_app` whenever its password needs to be
changed to match the connection string.

### Configure the test connection string

On Windows, add a **user** environment variable so new terminals and Codex sessions can
run the tests without setting it for each individual PowerShell process:

1. Open **Edit environment variables for your account** from the Start menu.
2. Under **User variables**, select **New**.
3. Set the variable name to `CONFAST_TEST_CONNECTION_STRING`.
4. Set its value to the connection string below, replacing `YOUR_PASSWORD` with the
   password entered through `\password confast_app`.
5. Close and reopen terminals and Codex so they inherit the new user environment.

```text
Host=localhost;Port=5432;Database=confast_test;Username=confast_app;Password=YOUR_PASSWORD
```

Then run:

```powershell
dotnet test
```

The test fixture applies the EF Core migrations automatically and holds a PostgreSQL
advisory lock for the entire suite. The lock prevents two `dotnet test` processes from
concurrently truncating the same database. If a run reports that another test runner is
using the database, wait for that run to finish instead of launching a replacement.

A full suite can take about a minute and may be quiet when using minimal console logging.
Quiet output is not a hang: keep polling the original process. Starting a second run while
the first is still active used to cause PostgreSQL deadlocks during fixture resets.

The database-name check is only a guardrail, not magic; `confast_prod_test` technically
passes it and is still a terrible place to run destructive tests.
