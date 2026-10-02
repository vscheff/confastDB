using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AllowMultipleChatAttachments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_chat_attachments",
                table: "chat_attachments");

            migrationBuilder.AddColumn<long>(
                name: "id",
                table: "chat_attachments",
                type: "bigint",
                nullable: false,
                defaultValue: 0L)
                .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn);

            migrationBuilder.AddPrimaryKey(
                name: "PK_chat_attachments",
                table: "chat_attachments",
                column: "id");

            migrationBuilder.CreateIndex(
                name: "IX_chat_attachments_message_id",
                table: "chat_attachments",
                column: "message_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_chat_attachments",
                table: "chat_attachments");

            migrationBuilder.DropIndex(
                name: "IX_chat_attachments_message_id",
                table: "chat_attachments");

            migrationBuilder.DropColumn(
                name: "id",
                table: "chat_attachments");

            migrationBuilder.AddPrimaryKey(
                name: "PK_chat_attachments",
                table: "chat_attachments",
                column: "message_id");
        }
    }
}
