using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIdlePresencePreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_identity_users_presence_preference",
                table: "identity_users");

            migrationBuilder.AddCheckConstraint(
                name: "CK_identity_users_presence_preference",
                table: "identity_users",
                sql: "presence_preference IN (0, 1, 2, 4)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_identity_users_presence_preference",
                table: "identity_users");

            migrationBuilder.AddCheckConstraint(
                name: "CK_identity_users_presence_preference",
                table: "identity_users",
                sql: "presence_preference IN (0, 2, 4)");
        }
    }
}
