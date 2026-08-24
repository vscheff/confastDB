using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddVersionedInspectionCriteria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "inspection_criteria_revisions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    revision_number = table.Column<int>(type: "integer", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    published_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    superseded_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    change_note = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inspection_criteria_revisions", x => x.id);
                    table.CheckConstraint("CK_inspection_criteria_revisions_revision_number", "revision_number > 0");
                    table.CheckConstraint("CK_inspection_criteria_revisions_superseded_after_published", "superseded_at_utc IS NULL OR published_at_utc IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_inspection_criteria_revisions_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inspection_criteria",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    inspection_criteria_revision_id = table.Column<long>(type: "bigint", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    inspection_method = table.Column<string>(type: "text", nullable: true),
                    minimum_value = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    maximum_value = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    unit = table.Column<string>(type: "text", nullable: true),
                    display_order = table.Column<int>(type: "integer", nullable: false),
                    notes = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inspection_criteria", x => x.id);
                    table.CheckConstraint("CK_inspection_criteria_display_order", "display_order > 0");
                    table.CheckConstraint("CK_inspection_criteria_minimum_maximum", "minimum_value IS NULL OR maximum_value IS NULL OR minimum_value <= maximum_value");
                    table.ForeignKey(
                        name: "FK_inspection_criteria_revision_id",
                        column: x => x.inspection_criteria_revision_id,
                        principalTable: "inspection_criteria_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "UX_inspection_criteria_revision_id_display_order",
                table: "inspection_criteria",
                columns: new[] { "inspection_criteria_revision_id", "display_order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_inspection_criteria_revisions_one_current_per_part",
                table: "inspection_criteria_revisions",
                column: "part_id",
                unique: true,
                filter: "published_at_utc IS NOT NULL AND superseded_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_inspection_criteria_revisions_one_draft_per_part",
                table: "inspection_criteria_revisions",
                column: "part_id",
                unique: true,
                filter: "published_at_utc IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_inspection_criteria_revisions_part_id_revision_number",
                table: "inspection_criteria_revisions",
                columns: new[] { "part_id", "revision_number" },
                unique: true);

            // Published revisions are historical records. These triggers protect that
            // invariant even when data is changed outside this application.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION prevent_published_criteria_revision_changes()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF OLD.published_at_utc IS NOT NULL THEN
                        IF TG_OP = 'DELETE' THEN
                            RAISE EXCEPTION 'Published inspection criteria revisions cannot be deleted.'
                                USING ERRCODE = '55000';
                        END IF;

                        IF NOT (
                            OLD.superseded_at_utc IS NULL
                            AND NEW.superseded_at_utc IS NOT NULL
                            AND NEW.id IS NOT DISTINCT FROM OLD.id
                            AND NEW.part_id IS NOT DISTINCT FROM OLD.part_id
                            AND NEW.revision_number IS NOT DISTINCT FROM OLD.revision_number
                            AND NEW.created_at_utc IS NOT DISTINCT FROM OLD.created_at_utc
                            AND NEW.published_at_utc IS NOT DISTINCT FROM OLD.published_at_utc
                            AND NEW.change_note IS NOT DISTINCT FROM OLD.change_note
                        ) THEN
                            RAISE EXCEPTION 'Published inspection criteria revisions are immutable.'
                                USING ERRCODE = '55000';
                        END IF;
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER TR_inspection_criteria_revisions_protect_published
                BEFORE UPDATE OR DELETE ON inspection_criteria_revisions
                FOR EACH ROW
                EXECUTE FUNCTION prevent_published_criteria_revision_changes();

                CREATE FUNCTION prevent_published_inspection_criteria_changes()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    old_is_published boolean := false;
                    new_is_published boolean := false;
                BEGIN
                    IF TG_OP IN ('UPDATE', 'DELETE') THEN
                        SELECT published_at_utc IS NOT NULL INTO old_is_published
                        FROM inspection_criteria_revisions
                        WHERE id = OLD.inspection_criteria_revision_id;
                    END IF;

                    IF TG_OP IN ('INSERT', 'UPDATE') THEN
                        SELECT published_at_utc IS NOT NULL INTO new_is_published
                        FROM inspection_criteria_revisions
                        WHERE id = NEW.inspection_criteria_revision_id;
                    END IF;

                    IF old_is_published OR new_is_published THEN
                        RAISE EXCEPTION 'Criteria in a published revision are immutable.'
                            USING ERRCODE = '55000';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER TR_inspection_criteria_protect_published
                BEFORE INSERT OR UPDATE OR DELETE ON inspection_criteria
                FOR EACH ROW
                EXECUTE FUNCTION prevent_published_inspection_criteria_changes();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS TR_inspection_criteria_protect_published ON inspection_criteria;
                DROP FUNCTION IF EXISTS prevent_published_inspection_criteria_changes();
                DROP TRIGGER IF EXISTS TR_inspection_criteria_revisions_protect_published ON inspection_criteria_revisions;
                DROP FUNCTION IF EXISTS prevent_published_criteria_revision_changes();
                """);

            migrationBuilder.DropTable(
                name: "inspection_criteria");

            migrationBuilder.DropTable(
                name: "inspection_criteria_revisions");
        }
    }
}
