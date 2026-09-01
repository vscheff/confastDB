using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowDuplicateInspectionRequirementNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_inspection_criteria_revision_id_inspection_number",
                table: "inspection_criteria");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_inspection_criteria_revision_id_inspection_number",
                table: "inspection_criteria",
                columns: new[] { "inspection_criteria_revision_id", "inspection_number" },
                unique: true);
        }
    }
}
