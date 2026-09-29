using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatMessagePinning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "pinned_at_utc",
                table: "chat_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "pinned_by_user_id",
                table: "chat_messages",
                type: "text",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_conversation_id_pinned_at_utc",
                table: "chat_messages",
                columns: new[] { "conversation_id", "pinned_at_utc" },
                filter: "pinned_at_utc IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_pinned_by_user_id",
                table: "chat_messages",
                column: "pinned_by_user_id");

            migrationBuilder.AddCheckConstraint(
                name: "CK_chat_messages_pin",
                table: "chat_messages",
                sql: "(pinned_at_utc IS NULL) = (pinned_by_user_id IS NULL) AND (deleted_at_utc IS NULL OR pinned_at_utc IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_chat_messages_identity_users_pinned_by_user_id",
                table: "chat_messages",
                column: "pinned_by_user_id",
                principalTable: "identity_users",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_chat_messages_identity_users_pinned_by_user_id",
                table: "chat_messages");

            migrationBuilder.DropIndex(
                name: "IX_chat_messages_conversation_id_pinned_at_utc",
                table: "chat_messages");

            migrationBuilder.DropIndex(
                name: "IX_chat_messages_pinned_by_user_id",
                table: "chat_messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_chat_messages_pin",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "pinned_at_utc",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "pinned_by_user_id",
                table: "chat_messages");
        }
    }
}
