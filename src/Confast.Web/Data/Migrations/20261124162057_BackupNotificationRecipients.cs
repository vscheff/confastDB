using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackupNotificationRecipients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_backup_alerts",
                table: "backup_alerts");

            migrationBuilder.Sql("""
                CREATE TEMP TABLE backup_shared_alert_state ON COMMIT DROP AS TABLE public.backup_alerts;
                TRUNCATE TABLE public.backup_alerts;
                """);

            migrationBuilder.AddColumn<string>(
                name: "recipient_email",
                table: "backup_alerts",
                type: "character varying(254)",
                nullable: false);

            migrationBuilder.AddPrimaryKey(
                name: "PK_backup_alerts",
                table: "backup_alerts",
                columns: new[] { "deployment_id", "recipient_email", "kind" });

            migrationBuilder.CreateTable(
                name: "backup_notification_recipients",
                columns: table => new
                {
                    email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    notices = table.Column<int>(type: "integer", nullable: false),
                    settings_id = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_backup_notification_recipients", x => x.email);
                    table.CheckConstraint("ck_backup_recipient_notices", "notices BETWEEN 0 AND 255");
                    table.ForeignKey(
                        name: "FK_backup_notification_recipients_backup_settings_settings_id",
                        column: x => x.settings_id,
                        principalTable: "backup_settings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql("""
                INSERT INTO public.backup_notification_recipients (email, notices, settings_id)
                SELECT DISTINCT ON (lower(address)) address, settings.notices, settings.id
                FROM public.backup_settings AS settings
                CROSS JOIN LATERAL unnest(settings.recipients) AS address
                ORDER BY lower(address), address;

                INSERT INTO public.backup_alerts
                    (deployment_id, recipient_email, kind, message, last_notified_message, next_delivery_at_utc, delivery_claim)
                SELECT state.deployment_id, recipient.email, state.kind, state.message,
                       state.last_notified_message, state.next_delivery_at_utc, state.delivery_claim
                FROM backup_shared_alert_state AS state
                JOIN public.backup_settings AS settings ON settings.deployment_id = state.deployment_id
                JOIN public.backup_notification_recipients AS recipient ON recipient.settings_id = settings.id
                WHERE (recipient.notices & state.kind) <> 0;
                """);

            migrationBuilder.DropColumn(name: "notices", table: "backup_settings");
            migrationBuilder.DropColumn(name: "recipients", table: "backup_settings");

            migrationBuilder.CreateIndex(
                name: "IX_backup_alerts_recipient_email",
                table: "backup_alerts",
                column: "recipient_email");

            migrationBuilder.CreateIndex(
                name: "IX_backup_notification_recipients_settings_id",
                table: "backup_notification_recipients",
                column: "settings_id");

            migrationBuilder.AddForeignKey(
                name: "FK_backup_alerts_backup_notification_recipients_recipient_email",
                table: "backup_alerts",
                column: "recipient_email",
                principalTable: "backup_notification_recipients",
                principalColumn: "email",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_backup_alerts_backup_notification_recipients_recipient_email",
                table: "backup_alerts");

            migrationBuilder.DropPrimaryKey(
                name: "PK_backup_alerts",
                table: "backup_alerts");

            migrationBuilder.DropIndex(
                name: "IX_backup_alerts_recipient_email",
                table: "backup_alerts");

            migrationBuilder.Sql("TRUNCATE TABLE public.backup_alerts;");

            migrationBuilder.DropColumn(
                name: "recipient_email",
                table: "backup_alerts");

            migrationBuilder.AddColumn<int>(
                name: "notices",
                table: "backup_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string[]>(
                name: "recipients",
                table: "backup_settings",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddPrimaryKey(
                name: "PK_backup_alerts",
                table: "backup_alerts",
                columns: new[] { "deployment_id", "kind" });

            // Keep only notice types shared by all recipients when reverting to a single selection.
            migrationBuilder.Sql("""
                UPDATE public.backup_settings AS settings
                SET notices = COALESCE((SELECT bit_and(recipient.notices)
                    FROM public.backup_notification_recipients AS recipient WHERE recipient.settings_id = settings.id), 127),
                    recipients = ARRAY(SELECT recipient.email
                    FROM public.backup_notification_recipients AS recipient WHERE recipient.settings_id = settings.id
                    ORDER BY recipient.email);
                """);
            migrationBuilder.DropTable(name: "backup_notification_recipients");
        }
    }
}
