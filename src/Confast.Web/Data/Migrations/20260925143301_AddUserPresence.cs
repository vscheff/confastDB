using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserPresence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "presence_preference",
                table: "identity_users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "status_emoji",
                table: "identity_users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "status_message",
                table: "identity_users",
                type: "character varying(140)",
                maxLength: 140,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "chat_presence_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    last_heartbeat_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_activity_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_presence_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_chat_presence_sessions_identity_users_user_id",
                        column: x => x.user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_identity_users_presence_preference",
                table: "identity_users",
                sql: "presence_preference IN (0, 2, 4)");

            migrationBuilder.CreateIndex(
                name: "IX_chat_presence_sessions_user_id_last_heartbeat_at_utc",
                table: "chat_presence_sessions",
                columns: new[] { "user_id", "last_heartbeat_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_presence_sessions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_identity_users_presence_preference",
                table: "identity_users");

            migrationBuilder.DropColumn(
                name: "presence_preference",
                table: "identity_users");

            migrationBuilder.DropColumn(
                name: "status_emoji",
                table: "identity_users");

            migrationBuilder.DropColumn(
                name: "status_message",
                table: "identity_users");
        }
    }
}
