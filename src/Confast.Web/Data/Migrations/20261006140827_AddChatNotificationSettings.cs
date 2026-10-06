using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatNotificationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_muted",
                table: "chat_conversation_members",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "muted_until_utc",
                table: "chat_conversation_members",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "notification_mode",
                table: "chat_conversation_members",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "chat_category_notification_preferences",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    group_id = table.Column<long>(type: "bigint", nullable: false),
                    mode = table.Column<int>(type: "integer", nullable: false),
                    is_muted = table.Column<bool>(type: "boolean", nullable: false),
                    muted_until_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_category_notification_preferences", x => new { x.user_id, x.group_id });
                    table.CheckConstraint("CK_chat_category_notifications_mode", "mode BETWEEN 1 AND 4");
                    table.CheckConstraint("CK_chat_category_notifications_mute", "is_muted OR muted_until_utc IS NULL");
                    table.ForeignKey(
                        name: "FK_chat_category_notification_preferences_chat_channel_groups_~",
                        column: x => x.group_id,
                        principalTable: "chat_channel_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_chat_category_notification_preferences_identity_users_user_~",
                        column: x => x.user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_chat_members_mute",
                table: "chat_conversation_members",
                sql: "is_muted OR muted_until_utc IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_chat_members_notification_mode",
                table: "chat_conversation_members",
                sql: "notification_mode BETWEEN 0 AND 4");

            migrationBuilder.CreateIndex(
                name: "IX_chat_category_notification_preferences_group_id",
                table: "chat_category_notification_preferences",
                column: "group_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_category_notification_preferences");

            migrationBuilder.DropCheckConstraint(
                name: "CK_chat_members_mute",
                table: "chat_conversation_members");

            migrationBuilder.DropCheckConstraint(
                name: "CK_chat_members_notification_mode",
                table: "chat_conversation_members");

            migrationBuilder.DropColumn(
                name: "is_muted",
                table: "chat_conversation_members");

            migrationBuilder.DropColumn(
                name: "muted_until_utc",
                table: "chat_conversation_members");

            migrationBuilder.DropColumn(
                name: "notification_mode",
                table: "chat_conversation_members");
        }
    }
}
