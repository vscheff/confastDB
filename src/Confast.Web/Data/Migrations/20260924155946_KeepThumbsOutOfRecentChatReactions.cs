using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class KeepThumbsOutOfRecentChatReactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE identity_users AS users
                SET last_reaction_emoji = (
                    SELECT reaction.emoji
                    FROM chat_message_reactions AS reaction
                    WHERE reaction.user_id = users.id
                      AND reaction.emoji NOT LIKE '👍%'
                      AND reaction.emoji NOT LIKE '👎%'
                    ORDER BY reaction.reacted_at_utc DESC, reaction.message_id DESC, reaction.emoji DESC
                    LIMIT 1
                )
                WHERE users.last_reaction_emoji LIKE '👍%'
                   OR users.last_reaction_emoji LIKE '👎%'
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The overwritten thumb choice cannot be reconstructed after this cleanup.
        }
    }
}
