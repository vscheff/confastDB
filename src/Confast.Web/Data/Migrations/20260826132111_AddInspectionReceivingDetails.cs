using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddInspectionReceivingDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "conformance_po_number",
                table: "inspections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "date_received",
                table: "inspections",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "inspector",
                table: "inspections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "manufacturer_lot_number",
                table: "inspections",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "quantity_inspected",
                table: "inspections",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_inspections_quantity_inspected",
                table: "inspections",
                sql: "quantity_inspected IS NULL OR quantity_inspected > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_inspections_quantity_inspected",
                table: "inspections");

            migrationBuilder.DropColumn(
                name: "conformance_po_number",
                table: "inspections");

            migrationBuilder.DropColumn(
                name: "date_received",
                table: "inspections");

            migrationBuilder.DropColumn(
                name: "inspector",
                table: "inspections");

            migrationBuilder.DropColumn(
                name: "manufacturer_lot_number",
                table: "inspections");

            migrationBuilder.DropColumn(
                name: "quantity_inspected",
                table: "inspections");
        }
    }
}
