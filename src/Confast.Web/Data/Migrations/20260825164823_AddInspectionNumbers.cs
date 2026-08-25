using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInspectionNumbers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "inspection_number",
                table: "inspection_criteria",
                type: "integer",
                nullable: true);

            // Existing row order is the only reliable initial number. Temporarily
            // suspend the published-row guard solely for this deterministic backfill.
            migrationBuilder.Sql(
                """
                ALTER TABLE inspection_criteria
                    DISABLE TRIGGER TR_inspection_criteria_protect_published;

                UPDATE inspection_criteria
                SET inspection_number = display_order;

                ALTER TABLE inspection_criteria
                    ENABLE TRIGGER TR_inspection_criteria_protect_published;
                """);

            migrationBuilder.AlterColumn<int>(
                name: "inspection_number",
                table: "inspection_criteria",
                type: "integer",
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "UX_inspection_criteria_revision_id_inspection_number",
                table: "inspection_criteria",
                columns: new[] { "inspection_criteria_revision_id", "inspection_number" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_inspection_criteria_inspection_number",
                table: "inspection_criteria",
                sql: "inspection_number > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_inspection_criteria_revision_id_inspection_number",
                table: "inspection_criteria");

            migrationBuilder.DropCheckConstraint(
                name: "CK_inspection_criteria_inspection_number",
                table: "inspection_criteria");

            migrationBuilder.DropColumn(
                name: "inspection_number",
                table: "inspection_criteria");
        }
    }
}
