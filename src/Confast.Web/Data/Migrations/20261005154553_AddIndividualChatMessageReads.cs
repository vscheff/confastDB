using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIndividualChatMessageReads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_message_reads",
                columns: table => new
                {
                    message_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    conversation_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_message_reads", x => new { x.user_id, x.message_id });
                    table.ForeignKey(
                        name: "FK_chat_message_reads_chat_conversation_members_conversation_i~",
                        columns: x => new { x.conversation_id, x.user_id },
                        principalTable: "chat_conversation_members",
                        principalColumns: new[] { "conversation_id", "user_id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_chat_message_reads_chat_messages_conversation_id_message_id",
                        columns: x => new { x.conversation_id, x.message_id },
                        principalTable: "chat_messages",
                        principalColumns: new[] { "conversation_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_message_reads_conversation_id_message_id",
                table: "chat_message_reads",
                columns: new[] { "conversation_id", "message_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_message_reads_conversation_id_user_id",
                table: "chat_message_reads",
                columns: new[] { "conversation_id", "user_id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_message_reads");
        }
    }
}
