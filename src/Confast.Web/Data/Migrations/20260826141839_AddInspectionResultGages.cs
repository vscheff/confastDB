using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInspectionResultGages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "gage_id",
                table: "inspection_results",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "gage_number",
                table: "inspection_results",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_inspection_results_gage_id",
                table: "inspection_results",
                column: "gage_id");

            migrationBuilder.AddForeignKey(
                name: "FK_inspection_results_gages_gage_id",
                table: "inspection_results",
                column: "gage_id",
                principalTable: "gages",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(
                """
                CREATE FUNCTION validate_inspection_result_gage_selection()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                DECLARE
                    selected_gage_type_id bigint;
                    selected_gage_number text;
                    criterion_gage_type_id bigint;
                BEGIN
                    IF NEW.gage_id IS NULL THEN
                        NEW.gage_number := NULL;
                        RETURN NEW;
                    END IF;

                    SELECT gage_type_id, gage_number
                    INTO selected_gage_type_id, selected_gage_number
                    FROM gages
                    WHERE id = NEW.gage_id;

                    SELECT gage_type_id
                    INTO criterion_gage_type_id
                    FROM inspection_criteria
                    WHERE id = NEW.inspection_criterion_id;

                    IF selected_gage_type_id IS DISTINCT FROM criterion_gage_type_id THEN
                        RAISE EXCEPTION 'The selected gage does not match the inspection method.'
                            USING ERRCODE = '23514';
                    END IF;

                    IF TG_OP = 'INSERT' OR NEW.gage_id IS DISTINCT FROM OLD.gage_id THEN
                        NEW.gage_number := selected_gage_number;
                    ELSIF NEW.gage_number IS DISTINCT FROM OLD.gage_number THEN
                        RAISE EXCEPTION 'The recorded gage number snapshot is immutable.'
                            USING ERRCODE = '55000';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER TR_inspection_results_validate_gage
                BEFORE INSERT OR UPDATE ON inspection_results
                FOR EACH ROW
                EXECUTE FUNCTION validate_inspection_result_gage_selection();

                CREATE FUNCTION prevent_used_gage_type_change()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    IF NEW.gage_type_id IS DISTINCT FROM OLD.gage_type_id
                        AND EXISTS (
                            SELECT 1
                            FROM inspection_results
                            WHERE gage_id = OLD.id)
                    THEN
                        RAISE EXCEPTION 'A gage used on an inspection cannot change gage type.'
                            USING ERRCODE = '23001';
                    END IF;

                    RETURN NEW;
                END;
                $$;

                CREATE TRIGGER TR_gages_protect_used_type
                BEFORE UPDATE OF gage_type_id ON gages
                FOR EACH ROW
                EXECUTE FUNCTION prevent_used_gage_type_change();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TRIGGER IF EXISTS TR_gages_protect_used_type ON gages;
                DROP FUNCTION IF EXISTS prevent_used_gage_type_change();
                DROP TRIGGER IF EXISTS TR_inspection_results_validate_gage ON inspection_results;
                DROP FUNCTION IF EXISTS validate_inspection_result_gage_selection();
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_inspection_results_gages_gage_id",
                table: "inspection_results");

            migrationBuilder.DropIndex(
                name: "IX_inspection_results_gage_id",
                table: "inspection_results");

            migrationBuilder.DropColumn(
                name: "gage_id",
                table: "inspection_results");

            migrationBuilder.DropColumn(
                name: "gage_number",
                table: "inspection_results");
        }
    }
}
