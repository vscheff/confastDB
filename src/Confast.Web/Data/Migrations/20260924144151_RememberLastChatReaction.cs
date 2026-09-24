using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class RememberLastChatReaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "last_reaction_emoji",
                table: "identity_users",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE identity_users AS users
                SET last_reaction_emoji = latest.emoji
                FROM (
                    SELECT DISTINCT ON (user_id) user_id, emoji
                    FROM chat_message_reactions
                    WHERE emoji NOT LIKE '👍%' AND emoji NOT LIKE '👎%'
                    ORDER BY user_id, reacted_at_utc DESC, message_id DESC, emoji DESC
                ) AS latest
                WHERE users.id = latest.user_id
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "last_reaction_emoji",
                table: "identity_users");
        }
    }
}
