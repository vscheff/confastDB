using Confast.Web.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20260828143400_AddCertificationPreviewContent")]
public partial class AddCertificationPreviewContent : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<byte[]>(
            name: "preview_content",
            table: "certification_documents",
            type: "bytea",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "preview_content",
            table: "certification_documents");
    }
}
