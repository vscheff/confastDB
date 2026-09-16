using Confast.Web.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260916160000_RemoveSortLogDowntimeCauseDisplayOrder")]
public partial class RemoveSortLogDowntimeCauseDisplayOrder : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_sort_log_downtime_causes_display_order",
            table: "sort_log_downtime_causes");

        migrationBuilder.DropColumn(
            name: "display_order",
            table: "sort_log_downtime_causes");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "display_order",
            table: "sort_log_downtime_causes",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddCheckConstraint(
            name: "CK_sort_log_downtime_causes_display_order",
            table: "sort_log_downtime_causes",
            sql: "display_order >= 0");
    }
}
