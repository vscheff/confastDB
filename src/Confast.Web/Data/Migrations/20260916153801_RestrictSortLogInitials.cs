using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations;

public partial class RestrictSortLogInitials : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<string>(name: "quality_initials", table: "sort_log_lines", type: "character varying(2)", maxLength: 2, nullable: true, oldClrType: typeof(string), oldType: "character varying(20)", oldMaxLength: 20, oldNullable: true);
        migrationBuilder.AlterColumn<string>(name: "production_initials", table: "sort_log_lines", type: "character varying(2)", maxLength: 2, nullable: true, oldClrType: typeof(string), oldType: "character varying(20)", oldMaxLength: 20, oldNullable: true);
        migrationBuilder.AddCheckConstraint(name: "CK_sort_log_lines_initials", table: "sort_log_lines", sql: "(production_initials IS NOT NULL OR quality_initials IS NOT NULL) AND (production_initials IS NULL OR char_length(production_initials) = 2) AND (quality_initials IS NULL OR char_length(quality_initials) = 2)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "CK_sort_log_lines_initials", table: "sort_log_lines");
        migrationBuilder.AlterColumn<string>(name: "quality_initials", table: "sort_log_lines", type: "character varying(20)", maxLength: 20, nullable: true, oldClrType: typeof(string), oldType: "character varying(2)", oldMaxLength: 2, oldNullable: true);
        migrationBuilder.AlterColumn<string>(name: "production_initials", table: "sort_log_lines", type: "character varying(20)", maxLength: 20, nullable: true, oldClrType: typeof(string), oldType: "character varying(2)", oldMaxLength: 2, oldNullable: true);
    }
}
