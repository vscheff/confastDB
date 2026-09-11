using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations;

public partial class WholeNumberProductionEfficiency : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_production_efficiency",
            table: "production_settings");

        migrationBuilder.AlterColumn<decimal>(
            name: "efficiency_percent",
            table: "production_settings",
            type: "numeric(24,0)",
            precision: 24,
            scale: 0,
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(24,6)",
            oldPrecision: 24,
            oldScale: 6);

        migrationBuilder.AddCheckConstraint(
            name: "ck_production_efficiency",
            table: "production_settings",
            sql: "id = 1 AND efficiency_percent = trunc(efficiency_percent) AND efficiency_percent > 0 AND efficiency_percent <= 100");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_production_efficiency",
            table: "production_settings");

        migrationBuilder.AlterColumn<decimal>(
            name: "efficiency_percent",
            table: "production_settings",
            type: "numeric(24,6)",
            precision: 24,
            scale: 6,
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(24,0)",
            oldPrecision: 24,
            oldScale: 0);

        migrationBuilder.AddCheckConstraint(
            name: "ck_production_efficiency",
            table: "production_settings",
            sql: "id = 1 AND efficiency_percent > 0 AND efficiency_percent <= 100");
    }
}
