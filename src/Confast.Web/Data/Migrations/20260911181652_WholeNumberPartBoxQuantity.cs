using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class WholeNumberPartBoxQuantity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_part_box_quantity",
                table: "parts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_part_box_quantity",
                table: "parts",
                sql: "box_quantity IS NULL OR (box_quantity = trunc(box_quantity) AND box_quantity > 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_part_box_quantity",
                table: "parts");

            migrationBuilder.AddCheckConstraint(
                name: "ck_part_box_quantity",
                table: "parts",
                sql: "box_quantity IS NULL OR box_quantity > 0");
        }
    }
}
