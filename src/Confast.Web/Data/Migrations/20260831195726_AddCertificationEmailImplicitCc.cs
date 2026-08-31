using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCertificationEmailImplicitCc : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "certification_email_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false),
                    implicit_cc_address = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_certification_email_settings", x => x.id);
                    table.CheckConstraint("CK_certification_email_settings_implicit_cc_not_blank", "implicit_cc_address IS NULL OR btrim(implicit_cc_address) <> ''");
                    table.CheckConstraint("CK_certification_email_settings_singleton", "id = 1");
                });

            migrationBuilder.InsertData(
                table: "certification_email_settings",
                columns: ["id", "implicit_cc_address"],
                values: [1, "quality@conformancefasteners.com"]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "certification_email_settings");
        }
    }
}
