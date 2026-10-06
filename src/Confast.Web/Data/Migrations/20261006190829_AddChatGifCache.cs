using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatGifCache : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "chat_gif_metadata",
                columns: table => new
                {
                    giphy_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    cached_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_gif_metadata", x => x.giphy_id);
                });

            migrationBuilder.CreateTable(
                name: "chat_gif_search_pages",
                columns: table => new
                {
                    query = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    rating = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    offset = table.Column<int>(type: "integer", nullable: false),
                    limit = table.Column<int>(type: "integer", nullable: false),
                    gif_ids = table.Column<string>(type: "jsonb", nullable: false),
                    total = table.Column<int>(type: "integer", nullable: false),
                    cached_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_gif_search_pages", x => new { x.query, x.rating, x.language, x.offset, x.limit });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "chat_gif_metadata");

            migrationBuilder.DropTable(
                name: "chat_gif_search_pages");
        }
    }
}
