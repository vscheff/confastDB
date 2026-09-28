using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNestedChatChannelFolders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "parent_group_id",
                table: "chat_channel_groups",
                type: "bigint",
                nullable: true);

            // The old sidebar showed every ungrouped channel before the flat folder list.
            migrationBuilder.Sql("""
                UPDATE chat_channel_groups
                SET sort_order = sort_order + (
                    SELECT COALESCE(MAX(channel_sort_order) + 1, 0)
                    FROM chat_conversations
                    WHERE kind = 1 AND channel_group_id IS NULL
                )
                """);

            migrationBuilder.CreateIndex(
                name: "IX_chat_channel_groups_parent_group_id_sort_order_id",
                table: "chat_channel_groups",
                columns: new[] { "parent_group_id", "sort_order", "id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_chat_channel_groups_parent",
                table: "chat_channel_groups",
                sql: "parent_group_id IS NULL OR parent_group_id <> id");

            migrationBuilder.AddForeignKey(
                name: "FK_chat_channel_groups_chat_channel_groups_parent_group_id",
                table: "chat_channel_groups",
                column: "parent_group_id",
                principalTable: "chat_channel_groups",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_chat_channel_groups_chat_channel_groups_parent_group_id",
                table: "chat_channel_groups");

            migrationBuilder.DropIndex(
                name: "IX_chat_channel_groups_parent_group_id_sort_order_id",
                table: "chat_channel_groups");

            migrationBuilder.DropCheckConstraint(
                name: "CK_chat_channel_groups_parent",
                table: "chat_channel_groups");

            migrationBuilder.DropColumn(
                name: "parent_group_id",
                table: "chat_channel_groups");
        }
    }
}
