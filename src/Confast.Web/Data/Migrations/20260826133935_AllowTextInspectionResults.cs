using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowTextInspectionResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_inspection_results_actual_minimum_maximum",
                table: "inspection_results");

            migrationBuilder.Sql(
                """
                ALTER TABLE inspection_results
                    ALTER COLUMN actual_min TYPE text
                    USING actual_min::text;
                ALTER TABLE inspection_results
                    ALTER COLUMN actual_max TYPE text
                    USING actual_max::text;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // This intentionally fails rather than discarding data if Pass or OK
            // has been stored and cannot be represented by the old numeric schema.
            migrationBuilder.Sql(
                """
                ALTER TABLE inspection_results
                    ALTER COLUMN actual_min TYPE numeric(18,6)
                    USING NULLIF(BTRIM(actual_min), '')::numeric;
                ALTER TABLE inspection_results
                    ALTER COLUMN actual_max TYPE numeric(18,6)
                    USING NULLIF(BTRIM(actual_max), '')::numeric;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "CK_inspection_results_actual_minimum_maximum",
                table: "inspection_results",
                sql: "actual_min IS NULL OR actual_max IS NULL OR actual_min <= actual_max");
        }
    }
}
