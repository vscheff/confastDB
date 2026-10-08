using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupChats : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_chat_conversations_shape",
                table: "chat_conversations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_chat_conversations_shape",
                table: "chat_conversations",
                sql: "(kind = 0 AND visibility IS NULL AND name IS NULL AND direct_pair_key IS NOT NULL AND channel_group_id IS NULL AND channel_sort_order = 0) OR (kind = 1 AND visibility IS NOT NULL AND name IS NOT NULL AND btrim(name) <> '' AND direct_pair_key IS NULL AND channel_sort_order >= 0) OR (kind = 2 AND visibility IS NULL AND (name IS NULL OR btrim(name) <> '') AND direct_pair_key IS NULL AND channel_group_id IS NULL AND channel_sort_order = 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_chat_conversations_shape",
                table: "chat_conversations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_chat_conversations_shape",
                table: "chat_conversations",
                sql: "(kind = 0 AND visibility IS NULL AND name IS NULL AND direct_pair_key IS NOT NULL AND channel_group_id IS NULL AND channel_sort_order = 0) OR (kind = 1 AND visibility IS NOT NULL AND name IS NOT NULL AND btrim(name) <> '' AND direct_pair_key IS NULL AND channel_sort_order >= 0)");
        }
    }
}
