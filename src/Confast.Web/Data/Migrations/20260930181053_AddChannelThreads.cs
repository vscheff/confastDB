using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChannelThreads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "channel_thread_id",
                table: "chat_messages",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_chat_conversations_id_kind",
                table: "chat_conversations",
                columns: new[] { "id", "kind" });

            migrationBuilder.CreateTable(
                name: "chat_channel_threads",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    conversation_id = table.Column<long>(type: "bigint", nullable: false),
                    conversation_kind = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    created_by_user_id = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    last_message_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    started_from_message_id = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_channel_threads", x => x.id);
                    table.UniqueConstraint("AK_chat_channel_threads_conversation_id_id", x => new { x.conversation_id, x.id });
                    table.CheckConstraint("CK_chat_channel_threads_channel", "conversation_kind = 1");
                    table.CheckConstraint("CK_chat_channel_threads_title", "btrim(title) <> ''");
                    table.ForeignKey(
                        name: "FK_chat_channel_threads_chat_conversations_conversation_id_con~",
                        columns: x => new { x.conversation_id, x.conversation_kind },
                        principalTable: "chat_conversations",
                        principalColumns: new[] { "id", "kind" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_channel_threads_chat_messages_conversation_id_started_~",
                        columns: x => new { x.conversation_id, x.started_from_message_id },
                        principalTable: "chat_messages",
                        principalColumns: new[] { "conversation_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_channel_threads_identity_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_messages_conversation_id_channel_thread_id_id",
                table: "chat_messages",
                columns: new[] { "conversation_id", "channel_thread_id", "id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_chat_messages_thread_notice",
                table: "chat_messages",
                sql: "type <> 2 OR channel_thread_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_chat_channel_threads_conversation_id_conversation_kind",
                table: "chat_channel_threads",
                columns: new[] { "conversation_id", "conversation_kind" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_channel_threads_conversation_id_last_message_at_utc",
                table: "chat_channel_threads",
                columns: new[] { "conversation_id", "last_message_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_channel_threads_conversation_id_started_from_message_id",
                table: "chat_channel_threads",
                columns: new[] { "conversation_id", "started_from_message_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_channel_threads_created_by_user_id",
                table: "chat_channel_threads",
                column: "created_by_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_chat_messages_chat_channel_threads_conversation_id_channel_~",
                table: "chat_messages",
                columns: new[] { "conversation_id", "channel_thread_id" },
                principalTable: "chat_channel_threads",
                principalColumns: new[] { "conversation_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_chat_messages_chat_channel_threads_conversation_id_channel_~",
                table: "chat_messages");

            migrationBuilder.DropTable(
                name: "chat_channel_threads");

            migrationBuilder.DropIndex(
                name: "IX_chat_messages_conversation_id_channel_thread_id_id",
                table: "chat_messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_chat_messages_thread_notice",
                table: "chat_messages");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_chat_conversations_id_kind",
                table: "chat_conversations");

            migrationBuilder.DropColumn(
                name: "channel_thread_id",
                table: "chat_messages");
        }
    }
}
