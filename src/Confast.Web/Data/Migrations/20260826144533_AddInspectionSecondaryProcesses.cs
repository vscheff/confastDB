using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInspectionSecondaryProcesses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_secondary_process_requirements_id_revision_id",
                table: "secondary_process_requirements",
                columns: new[] { "id", "inspection_criteria_revision_id" });

            migrationBuilder.CreateTable(
                name: "inspection_secondary_processes",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    inspection_id = table.Column<long>(type: "bigint", nullable: false),
                    inspection_criteria_revision_id = table.Column<long>(type: "bigint", nullable: false),
                    secondary_process_requirement_id = table.Column<long>(type: "bigint", nullable: false),
                    process_name = table.Column<string>(type: "text", nullable: false),
                    specification = table.Column<string>(type: "text", nullable: true),
                    purchase_order_number = table.Column<string>(type: "text", nullable: true),
                    is_complete = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inspection_secondary_processes", x => x.id);
                    table.CheckConstraint("CK_inspection_secondary_processes_process_name_not_blank", "btrim(process_name) <> ''");
                    table.ForeignKey(
                        name: "FK_inspection_secondary_processes_inspection_id_revision_id",
                        columns: x => new { x.inspection_id, x.inspection_criteria_revision_id },
                        principalTable: "inspections",
                        principalColumns: new[] { "id", "inspection_criteria_revision_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inspection_secondary_processes_requirement_id_revision_id",
                        columns: x => new { x.secondary_process_requirement_id, x.inspection_criteria_revision_id },
                        principalTable: "secondary_process_requirements",
                        principalColumns: new[] { "id", "inspection_criteria_revision_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inspection_secondary_processes_inspection_id_inspection_cri~",
                table: "inspection_secondary_processes",
                columns: new[] { "inspection_id", "inspection_criteria_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_inspection_secondary_processes_requirement_id_revision_id",
                table: "inspection_secondary_processes",
                columns: new[] { "secondary_process_requirement_id", "inspection_criteria_revision_id" });

            migrationBuilder.CreateIndex(
                name: "UX_inspection_secondary_processes_inspection_id_requirement_id",
                table: "inspection_secondary_processes",
                columns: new[] { "inspection_id", "secondary_process_requirement_id" },
                unique: true);

            migrationBuilder.Sql(
                """
                INSERT INTO inspection_secondary_processes
                    (inspection_id,
                     inspection_criteria_revision_id,
                     secondary_process_requirement_id,
                     process_name,
                     specification,
                     is_complete)
                SELECT inspection.id,
                       inspection.inspection_criteria_revision_id,
                       requirement.id,
                       process_type.name,
                       requirement.specification,
                       FALSE
                FROM inspections AS inspection
                INNER JOIN secondary_process_requirements AS requirement
                    ON requirement.inspection_criteria_revision_id =
                       inspection.inspection_criteria_revision_id
                INNER JOIN secondary_process_types AS process_type
                    ON process_type.id = requirement.secondary_process_type_id;

                CREATE FUNCTION protect_inspection_secondary_process_history()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    required_revision_id bigint;
                    required_process_name text;
                    required_specification text;
                BEGIN
                    SELECT requirement.inspection_criteria_revision_id,
                           process_type.name,
                           requirement.specification
                    INTO required_revision_id,
                         required_process_name,
                         required_specification
                    FROM secondary_process_requirements AS requirement
                    INNER JOIN secondary_process_types AS process_type
                        ON process_type.id = requirement.secondary_process_type_id
                    WHERE requirement.id = NEW.secondary_process_requirement_id;

                    IF required_revision_id IS NULL
                        OR required_revision_id <> NEW.inspection_criteria_revision_id THEN
                        RAISE EXCEPTION
                            'The secondary-process requirement does not belong to the inspection criteria revision.'
                            USING ERRCODE = '23503';
                    END IF;

                    IF TG_OP = 'INSERT' THEN
                        NEW.process_name := required_process_name;
                        NEW.specification := required_specification;
                    ELSIF NEW.inspection_id IS DISTINCT FROM OLD.inspection_id
                        OR NEW.inspection_criteria_revision_id IS DISTINCT FROM
                            OLD.inspection_criteria_revision_id
                        OR NEW.secondary_process_requirement_id IS DISTINCT FROM
                            OLD.secondary_process_requirement_id
                        OR NEW.process_name IS DISTINCT FROM OLD.process_name
                        OR NEW.specification IS DISTINCT FROM OLD.specification THEN
                        RAISE EXCEPTION
                            'Inspection secondary-process history cannot be changed.'
                            USING ERRCODE = '55000';
                    END IF;

                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER TR_inspection_secondary_processes_protect_history
                BEFORE INSERT OR UPDATE ON inspection_secondary_processes
                FOR EACH ROW
                EXECUTE FUNCTION protect_inspection_secondary_process_history();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS TR_inspection_secondary_processes_protect_history
                    ON inspection_secondary_processes;
                DROP FUNCTION IF EXISTS protect_inspection_secondary_process_history();
                """);

            migrationBuilder.DropTable(
                name: "inspection_secondary_processes");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_secondary_process_requirements_id_revision_id",
                table: "secondary_process_requirements");
        }
    }
}
