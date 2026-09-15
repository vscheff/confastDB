using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDepartedContainerScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "container_group_part_id",
                table: "production_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_production_jobs_container_group_part_id",
                table: "production_jobs",
                column: "container_group_part_id",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_production_jobs_container_group_parts_container_group_part_id",
                table: "production_jobs",
                column: "container_group_part_id",
                principalTable: "container_group_parts",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_production_jobs_container_group_parts_container_group_part_id",
                table: "production_jobs");

            migrationBuilder.DropIndex(
                name: "IX_production_jobs_container_group_part_id",
                table: "production_jobs");

            migrationBuilder.DropColumn(
                name: "container_group_part_id",
                table: "production_jobs");
        }
    }
}
