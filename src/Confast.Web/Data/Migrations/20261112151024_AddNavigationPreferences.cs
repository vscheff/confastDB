using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddNavigationPreferences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "navigation_preferences",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    capacity = table.Column<int>(type: "integer", nullable: false, defaultValue: 8),
                    recommendation_day = table.Column<DateOnly>(type: "date", nullable: true),
                    recommendation_order = table.Column<string[]>(type: "text[]", nullable: false, defaultValueSql: "ARRAY[]::text[]")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_navigation_preferences", x => x.user_id);
                    table.CheckConstraint("CK_navigation_preferences_capacity", "capacity IN (6, 8, 12)");
                    table.ForeignKey(
                        name: "FK_navigation_preferences_identity_users_user_id",
                        column: x => x.user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "navigation_usage",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    destination_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    visit_count = table.Column<long>(type: "bigint", nullable: false),
                    last_visited_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    visit_score = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_navigation_usage", x => new { x.user_id, x.destination_id });
                    table.CheckConstraint("CK_navigation_usage_visit_count", "visit_count > 0");
                    table.CheckConstraint("CK_navigation_usage_visit_score", "visit_score > 0 AND visit_score < 'Infinity'::double precision");
                    table.ForeignKey(
                        name: "FK_navigation_usage_identity_users_user_id",
                        column: x => x.user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "navigation_shortcut_preferences",
                columns: table => new
                {
                    user_id = table.Column<string>(type: "text", nullable: false),
                    destination_id = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    pin_order = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_navigation_shortcut_preferences", x => new { x.user_id, x.destination_id });
                    table.CheckConstraint("CK_navigation_shortcut_preferences_pin_order", "pin_order >= 0");
                    table.ForeignKey(
                        name: "FK_navigation_shortcut_preferences_navigation_preferences_user~",
                        column: x => x.user_id,
                        principalTable: "navigation_preferences",
                        principalColumn: "user_id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "navigation_shortcut_preferences");

            migrationBuilder.DropTable(
                name: "navigation_usage");

            migrationBuilder.DropTable(
                name: "navigation_preferences");
        }
    }
}
