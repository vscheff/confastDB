using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGagesAndInspectionMethods : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "gage_type_id",
                table: "inspection_criteria",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "gage_types",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gage_types", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "gages",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    gage_type_id = table.Column<long>(type: "bigint", nullable: false),
                    gage_number = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_gages", x => x.id);
                    table.ForeignKey(
                        name: "FK_gages_gage_types_gage_type_id",
                        column: x => x.gage_type_id,
                        principalTable: "gage_types",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inspection_criteria_gage_type_id",
                table: "inspection_criteria",
                column: "gage_type_id");

            migrationBuilder.CreateIndex(
                name: "UX_gage_types_name",
                table: "gage_types",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_gages_gage_type_id",
                table: "gages",
                column: "gage_type_id");

            migrationBuilder.CreateIndex(
                name: "UX_gages_gage_number",
                table: "gages",
                column: "gage_number",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_inspection_criteria_gage_types_gage_type_id",
                table: "inspection_criteria",
                column: "gage_type_id",
                principalTable: "gage_types",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_inspection_criteria_gage_types_gage_type_id",
                table: "inspection_criteria");

            migrationBuilder.DropTable(
                name: "gages");

            migrationBuilder.DropTable(
                name: "gage_types");

            migrationBuilder.DropIndex(
                name: "IX_inspection_criteria_gage_type_id",
                table: "inspection_criteria");

            migrationBuilder.DropColumn(
                name: "gage_type_id",
                table: "inspection_criteria");
        }
    }
}
