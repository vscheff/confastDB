using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatGifs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "giphy_id",
                table: "chat_messages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "chat_gif_favorites",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    giphy_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_gif_favorites", x => new { x.user_id, x.giphy_id });
                    table.CheckConstraint("CK_chat_gif_favorites_id", "giphy_id ~ '^[A-Za-z0-9]{1,100}$'");
                    table.ForeignKey(
                        name: "FK_chat_gif_favorites_identity_users_user_id",
                        column: x => x.user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_chat_messages_giphy_id",
                table: "chat_messages",
                sql: "giphy_id IS NULL OR giphy_id ~ '^[A-Za-z0-9]{1,100}$'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_gif_favorites");

            migrationBuilder.DropCheckConstraint(
                name: "CK_chat_messages_giphy_id",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "giphy_id",
                table: "chat_messages");
        }
    }
}
