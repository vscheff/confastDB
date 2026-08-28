using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations;

public partial class DefaultInspectionSheetForCustomers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Retained as a no-op so databases that already recorded this migration
        // keep a consistent migration history. Inspection Sheet is not a
        // certification and is excluded from active configuration and packaging.
    }

    protected override void Down(MigrationBuilder migrationBuilder) { }
}
