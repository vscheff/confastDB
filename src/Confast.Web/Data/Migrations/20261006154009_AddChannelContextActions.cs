using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelContextActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "deleted_at_utc",
                table: "chat_conversations",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "deleted_by_user_id",
                table: "chat_conversations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "pinned_to_top_at_utc",
                table: "chat_conversation_members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_conversations_deleted_by_user_id",
                table: "chat_conversations",
                column: "deleted_by_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_chat_conversations_identity_users_deleted_by_user_id",
                table: "chat_conversations",
                column: "deleted_by_user_id",
                principalTable: "identity_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_chat_conversations_identity_users_deleted_by_user_id",
                table: "chat_conversations");

            migrationBuilder.DropIndex(
                name: "IX_chat_conversations_deleted_by_user_id",
                table: "chat_conversations");

            migrationBuilder.DropColumn(
                name: "deleted_at_utc",
                table: "chat_conversations");

            migrationBuilder.DropColumn(
                name: "deleted_by_user_id",
                table: "chat_conversations");

            migrationBuilder.DropColumn(
                name: "pinned_to_top_at_utc",
                table: "chat_conversation_members");
        }
    }
}
