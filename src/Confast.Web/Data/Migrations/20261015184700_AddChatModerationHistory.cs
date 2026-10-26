using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatModerationHistory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_moderation_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    message_id = table.Column<long>(type: "bigint", nullable: false),
                    conversation_id = table.Column<long>(type: "bigint", nullable: false),
                    channel_thread_id = table.Column<long>(type: "bigint", nullable: true),
                    moderator_user_id = table.Column<string>(type: "text", nullable: false),
                    original_sender_user_id = table.Column<string>(type: "text", nullable: false),
                    deleted_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    was_pinned = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_moderation_history", x => x.id);
                    table.CheckConstraint("CK_chat_moderation_other_sender", "moderator_user_id <> original_sender_user_id");
                    table.CheckConstraint("CK_chat_moderation_reason", "btrim(reason) <> ''");
                    table.ForeignKey(
                        name: "FK_chat_moderation_history_chat_channel_threads_conversation_i~",
                        columns: x => new { x.conversation_id, x.channel_thread_id },
                        principalTable: "chat_channel_threads",
                        principalColumns: new[] { "conversation_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_moderation_history_chat_messages_conversation_id_messa~",
                        columns: x => new { x.conversation_id, x.message_id },
                        principalTable: "chat_messages",
                        principalColumns: new[] { "conversation_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_moderation_history_identity_users_moderator_user_id",
                        column: x => x.moderator_user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chat_moderation_history_identity_users_original_sender_user~",
                        column: x => x.original_sender_user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_moderation_history_conversation_id_channel_thread_id",
                table: "chat_moderation_history",
                columns: new[] { "conversation_id", "channel_thread_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_moderation_history_conversation_id_id",
                table: "chat_moderation_history",
                columns: new[] { "conversation_id", "id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_moderation_history_conversation_id_message_id",
                table: "chat_moderation_history",
                columns: new[] { "conversation_id", "message_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_moderation_history_message_id",
                table: "chat_moderation_history",
                column: "message_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_moderation_history_moderator_user_id",
                table: "chat_moderation_history",
                column: "moderator_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_chat_moderation_history_original_sender_user_id",
                table: "chat_moderation_history",
                column: "original_sender_user_id");
            migrationBuilder.Sql("""
                CREATE FUNCTION protect_chat_moderation_history() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    RAISE EXCEPTION 'Chat moderation history is append-only' USING ERRCODE = '23514';
                END;
                $$;
                CREATE TRIGGER chat_moderation_immutable BEFORE UPDATE OR DELETE ON chat_moderation_history
                    FOR EACH ROW EXECUTE FUNCTION protect_chat_moderation_history();
                CREATE TRIGGER chat_moderation_no_truncate BEFORE TRUNCATE ON chat_moderation_history
                    FOR EACH STATEMENT EXECUTE FUNCTION protect_chat_moderation_history();

                CREATE FUNCTION validate_chat_moderation_history() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF NOT EXISTS (
                        SELECT 1 FROM chat_messages m
                        JOIN chat_conversations c ON c.id = m.conversation_id
                        JOIN chat_conversation_members member ON member.conversation_id = c.id
                            AND member.user_id = NEW.moderator_user_id
                        WHERE m.id = NEW.message_id AND m.conversation_id = NEW.conversation_id
                            AND m.channel_thread_id IS NOT DISTINCT FROM NEW.channel_thread_id
                            AND c.kind = 1 AND c.deleted_at_utc IS NULL AND m.type = 0 AND m.giphy_id IS NULL
                            AND m.sender_user_id = NEW.original_sender_user_id
                            AND m.deleted_by_user_id = NEW.moderator_user_id AND m.deleted_at_utc = NEW.deleted_at_utc
                            AND m.pinned_at_utc IS NULL AND m.pinned_by_user_id IS NULL
                    ) THEN
                        RAISE EXCEPTION 'Moderation history must match a channel message deletion' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE CONSTRAINT TRIGGER chat_moderation_matches_deletion AFTER INSERT ON chat_moderation_history
                    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION validate_chat_moderation_history();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_moderation_history");
            migrationBuilder.Sql("DROP FUNCTION validate_chat_moderation_history(); DROP FUNCTION protect_chat_moderation_history();");
        }
    }
}
