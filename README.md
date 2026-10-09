# Confast

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
with User Secrets. The account is created on Development startup with the `Quality` and
`Production` roles, which exercise normal floor workflows without granting administration privileges:

```powershell
dotnet user-secrets set --project src/Confast.Web "BrowserTestUser:Username" "browser-test"
dotnet user-secrets set --project src/Confast.Web "BrowserTestUser:Password" "REPLACE-WITH-A-UNIQUE-LONG-PASSWORD"
```

The password placeholder above is not a credential; replace it locally with a password
that meets the application's normal Identity policy. Environment variables can override
the same settings as `BrowserTestUser__Username` and `BrowserTestUser__Password`.
When either setting is missing, browser-test-user provisioning is skipped and Production
never provisions this account.

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
unassigned `Root Administrator Prime`. Every human account retains ReadOnly. The rest of the application
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
exist. If the configured username already exists, it ensures that account has the
Administrator role. Remove all four variables after the account has been created;
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
accounts must be deactivated instead of deleted. The last active administrator cannot
be deactivated or stripped of that role through the UI.

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

### Run the Rackspace SMTP proof of concept

This test sends one small email through the configured Rackspace mailbox using the
currently logged-in Confast user as the preferred sender identity. It is available only
in Development and only to an Administrator. It does not modify certification packages
or database records.

1. Set `Email:TestRecipient` to an inbox you can inspect. The logged-in Administrator
   also needs a valid email address on their Users-page account.
2. Start the app in Development from PowerShell:

   ```powershell
   $env:ASPNETCORE_ENVIRONMENT = "Development"
   dotnet run --project src/Confast.Web
   ```

3. Open the local app, log in as an Administrator, and open the browser developer
   console (`F12`, then **Console**).
4. Run this command in that console. It uses the logged-in browser session; do not put
   SMTP credentials in the browser or in this command.

   ```js
   fetch("/development/smtp-test", {
     method: "POST",
     credentials: "same-origin"
   })
   .then(async response => ({ status: response.status, body: await response.text() }))
   .then(console.log)
   ```

5. A `200` response means the SMTP server accepted the message. Inspect the test
   recipient's message source and confirm:

   - `From` displays the logged-in user.
   - `Reply-To` is the logged-in user's address, then send a reply to verify delivery.
   - `Return-Path` is what Rackspace accepted as the envelope sender.
   - SPF, DKIM, and DMARC do not report obvious failures.

An SMTP rejection returns a `400` with a safe error message; inspect the server console
log for the detailed exception. A `403` means the logged-in account is not an
Administrator. The route is not mapped outside Development and deliberately disables
antiforgery because it has no form/UI; role authorization and the Development-only
mapping remain in force.

If Rackspace rejects or rewrites the preferred `From` identity, configure the fallback
and restart the app:

```powershell
dotnet user-secrets set --project src/Confast.Web "Email:SenderMode" "ApplicationMailbox"
```

The fallback keeps the logged-in user as `Reply-To` but uses the authenticated
certifications mailbox as visible `From`. Do not configure actual SMTP credentials in
`appsettings.json`.

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

Public channels are discoverable without membership, but users must join before
reading or sending. Private channels and direct messages require membership for
all reads and writes. Private channel owners manage membership. Unread counts
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
