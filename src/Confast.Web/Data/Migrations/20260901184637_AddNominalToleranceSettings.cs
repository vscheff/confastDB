using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNominalToleranceSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "nominal_tolerance_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    tolerance_floor = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    large_dimension_divisor = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: false),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_nominal_tolerance_settings", x => x.id);
                    table.CheckConstraint("CK_nominal_tolerance_settings_divisor_positive", "large_dimension_divisor > 0");
                    table.CheckConstraint("CK_nominal_tolerance_settings_floor_positive", "tolerance_floor > 0");
                    table.CheckConstraint("CK_nominal_tolerance_settings_singleton", "id = 1");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "nominal_tolerance_settings");
        }
    }
}
