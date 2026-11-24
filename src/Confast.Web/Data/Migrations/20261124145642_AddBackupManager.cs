using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBackupManager : Migration
    {
        private const string PreviousCatalog = "FD8B0A9A5ADEEAD2F167A825869E4EC9CDAAC1A29EC0A766FF1E3777438AD544";
        private const string BackupCatalog = "5C2448318A1AEDB99D4739CCDCC4B3AF7226EBB757C1B6FCFF3D6CF9E45EA822";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "backup_alerts",
                columns: table => new
                {
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    message = table.Column<string>(type: "text", nullable: true),
                    last_notified_message = table.Column<string>(type: "text", nullable: true),
                    next_delivery_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    delivery_claim = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_alerts", x => new { x.deployment_id, x.kind });
                });

            migrationBuilder.CreateTable(
                name: "backup_audit",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    execution_id = table.Column<Guid>(type: "uuid", nullable: true),
                    actor_user_id = table.Column<string>(type: "text", nullable: true),
                    occurred_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    action = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    before = table.Column<string>(type: "jsonb", nullable: true),
                    after = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_audit", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "backup_executions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    destination = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    destination_id = table.Column<Guid>(type: "uuid", nullable: false),
                    directory_name = table.Column<string>(type: "character varying(72)", maxLength: 72, nullable: true),
                    trigger = table.Column<int>(type: "integer", nullable: false),
                    scheduled_for_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    settings_revision = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_generation = table.Column<Guid>(type: "uuid", nullable: true),
                    requested_by = table.Column<string>(type: "text", nullable: true),
                    requested_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    finished_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    duration_milliseconds = table.Column<long>(type: "bigint", nullable: true),
                    outcome = table.Column<int>(type: "integer", nullable: false),
                    stage = table.Column<int>(type: "integer", nullable: false),
                    availability = table.Column<int>(type: "integer", nullable: false),
                    diagnostic = table.Column<string>(type: "text", nullable: true),
                    retention_diagnostic = table.Column<string>(type: "text", nullable: true),
                    total_bytes = table.Column<long>(type: "bigint", nullable: true),
                    pinned = table.Column<bool>(type: "boolean", nullable: false),
                    label = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                    notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    metadata_version = table.Column<Guid>(type: "uuid", nullable: false),
                    verification = table.Column<int>(type: "integer", nullable: false),
                    verified_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    verification_diagnostic = table.Column<string>(type: "text", nullable: true),
                    verify_requested_by = table.Column<string>(type: "text", nullable: true),
                    verify_generation = table.Column<Guid>(type: "uuid", nullable: true),
                    verify_started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    removed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_executions", x => x.id);
                    table.CheckConstraint("ck_backup_execution_trigger", "(trigger = 0 AND scheduled_for_utc IS NULL AND attempt = 0) OR (trigger = 1 AND scheduled_for_utc IS NOT NULL AND attempt BETWEEN 1 AND 2)");
                });

            migrationBuilder.CreateTable(
                name: "backup_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    revision = table.Column<Guid>(type: "uuid", nullable: false),
                    deployment_id = table.Column<Guid>(type: "uuid", nullable: true),
                    destination = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    destination_id = table.Column<Guid>(type: "uuid", nullable: true),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    catch_up = table.Column<bool>(type: "boolean", nullable: false),
                    full_verification = table.Column<bool>(type: "boolean", nullable: false),
                    retain = table.Column<int>(type: "integer", nullable: false),
                    reserve_bytes = table.Column<long>(type: "bigint", nullable: false),
                    notices = table.Column<int>(type: "integer", nullable: false),
                    recipients = table.Column<string[]>(type: "text[]", nullable: false),
                    server_zone_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    frequency = table.Column<int>(type: "integer", nullable: false),
                    local_time = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    hours = table.Column<int>(type: "integer", nullable: false),
                    weekdays = table.Column<int>(type: "integer", nullable: false),
                    month_day = table.Column<int>(type: "integer", nullable: false),
                    month_shift = table.Column<int>(type: "integer", nullable: false),
                    hour_anchor = table.Column<DateOnly>(type: "date", nullable: false),
                    next_due_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    schedule_started_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_reconciled_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    catalog_issue = table.Column<string>(type: "text", nullable: true),
                    retention_issue = table.Column<string>(type: "text", nullable: true),
                    reconcile_requested_by = table.Column<string>(type: "text", nullable: true),
                    reconcile_generation = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_settings", x => x.id);
                    table.CheckConstraint("ck_backup_settings", "id = 1 AND retain BETWEEN 1 AND 1000 AND reserve_bytes >= 0 AND hours BETWEEN 1 AND 168 AND weekdays BETWEEN 1 AND 127 AND month_day BETWEEN 1 AND 31 AND month_shift BETWEEN -4 AND 4");
                });

            migrationBuilder.InsertData(
                table: "backup_settings",
                columns: new[] { "id", "catalog_issue", "catch_up", "deployment_id", "destination", "destination_id", "enabled", "frequency", "full_verification", "hour_anchor", "hours", "last_reconciled_at_utc", "local_time", "month_day", "month_shift", "next_due_at_utc", "notices", "recipients", "reconcile_generation", "reconcile_requested_by", "reserve_bytes", "retain", "retention_issue", "revision", "schedule_started_at_utc", "server_zone_id", "weekdays" },
                values: new object[] { 1, null, true, null, null, null, false, 1, true, new DateOnly(2000, 1, 1), 1, null, new TimeOnly(2, 0, 0), 1, 0, null, 127, new string[0], null, null, 2147483648L, 14, null, new Guid("00000000-0000-0000-0000-000000000000"), null, null, 62 });

            migrationBuilder.Sql("ALTER TABLE public.permissions DISABLE TRIGGER auth_manifest;");
            ChangeCatalog(migrationBuilder, PreviousCatalog, BackupCatalog);

            migrationBuilder.InsertData(
                table: "permissions",
                columns: new[] { "key", "allowed_in_baseline", "authority", "catalog_version", "category", "display_name", "kind" },
                values: new object[,]
                {
                    { "DatabaseBackups.Configure", false, 0, "5C2448318A1AEDB99D4739CCDCC4B3AF7226EBB757C1B6FCFF3D6CF9E45EA822", "Database Backups", "Configure Backups", 1 },
                    { "DatabaseBackups.Create", false, 0, "5C2448318A1AEDB99D4739CCDCC4B3AF7226EBB757C1B6FCFF3D6CF9E45EA822", "Database Backups", "Create Backups", 1 },
                    { "DatabaseBackups.Delete", false, 0, "5C2448318A1AEDB99D4739CCDCC4B3AF7226EBB757C1B6FCFF3D6CF9E45EA822", "Database Backups", "Delete Backups", 1 },
                    { "DatabaseBackups.Download", false, 0, "5C2448318A1AEDB99D4739CCDCC4B3AF7226EBB757C1B6FCFF3D6CF9E45EA822", "Database Backups", "Download Database Backups", 1 },
                    { "DatabaseBackups.EditMetadata", false, 0, "5C2448318A1AEDB99D4739CCDCC4B3AF7226EBB757C1B6FCFF3D6CF9E45EA822", "Database Backups", "Edit Backup Labels and Notes", 1 },
                    { "DatabaseBackups.Pin", false, 0, "5C2448318A1AEDB99D4739CCDCC4B3AF7226EBB757C1B6FCFF3D6CF9E45EA822", "Database Backups", "Pin and Unpin Backups", 1 },
                    { "DatabaseBackups.Reconcile", false, 0, "5C2448318A1AEDB99D4739CCDCC4B3AF7226EBB757C1B6FCFF3D6CF9E45EA822", "Database Backups", "Reconcile Backup Catalog", 1 },
                    { "DatabaseBackups.Verify", false, 0, "5C2448318A1AEDB99D4739CCDCC4B3AF7226EBB757C1B6FCFF3D6CF9E45EA822", "Database Backups", "Verify Backup Integrity", 1 },
                    { "DatabaseBackups.View", false, 0, "5C2448318A1AEDB99D4739CCDCC4B3AF7226EBB757C1B6FCFF3D6CF9E45EA822", "Database Backups", "View Backup Manager", 0 }
                });

            migrationBuilder.Sql("ALTER TABLE public.permissions ENABLE TRIGGER auth_manifest;");

            migrationBuilder.CreateIndex(
                name: "IX_backup_audit_deployment_id_execution_id_occurred_at_utc",
                table: "backup_audit",
                columns: new[] { "deployment_id", "execution_id", "occurred_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_executions_deployment_id_requested_at_utc",
                table: "backup_executions",
                columns: new[] { "deployment_id", "requested_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_backup_executions_deployment_id_scheduled_for_utc_attempt",
                table: "backup_executions",
                columns: new[] { "deployment_id", "scheduled_for_utc", "attempt" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_backup_executions_running",
                table: "backup_executions",
                column: "deployment_id",
                unique: true,
                filter: "outcome = 1");

            migrationBuilder.Sql("""
                CREATE FUNCTION public.confast_protect_backup_audit() RETURNS trigger LANGUAGE plpgsql
                SET search_path = pg_catalog AS $$
                BEGIN
                    RAISE EXCEPTION 'Backup audit history is append-only' USING ERRCODE = '23514';
                END $$;
                CREATE TRIGGER backup_audit_immutable BEFORE UPDATE OR DELETE ON public.backup_audit
                    FOR EACH ROW EXECUTE FUNCTION public.confast_protect_backup_audit();
                CREATE TRIGGER backup_audit_no_truncate BEFORE TRUNCATE ON public.backup_audit
                    FOR EACH STATEMENT EXECUTE FUNCTION public.confast_protect_backup_audit();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "backup_alerts");

            migrationBuilder.DropTable(
                name: "backup_audit");

            migrationBuilder.DropTable(
                name: "backup_executions");

            migrationBuilder.DropTable(
                name: "backup_settings");

            migrationBuilder.Sql("DROP FUNCTION public.confast_protect_backup_audit();");
            migrationBuilder.Sql("ALTER TABLE public.permissions DISABLE TRIGGER auth_manifest;");

            migrationBuilder.DeleteData(
                table: "permissions",
                keyColumn: "key",
                keyValue: "DatabaseBackups.Configure");

            migrationBuilder.DeleteData(
                table: "permissions",
                keyColumn: "key",
                keyValue: "DatabaseBackups.Create");

            migrationBuilder.DeleteData(
                table: "permissions",
                keyColumn: "key",
                keyValue: "DatabaseBackups.Delete");

            migrationBuilder.DeleteData(
                table: "permissions",
                keyColumn: "key",
                keyValue: "DatabaseBackups.Download");

            migrationBuilder.DeleteData(
                table: "permissions",
                keyColumn: "key",
                keyValue: "DatabaseBackups.EditMetadata");

            migrationBuilder.DeleteData(
                table: "permissions",
                keyColumn: "key",
                keyValue: "DatabaseBackups.Pin");

            migrationBuilder.DeleteData(
                table: "permissions",
                keyColumn: "key",
                keyValue: "DatabaseBackups.Reconcile");

            migrationBuilder.DeleteData(
                table: "permissions",
                keyColumn: "key",
                keyValue: "DatabaseBackups.Verify");

            migrationBuilder.DeleteData(
                table: "permissions",
                keyColumn: "key",
                keyValue: "DatabaseBackups.View");

            ChangeCatalog(migrationBuilder, BackupCatalog, PreviousCatalog);
            migrationBuilder.Sql("ALTER TABLE public.permissions ENABLE TRIGGER auth_manifest;");
        }

        private static void ChangeCatalog(MigrationBuilder migrationBuilder, string previous, string current)
        {
            // Both constants belong to this historical migration. Leave grants and
            // protected role identities intact and advance the existing catalog epoch.
            migrationBuilder.Sql($"""
                DO $$
                BEGIN
                    IF (SELECT catalog_version FROM public.authorization_state WHERE id = 1)
                            IS DISTINCT FROM '{previous}' THEN
                        RAISE EXCEPTION 'Backup Manager migration requires the expected authorization catalog';
                    END IF;
                END $$;
                UPDATE public.permissions SET catalog_version = '{current}' WHERE catalog_version = '{previous}';
                UPDATE public.authorization_state SET catalog_version = '{current}' WHERE id = 1;
                """);
        }
    }
}
