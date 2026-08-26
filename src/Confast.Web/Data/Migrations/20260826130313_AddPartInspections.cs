using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPartInspections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddUniqueConstraint(
                name: "AK_inspection_criteria_revisions_id_part_id",
                table: "inspection_criteria_revisions",
                columns: new[] { "id", "part_id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_inspection_criteria_id_revision_id",
                table: "inspection_criteria",
                columns: new[] { "id", "inspection_criteria_revision_id" });

            migrationBuilder.CreateTable(
                name: "inspections",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    inspection_criteria_revision_id = table.Column<long>(type: "bigint", nullable: false),
                    lot_number = table.Column<string>(type: "text", nullable: true),
                    inspection_date = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inspections", x => x.id);
                    table.UniqueConstraint("AK_inspections_id_revision_id", x => new { x.id, x.inspection_criteria_revision_id });
                    table.ForeignKey(
                        name: "FK_inspections_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inspections_revision_id_part_id",
                        columns: x => new { x.inspection_criteria_revision_id, x.part_id },
                        principalTable: "inspection_criteria_revisions",
                        principalColumns: new[] { "id", "part_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "inspection_results",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    inspection_id = table.Column<long>(type: "bigint", nullable: false),
                    inspection_criteria_revision_id = table.Column<long>(type: "bigint", nullable: false),
                    inspection_criterion_id = table.Column<long>(type: "bigint", nullable: false),
                    actual_min = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    actual_max = table.Column<decimal>(type: "numeric(18,6)", precision: 18, scale: 6, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inspection_results", x => x.id);
                    table.CheckConstraint("CK_inspection_results_actual_minimum_maximum", "actual_min IS NULL OR actual_max IS NULL OR actual_min <= actual_max");
                    table.ForeignKey(
                        name: "FK_inspection_results_criterion_id_revision_id",
                        columns: x => new { x.inspection_criterion_id, x.inspection_criteria_revision_id },
                        principalTable: "inspection_criteria",
                        principalColumns: new[] { "id", "inspection_criteria_revision_id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_inspection_results_inspection_id_revision_id",
                        columns: x => new { x.inspection_id, x.inspection_criteria_revision_id },
                        principalTable: "inspections",
                        principalColumns: new[] { "id", "inspection_criteria_revision_id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_inspection_results_criterion_id_revision_id",
                table: "inspection_results",
                columns: new[] { "inspection_criterion_id", "inspection_criteria_revision_id" });

            migrationBuilder.CreateIndex(
                name: "IX_inspection_results_inspection_id_inspection_criteria_revisi~",
                table: "inspection_results",
                columns: new[] { "inspection_id", "inspection_criteria_revision_id" });

            migrationBuilder.CreateIndex(
                name: "UX_inspection_results_inspection_id_criterion_id",
                table: "inspection_results",
                columns: new[] { "inspection_id", "inspection_criterion_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_inspections_inspection_criteria_revision_id_part_id",
                table: "inspections",
                columns: new[] { "inspection_criteria_revision_id", "part_id" });

            migrationBuilder.CreateIndex(
                name: "IX_inspections_inspection_date",
                table: "inspections",
                column: "inspection_date");

            migrationBuilder.CreateIndex(
                name: "IX_inspections_part_id",
                table: "inspections",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "IX_inspections_revision_id",
                table: "inspections",
                column: "inspection_criteria_revision_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "inspection_results");

            migrationBuilder.DropTable(
                name: "inspections");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_inspection_criteria_revisions_id_part_id",
                table: "inspection_criteria_revisions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_inspection_criteria_id_revision_id",
                table: "inspection_criteria");
        }
    }
}
