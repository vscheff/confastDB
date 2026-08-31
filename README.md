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
   CREATE DATABASE confast_dev OWNER confast_app;
   ```

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

## Authentication and users

Confast DB uses ASP.NET Core Identity in the existing PostgreSQL database. Applying
the migrations creates the `identity_*` tables and seeds these roles:
`Administrator`, `Quality`, `Production`, and `ReadOnly`. The rest of the application
requires an authenticated user; only administrators can open `/admin/users`.

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
CREATE DATABASE confast_test OWNER confast_app;
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

The test fixture applies the EF Core migrations automatically. Its database-name check
is only a guardrail, not magic; `confast_prod_test` technically passes it and is still a
terrible place to run destructive tests.
