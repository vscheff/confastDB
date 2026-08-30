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

4. Add a few development customers and parts with `scripts/seed-development.sql`.
5. Run the application:

   ```powershell
   dotnet run --project src/Confast.Web
   ```

Database migrations are applied explicitly; the web application does not modify the
schema automatically on startup.

Development startup uses an ephemeral Data Protection key ring and Console logging.
This keeps local sandboxed runs independent of Windows DPAPI and Event Log permissions;
production uses the normal persistent Data Protection and logging configuration.

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

The inspection-criteria tests use PostgreSQL because they exercise PostgreSQL-specific
constraints, transactions, triggers, and `xmin` concurrency tokens. They deliberately
truncate application tables between cases, so they need a disposable database whose name
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
