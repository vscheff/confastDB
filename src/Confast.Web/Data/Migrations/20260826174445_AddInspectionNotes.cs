using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInspectionNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "in_house_notes",
                table: "inspections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "inspector_notes",
                table: "inspections",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "in_house_notes",
                table: "inspections");

            migrationBuilder.DropColumn(
                name: "inspector_notes",
                table: "inspections");
        }
    }
}
