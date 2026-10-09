using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDelegatedAdministrationAndInspectorIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "inspector_user_id",
                table: "inspections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "field_name",
                table: "authorization_change_details",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "previous_value",
                table: "authorization_change_details",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "resulting_value",
                table: "authorization_change_details",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "password_reset_delegations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    issuer_user_id = table.Column<string>(type: "text", nullable: false),
                    target_user_id = table.Column<string>(type: "text", nullable: false),
                    issuer_security_stamp = table.Column<string>(type: "text", nullable: false),
                    installation_generation = table.Column<Guid>(type: "uuid", nullable: false),
                    token_digest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    issued_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    expires_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    consumed_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_reset_delegations", x => x.id);
                    table.CheckConstraint("CK_reset_delegation_expiry", "expires_at_utc > issued_at_utc AND (consumed_at_utc IS NULL OR consumed_at_utc >= issued_at_utc)");
                });

            migrationBuilder.CreateIndex(
                name: "IX_inspections_inspector_user_id",
                table: "inspections",
                column: "inspector_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_delegations_target_user_id_expires_at_utc",
                table: "password_reset_delegations",
                columns: new[] { "target_user_id", "expires_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_delegations_token_digest",
                table: "password_reset_delegations",
                column: "token_digest",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_inspections_identity_users_inspector_user_id",
                table: "inspections",
                column: "inspector_user_id",
                principalTable: "identity_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            // Conservative matching: preserve every original string, including whitespace;
            // a duplicated normalized display name is never resolved by username/order.
            migrationBuilder.Sql("""
                UPDATE inspections AS i SET inspector_user_id = matches.id
                FROM (SELECT btrim(display_name) AS label, min(id) AS id
                      FROM identity_users WHERE btrim(display_name) <> ''
                      GROUP BY btrim(display_name) HAVING count(*) = 1) AS matches
                WHERE i.inspector_user_id IS NULL AND btrim(i.inspector) = matches.label;

                CREATE FUNCTION confast_guard_reset_delegation() RETURNS trigger LANGUAGE plpgsql
                SET search_path = pg_catalog, public AS $$
                BEGIN
                    IF TG_OP = 'INSERT' THEN
                        IF NEW.consumed_at_utc IS NOT NULL OR NEW.token_digest !~ '^[0-9A-F]{64}$'
                            OR NOT EXISTS (SELECT 1 FROM public.identity_users WHERE id = NEW.issuer_user_id AND is_active
                                AND security_stamp = NEW.issuer_security_stamp)
                            OR NOT EXISTS (SELECT 1 FROM public.identity_users WHERE id = NEW.target_user_id)
                            OR NOT EXISTS (SELECT 1 FROM public.authorization_state WHERE id = 1 AND readiness = 1
                                AND root_user_id <> NEW.target_user_id AND installation_generation = NEW.installation_generation)
                            OR NEW.issuer_user_id = NEW.target_user_id THEN
                            RAISE EXCEPTION 'Invalid password reset delegation' USING ERRCODE = '23514';
                        END IF;
                    ELSIF (NEW.id, NEW.issuer_user_id, NEW.target_user_id, NEW.issuer_security_stamp,
                            NEW.installation_generation, NEW.token_digest, NEW.issued_at_utc, NEW.expires_at_utc)
                        IS DISTINCT FROM (OLD.id, OLD.issuer_user_id, OLD.target_user_id, OLD.issuer_security_stamp,
                            OLD.installation_generation, OLD.token_digest, OLD.issued_at_utc, OLD.expires_at_utc)
                        OR OLD.consumed_at_utc IS NOT NULL OR NEW.consumed_at_utc IS NULL
                        OR NEW.consumed_at_utc >= NEW.expires_at_utc THEN
                        RAISE EXCEPTION 'Reset provenance is immutable and consumption is one-use' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER auth_lock BEFORE INSERT OR UPDATE OR DELETE ON password_reset_delegations
                    FOR EACH STATEMENT EXECUTE FUNCTION confast_security_lock();
                CREATE TRIGGER auth_epoch AFTER INSERT OR UPDATE OR DELETE ON password_reset_delegations
                    FOR EACH STATEMENT EXECUTE FUNCTION confast_security_epoch();
                CREATE TRIGGER auth_guard BEFORE INSERT OR UPDATE ON password_reset_delegations
                    FOR EACH ROW EXECUTE FUNCTION confast_guard_reset_delegation();
                CREATE TRIGGER auth_no_truncate BEFORE TRUNCATE ON password_reset_delegations
                    FOR EACH STATEMENT EXECUTE FUNCTION confast_reject_immutable();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER auth_guard ON password_reset_delegations; DROP FUNCTION confast_guard_reset_delegation();");
            migrationBuilder.DropForeignKey(
                name: "FK_inspections_identity_users_inspector_user_id",
                table: "inspections");

            migrationBuilder.DropTable(
                name: "password_reset_delegations");

            migrationBuilder.DropIndex(
                name: "IX_inspections_inspector_user_id",
                table: "inspections");

            migrationBuilder.DropColumn(
                name: "inspector_user_id",
                table: "inspections");

            migrationBuilder.DropColumn(
                name: "field_name",
                table: "authorization_change_details");

            migrationBuilder.DropColumn(
                name: "previous_value",
                table: "authorization_change_details");

            migrationBuilder.DropColumn(
                name: "resulting_value",
                table: "authorization_change_details");
        }
    }
}
