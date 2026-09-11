using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class DefaultProductionWorkingDays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "uses_default_working_days",
                table: "sorting_machines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "production_default_working_days",
                columns: table => new
                {
                    settings_id = table.Column<int>(type: "integer", nullable: false),
                    day = table.Column<int>(type: "integer", nullable: false),
                    hours = table.Column<decimal>(type: "numeric(24,1)", precision: 24, scale: 1, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_production_default_working_days", x => new { x.settings_id, x.day });
                    table.CheckConstraint("ck_default_machine_hours", "day BETWEEN 0 AND 6 AND hours = round(hours, 1) AND hours >= 0 AND hours <= 24");
                    table.ForeignKey(
                        name: "FK_production_default_working_days_production_settings_setting~",
                        column: x => x.settings_id,
                        principalTable: "production_settings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "production_default_working_days",
                columns: new[] { "day", "settings_id", "hours" },
                values: new object[,]
                {
                    { 0, 1, 0m },
                    { 1, 1, 8m },
                    { 2, 1, 8m },
                    { 3, 1, 8m },
                    { 4, 1, 8m },
                    { 5, 1, 8m },
                    { 6, 1, 0m }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "production_default_working_days");

            migrationBuilder.DropColumn(
                name: "uses_default_working_days",
                table: "sorting_machines");
        }
    }
}
