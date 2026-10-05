using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledChatMessages : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_scheduled_messages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    conversation_id = table.Column<long>(type: "bigint", nullable: false),
                    sender_user_id = table.Column<string>(type: "text", nullable: false),
                    body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    channel_thread_id = table.Column<long>(type: "bigint", nullable: true),
                    reply_to_message_id = table.Column<long>(type: "bigint", nullable: true),
                    scheduled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    failure = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_scheduled_messages", x => x.id);
                    table.CheckConstraint("CK_chat_scheduled_messages_body", "char_length(body) BETWEEN 1 AND 4000");
                    table.ForeignKey(
                        name: "FK_chat_scheduled_messages_chat_channel_threads_conversation_i~",
                        columns: x => new { x.conversation_id, x.channel_thread_id },
                        principalTable: "chat_channel_threads",
                        principalColumns: new[] { "conversation_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_scheduled_messages_chat_conversations_conversation_id",
                        column: x => x.conversation_id,
                        principalTable: "chat_conversations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_scheduled_messages_chat_messages_conversation_id_reply~",
                        columns: x => new { x.conversation_id, x.reply_to_message_id },
                        principalTable: "chat_messages",
                        principalColumns: new[] { "conversation_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_scheduled_messages_identity_users_sender_user_id",
                        column: x => x.sender_user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "chat_scheduled_attachments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    scheduled_message_id = table.Column<long>(type: "bigint", nullable: false),
                    file_name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    content = table.Column<byte[]>(type: "bytea", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_scheduled_attachments", x => x.id);
                    table.CheckConstraint("CK_chat_scheduled_attachments_content", "octet_length(content) BETWEEN 1 AND 26214400");
                    table.CheckConstraint("CK_chat_scheduled_attachments_name", "btrim(file_name) <> ''");
                    table.ForeignKey(
                        name: "FK_chat_scheduled_attachments_chat_scheduled_messages_schedule~",
                        column: x => x.scheduled_message_id,
                        principalTable: "chat_scheduled_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_scheduled_attachments_scheduled_message_id",
                table: "chat_scheduled_attachments",
                column: "scheduled_message_id");

            migrationBuilder.CreateIndex(
                name: "IX_chat_scheduled_messages_conversation_id_channel_thread_id",
                table: "chat_scheduled_messages",
                columns: new[] { "conversation_id", "channel_thread_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_scheduled_messages_conversation_id_reply_to_message_id",
                table: "chat_scheduled_messages",
                columns: new[] { "conversation_id", "reply_to_message_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_scheduled_messages_scheduled_at_utc",
                table: "chat_scheduled_messages",
                column: "scheduled_at_utc",
                filter: "failure IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_chat_scheduled_messages_sender_user_id",
                table: "chat_scheduled_messages",
                column: "sender_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_scheduled_attachments");

            migrationBuilder.DropTable(
                name: "chat_scheduled_messages");
        }
    }
}
