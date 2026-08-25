using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSecondaryProcessRequirements : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "secondary_process_types",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secondary_process_types", x => x.id);
                    table.CheckConstraint("CK_secondary_process_types_name_not_blank", "btrim(name) <> ''");
                });

            migrationBuilder.CreateTable(
                name: "secondary_process_requirements",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    inspection_criteria_revision_id = table.Column<long>(type: "bigint", nullable: false),
                    secondary_process_type_id = table.Column<long>(type: "bigint", nullable: false),
                    specification = table.Column<string>(type: "text", nullable: true),
                    po_number = table.Column<string>(type: "text", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_secondary_process_requirements", x => x.id);
                    table.ForeignKey(
                        name: "FK_secondary_process_requirements_revision_id",
                        column: x => x.inspection_criteria_revision_id,
                        principalTable: "inspection_criteria_revisions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_secondary_process_requirements_type_id",
                        column: x => x.secondary_process_type_id,
                        principalTable: "secondary_process_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "secondary_process_types",
                columns: new[] { "id", "name" },
                values: new object[,]
                {
                    { 1L, "Heat Treat" },
                    { 2L, "Clean" },
                    { 3L, "Patch" },
                    { 4L, "Plate" },
                    { 5L, "Sort" }
                });

            migrationBuilder.Sql(
                "SELECT setval(pg_get_serial_sequence('secondary_process_types', 'id'), (SELECT MAX(id) FROM secondary_process_types), true);");

            migrationBuilder.CreateIndex(
                name: "IX_secondary_process_requirements_revision_id",
                table: "secondary_process_requirements",
                column: "inspection_criteria_revision_id");

            migrationBuilder.CreateIndex(
                name: "IX_secondary_process_requirements_type_id",
                table: "secondary_process_requirements",
                column: "secondary_process_type_id");

            migrationBuilder.CreateIndex(
                name: "UX_secondary_process_types_name",
                table: "secondary_process_types",
                column: "name",
                unique: true);

            // Secondary processes are part of the revision's historical meaning,
            // so published rows receive the same database-level protection as criteria.
            migrationBuilder.Sql(
                """
                CREATE FUNCTION prevent_published_secondary_process_requirement_changes()
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
                        RAISE EXCEPTION 'Secondary processes in a published revision are immutable.'
                            USING ERRCODE = '55000';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER TR_secondary_process_requirements_protect_published
                BEFORE INSERT OR UPDATE OR DELETE ON secondary_process_requirements
                FOR EACH ROW
                EXECUTE FUNCTION prevent_published_secondary_process_requirement_changes();

                CREATE FUNCTION prevent_historical_secondary_process_type_changes()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM secondary_process_requirements AS requirement
                        JOIN inspection_criteria_revisions AS revision
                            ON revision.id = requirement.inspection_criteria_revision_id
                        WHERE requirement.secondary_process_type_id = OLD.id
                            AND revision.published_at_utc IS NOT NULL
                    ) THEN
                        RAISE EXCEPTION 'Secondary process types used by published revisions are immutable.'
                            USING ERRCODE = '55000';
                    END IF;

                    IF TG_OP = 'DELETE' THEN
                        RETURN OLD;
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER TR_secondary_process_types_protect_history
                BEFORE UPDATE OR DELETE ON secondary_process_types
                FOR EACH ROW
                EXECUTE FUNCTION prevent_historical_secondary_process_type_changes();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS TR_secondary_process_types_protect_history ON secondary_process_types;
                DROP FUNCTION IF EXISTS prevent_historical_secondary_process_type_changes();
                DROP TRIGGER IF EXISTS TR_secondary_process_requirements_protect_published ON secondary_process_requirements;
                DROP FUNCTION IF EXISTS prevent_published_secondary_process_requirement_changes();
                """);

            migrationBuilder.DropTable(
                name: "secondary_process_requirements");

            migrationBuilder.DropTable(
                name: "secondary_process_types");
        }
    }
}
