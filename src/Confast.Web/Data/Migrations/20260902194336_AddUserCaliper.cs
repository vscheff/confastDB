using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserCaliper : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "caliper_id",
                table: "identity_users",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_identity_users_caliper_id",
                table: "identity_users",
                column: "caliper_id");

            migrationBuilder.AddForeignKey(
                name: "FK_identity_users_caliper_id",
                table: "identity_users",
                column: "caliper_id",
                principalTable: "gages",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_identity_users_caliper_id",
                table: "identity_users");

            migrationBuilder.DropIndex(
                name: "IX_identity_users_caliper_id",
                table: "identity_users");

            migrationBuilder.DropColumn(
                name: "caliper_id",
                table: "identity_users");
        }
    }
}
