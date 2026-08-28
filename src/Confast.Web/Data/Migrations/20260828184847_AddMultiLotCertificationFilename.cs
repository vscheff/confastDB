using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Confast.Web.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMultiLotCertificationFilename : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "filename_template",
                table: "customer_certification_settings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500);

            migrationBuilder.AddColumn<string>(
                name: "multi_lot_filename_template",
                table: "customer_certification_settings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_customer_cert_settings_multi_lot_filename_not_blank",
                table: "customer_certification_settings",
                sql: "btrim(multi_lot_filename_template) <> ''");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_customer_cert_settings_multi_lot_filename_not_blank",
                table: "customer_certification_settings");

            migrationBuilder.DropColumn(
                name: "multi_lot_filename_template",
                table: "customer_certification_settings");

            migrationBuilder.Sql(
                "UPDATE customer_certification_settings SET filename_template = '{CustomerName}_{PartNumber}_{LotNumber}_CERTS' WHERE filename_template IS NULL;");

            migrationBuilder.AlterColumn<string>(
                name: "filename_template",
                table: "customer_certification_settings",
                type: "character varying(500)",
                maxLength: 500,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(500)",
                oldMaxLength: 500,
                oldNullable: true);
        }
    }
}
