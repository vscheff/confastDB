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
constraints, transactions, triggers, and `xmin` concurrency tokens. Create a disposable
database whose name contains `test`, then provide its connection string for the test
process:

```powershell
$env:CONFAST_TEST_CONNECTION_STRING = "Host=localhost;Port=5432;Database=confast_test;Username=confast_app;Password=YOUR_PASSWORD"
dotnet test
```

The name check is a guard because the tests truncate application tables between cases.
Do not point this variable at the development or production database. That would be an
impressively efficient way to ruin the afternoon.
