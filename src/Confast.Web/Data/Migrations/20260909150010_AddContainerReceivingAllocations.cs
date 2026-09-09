using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddContainerReceivingAllocations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "received_at_utc",
                table: "containers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "received_by_user_id",
                table: "containers",
                type: "character varying(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "actual_received_quantity",
                table: "container_group_parts",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "container_receipt_allocations",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    operation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    container_group_part_id = table.Column<long>(type: "bigint", nullable: false),
                    inspection_id = table.Column<long>(type: "bigint", nullable: true),
                    quantity = table.Column<int>(type: "integer", nullable: false),
                    manufacturer_lot_number = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    internal_lot_number = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    action = table.Column<int>(type: "integer", nullable: false),
                    established_destination_manufacturer_lot = table.Column<bool>(type: "boolean", nullable: false),
                    manufacturer_lot_establishment_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    acting_user_id = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    performed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    reversal_reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    reversed_by_user_id = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: true),
                    reversed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_container_receipt_allocations", x => x.id);
                    table.CheckConstraint("CK_container_receipt_allocations_action", "action IN (1, 2)");
                    table.CheckConstraint("CK_container_receipt_allocations_manufacturer_lot", "btrim(manufacturer_lot_number) <> ''");
                    table.CheckConstraint("CK_container_receipt_allocations_positive_quantity", "quantity > 0");
                    table.CheckConstraint("CK_container_receipt_allocations_reversal", "(reversed_at_utc IS NULL AND reversed_by_user_id IS NULL AND reversal_reason IS NULL) OR (reversed_at_utc IS NOT NULL AND reversed_by_user_id IS NOT NULL AND btrim(reversal_reason) <> '')");
                    table.ForeignKey(
                        name: "FK_container_receipt_allocations_container_group_parts_contain~",
                        column: x => x.container_group_part_id,
                        principalTable: "container_group_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_container_receipt_allocations_inspections_inspection_id",
                        column: x => x.inspection_id,
                        principalTable: "inspections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "container_receipt_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    container_id = table.Column<long>(type: "bigint", nullable: false),
                    previous_received_date = table.Column<DateOnly>(type: "date", nullable: true),
                    received_date = table.Column<DateOnly>(type: "date", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    acting_user_id = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    performed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_container_receipt_history", x => x.id);
                    table.ForeignKey(
                        name: "FK_container_receipt_history_containers_container_id",
                        column: x => x.container_id,
                        principalTable: "containers",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "container_received_quantity_history",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    container_group_part_id = table.Column<long>(type: "bigint", nullable: false),
                    previous_quantity = table.Column<int>(type: "integer", nullable: false),
                    new_quantity = table.Column<int>(type: "integer", nullable: false),
                    reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    acting_user_id = table.Column<string>(type: "character varying(450)", maxLength: 450, nullable: false),
                    performed_at_utc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_container_received_quantity_history", x => x.id);
                    table.CheckConstraint("CK_container_received_quantity_history_nonnegative", "previous_quantity >= 0 AND new_quantity >= 0");
                    table.ForeignKey(
                        name: "FK_container_received_quantity_history_container_group_parts_c~",
                        column: x => x.container_group_part_id,
                        principalTable: "container_group_parts",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_container_group_parts_actual_received_quantity",
                table: "container_group_parts",
                sql: "actual_received_quantity IS NULL OR actual_received_quantity >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_container_receipt_allocations_container_group_part_id",
                table: "container_receipt_allocations",
                column: "container_group_part_id");

            migrationBuilder.CreateIndex(
                name: "IX_container_receipt_allocations_inspection_id",
                table: "container_receipt_allocations",
                column: "inspection_id");

            migrationBuilder.CreateIndex(
                name: "UX_container_receipt_allocations_operation_id",
                table: "container_receipt_allocations",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_container_receipt_history_container_id",
                table: "container_receipt_history",
                column: "container_id");

            migrationBuilder.CreateIndex(
                name: "IX_container_received_quantity_history_container_group_part_id",
                table: "container_received_quantity_history",
                column: "container_group_part_id");

            migrationBuilder.Sql("""
                CREATE FUNCTION validate_container_receipt_allocation() RETURNS trigger AS $$
                DECLARE
                    source_actual integer;
                    source_part bigint;
                    source_po text;
                    source_received date;
                    destination_part bigint;
                    destination_po text;
                    destination_manufacturer_lot text;
                    allocated integer;
                BEGIN
                    SELECT p.actual_received_quantity, p.part_id, p.purchase_order_number, c.received_date
                    INTO source_actual, source_part, source_po, source_received
                    FROM container_group_parts p
                    JOIN container_groups g ON g.id = p.container_group_id
                    JOIN containers c ON c.id = g.container_id
                    WHERE p.id = NEW.container_group_part_id
                    FOR UPDATE OF p;

                    IF source_received IS NULL OR source_actual IS NULL THEN
                        RAISE EXCEPTION 'Only received container lines can be allocated' USING ERRCODE = '23514';
                    END IF;
                    IF NEW.reversed_at_utc IS NULL THEN
                        IF NEW.inspection_id IS NULL THEN
                            RAISE EXCEPTION 'An active receipt allocation requires an inspection' USING ERRCODE = '23514';
                        END IF;
                        SELECT i.part_id, i.conformance_po_number, i.manufacturer_lot_number
                        INTO destination_part, destination_po, destination_manufacturer_lot
                        FROM inspections i WHERE i.id = NEW.inspection_id;
                        IF destination_part <> source_part
                            OR upper(btrim(destination_po)) IS DISTINCT FROM upper(btrim(source_po)) THEN
                            RAISE EXCEPTION 'Receipt allocation Part and PO must match the destination inspection' USING ERRCODE = '23514';
                        END IF;
                        IF destination_manufacturer_lot IS NULL
                            OR upper(btrim(destination_manufacturer_lot)) <> upper(btrim(NEW.manufacturer_lot_number)) THEN
                            RAISE EXCEPTION 'Receipt allocation manufacturer lot must match the destination inspection' USING ERRCODE = '23514';
                        END IF;
                        SELECT COALESCE(sum(a.quantity), 0) INTO allocated
                        FROM container_receipt_allocations a
                        WHERE a.container_group_part_id = NEW.container_group_part_id
                            AND a.reversed_at_utc IS NULL
                            AND a.id <> COALESCE(NEW.id, 0);
                        IF allocated + NEW.quantity > source_actual THEN
                            RAISE EXCEPTION 'Receipt allocations exceed actual received quantity' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER TR_container_receipt_allocations_validate
                BEFORE INSERT OR UPDATE ON container_receipt_allocations
                FOR EACH ROW EXECUTE FUNCTION validate_container_receipt_allocation();

                CREATE FUNCTION protect_container_received_quantity() RETURNS trigger AS $$
                DECLARE allocated integer;
                BEGIN
                    IF TG_OP = 'DELETE' OR NEW.part_id <> OLD.part_id
                        OR NEW.purchase_order_number <> OLD.purchase_order_number
                        OR NEW.quantity <> OLD.quantity THEN
                        IF EXISTS (SELECT 1 FROM container_receipt_allocations a WHERE a.container_group_part_id = OLD.id) THEN
                            RAISE EXCEPTION 'A receipt source line with allocation history cannot be deleted or have its identity changed' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    IF TG_OP <> 'DELETE' AND NEW.actual_received_quantity IS NOT NULL THEN
                        SELECT COALESCE(sum(a.quantity), 0) INTO allocated
                        FROM container_receipt_allocations a
                        WHERE a.container_group_part_id = NEW.id AND a.reversed_at_utc IS NULL;
                        IF NEW.actual_received_quantity < allocated THEN
                            RAISE EXCEPTION 'Actual received quantity cannot be below active allocations' USING ERRCODE = '23514';
                        END IF;
                    END IF;
                    RETURN CASE WHEN TG_OP = 'DELETE' THEN OLD ELSE NEW END;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER TR_container_group_parts_protect_receipt
                BEFORE UPDATE OR DELETE ON container_group_parts
                FOR EACH ROW EXECUTE FUNCTION protect_container_received_quantity();

                CREATE FUNCTION protect_container_receipt_status() RETURNS trigger AS $$
                BEGIN
                    IF OLD.received_date IS NOT NULL AND NEW.received_date IS NULL
                        AND EXISTS (
                            SELECT 1
                            FROM container_receipt_allocations a
                            JOIN container_group_parts p ON p.id = a.container_group_part_id
                            JOIN container_groups g ON g.id = p.container_group_id
                            WHERE g.container_id = OLD.id AND a.reversed_at_utc IS NULL)
                    THEN
                        RAISE EXCEPTION 'A container with active allocations cannot have receipt status cleared' USING ERRCODE = '23514';
                    END IF;
                    RETURN NEW;
                END;
                $$ LANGUAGE plpgsql;

                CREATE TRIGGER TR_containers_protect_receipt_status
                BEFORE UPDATE OF received_date ON containers
                FOR EACH ROW EXECUTE FUNCTION protect_container_receipt_status();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER IF EXISTS TR_containers_protect_receipt_status ON containers;
                DROP FUNCTION IF EXISTS protect_container_receipt_status();
                DROP TRIGGER IF EXISTS TR_container_group_parts_protect_receipt ON container_group_parts;
                DROP FUNCTION IF EXISTS protect_container_received_quantity();
                DROP TRIGGER IF EXISTS TR_container_receipt_allocations_validate ON container_receipt_allocations;
                DROP FUNCTION IF EXISTS validate_container_receipt_allocation();
                """);
            migrationBuilder.DropTable(
                name: "container_receipt_allocations");

            migrationBuilder.DropTable(
                name: "container_receipt_history");

            migrationBuilder.DropTable(
                name: "container_received_quantity_history");

            migrationBuilder.DropCheckConstraint(
                name: "CK_container_group_parts_actual_received_quantity",
                table: "container_group_parts");

            migrationBuilder.DropColumn(
                name: "received_at_utc",
                table: "containers");

            migrationBuilder.DropColumn(
                name: "received_by_user_id",
                table: "containers");

            migrationBuilder.DropColumn(
                name: "actual_received_quantity",
                table: "container_group_parts");
        }
    }
}
