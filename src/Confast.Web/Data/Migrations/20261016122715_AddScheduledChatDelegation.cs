using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddScheduledChatDelegation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_chat_scheduled_messages_scheduled_at_utc",
                table: "chat_scheduled_messages");

            migrationBuilder.AddColumn<int>(
                name: "attempt_count",
                table: "chat_scheduled_messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "authorization_epoch",
                table: "chat_scheduled_messages",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "catalog_version",
                table: "chat_scheduled_messages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTime>(
                name: "completed_at_utc",
                table: "chat_scheduled_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "created_at_utc",
                table: "chat_scheduled_messages",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<long>(
                name: "delivered_message_id",
                table: "chat_scheduled_messages",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "failure_code",
                table: "chat_scheduled_messages",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "installation_generation",
                table: "chat_scheduled_messages",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "next_attempt_at_utc",
                table: "chat_scheduled_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "purpose",
                table: "chat_scheduled_messages",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<Guid>(
                name: "request_id",
                table: "chat_scheduled_messages",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<int>(
                name: "state",
                table: "chat_scheduled_messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "valid_until_utc",
                table: "chat_scheduled_messages",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            // Preserve all legacy bodies/attachments, but do not invent proof of authorization.
            migrationBuilder.Sql("""
                UPDATE chat_scheduled_messages SET request_id = md5('legacy-chat:' || id::text)::uuid,
                    purpose = 'Legacy.Unverified', created_at_utc = CURRENT_TIMESTAMP,
                    valid_until_utc = scheduled_at_utc + interval '7 days', state = 2,
                    completed_at_utc = CURRENT_TIMESTAMP, failure_code = 'UnverifiedIntent',
                    failure = COALESCE(failure, 'This older draft has no verified delegation. Review and schedule it again.');
                """);
            migrationBuilder.CreateIndex(
                name: "IX_chat_scheduled_messages_conversation_id_delivered_message_id",
                table: "chat_scheduled_messages",
                columns: new[] { "conversation_id", "delivered_message_id" });

            migrationBuilder.CreateIndex(
                name: "IX_chat_scheduled_messages_request_id",
                table: "chat_scheduled_messages",
                column: "request_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_chat_scheduled_messages_scheduled_at_utc",
                table: "chat_scheduled_messages",
                column: "scheduled_at_utc",
                filter: "state = 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_chat_scheduled_messages_attempts",
                table: "chat_scheduled_messages",
                sql: "attempt_count BETWEEN 0 AND 5");

            migrationBuilder.AddCheckConstraint(
                name: "CK_chat_scheduled_messages_lifecycle",
                table: "chat_scheduled_messages",
                sql: "state BETWEEN 0 AND 3 AND ((state = 0 AND completed_at_utc IS NULL AND failure IS NULL) OR (state <> 0 AND completed_at_utc IS NOT NULL)) AND ((state = 1) = (delivered_message_id IS NOT NULL))");

            migrationBuilder.AddForeignKey(
                name: "FK_chat_scheduled_messages_chat_messages_conversation_id_deliv~",
                table: "chat_scheduled_messages",
                columns: new[] { "conversation_id", "delivered_message_id" },
                principalTable: "chat_messages",
                principalColumns: new[] { "conversation_id", "id" },
                onDelete: ReferentialAction.Restrict);
            migrationBuilder.Sql("""
                CREATE FUNCTION protect_scheduled_chat_intent() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF TG_OP = 'DELETE' THEN
                        RAISE EXCEPTION 'Scheduled chat receipts must be retained' USING ERRCODE = '23514';
                    END IF;
                    IF (NEW.request_id, NEW.purpose, NEW.sender_user_id, NEW.conversation_id, NEW.channel_thread_id,
                        NEW.reply_to_message_id, NEW.body, NEW.scheduled_at_utc, NEW.created_at_utc,
                        NEW.installation_generation, NEW.catalog_version, NEW.authorization_epoch, NEW.valid_until_utc)
                        IS DISTINCT FROM
                       (OLD.request_id, OLD.purpose, OLD.sender_user_id, OLD.conversation_id, OLD.channel_thread_id,
                        OLD.reply_to_message_id, OLD.body, OLD.scheduled_at_utc, OLD.created_at_utc,
                        OLD.installation_generation, OLD.catalog_version, OLD.authorization_epoch, OLD.valid_until_utc)
                        OR (OLD.state <> 0 AND NOT (OLD.state = 2 AND NEW.state = 3))
                        OR NEW.attempt_count < OLD.attempt_count THEN
                        RAISE EXCEPTION 'Scheduled chat intent or terminal receipt cannot be changed' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER protect_scheduled_chat_intent BEFORE UPDATE OR DELETE ON chat_scheduled_messages
                    FOR EACH ROW EXECUTE FUNCTION protect_scheduled_chat_intent();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // An older worker would redeliver retained receipts after downgrade. Refuse populated rollback.
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM chat_scheduled_messages) THEN
                        RAISE EXCEPTION 'Review and archive scheduled chat receipts before downgrade';
                    END IF;
                END $$;
                DROP TRIGGER protect_scheduled_chat_intent ON chat_scheduled_messages;
                DROP FUNCTION protect_scheduled_chat_intent();
                """);
            migrationBuilder.DropForeignKey(
                name: "FK_chat_scheduled_messages_chat_messages_conversation_id_deliv~",
                table: "chat_scheduled_messages");

            migrationBuilder.DropIndex(
                name: "IX_chat_scheduled_messages_conversation_id_delivered_message_id",
                table: "chat_scheduled_messages");

            migrationBuilder.DropIndex(
                name: "IX_chat_scheduled_messages_request_id",
                table: "chat_scheduled_messages");

            migrationBuilder.DropIndex(
                name: "IX_chat_scheduled_messages_scheduled_at_utc",
                table: "chat_scheduled_messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_chat_scheduled_messages_attempts",
                table: "chat_scheduled_messages");

            migrationBuilder.DropCheckConstraint(
                name: "CK_chat_scheduled_messages_lifecycle",
                table: "chat_scheduled_messages");

            migrationBuilder.DropColumn(
                name: "attempt_count",
                table: "chat_scheduled_messages");

            migrationBuilder.DropColumn(
                name: "authorization_epoch",
                table: "chat_scheduled_messages");

            migrationBuilder.DropColumn(
                name: "catalog_version",
                table: "chat_scheduled_messages");

            migrationBuilder.DropColumn(
                name: "completed_at_utc",
                table: "chat_scheduled_messages");

            migrationBuilder.DropColumn(
                name: "created_at_utc",
                table: "chat_scheduled_messages");

            migrationBuilder.DropColumn(
                name: "delivered_message_id",
                table: "chat_scheduled_messages");

            migrationBuilder.DropColumn(
                name: "failure_code",
                table: "chat_scheduled_messages");

            migrationBuilder.DropColumn(
                name: "installation_generation",
                table: "chat_scheduled_messages");

            migrationBuilder.DropColumn(
                name: "next_attempt_at_utc",
                table: "chat_scheduled_messages");

            migrationBuilder.DropColumn(
                name: "purpose",
                table: "chat_scheduled_messages");

            migrationBuilder.DropColumn(
                name: "request_id",
                table: "chat_scheduled_messages");

            migrationBuilder.DropColumn(
                name: "state",
                table: "chat_scheduled_messages");

            migrationBuilder.DropColumn(
                name: "valid_until_utc",
                table: "chat_scheduled_messages");

            migrationBuilder.CreateIndex(
                name: "IX_chat_scheduled_messages_scheduled_at_utc",
                table: "chat_scheduled_messages",
                column: "scheduled_at_utc",
                filter: "failure IS NULL");
        }
    }
}

