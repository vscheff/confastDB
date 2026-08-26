using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInspectionQuantityReceived : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "quantity_received",
                table: "inspections",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_inspections_quantity_received",
                table: "inspections",
                sql: "quantity_received IS NULL OR quantity_received > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_inspections_quantity_received",
                table: "inspections");

            migrationBuilder.DropColumn(
                name: "quantity_received",
                table: "inspections");
        }
    }
}
