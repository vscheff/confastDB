using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatMessageReplies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "reply_to_message_id",
                table: "chat_messages",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_conversation_id_reply_to_message_id",
                table: "chat_messages",
                columns: new[] { "conversation_id", "reply_to_message_id" });

            migrationBuilder.AddForeignKey(
                name: "FK_chat_messages_chat_messages_conversation_id_reply_to_messag~",
                table: "chat_messages",
                columns: new[] { "conversation_id", "reply_to_message_id" },
                principalTable: "chat_messages",
                principalColumns: new[] { "conversation_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_chat_messages_chat_messages_conversation_id_reply_to_messag~",
                table: "chat_messages");

            migrationBuilder.DropIndex(
                name: "IX_chat_messages_conversation_id_reply_to_message_id",
                table: "chat_messages");

            migrationBuilder.DropColumn(
                name: "reply_to_message_id",
                table: "chat_messages");
        }
    }
}
