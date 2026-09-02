using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInspectionLotTransfers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "lot_transfers",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    source_inspection_id = table.Column<long>(type: "bigint", nullable: false),
                    destination_inspection_id = table.Column<long>(type: "bigint", nullable: false),
                    quantity_moved = table.Column<int>(type: "integer", nullable: false),
                    performed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_lot_transfers", x => x.id);
                    table.CheckConstraint("CK_lot_transfers_different_inspections", "source_inspection_id <> destination_inspection_id");
                    table.CheckConstraint("CK_lot_transfers_positive_quantity", "quantity_moved > 0");
                    table.ForeignKey(
                        name: "FK_lot_transfers_destination_inspection_id",
                        column: x => x.destination_inspection_id,
                        principalTable: "inspections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_lot_transfers_source_inspection_id",
                        column: x => x.source_inspection_id,
                        principalTable: "inspections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_lot_transfers_destination_inspection_id",
                table: "lot_transfers",
                column: "destination_inspection_id");

            migrationBuilder.CreateIndex(
                name: "IX_lot_transfers_source_inspection_id",
                table: "lot_transfers",
                column: "source_inspection_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "lot_transfers");
        }
    }
}
