using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatChannelGroups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_chat_conversations_shape",
                table: "chat_conversations");

            migrationBuilder.AddColumn<long>(
                name: "channel_group_id",
                table: "chat_conversations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "channel_sort_order",
                table: "chat_conversations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Preserve the current channel list order when the layout first becomes persistent.
            migrationBuilder.Sql("""
                WITH ordered AS (
                    SELECT id, row_number() OVER (ORDER BY last_activity_at_utc DESC, id DESC) - 1 AS position
                    FROM chat_conversations
                    WHERE kind = 1
                )
                UPDATE chat_conversations AS channel
                SET channel_sort_order = ordered.position
                FROM ordered
                WHERE channel.id = ordered.id
                """);

            migrationBuilder.CreateTable(
                name: "chat_channel_groups",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_by_user_id = table.Column<string>(type: "text", nullable: false),
                    created_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chat_channel_groups", x => x.id);
                    table.CheckConstraint("CK_chat_channel_groups_name", "btrim(name) <> ''");
                    table.ForeignKey(
                        name: "FK_chat_channel_groups_identity_users_created_by_user_id",
                        column: x => x.created_by_user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_chat_conversations_channel_group_id_channel_sort_order_id",
                table: "chat_conversations",
                columns: new[] { "channel_group_id", "channel_sort_order", "id" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_chat_conversations_shape",
                table: "chat_conversations",
                sql: "(kind = 0 AND visibility IS NULL AND name IS NULL AND direct_pair_key IS NOT NULL AND channel_group_id IS NULL AND channel_sort_order = 0) OR (kind = 1 AND visibility IS NOT NULL AND name IS NOT NULL AND btrim(name) <> '' AND direct_pair_key IS NULL AND channel_sort_order >= 0)");

            migrationBuilder.CreateIndex(
                name: "IX_chat_channel_groups_created_by_user_id",
                table: "chat_channel_groups",
                column: "created_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_chat_channel_groups_name",
                table: "chat_channel_groups",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "IX_chat_channel_groups_sort_order_id",
                table: "chat_channel_groups",
                columns: new[] { "sort_order", "id" });

            migrationBuilder.AddForeignKey(
                name: "FK_chat_conversations_chat_channel_groups_channel_group_id",
                table: "chat_conversations",
                column: "channel_group_id",
                principalTable: "chat_channel_groups",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_chat_conversations_chat_channel_groups_channel_group_id",
                table: "chat_conversations");

            migrationBuilder.DropTable(
                name: "chat_channel_groups");

            migrationBuilder.DropIndex(
                name: "IX_chat_conversations_channel_group_id_channel_sort_order_id",
                table: "chat_conversations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_chat_conversations_shape",
                table: "chat_conversations");

            migrationBuilder.DropColumn(
                name: "channel_group_id",
                table: "chat_conversations");

            migrationBuilder.DropColumn(
                name: "channel_sort_order",
                table: "chat_conversations");

            migrationBuilder.AddCheckConstraint(
                name: "CK_chat_conversations_shape",
                table: "chat_conversations",
                sql: "(kind = 0 AND visibility IS NULL AND name IS NULL AND direct_pair_key IS NOT NULL) OR (kind = 1 AND visibility IS NOT NULL AND name IS NOT NULL AND btrim(name) <> '' AND direct_pair_key IS NULL)");
        }
    }
}
