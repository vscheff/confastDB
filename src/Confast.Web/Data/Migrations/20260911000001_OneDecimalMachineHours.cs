using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations;

public partial class OneDecimalMachineHours : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_machine_hours",
            table: "machine_working_days");

        migrationBuilder.AlterColumn<decimal>(
            name: "hours",
            table: "machine_working_days",
            type: "numeric(24,1)",
            precision: 24,
            scale: 1,
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(24,6)",
            oldPrecision: 24,
            oldScale: 6);

        migrationBuilder.AddCheckConstraint(
            name: "ck_machine_hours",
            table: "machine_working_days",
            sql: "day BETWEEN 0 AND 6 AND hours = round(hours, 1) AND hours >= 0 AND hours <= 24");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_machine_hours",
            table: "machine_working_days");

        migrationBuilder.AlterColumn<decimal>(
            name: "hours",
            table: "machine_working_days",
            type: "numeric(24,6)",
            precision: 24,
            scale: 6,
            nullable: false,
            oldClrType: typeof(decimal),
            oldType: "numeric(24,1)",
            oldPrecision: 24,
            oldScale: 1);

        migrationBuilder.AddCheckConstraint(
            name: "ck_machine_hours",
            table: "machine_working_days",
            sql: "day BETWEEN 0 AND 6 AND hours >= 0 AND hours <= 24");
    }
}
