using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class WholeNumberTargetPph : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_machine_pph",
                table: "part_machines");

            migrationBuilder.AddCheckConstraint(
                name: "ck_machine_pph",
                table: "part_machines",
                sql: "target_pph = trunc(target_pph) AND target_pph > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_machine_pph",
                table: "part_machines");

            migrationBuilder.AddCheckConstraint(
                name: "ck_machine_pph",
                table: "part_machines",
                sql: "target_pph > 0");
        }
    }
}
