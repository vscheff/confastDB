using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatPolls : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_polls",
                columns: table => new
                {
                    message_id = table.Column<long>(type: "bigint", nullable: false),
                    starts_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ends_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    allow_multiple_answers = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_polls", x => x.message_id);
                    table.CheckConstraint("CK_chat_polls_duration", "ends_at_utc - starts_at_utc IN (interval '1 hour', interval '4 hours', interval '8 hours', interval '24 hours', interval '3 days', interval '7 days', interval '14 days')");
                    table.ForeignKey(
                        name: "FK_chat_polls_chat_messages_message_id",
                        column: x => x.message_id,
                        principalTable: "chat_messages",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chat_poll_answers",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    poll_message_id = table.Column<long>(type: "bigint", nullable: false),
                    position = table.Column<int>(type: "integer", nullable: false),
                    text = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    emoji = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_poll_answers", x => x.id);
                    table.UniqueConstraint("AK_chat_poll_answers_poll_message_id_id", x => new { x.poll_message_id, x.id });
                    table.CheckConstraint("CK_chat_poll_answers_position", "position >= 0");
                    table.CheckConstraint("CK_chat_poll_answers_text", "btrim(text) <> ''");
                    table.ForeignKey(
                        name: "FK_chat_poll_answers_chat_polls_poll_message_id",
                        column: x => x.poll_message_id,
                        principalTable: "chat_polls",
                        principalColumn: "message_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chat_poll_votes",
                columns: table => new
                {
                    poll_message_id = table.Column<long>(type: "bigint", nullable: false),
                    answer_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_poll_votes", x => new { x.poll_message_id, x.user_id, x.answer_id });
                    table.ForeignKey(
                        name: "FK_chat_poll_votes_chat_poll_answers_poll_message_id_answer_id",
                        columns: x => new { x.poll_message_id, x.answer_id },
                        principalTable: "chat_poll_answers",
                        principalColumns: new[] { "poll_message_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_chat_poll_votes_identity_users_user_id",
                        column: x => x.user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_poll_answers_poll_message_id_position",
                table: "chat_poll_answers",
                columns: new[] { "poll_message_id", "position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_poll_votes_poll_message_id_answer_id",
                table: "chat_poll_votes",
                columns: new[] { "poll_message_id", "answer_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_poll_votes_user_id",
                table: "chat_poll_votes",
                column: "user_id");
            migrationBuilder.Sql("""
                CREATE FUNCTION enforce_chat_poll_vote() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE multiple_answers boolean;
                BEGIN
                    PERFORM id FROM chat_messages WHERE id = NEW.poll_message_id FOR UPDATE;
                    SELECT allow_multiple_answers INTO multiple_answers FROM chat_polls WHERE message_id = NEW.poll_message_id;
                    IF NOT multiple_answers AND EXISTS (
                        SELECT 1 FROM chat_poll_votes WHERE poll_message_id = NEW.poll_message_id
                        AND user_id = NEW.user_id AND answer_id <> NEW.answer_id) THEN
                        RAISE EXCEPTION 'Only one answer may be selected in this poll' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$;
                CREATE TRIGGER chat_poll_vote_guard BEFORE INSERT OR UPDATE ON chat_poll_votes
                FOR EACH ROW EXECUTE FUNCTION enforce_chat_poll_vote();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_poll_votes");

            migrationBuilder.Sql("DROP FUNCTION enforce_chat_poll_vote();");
            migrationBuilder.DropTable(
                name: "chat_poll_answers");

            migrationBuilder.DropTable(
                name: "chat_polls");
        }
    }
}

