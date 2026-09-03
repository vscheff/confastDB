using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContainerTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shipments",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    updated_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "suppliers",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_suppliers", x => x.id);
                    table.CheckConstraint("CK_suppliers_name", "btrim(name) <> ''");
                });

            migrationBuilder.CreateTable(
                name: "containers",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    shipment_id = table.Column<long>(type: "bigint", nullable: false),
                    container_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    received_date = table.Column<DateOnly>(type: "date", nullable: true),
                    quoted_rate = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    drayage_charge = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true),
                    estimated_departure_date = table.Column<DateOnly>(type: "date", nullable: true),
                    estimated_arrival_date = table.Column<DateOnly>(type: "date", nullable: true),
                    added_to_production_schedule = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_containers", x => x.id);
                    table.CheckConstraint("CK_containers_money", "quoted_rate >= 0 AND drayage_charge >= 0");
                    table.CheckConstraint("CK_containers_number", "btrim(container_number) <> ''");
                    table.ForeignKey(
                        name: "FK_containers_shipments_shipment_id",
                        column: x => x.shipment_id,
                        principalTable: "shipments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shipment_bill_numbers",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    shipment_id = table.Column<long>(type: "bigint", nullable: false),
                    number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shipment_bill_numbers", x => x.id);
                    table.CheckConstraint("CK_shipment_bill_numbers_number", "btrim(number) <> ''");
                    table.ForeignKey(
                        name: "FK_shipment_bill_numbers_shipments_shipment_id",
                        column: x => x.shipment_id,
                        principalTable: "shipments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "bills_of_lading",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    supplier_id = table.Column<long>(type: "bigint", nullable: false),
                    duty = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bills_of_lading", x => x.id);
                    table.CheckConstraint("CK_bills_of_lading_duty", "duty >= 0");
                    table.CheckConstraint("CK_bills_of_lading_number", "number <> '' AND number = upper(btrim(number))");
                    table.ForeignKey(
                        name: "FK_bills_of_lading_suppliers_supplier_id",
                        column: x => x.supplier_id,
                        principalTable: "suppliers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "container_groups",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    container_id = table.Column<long>(type: "bigint", nullable: false),
                    bill_of_lading_id = table.Column<long>(type: "bigint", nullable: false),
                    total_weight = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    pallet_count = table.Column<int>(type: "integer", nullable: true),
                    certifications_received = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_container_groups", x => x.id);
                    table.CheckConstraint("CK_container_groups_totals", "total_weight >= 0 AND pallet_count >= 0");
                    table.ForeignKey(
                        name: "FK_container_groups_bills_of_lading_bill_of_lading_id",
                        column: x => x.bill_of_lading_id,
                        principalTable: "bills_of_lading",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_container_groups_containers_container_id",
                        column: x => x.container_id,
                        principalTable: "containers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "container_group_parts",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    container_group_id = table.Column<long>(type: "bigint", nullable: false),
                    part_id = table.Column<long>(type: "bigint", nullable: false),
                    purchase_order_number = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    quantity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_container_group_parts", x => x.id);
                    table.CheckConstraint("CK_container_group_parts_po", "btrim(purchase_order_number) <> ''");
                    table.CheckConstraint("CK_container_group_parts_quantity", "quantity >= 0");
                    table.ForeignKey(
                        name: "FK_container_group_parts_container_groups_container_group_id",
                        column: x => x.container_group_id,
                        principalTable: "container_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_container_group_parts_parts_part_id",
                        column: x => x.part_id,
                        principalTable: "parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_bills_of_lading_supplier_id",
                table: "bills_of_lading",
                column: "supplier_id");

            migrationBuilder.CreateIndex(
                name: "UX_bills_of_lading_number",
                table: "bills_of_lading",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_container_group_parts_container_group_id",
                table: "container_group_parts",
                column: "container_group_id");

            migrationBuilder.CreateIndex(
                name: "IX_container_group_parts_part_id",
                table: "container_group_parts",
                column: "part_id");

            migrationBuilder.CreateIndex(
                name: "IX_container_group_parts_purchase_order_number",
                table: "container_group_parts",
                column: "purchase_order_number");

            migrationBuilder.CreateIndex(
                name: "IX_container_groups_bill_of_lading_id",
                table: "container_groups",
                column: "bill_of_lading_id");

            migrationBuilder.CreateIndex(
                name: "IX_container_groups_container_id",
                table: "container_groups",
                column: "container_id");

            migrationBuilder.CreateIndex(
                name: "IX_containers_container_number",
                table: "containers",
                column: "container_number");

            migrationBuilder.CreateIndex(
                name: "IX_containers_shipment_id",
                table: "containers",
                column: "shipment_id");

            migrationBuilder.CreateIndex(
                name: "IX_shipment_bill_numbers_number",
                table: "shipment_bill_numbers",
                column: "number");

            migrationBuilder.CreateIndex(
                name: "IX_shipment_bill_numbers_shipment_id",
                table: "shipment_bill_numbers",
                column: "shipment_id");

            migrationBuilder.CreateIndex(
                name: "IX_suppliers_name",
                table: "suppliers",
                column: "name");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "container_group_parts");

            migrationBuilder.DropTable(
                name: "shipment_bill_numbers");

            migrationBuilder.DropTable(
                name: "container_groups");

            migrationBuilder.DropTable(
                name: "bills_of_lading");

            migrationBuilder.DropTable(
                name: "containers");

            migrationBuilder.DropTable(
                name: "suppliers");

            migrationBuilder.DropTable(
                name: "shipments");
        }
    }
}
