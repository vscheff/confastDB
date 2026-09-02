using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInspectionLotFlips : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "part_flip_definitions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_part_id = table.Column<long>(type: "bigint", nullable: false),
                    target_part_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_part_flip_definitions", x => x.id);
                    table.CheckConstraint("CK_part_flip_definitions_different_parts", "source_part_id <> target_part_id");
                    table.ForeignKey(
                        name: "FK_part_flip_definitions_source_part_id",
                        column: x => x.source_part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_part_flip_definitions_target_part_id",
                        column: x => x.target_part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lot_flips",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_inspection_id = table.Column<long>(type: "bigint", nullable: false),
                    destination_inspection_id = table.Column<long>(type: "bigint", nullable: false),
                    part_flip_definition_id = table.Column<long>(type: "bigint", nullable: false),
                    performed_by_user_id = table.Column<string>(type: "text", nullable: true),
                    performed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lot_flips", x => x.id);
                    table.CheckConstraint("CK_lot_flips_different_inspections", "source_inspection_id <> destination_inspection_id");
                    table.ForeignKey(
                        name: "FK_lot_flips_definition_id",
                        column: x => x.part_flip_definition_id,
                        principalTable: "part_flip_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lot_flips_destination_inspection_id",
                        column: x => x.destination_inspection_id,
                        principalTable: "inspections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lot_flips_source_inspection_id",
                        column: x => x.source_inspection_id,
                        principalTable: "inspections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lot_flips_user_id",
                        column: x => x.performed_by_user_id,
                        principalTable: "identity_users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "part_flip_criterion_mappings",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    part_flip_definition_id = table.Column<long>(type: "bigint", nullable: false),
                    source_criterion_id = table.Column<long>(type: "bigint", nullable: false),
                    target_criterion_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_part_flip_criterion_mappings", x => x.id);
                    table.ForeignKey(
                        name: "FK_part_flip_criterion_mappings_definition_id",
                        column: x => x.part_flip_definition_id,
                        principalTable: "part_flip_definitions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_part_flip_criterion_mappings_source_criterion_id",
                        column: x => x.source_criterion_id,
                        principalTable: "inspection_criteria",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_part_flip_criterion_mappings_target_criterion_id",
                        column: x => x.target_criterion_id,
                        principalTable: "inspection_criteria",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lot_flips_part_flip_definition_id",
                table: "lot_flips",
                column: "part_flip_definition_id");

            migrationBuilder.CreateIndex(
                name: "IX_lot_flips_performed_by_user_id",
                table: "lot_flips",
                column: "performed_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_lot_flips_source_inspection_id",
                table: "lot_flips",
                column: "source_inspection_id");

            migrationBuilder.CreateIndex(
                name: "UX_lot_flips_destination_inspection_id",
                table: "lot_flips",
                column: "destination_inspection_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_part_flip_criterion_mappings_source_criterion_id",
                table: "part_flip_criterion_mappings",
                column: "source_criterion_id");

            migrationBuilder.CreateIndex(
                name: "IX_part_flip_criterion_mappings_target_criterion_id",
                table: "part_flip_criterion_mappings",
                column: "target_criterion_id");

            migrationBuilder.CreateIndex(
                name: "UX_part_flip_mappings_definition_source",
                table: "part_flip_criterion_mappings",
                columns: new[] { "part_flip_definition_id", "source_criterion_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_part_flip_mappings_definition_target",
                table: "part_flip_criterion_mappings",
                columns: new[] { "part_flip_definition_id", "target_criterion_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_part_flip_definitions_target_part_id",
                table: "part_flip_definitions",
                column: "target_part_id");

            migrationBuilder.CreateIndex(
                name: "UX_part_flip_definitions_source_target",
                table: "part_flip_definitions",
                columns: new[] { "source_part_id", "target_part_id" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lot_flips");

            migrationBuilder.DropTable(
                name: "part_flip_criterion_mappings");

            migrationBuilder.DropTable(
                name: "part_flip_definitions");
        }
    }
}
