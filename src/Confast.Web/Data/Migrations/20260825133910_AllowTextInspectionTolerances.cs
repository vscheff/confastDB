using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowTextInspectionTolerances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_inspection_criteria_minimum_maximum",
                table: "inspection_criteria");

            migrationBuilder.Sql(
                """
                ALTER TABLE inspection_criteria
                    RENAME COLUMN maximum_value TO maximum_or_tolerance;
                ALTER TABLE inspection_criteria
                    ALTER COLUMN maximum_or_tolerance TYPE text
                    USING maximum_or_tolerance::text;

                ALTER TABLE inspection_criteria
                    RENAME COLUMN minimum_value TO minimum;
                ALTER TABLE inspection_criteria
                    ALTER COLUMN minimum TYPE text
                    USING minimum::text;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // This intentionally fails rather than discarding data if a text callout
            // cannot be represented by the old numeric schema.
            migrationBuilder.Sql(
                """
                ALTER TABLE inspection_criteria
                    ALTER COLUMN maximum_or_tolerance TYPE numeric(18,6)
                    USING NULLIF(BTRIM(maximum_or_tolerance), '')::numeric;
                ALTER TABLE inspection_criteria
                    RENAME COLUMN maximum_or_tolerance TO maximum_value;

                ALTER TABLE inspection_criteria
                    ALTER COLUMN minimum TYPE numeric(18,6)
                    USING NULLIF(BTRIM(minimum), '')::numeric;
                ALTER TABLE inspection_criteria
                    RENAME COLUMN minimum TO minimum_value;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_inspection_criteria_minimum_maximum",
                table: "inspection_criteria",
                sql: "minimum_value IS NULL OR maximum_value IS NULL OR minimum_value <= maximum_value");
        }
    }
}
