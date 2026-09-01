using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSecondaryProcessGatedInspectionCriteria : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "secondary_process_requirement_id",
                table: "inspection_criteria",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_inspection_criteria_secondary_process_requirement_id_revision_id",
                table: "inspection_criteria",
                columns: new[] { "secondary_process_requirement_id", "inspection_criteria_revision_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_inspection_criteria_secondary_process_requirement_id_revision_id",
                table: "inspection_criteria",
                columns: new[] { "secondary_process_requirement_id", "inspection_criteria_revision_id" },
                principalTable: "secondary_process_requirements",
                principalColumns: new[] { "id", "inspection_criteria_revision_id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_inspection_criteria_secondary_process_requirement_id_revision_id",
                table: "inspection_criteria");

            migrationBuilder.DropIndex(
                name: "IX_inspection_criteria_secondary_process_requirement_id_revision_id",
                table: "inspection_criteria");

            migrationBuilder.DropColumn(
                name: "secondary_process_requirement_id",
                table: "inspection_criteria");
        }
    }
}
