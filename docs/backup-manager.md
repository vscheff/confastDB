# Backup Manager

Open **Administration → Backup Manager** for backup health, server-side configuration and execution history. The migration seeds a disabled daily 02:00 server schedule, a 14-backup rolling limit, a 2 GiB minimum free-space reserve, catch-up, and full archive reads. Select and test a provisioned destination before saving the first configuration. Choose each address's notification types under **Notifications**. Success email is opt-in for each address.

## What a backup contains

| Resource | Recovery source |
| --- | --- |
| PostgreSQL schema, business and inspection history, Identity users, roles and grants, authorization audit, backup configuration and catalog | A complete custom-format archive of the application database |
| Master prints | `inspection_criteria_revisions.master_print_content` and associated revision metadata |
| Material, heat treatment, and other certification PDFs | `certification_documents.content`, generated `preview_content` when stored, and their inspection and certification relationships |
| Chat and attached files | Message and scheduled-message attachment tables, their original bytes and associations, and database-backed group icons |
| User profile images | `identity_user_profile_pictures.data` |
| Other PostgreSQL storage | Application tables, sequences and PostgreSQL large objects, including tables used by application caches. The Factory Reset operation journal is restored as an empty table. |
| GIF category artwork map, if present | `chat-gif-category-backgrounds.json` in `ManagedFiles:Directory` or the default `App_Data`. Restore it to that directory. |

The database uses a consistent PostgreSQL logical snapshot. Ordinary backups copy the artwork map after the dump. If instances maintain separate copies, an ordinary backup contains the producing instance’s copy. [Factory Reset](factory-reset.md) suspends application mutations for both snapshots and requires shared managed storage across replicas. The rest of `App_Data`, temporary files, generated print output on disk, tool caches, binaries, logs, build artifacts, static assets, and other backup sets are outside the filesystem allowlist.

Separately retain matching application releases and tools, deployment configuration and secrets, PostgreSQL roles and other cluster-wide resources, and the Data Protection key ring and its wrapping credentials. This includes TLS and SMTP credentials, the backup login and private password file, and the identity markers and control directory described below. Back up the deployment’s actual configuration sources, including environment variables, service settings or .NET User Secrets where used. The manifest carries database and deployment identifiers plus administrator-supplied labels and notes. Treat the entire set as highly sensitive and keep secrets out of labels and notes. [PostgreSQL 18 pg_dump documentation](https://www.postgresql.org/docs/18/app-pgdump.html)

### Data Protection

`DataProtection:KeyRingPath` selects a persistent filesystem ring. Instances share a ring when they use the same path contents and `DataProtection:ApplicationName`. Development defaults to ephemeral keys without a configured path. Explicit filesystem persistence uses current-user DPAPI only when `DataProtection:ProtectWithDpapi=true` on Windows. Otherwise at-rest protection depends on the host and storage configuration. Production should provision the ring deliberately instead of relying on the framework’s host-dependent default location.

Authentication cookies, antiforgery, Identity password reset tokens, and time-limited inspection print tokens depend on Data Protection. The repository inventory found no business-document bytes or durable business fields encrypted with this ring. Identity passwords are hashed, and password-reset delegation stores a token digest. Loss of the ring invalidates sessions and outstanding protected tokens. Recover the ring together with its application name and protection material. For DPAPI this includes the service identity and recoverable Windows user key material. If infrastructure wraps the ring with a certificate, preserve the private key and certificate access as well. [Deployment Data Protection guidance](deployment-readiness.md#data-protection-and-authentication)

## Provisioning

1. Install matching PostgreSQL 18 `pg_dump` and `pg_restore` binaries in a trusted directory accessible to the application service. The app checks utility versions before dumping. Stop old application instances before applying `20261124145642_AddBackupManager` as the schema owner. Reapply `scripts/security/runtime-grants.sql` before starting the new version. The migration adds `backup_settings`, `backup_executions`, `backup_audit` and `backup_alerts`. Backup audit rows are append-only, and no existing roles gain backup permissions.
2. Provision a dedicated PostgreSQL login that can connect to this database and read all of its application schemas, tables, sequences and large objects. Use `scripts/security/backup-grants.sql` as the schema owner to grant read access to current objects in `public`. Review privileges after new schema or large-object changes. Keep the application runtime login restricted to its ordinary grants. If a future table uses row-level security, explicitly review how the backup login can bypass it. `pg_dump` runs with row security disabled and fails if the login would see an incomplete table. Current application tables do not require `BYPASSRLS`. Foreign-table contents cannot be included in this pipeline. [PostgreSQL 18 pg_dump documentation](https://www.postgresql.org/docs/18/app-pgdump.html)
3. Put the backup login’s authentication in an explicit libpq password file under the service account’s control. On Unix restrict it to mode `0600`. On Windows use a private ACL. The process receives the file path in its environment. Use the application’s single PostgreSQL host, port, database name and TLS mode for the backup login. Allow that login from the application services in `pg_hba.conf`. Keep the file outside the stores, coordination directory and web root. [PostgreSQL 18 password file documentation](https://www.postgresql.org/docs/18/libpq-pgpass.html)
4. Provision a private shared coordination directory and one private directory per approved destination outside the application, web and Data Protection directories. Assign stable IDs. Give the service identity read, create, write, rename and delete rights on managed directories. Use restrictive inherited ACLs on Windows and a private owner directory on Unix. Use encrypted volumes or storage-managed encryption at rest with appropriate key custody and restrict backup access to authorized recovery operators.
5. Write the identity files below on the actual mounted storage. On every app instance, configure the same deployment ID, destination aliases and IDs, and shared coordination directory. All instances must use the same server time zone. Confirm file locks and atomic rename work across the service identities and hosts used in the deployment.

An example host configuration without credentials follows. Environment variables use the standard double-underscore separator, such as `Backups__Destinations__primary__Path`.

```json
{
  "Backups": {
    "DeploymentId": "00000000-0000-4000-8000-000000000001",
    "CoordinationDirectory": "D:\\confast-backup-control",
    "PostgresBinDirectory": "C:\\Program Files\\PostgreSQL\\18\\bin",
    "UserName": "confast_backup",
    "PassFile": "D:\\confast-private\\backup.pgpass",
    "TimeoutMinutes": 120,
    "DownloadTimeoutMinutes": 45,
    "Destinations": {
      "primary": {
        "Id": "00000000-0000-4000-8000-000000000002",
        "Path": "\\\\backupserver\\confast\\primary"
      }
    }
  }
}
```

Use installation-specific IDs and paths. The coordination directory contains `.confast-backup-control` with the lowercase deployment GUID followed by an optional newline, and an empty `.confast-backup-lock` file. Each destination contains `.confast-backup-owner` with the deployment GUID on the first line and that destination’s GUID on the second. Use UTF-8 text. Create marker files only on the intended volumes. Keep the mount point underneath a Linux mount empty and never copy a marker into a network share’s local fallback directory. Windows services should use a service-accessible UNC path or their own mapped drive. Reparse points, junctions and symlink components are rejected. If storage detaches or the expected identity changes, the worker records unavailability and preserves prior sets.

Choose the approved destination in Backup Manager and run **Test Destination**. This tests temporary-file read, write, rename, delete and reports available capacity when the platform supports it. A destination change leaves existing sets in their original locations. Keep their aliases and identities in host configuration so that all historical locations remain discoverable. Move or change a storage root only during coordinated maintenance. Preserve the existing marker and check the physical volumes before reopening the application.

### Local Windows setup

Run the PowerShell blocks from the repository root in the same session. Use the PostgreSQL 18 client tools and your existing [Development database connection](../README.md#local-setup).

1. Match the PowerShell connection values to the effective Development `ConnectionStrings:Confast`. The checked-in default is `confast_dev`. For example, if Development User Secrets point to `confast_dev_utf8`, set `$dbName` to `confast_dev_utf8`. Use that same database for the grants and the password file. If the Backup Manager migration is pending, stop the app and apply the migrations as the schema owner.

   ```powershell
   $dbHost = 'localhost'
   $dbPort = 5432
   $dbName = 'confast_dev'
   $dbOwner = 'confast_app'
   $backupRole = 'confast_backup'

   dotnet ef database update --project src/Confast.Web --startup-project src/Confast.Web --configuration Release -- --environment Development
   ```

2. Create the backup login once as a PostgreSQL administrator. For a typical local installation, open `psql` as `postgres`.

   ```powershell
   psql -X -h $dbHost -p $dbPort -U postgres -d postgres
   ```

   At the `psql` prompt, enter the following commands. `\password` prompts for the new password.

   ```sql
   CREATE ROLE confast_backup LOGIN NOINHERIT
       NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
   \password confast_backup
   \q
   ```

3. From PowerShell, run the grant script as the **schema owner**.

   ```powershell
   psql -X -h $dbHost -p $dbPort -U $dbOwner -d $dbName `
       -v "database=$dbName" -v "backup_role=$backupRole" `
       -f scripts/security/backup-grants.sql
   ```

   If the script reports that `public.backup_settings` does not exist, check the selected database and whether it contains the Backup Manager migration.

   ```powershell
   psql -X -h $dbHost -p $dbPort -U $dbOwner -d $dbName `
       -c "SELECT current_database() AS database, to_regclass('public.backup_settings') AS backup_table;"
   ```

4. Create the control directory, the first destination and the private credentials directory. Run this once under the Windows account that runs the local app. Keep the generated GUIDs and their markers for future use. For an existing setup, continue at [Add another destination](#add-another-destination).

   ```powershell
   $backupBase = Join-Path $env:LOCALAPPDATA 'ConfastDB-Backup'
   if (Test-Path -LiteralPath $backupBase) {
       throw 'That directory already exists. Keep its markers or choose a fresh directory.'
   }

   $control = Join-Path $backupBase 'control'
   $store = Join-Path $backupBase 'primary'
   $credentials = Join-Path $backupBase 'credentials'
   New-Item -ItemType Directory -Force -Path $control, $store, $credentials | Out-Null

   $deploymentId = (New-Guid).ToString('D')
   $storeId = (New-Guid).ToString('D')

   [IO.File]::WriteAllText((Join-Path $control '.confast-backup-control'), $deploymentId)
   [IO.File]::WriteAllText((Join-Path $control '.confast-backup-lock'), '')
   [IO.File]::WriteAllText((Join-Path $store '.confast-backup-owner'), "$deploymentId`n$storeId")
   ```

5. Write the password file and set the app configuration in Development User Secrets. Enter the password you just set for `confast_backup` when prompted. The password file contains plaintext, so keep the credentials directory private. Set `$pgBin` to the installed PostgreSQL 18 client directory if it differs.

   ```powershell
   $pgBin = 'C:\Program Files\PostgreSQL\18\bin'
   $passFile = Join-Path $credentials 'backup.pgpass'
   $backupCredential = Get-Credential -UserName $backupRole -Message 'Enter the backup login password'
   if (!$backupCredential) { throw 'Password entry canceled.' }

   $passFields = @($dbHost, "$dbPort", $dbName, $backupRole, $backupCredential.GetNetworkCredential().Password)
   $passLine = ($passFields | ForEach-Object { $_.Replace('\', '\\').Replace(':', '\:') }) -join ':'
   [IO.File]::WriteAllText($passFile, "$passLine`n")
   Remove-Variable backupCredential, passFields, passLine

   @{
       Backups = @{
           DeploymentId          = $deploymentId
           CoordinationDirectory = $control
           PostgresBinDirectory  = $pgBin
           UserName              = $backupRole
           PassFile              = $passFile
           Destinations          = @{
               primary = @{ Id = $storeId; Path = $store }
           }
       }
   } | ConvertTo-Json -Depth 5 | dotnet user-secrets set --project src/Confast.Web
   ```

6. Start the app and sign in as Root Administrator. Open **Administration → Backup Manager**, select **primary**, and click **Test Destination**. Leave **Automatic Backups** off and **Read Full Archive After Creation** on for the first check, then save. Queue a manual backup and confirm that it reaches **Completed** with **Archive Read · SHA-256 Checked**. Enable automatic backups, save, and review the upcoming executions. Test [offline restoration](#offline-restore) into a disposable database before relying on the backup for recovery.

### Add another destination

Choose an unused name and a separate, non-overlapping directory on the application host. The example below creates a sibling of `primary`. For another drive or network store, set `$destinationPath` to an absolute path or a service-accessible UNC subdirectory on the intended storage. Use your configured `Backups:CoordinationDirectory` if it differs from the example.

The new directory uses the existing deployment GUID and a fresh destination GUID. For an existing store from this deployment, reuse the destination GUID on line two of its marker.

```powershell
$backupBase = Join-Path $env:LOCALAPPDATA 'ConfastDB-Backup'
$control = Join-Path $backupBase 'control'
$destinationName = 'secondary'
$destinationPath = Join-Path $backupBase 'secondary'

$deploymentId = (Get-Content -Raw -ErrorAction Stop -LiteralPath `
    (Join-Path $control '.confast-backup-control')).Trim()
$marker = Join-Path $destinationPath '.confast-backup-owner'

if (Test-Path -LiteralPath $marker) {
    throw 'This store already has a marker. Reuse its existing destination GUID.'
}

New-Item -ItemType Directory -Force -Path $destinationPath | Out-Null
$destinationId = (New-Guid).ToString('D')
[IO.File]::WriteAllText($marker, "$deploymentId`n$destinationId")

dotnet user-secrets set --project src/Confast.Web "Backups:Destinations:${destinationName}:Id" $destinationId
dotnet user-secrets set --project src/Confast.Web "Backups:Destinations:${destinationName}:Path" $destinationPath
```

For a deployed service, add the equivalent destination `Id` and `Path` in its host configuration and give the service identity the required access. Restart the app, select the new name under **Server Destination**, and click **Test Destination**. Save the configuration to send future backups there.

Each run writes to the selected store. Existing sets remain in the folders where they were created and stay visible while those locations are configured. [Rolling retention](#backup-sets-verification-and-retention) applies to eligible unpinned backups across all configured stores.

## Schedule, execution and monitoring

Scheduling rules and the next eligible UTC occurrence are persisted in PostgreSQL. The worker anchors recurrence to the server clock and uses the browser’s IANA time zone when it can resolve it. It uses the next occurrence as the conversion reference for an edited schedule, including any weekday rotation or monthly calendar shift. Saving an unchanged schedule preserves the server rule. The preview shows the actual next five instants in the browser’s zone, or the server zone when browser detection fails. Server time-zone changes pause scheduling until an administrator confirms the rule.

Hourly schedules advance by the selected number of wall-clock hours from their reference date and time. At a repeated wall time the earlier instant runs once. A nonexistent wall time advances to the first valid minute and an identical resulting instant is scheduled once. Monthly rules clamp to the actual last day of the nominal month, then apply any cross-zone day shift. Local display times can change when the browser and server change their clocks on different dates.

After downtime, catch-up uses the latest missed occurrence and advances to the next regular one. With catch-up disabled, the scheduler skips occurrences outside its 45-second polling allowance. Failed or interrupted scheduled runs receive at most one retry after five minutes if the same configuration is still active and the next occurrence has not arrived. Manual requests queue durably, recheck the requester’s current grants, and leave the recurring schedule intact. Startup and periodic reconciliation recover interrupted rows and completed sets published before the database catalog was updated.

Choose **Create Backup**, enter any label or notes, then choose **Queue Backup**. With pinning permission you can select **Pin This Backup** before queuing. The History and Status sections show progress after you leave the page. Open **Details** to verify, update protection, download, or delete a set according to your grants. Deletion requires confirmation and pinned sets must be unpinned first.

The worker coordinates through an exclusive file in the shared control directory and a dedicated PostgreSQL advisory-lock session. The same coordination covers pinning, verification, retention and deletion. A download streams the set as a tar attachment under that lease and can defer another backup operation until the client finishes or the timeout expires. The worker cancels client tools when it loses ownership, and abandoned staging directories cannot be catalogued as completed.

Preflight requires measured capacity for the estimated new archive and the configured reserve. The estimate is the larger of the current database size and 115% of the largest known completed backup, with room for the artwork map and manifest. The reserve is checked again before publication. Failure to create or validate a backup leaves retention untouched. A complete backup with a cleanup problem remains usable and carries a separate retention diagnostic.

The health panel shows warnings independently of the scheduler’s enabled flag. Under **Notifications**, choose **Add Recipient**, enter an email address, and select its notification types. For example, select **Backup Failure** for one address and add **Successful Backups** for another. Save the configuration to apply the changes. Each address receives its own message, with independent delivery retries and daily reminders for subscribed health conditions. Recovery notices go to subscribed recipients who received the original warning.

The `20261124162057_BackupNotificationRecipients` migration carries shared notification choices and alert cooldowns forward to each existing recipient. Reapply `scripts/security/runtime-grants.sql` for a restricted app login and `scripts/security/backup-grants.sql` for the backup reader after applying it. Configure a valid application sender and test SMTP delivery in the deployment. Alerts require a running application and database. Use independent monitoring for site outages, missed copies and actual recovery-point age.

## Backup sets, verification and retention

Each set has a UTC timestamp and UUID in the name, for example `confastDB_20261124T070000Z_<uuid>`. It contains `database.dump`, `manifest.json`, and the optional artwork map. The version 1 UTF-8 JSON manifest contains the identity of the deployment and destination, database and tool versions, UTC timestamps, execution type and user ID, duration, file paths and byte lengths, SHA-256 digests, pin, label and notes. Its file names are limited to the exact known set names. The worker writes and validates the set under `.stage_…` on the same store before renaming it into place.

All published sets pass archive enumeration and have file lengths and SHA-256 digests recorded. Full verification also reads the archive through `pg_restore` without a database connection and compares staged checksums against the manifest. On-demand verification compares current hashes and sizes and repeats the archive-read checks. A real restore exercises additional constraints, extensions and functional behavior.

Successful manual and scheduled backups count toward rolling retention when unpinned. A new successful backup is required before automatic cleanup. Reducing the count or unpinning a set takes effect on a subsequent successful backup. Pinned, corrupt, unavailable, foreign and unrecognized sets are preserved. Retention rechecks ownership, manifest metadata, file integrity and policy just before retirement. The external `retention-policy.json` checkpoint pairs the current settings revision and database installation generation with the approved deployment and storage. A missing or mismatched checkpoint or a recovered catalog disagreement pauses automatic deletion until an administrator reviews the inventory and saves configuration.

**Reconcile Catalog** imports recognized published sets, recognizes missing and unavailable locations separately, and reports duplicates, manifest errors, interrupted staging and leftover retired directories. The manifest is authoritative for pinning. When it differs from database metadata, the worker preserves the files and pauses retention for review. Leave unfamiliar files and partial directories in place until an operator has verified their identity and copied any needed evidence. Never delete the newest recovery point just to free space for another attempt.

To rebuild the catalog after metadata loss, restore the backup schema through the normal database recovery procedure and point the deployment at its provisioned stores. The worker imports the sets from the manifests. Execution history for removed files still depends on the recovered PostgreSQL history.

Backup history survives file expiry and deletion. Raw diagnostic output from PostgreSQL tools is reduced to safe exit codes and troubleshooting classifications. The append-only `backup_audit` table records the account or system operation, time, result, and relevant before-and-after values. New backup permissions are assignable by authorized role editors and have no seed grants, including for ordinary Administrator. The protected Root account receives the full catalog. Grant download narrowly because a backup contains data across normal application permission boundaries.

## Offline restore

1. Select a trusted completed set. Verify the manifest’s deployment and database identity and compare each listed file’s byte length and SHA-256 digest. For example, use `Get-FileHash -Algorithm SHA256` on each recognized file. Run `pg_restore --list` on `database.dump` with compatible client tools.
2. Isolate the restore target from application traffic. Supply the matching PostgreSQL major version and any required extensions and locale, and create a **new empty database** for the schema owner. As the approved operator, run the following against that explicit target. Use environment or password-file authentication.

   ```powershell
   $backupArchive = 'D:\approved\backup-set\database.dump'
   pg_restore --exit-on-error --single-transaction --no-owner --no-privileges --dbname=APPROVED_EMPTY_TARGET $backupArchive
   ```

3. Recreate the required PostgreSQL cluster roles, ownership and tablespaces according to the infrastructure recovery record. Reapply reviewed runtime and backup grants as the schema owner. Install the compatible application release and apply newer migrations deliberately. Copy the artwork map to `ManagedFiles:Directory` or the restored instance’s default `App_Data` if the manifest lists it. Restore service configuration, the Data Protection ring and wrapping material through the infrastructure recovery procedure.
4. Before reopening traffic, validate representative logins, permissions, revisions, documents, chat and business workflows. Rotate the installation generation using the existing offline procedure when restoring an installation. Run reconciliation against the accessible approved storage and check that newer and pinned sets are visible before saving the intended configuration to re-arm retention. When rehearsing a restore to a separate environment, use an isolated copy of the backup inventory and its control state. Never start a second active installation against the original backup stores. [Offline authorization recovery](deployment-readiness.md#backup-restore-and-offline-recovery)

Logical snapshots do not capture transactions after their start time. Schedule and monitor against the acceptable recovery point and allow time to restore and validate. Replicate only completed backup directories and their manifests through the existing infrastructure backup solution. Keep the primary backup store separate from the PostgreSQL storage and maintain an independently protected offsite or immutable copy. Preserve the deployment and storage IDs and control checkpoint for reconstruction. Repeat a restore drill after material changes to the database, deployment or storage. [PostgreSQL 18 pg_restore documentation](https://www.postgresql.org/docs/18/app-pgrestore.html)

## Validation commands

The normal Release suite exercises schedule changes, retention, reconciliation, storage identity, permissions and execution coordination using the disposable database configured in the repository README. For the native dump and restore test, start a private cluster with `scripts/security/start-disposable-rehearsal.ps1`, take the disposable connection from its output, and set `CONFAST_ISOLATED_REHEARSAL=1`. Run `dotnet test -c Release --filter FullyQualifiedName~BackupManagerRestoresDocuments`. Stop the cluster with `scripts/security/stop-disposable-rehearsal.ps1` when finished.

For browser acceptance, configure the Development `BrowserTestUser` and opt in with `CONFAST_BACKUP_BROWSER_TESTS=1`. Run `dotnet test -c Release --filter FullyQualifiedName~BackupManagerShowsLocalPreview` against the disposable test database. Set `CONFAST_BACKUP_BROWSER_CHANNEL=msedge` to use an installed Edge browser. `CONFAST_BACKUP_TEST_PG_BIN` overrides the PostgreSQL 18 binary directory for these tests.
