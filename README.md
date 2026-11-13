# ConfastDB

ConfastDB is a browser-based replacement for Conformance Fasteners’ FileMaker
applications. It uses .NET 10, ASP.NET Core, Blazor Interactive Server, Entity
Framework Core, and PostgreSQL. The interface supports desktop and tablet workflows.

- **Quality and master data** include customers and plants, parts, suppliers,
  gages and gage types, master prints, inspections, and certification packages.
  Each inspection retains the inspection criteria revision it was created against.
- **Receiving and logistics** cover received parts, inbound shipments, containers,
  and bills of lading. See [Container tracking](docs/container-tracking.md).
- **Production** includes tracking, scheduling, machine and calendar settings,
  and production review. See [Production scheduling](docs/production-scheduling.md).
- **Home and navigation** show the destinations available to each user and support
  personalized Quick Access shortcuts.
- **Internal chat** includes direct and group messages, public and private channels,
  threads, attachments, polls, scheduled messages, and presence. Private conversations
  require membership to access their content.
- **Administration** includes user accounts, password resets, and a permission matrix
  for managing role grants and inheritance.

## Local setup

Use the .NET 10 SDK selected by [global.json](global.json) and a local PostgreSQL
server with `psql`. PostgreSQL 18 was used for local verification. Run the
PowerShell commands below from the repository root.

1. In `psql` as a PostgreSQL administrator, create a dedicated local login and UTF-8
   database. `\password` prompts for the password without putting it in shell history.

   ```sql
   CREATE ROLE confast_app LOGIN;
   \password confast_app
   CREATE DATABASE confast_dev OWNER confast_app TEMPLATE template0 ENCODING 'UTF8';
   ```

2. Store the connection string in .NET User Secrets and apply the checked-in migrations.

   ```powershell
   dotnet user-secrets set --project src/Confast.Web "ConnectionStrings:Confast" "Host=localhost;Port=5432;Database=confast_dev;Username=confast_app;Password=YOUR_PASSWORD"
   dotnet tool restore
   dotnet ef database update --project src/Confast.Web -- --environment Development
   ```

   Run the migration command again after pulling new migrations. The application
   leaves schema updates to EF tooling.

3. Create the initial account and provision Root as described under
   [Authentication and users](#authentication-and-users).
4. Optionally load sample customers and parts into the development database.

   ```powershell
   psql -h localhost -U confast_app -d confast_dev -f scripts/seed-development.sql
   ```

5. Run the app and open `http://localhost:5105`.

   ```powershell
   dotnet run --project src/Confast.Web
   ```

Development uses an ephemeral Data Protection key ring unless
`DataProtection:KeyRingPath` is configured. With ephemeral keys, restarting the
app invalidates login cookies and outstanding password-reset links.

## Authentication and users

ConfastDB uses ASP.NET Core Identity with username-based login and administrator-managed
accounts. Migrations seed `Administrator`, `Quality`, `Production`, `ReadOnly`, and
the protected `Root Administrator Prime` role. New accounts created through **Users**
default to `ReadOnly`, which authorized administrators can remove. Ordinary users receive
permissions through assigned roles and their inheritance. The server checks permissions
for each protected operation.

For an empty local installation, set the bootstrap values in PowerShell before the
first startup. The password must be at least 12 characters with uppercase and lowercase
letters, a digit, and a symbol.

```powershell
$env:BootstrapAdmin__Username = "admin"
$env:BootstrapAdmin__Email = "admin@example.com"
$env:BootstrapAdmin__DisplayName = "Confast Administrator"
$env:BootstrapAdmin__Password = "REPLACE-WITH-A-UNIQUE-LONG-PASSWORD"
dotnet run --project src/Confast.Web
```

Startup creates the initial Administrator only while the installation has no accounts.
Verify that the account can sign in, then stop the app and clear the bootstrap settings
from the shell.

```powershell
Remove-Item Env:BootstrapAdmin__Username, Env:BootstrapAdmin__Email, Env:BootstrapAdmin__DisplayName, Env:BootstrapAdmin__Password
```

The database starts in `PendingRoot`. Permission-protected workflows require `Ready`
state, which a database owner establishes by explicitly designating an active account by
its Identity ID. Follow the [offline Root provisioning procedure](docs/authorization.md#explicit-installation-provisioning).

Use **Administration → Users** to create accounts, assign roles, issue one-use
password-reset links, or deactivate accounts. Share reset links through a trusted
channel. Use **Administration → Roles** to manage permissions and review the impact
of role changes. Both screens require the corresponding permissions.

## Optional integrations

### Inspection PDFs and certification previews

Inspection sheet PDF export uses Playwright Chromium. Install the browser for the
project’s Playwright version under the account that runs the app.

```powershell
dotnet build src/Confast.Web --configuration Release
pwsh -File src/Confast.Web/bin/Release/net10.0/playwright.ps1 install chromium
```

The inspection renderer uses the application listener for its internal callback.
Behind a proxy, `InspectionRendering:ApplicationOrigin` can set a trusted internal
HTTP or HTTPS origin. Keep render-token query strings out of request and proxy logs.

Certification previews use Poppler’s `pdftoppm` to generate a rasterized copy on first
view. Put `pdftoppm` on the app’s `PATH` or set `PdfPreview:RendererPath` to its full
path. `PdfPreview:ResolutionDpi` and `PdfPreview:MaximumPages` control preview generation.
The original PDF remains in PostgreSQL and is available for download. If the renderer is
unavailable, the embedded viewer attempts to display the original.

### Certification email

The server sends certification packages through one configured SMTP mailbox. Set
`Email:Host`, `Email:UserName`, `Email:Password`, and `Email:DefaultFrom` through
User Secrets in Development or the host’s secret store. The defaults for
`Email:Port` and `Email:UseSsl` are `465` and `true` for implicit TLS.

`Email:SenderMode` defaults to `LoggedInUser`, using the signed-in user’s email for
both `From` and `Reply-To`. The user’s address is also used for the SMTP envelope
sender when `Email:UseLoggedInUserAsEnvelopeSender` is enabled. With
`Email:SenderMode=ApplicationMailbox`, `From` and the envelope use `Email:DefaultFrom`
and `Reply-To` still uses the user’s address. Verify sender acceptance and message
headers with the actual SMTP provider before relying on live delivery.

In Development, `POST /development/smtp-test` prepares an in-memory message for
`fixture@browser-test.invalid`. From a signed-in loopback browser session with
`Certifications.SendEmail`, submit the login form’s `__RequestVerificationToken`
with the POST using the same session cookies. A successful response confirms MIME
preparation. Verify delivery separately with the configured mailbox.

### Chat GIFs

Set `Giphy:ApiKey` through User Secrets for Development or `Giphy__ApiKey` in the
deployment environment. The picker makes requests from the browser, where users can
see the key. GIF search uses a PG-13 filter and remains unavailable without a key.

PostgreSQL stores GIF IDs, favorites, search pages, metadata, and API usage. GIF images
load from GIPHY’s CDN. Category artwork URLs are stored in the writable
`App_Data/chat-gif-category-backgrounds.json` file under the app’s content root.
Retain that file across deployments.

`Giphy:SearchCacheLifetimeHours` defaults to 168. The app tracks API requests in a rolling
hour. `Giphy:HourlyRequestLimit` and `Giphy:RefreshRequestReserve` default to 100 and 20
and control when refreshes of cached searches pause. Uncached searches and ID lookups
still contact GIPHY and count toward usage.

## Integration tests

The integration suite applies migrations and truncates application and Identity tables
between tests. Create a dedicated disposable PostgreSQL database with `test` in its name.
The fixture requires the database owner login. Use a separate database from Development
and any deployed environment. The example reuses the local `confast_app` login from
[Local setup](#local-setup).

```sql
CREATE DATABASE confast_test OWNER confast_app TEMPLATE template0 ENCODING 'UTF8';
```

Set the connection string in PowerShell. For reuse across sessions, set it as a Windows
user environment variable and restart terminals or Codex so they inherit it.

```powershell
$env:CONFAST_TEST_CONNECTION_STRING = "Host=localhost;Port=5432;Database=confast_test;Username=confast_app;Password=YOUR_PASSWORD"
dotnet test --configuration Release
```

Release avoids locking conflicts with the DLL used by a running Debug instance. The fixture
holds a PostgreSQL advisory lock for the suite. Run one test process at a time against
the disposable database and let it finish. A full run can take about a minute, including
quiet stretches.

### Authenticated browser testing

Configure the dedicated `BrowserTestUser` credentials in .NET User Secrets.

```powershell
dotnet user-secrets set --project src/Confast.Web "BrowserTestUser:Username" "browser-test"
dotnet user-secrets set --project src/Confast.Web "BrowserTestUser:Password" "REPLACE-WITH-A-UNIQUE-LONG-PASSWORD"
```

Development startup creates the configured account with initial `Quality` and `Production`
roles if the username is new. Existing accounts keep their credentials, activation, and
role assignments. Leave both settings blank to skip provisioning. If you set either
one, supply both. Sign in through the normal login page with this account and keep it
separate from the designated Root account.

For interactive browser testing, start or reuse a local Development instance.

```powershell
.\scripts\start-browser-test.ps1
```

The helper checks port `5105` first and looks for a free port from `5167` if it is
occupied by another service. It builds new instances into an isolated output directory.

The opt-in Chromium navigation tests launch a separate app against the disposable test
database and use the configured `BrowserTestUser` credentials through the login page.
Install [Playwright Chromium](#inspection-pdfs-and-certification-previews) first.

```powershell
$env:CONFAST_ROLE_BROWSER_TESTS = "1"
dotnet test --configuration Release
```

## Deployment

Use separate PostgreSQL credentials for schema changes and application runtime. Configure
HTTPS, persistent Data Protection keys, backups, and the server-side PDF tools for the
actual host. Start with one application instance since live chat and permission refresh
notifications are process-local. Follow the [deployment runbook](docs/deployment-readiness.md)
for installation, grants, proxy configuration, and recovery.
