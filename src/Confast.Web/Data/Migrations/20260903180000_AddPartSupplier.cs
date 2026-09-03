using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations;

public partial class AddPartSupplier : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(
            name: "supplier_id",
            table: "parts",
            type: "bigint",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_parts_supplier_id",
            table: "parts",
            column: "supplier_id");

        migrationBuilder.AddForeignKey(
            name: "FK_parts_suppliers_supplier_id",
            table: "parts",
            column: "supplier_id",
            principalTable: "suppliers",
            principalColumn: "id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_parts_suppliers_supplier_id",
            table: "parts");

        migrationBuilder.DropIndex(
            name: "IX_parts_supplier_id",
            table: "parts");

        migrationBuilder.DropColumn(
            name: "supplier_id",
            table: "parts");
    }
}
