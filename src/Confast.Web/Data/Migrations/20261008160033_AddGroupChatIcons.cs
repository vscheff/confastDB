using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupChatIcons : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "icon_content_type",
                table: "chat_conversations",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<byte[]>(
                name: "icon_data",
                table: "chat_conversations",
                type: "bytea",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "icon_giphy_id",
                table: "chat_conversations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_chat_conversations_icon",
                table: "chat_conversations",
                sql: "(icon_data IS NULL AND icon_content_type IS NULL AND icon_giphy_id IS NULL) OR (kind = 2 AND icon_data IS NOT NULL AND octet_length(icon_data) BETWEEN 1 AND 1048576 AND icon_content_type IS NOT NULL AND icon_content_type IN ('image/png', 'image/jpeg', 'image/gif', 'image/webp') AND icon_giphy_id IS NULL) OR (kind = 2 AND icon_data IS NULL AND icon_content_type IS NULL AND icon_giphy_id IS NOT NULL AND icon_giphy_id ~ '^[A-Za-z0-9]{1,100}$')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_chat_conversations_icon",
                table: "chat_conversations");

            migrationBuilder.DropColumn(
                name: "icon_content_type",
                table: "chat_conversations");

            migrationBuilder.DropColumn(
                name: "icon_data",
                table: "chat_conversations");

            migrationBuilder.DropColumn(
                name: "icon_giphy_id",
                table: "chat_conversations");
        }
    }
}
