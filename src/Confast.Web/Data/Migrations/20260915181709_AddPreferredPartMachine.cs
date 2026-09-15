using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPreferredPartMachine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_part_machines_part_id",
                table: "part_machines");

            migrationBuilder.AddColumn<bool>(
                name: "is_preferred",
                table: "part_machines",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Preserve the automatic-assignment behavior that existed before an
            // explicit preference: use the first active eligible machine by name.
            migrationBuilder.Sql("""
                UPDATE part_machines AS rate
                SET is_preferred = TRUE
                FROM (
                    SELECT DISTINCT ON (candidate.part_id)
                        candidate.part_id,
                        candidate.machine_id
                    FROM part_machines AS candidate
                    INNER JOIN sorting_machines AS machine ON machine.id = candidate.machine_id
                    WHERE machine.is_active
                    ORDER BY candidate.part_id, machine.name, candidate.machine_id
                ) AS preferred
                WHERE rate.part_id = preferred.part_id
                  AND rate.machine_id = preferred.machine_id;
                """);

            migrationBuilder.CreateIndex(
                name: "UX_part_machines_preferred_part",
                table: "part_machines",
                column: "part_id",
                unique: true,
                filter: "is_preferred");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_part_machines_preferred_part",
                table: "part_machines");

            migrationBuilder.DropColumn(
                name: "is_preferred",
                table: "part_machines");

            migrationBuilder.CreateIndex(
                name: "IX_part_machines_part_id",
                table: "part_machines",
                column: "part_id");
        }
    }
}
